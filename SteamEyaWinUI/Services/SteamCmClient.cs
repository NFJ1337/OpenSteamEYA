using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal enum SteamEresult
{
    Ok = 1,
    InvalidPassword = 5,
    LoggedInElsewhere = 6,
    InvalidParam = 8,
    AccessDenied = 15,
    Revoked = 26,
    Expired = 27,
    LogonSessionReplaced = 34,
    AlreadyLoggedInElsewhere = 50,
    AccountLogonDenied = 63
}

internal sealed class SteamCmException : InvalidOperationException
{
    public SteamCmException(int result)
        : base(GetMessage(result))
    {
        Result = result;
    }

    /// <summary>带调用方文案的构造：保留 EResult（IsTokenFailure 才能正确判定），文案仍可写清是哪一步失败的。</summary>
    public SteamCmException(int result, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Result = result;
    }

    public int Result { get; }

    public bool IsTokenFailure => Result is
        (int)SteamEresult.InvalidPassword or
        (int)SteamEresult.InvalidParam or
        (int)SteamEresult.AccessDenied or
        (int)SteamEresult.Revoked or
        (int)SteamEresult.Expired or
        (int)SteamEresult.AccountLogonDenied;

    private static string GetMessage(int result)
    {
        return result switch
        {
            (int)SteamEresult.Revoked =>
                Loc.T("Cm_Error_TokenRevoked"),
            (int)SteamEresult.Expired =>
                Loc.T("Cm_Error_TokenExpired"),
            (int)SteamEresult.InvalidPassword or
            (int)SteamEresult.InvalidParam or
            (int)SteamEresult.AccessDenied or
            (int)SteamEresult.AccountLogonDenied =>
                Loc.T("Cm_Error_TokenRejected"),
            _ => Loc.Tf("Cm_Error_LogOnFailed_Format", result)
        };
    }
}

internal sealed class SteamCmClient : IAsyncDisposable
{
    private const uint ProtoMask = 0x80000000;
    private const ulong JobIdNone = ulong.MaxValue;
    private const int ProtocolVersion = 65580;
    private const uint LogonId = 0xA1A2A3A4;
    private const int ClientOsWindows10 = 16;
    private const int WebSocketBufferSize = 64 * 1024;
    private const int RequestTimeoutMs = 15_000;

    // 单台 CM 的连接超时。原来是 10 秒：CM 列表里总有连不上的节点，等满一次就白花 10 秒
    // （「一键查询」要 20 秒的主因）。连不上通常在 1~2 秒内就失败，4 秒足够，失败立刻换下一台。
    private const int ConnectTimeoutMs = 4_000;

    // CM 服务器列表缓存 + 上次连通的服务器：省掉每次查询都去 ISteamDirectory 拉一次，
    // 并且优先用验证过能连上的那台，通常第一台就成功、不再经历失败重试。
    private static readonly object ServerListLock = new();
    private static readonly TimeSpan ServerListTtl = TimeSpan.FromMinutes(10);

    private static List<string>? _cachedServers;
    private static DateTime _cachedServersAt = DateTime.MinValue;
    private static string? _lastGoodServer;

    private const int EMsgMulti = 1;
    private const int EMsgServiceMethodCallFromClient = 151;
    private const int EMsgClientHeartBeat = 703;
    private const int EMsgClientLogOff = 706;
    private const int EMsgClientLogOnResponse = 751;
    private const int EMsgClientLoggedOff = 757;
    private const int EMsgClientLogon = 5514;
    private const int EMsgClientGamesPlayedWithDataBlob = 5410;
    private const int EMsgClientToGC = 5452;
    private const int EMsgClientFromGC = 5453;
    private const int MaxCachedGcMessagesPerKey = 8;
    private const int MaxCachedGcMessageKeys = 64;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ServiceMethodResponse>> _jobs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SteamGcClientMessage>> _gcWaiters = new();
    private readonly Dictionary<string, Queue<SteamGcClientMessage>> _gcMessageCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cacheableGcMessageKeys = new(StringComparer.Ordinal);
    private readonly object _gcMessagesLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancellation = new();

    private ClientWebSocket? _socket;
    private Task? _receiveTask;
    private Timer? _heartbeatTimer;
    private TaskCompletionSource<LogonResponse>? _logonResponse;
    private TaskCompletionSource<bool>? _loggedOffResponse;
    private Action<SteamGcClientMessage>? _gcMessageTap;
    private int _sessionId;
    private ulong _steamId;
    private ulong _currentJobId = (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue);

    public SteamCmClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ConnectAndLogOnAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        _steamId = ulong.Parse(steamId, CultureInfo.InvariantCulture);

        var listWatch = Stopwatch.StartNew();
        var servers = await GetCmServersAsync(cancellationToken);
        AppLog.Info($"[cm] 拿到 {servers.Count} 台 CM（耗时 {listWatch.ElapsedMilliseconds} ms），首台 {servers[0]}");

        Exception? lastError = null;
        for (var index = 0; index < servers.Count && index < 8; index++)
        {
            var server = servers[index];
            cancellationToken.ThrowIfCancellationRequested();

            var attemptWatch = Stopwatch.StartNew();
            try
            {
                await ConnectAsync(server, cancellationToken);
                var connectMs = attemptWatch.ElapsedMilliseconds;
                await LogOnAsync(refreshToken, cancellationToken);
                AppLog.Info($"[cm] 第 {index + 1} 台 {server} 成功（连接 {connectMs} ms / 登录往返 {attemptWatch.ElapsedMilliseconds - connectMs} ms）");

                lock (ServerListLock)
                {
                    _lastGoodServer = server;
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is SteamCmException cmException && cmException.IsTokenFailure)
                {
                    throw;
                }

                AppLog.Warn($"[cm] 第 {index + 1} 台 {server} 失败（{attemptWatch.ElapsedMilliseconds} ms）：{ex.Message}");
                lastError = ex;
                await DisconnectSocketAsync();
            }
        }

        throw new InvalidOperationException(
            Loc.Tf("Cm_Error_CannotConnect_Format", lastError?.Message),
            lastError);
    }

    /// <summary>连接是否还活着（批内复用前的判断）。</summary>
    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>
    /// 复用已经握手好的 WebSocket 换账号登录：先登出旧账号，再用新 refresh token 登录。
    /// 目的是省掉每账号一次 WebSocket 握手（日志实测「连接 xxx ms」0.8~2.2 秒）。
    /// 连接已断、或换账号登录失败时返回 false，由调用方回退到 <see cref="ConnectAndLogOnAsync"/> 重新握手：
    /// 复用不成时行为与改动前一致，不会更差。
    /// </summary>
    public async Task<bool> TryRelogOnAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return false;
        }

        try
        {
            await LogOffAsync(cancellationToken);

            // 换账号：登录消息头里的 steamId 要跟着换，会话 id 归零（服务端会在登录响应里重新下发）。
            _steamId = ulong.Parse(steamId, CultureInfo.InvariantCulture);
            _sessionId = 0;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            await LogOnAsync(refreshToken, cancellationToken);
            AppLog.Info("[cm] 复用已连的 CM 连接换账号登录成功（省掉一次 WebSocket 握手）");
            return true;
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            // 令牌/账号本身的问题：照旧抛给调用方判定，不在复用层吞掉。
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"[cm] 复用连接换账号登录失败（{ex.Message}），改为重新握手。");
            return false;
        }
    }

    /// <summary>
    /// 发 ClientLogOff 并等 Steam 回 ClientLoggedOff。CM 偶尔不回也不影响后续登录
    /// （协议允许同一连接上重新发 ClientLogon），所以只等 5 秒，超时就继续。
    /// </summary>
    private async Task LogOffAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        var loggedOff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loggedOffResponse = loggedOff;
        try
        {
            await SendProtobufMessageAsync(
                EMsgClientLogOff,
                Array.Empty<byte>(),
                targetJobName: null,
                jobIdSource: null,
                realm: null,
                cancellationToken);

            await loggedOff.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            // 服务端没回 LoggedOff：直接继续，用登录响应判定结果。
        }
        finally
        {
            _loggedOffResponse = null;
        }
    }

    public async Task<string> GenerateAccessTokenForAppAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteString(1, refreshToken);
            writer.WriteFixed64(2, _steamId);
            writer.WriteInt32(3, 0);
        });

        var response = await SendServiceMethodAsync(
            "Authentication.GenerateAccessTokenForApp#1",
            requestBody,
            realm: 1,
            cancellationToken);

        EnsureOk(response, Loc.T("Cm_Error_GetAccessTokenFailed"));

        var accessToken = DecodeAccessTokenForAppResponse(response.Body);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(Loc.T("Cm_Error_AccessTokenEmpty"));
        }

        return accessToken;
    }

    public async Task<SteamEresult> UnsubscribePublishedFileAsync(
        ulong publishedFileId,
        uint appId,
        int listType,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt64(1, publishedFileId);
            writer.WriteUInt32(2, (uint)listType);
            writer.WriteInt32(3, (int)appId);
            writer.WriteBool(4, true);
        });

        var response = await SendServiceMethodAsync(
            "PublishedFile.Unsubscribe#1",
            requestBody,
            realm: null,
            cancellationToken);

        return (SteamEresult)response.Result;
    }

    public async Task SetGamesPlayedAsync(
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        var body = SteamProtoWriter.Build(writer =>
        {
            foreach (var appId in appIds)
            {
                writer.WriteBytes(1, SteamProtoWriter.Build(game =>
                    game.WriteFixed64(2, appId)));
            }

            writer.WriteUInt32(2, ClientOsWindows10);
        });

        await SendProtobufMessageAsync(
            EMsgClientGamesPlayedWithDataBlob,
            body,
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken);
    }

    public async Task SendGcProtobufMessageAsync(
        uint appId,
        uint msgType,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var gcPayload = BuildGcProtobufPayload(appId, msgType, payload);
        var body = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, appId);
            writer.WriteUInt32(2, msgType | ProtoMask);
            writer.WriteBytes(3, gcPayload);
        });

        await SendProtobufMessageAsync(
            EMsgClientToGC,
            body,
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken,
            routingAppId: appId);
    }

    public void SetGcMessageTap(Action<SteamGcClientMessage>? tap)
    {
        _gcMessageTap = tap;
    }

    public async Task<SteamGcClientMessage> WaitForGcMessageAsync(
        uint appId,
        uint msgType,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool cacheUnmatched = false)
    {
        var key = GetGcWaiterKey(appId, msgType);
        var completion = new TaskCompletionSource<SteamGcClientMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gcMessagesLock)
        {
            if (cacheUnmatched)
            {
                _cacheableGcMessageKeys.Add(key);
            }

            if (TryDequeueCachedGcMessage(key, out var cachedMessage))
            {
                return cachedMessage;
            }

            if (!_gcWaiters.TryAdd(key, completion))
            {
                throw new InvalidOperationException(Loc.T("Cm_Error_DuplicateGcWaiter"));
            }
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            lock (_gcMessagesLock)
            {
                _gcWaiters.TryRemove(key, out _);
            }
        }
    }

    public async Task<bool> IsPublishedFileSubscribedAsync(
        ulong publishedFileId,
        uint appId,
        int listType,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, appId);
            writer.WriteFixed64(2, publishedFileId);
            writer.WriteUInt32(3, (uint)listType);
        });

        var response = await SendServiceMethodAsync(
            "PublishedFile.AreFilesInSubscriptionList#1",
            requestBody,
            realm: null,
            cancellationToken);

        EnsureOk(response, Loc.T("Cm_Error_CheckSubscriptionFailed"));
        return DecodeAreFilesInSubscriptionListResponse(response.Body, publishedFileId);
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer?.Dispose();

        try
        {
            if (_socket?.State == WebSocketState.Open)
            {
                await SendProtobufMessageAsync(
                    EMsgClientLogOff,
                    Array.Empty<byte>(),
                    targetJobName: null,
                    jobIdSource: null,
                    realm: null,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort logoff; the socket is closed below either way.
        }

        _receiveCancellation.Cancel();
        await DisconnectSocketAsync();
        _receiveCancellation.Dispose();
        _sendLock.Dispose();
    }

    private async Task<List<string>> GetCmServersAsync(CancellationToken cancellationToken)
    {
        var servers = await GetCachedOrFetchServersAsync(cancellationToken);

        // 上次连通的服务器排到最前：绝大多数情况第一台就成功，省掉一轮失败重试的等待。
        lock (ServerListLock)
        {
            if (_lastGoodServer is { Length: > 0 } last && servers.Contains(last))
            {
                servers.Remove(last);
                servers.Insert(0, last);
            }

            return servers;
        }
    }

    private async Task<List<string>> GetCachedOrFetchServersAsync(CancellationToken cancellationToken) =>
        await GetCachedOrFetchServersAsync(_httpClient, cancellationToken);

    /// <summary>
    /// 预热：提前把 CM 服务器列表拉下来填进缓存（顺带把 DNS / TLS 握手 / 连接池都建好）。
    /// 「打开软件后第一次查询特别慢」基本都花在这上面，之后才走缓存。
    /// </summary>
    public static async Task PrewarmAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var servers = await GetCachedOrFetchServersAsync(httpClient, cancellationToken);
        AppLog.Info($"[cm] 预热完成：{servers.Count} 台 CM 已缓存（{watch.ElapsedMilliseconds} ms）。");
    }

    private static async Task<List<string>> GetCachedOrFetchServersAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        lock (ServerListLock)
        {
            if (_cachedServers is { Count: > 0 } cached && DateTime.UtcNow - _cachedServersAt < ServerListTtl)
            {
                return [.. cached];
            }
        }

        const string url = "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v0001/"
            + "?format=json&cellid=0&cmtype=websockets";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            !responseElement.TryGetProperty("serverlist", out var serverList))
        {
            throw new InvalidOperationException(Loc.T("Cm_Error_ServerListMalformed"));
        }

        var servers = serverList.EnumerateArray()
            .Where(server => GetString(server, "type") == "websockets" &&
                GetString(server, "realm") == "steamglobal")
            .OrderBy(server => GetDouble(server, "wtd_load"))
            .Select(server => GetString(server, "endpoint"))
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint!)
            .ToList();

        if (servers.Count == 0)
        {
            throw new InvalidOperationException(Loc.T("Cm_Error_NoWebSocketServers"));
        }

        lock (ServerListLock)
        {
            _cachedServers = servers;
            _cachedServersAt = DateTime.UtcNow;
        }

        return servers;
    }

    private async Task ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // CM 的 WebSocket 同样直连（我们自己接管系统代理时）：否则登录往返会被节点拖长。
        _socket.Options.Proxy = SystemProxyService.IsApplied ? null : WebRequest.GetSystemWebProxy();

        // 不回包的 CM 会让 ConnectAsync 挂起数分钟，必须按单个 endpoint 限时；
        // 超时抛 TimeoutException（而非 OperationCanceledException），
        // 让 ConnectAndLogOnAsync 的重试循环继续尝试下一个 CM。
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeoutMs);

        try
        {
            await _socket.ConnectAsync(new Uri($"wss://{endpoint}/cmsocket/"), connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.Tf("Cm_Error_ConnectTimeout_Format", endpoint));
        }

        _receiveTask = Task.Run(
            () => ReceiveLoopAsync(_receiveCancellation.Token),
            CancellationToken.None);
    }

    private async Task LogOnAsync(string refreshToken, CancellationToken cancellationToken)
    {
        _logonResponse = new TaskCompletionSource<LogonResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await SendProtobufMessageAsync(
            EMsgClientLogon,
            EncodeClientLogon(refreshToken, _steamId),
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken);

        var logon = await _logonResponse.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (logon.Result != (int)SteamEresult.Ok)
        {
            throw new SteamCmException(logon.Result);
        }

        if (logon.HeartbeatSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(logon.HeartbeatSeconds);
            _heartbeatTimer = new Timer(
                _ => _ = SendHeartbeatAsync(),
                null,
                interval,
                interval);
        }
    }

    private async Task<ServiceMethodResponse> SendServiceMethodAsync(
        string method,
        byte[] requestBody,
        uint? realm,
        CancellationToken cancellationToken)
    {
        var jobId = NextJobId();
        var completion = new TaskCompletionSource<ServiceMethodResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_jobs.TryAdd(jobId, completion))
        {
            throw new InvalidOperationException(Loc.T("Cm_Error_RequestQueueFailed"));
        }

        try
        {
            await SendProtobufMessageAsync(
                EMsgServiceMethodCallFromClient,
                requestBody,
                method,
                jobId,
                realm,
                cancellationToken);

            return await completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(RequestTimeoutMs),
                cancellationToken);
        }
        finally
        {
            _jobs.TryRemove(jobId, out _);
        }
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            if (_socket?.State != WebSocketState.Open)
            {
                return;
            }

            await SendProtobufMessageAsync(
                EMsgClientHeartBeat,
                Array.Empty<byte>(),
                targetJobName: null,
                jobIdSource: null,
                realm: null,
                CancellationToken.None);
        }
        catch
        {
            // Heartbeat failures surface through pending requests or socket close.
        }
    }

    private async Task SendProtobufMessageAsync(
        int eMsg,
        byte[] body,
        string? targetJobName,
        ulong? jobIdSource,
        uint? realm,
        CancellationToken cancellationToken,
        uint? routingAppId = null)
    {
        if (_socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(Loc.T("Cm_Error_NotConnected"));
        }

        var header = SteamProtoWriter.Build(writer =>
        {
            writer.WriteFixed64(1, _steamId);
            writer.WriteInt32(2, _sessionId);
            writer.WriteFixed64(10, jobIdSource ?? JobIdNone);
            writer.WriteFixed64(11, JobIdNone);

            if (routingAppId.HasValue)
            {
                writer.WriteUInt32(3, routingAppId.Value);
            }

            if (!string.IsNullOrWhiteSpace(targetJobName))
            {
                writer.WriteString(12, targetJobName);
            }

            if (realm.HasValue)
            {
                writer.WriteUInt32(32, realm.Value);
            }
        });

        var packet = new byte[8 + header.Length + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), (uint)eMsg | ProtoMask);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)header.Length);
        header.CopyTo(packet.AsSpan(8));
        body.CopyTo(packet.AsSpan(8 + header.Length));

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(
                packet,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[WebSocketBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                _socket?.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailPendingRequests(new InvalidOperationException(Loc.T("Cm_Error_ConnectionClosed")));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleNetMessage(message.ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailPendingRequests(ex);
        }
    }

    private void HandleNetMessage(byte[] packet)
    {
        if (packet.Length < 8)
        {
            return;
        }

        var rawMsg = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4));
        var eMsg = (int)(rawMsg & ~ProtoMask);
        var isProto = (rawMsg & ProtoMask) != 0;

        if (!isProto)
        {
            if (eMsg == EMsgClientLogOnResponse)
            {
                _logonResponse?.TrySetException(
                    new InvalidOperationException(Loc.T("Cm_Error_NonProtobufLogon")));
            }

            return;
        }

        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        if (headerLength < 0 || packet.Length < 8 + headerLength)
        {
            return;
        }

        var header = DecodeProtoHeader(packet.AsSpan(8, headerLength).ToArray());
        var body = packet[(8 + headerLength)..];

        if (header.ClientSessionId.HasValue && header.ClientSessionId.Value != 0)
        {
            _sessionId = header.ClientSessionId.Value;
        }

        if (header.SteamId.HasValue && header.SteamId.Value != 0)
        {
            _steamId = header.SteamId.Value;
        }

        if (eMsg == EMsgMulti)
        {
            ProcessMulti(body);
            return;
        }

        if (header.JobIdTarget.HasValue &&
            header.JobIdTarget.Value != JobIdNone &&
            _jobs.TryRemove(header.JobIdTarget.Value, out var job))
        {
            job.TrySetResult(new ServiceMethodResponse(
                // proto 定义为 optional int32 eresult = 13 [default = 2]，缺省语义是 Fail=2
                header.Result ?? 2,
                header.ErrorMessage,
                body));
            return;
        }

        switch (eMsg)
        {
            case EMsgClientLogOnResponse:
                _logonResponse?.TrySetResult(DecodeLogonResponse(body));
                break;

            case EMsgClientLoggedOff:
                _loggedOffResponse?.TrySetResult(true);
                FailPendingRequests(CreateLoggedOffException(body));
                break;

            case EMsgClientFromGC:
                HandleGcClientMessage(body);
                break;
        }
    }

    private void HandleGcClientMessage(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        uint? appId = null;
        uint? rawMsgType = null;
        byte[]? payload = null;

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    appId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    rawMsgType = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    payload = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (!appId.HasValue || !rawMsgType.HasValue || payload is null)
        {
            return;
        }

        var msgType = rawMsgType.Value & ~ProtoMask;
        var messageBody = ExtractGcMessageBody(rawMsgType.Value, payload, out var gcClientSessionId);
        if (gcClientSessionId is > 0)
        {
            _sessionId = gcClientSessionId.Value;
        }

        var message = new SteamGcClientMessage(appId.Value, msgType, messageBody);
        _gcMessageTap?.Invoke(message);

        var key = GetGcWaiterKey(appId.Value, msgType);
        TaskCompletionSource<SteamGcClientMessage>? waiter = null;

        lock (_gcMessagesLock)
        {
            if (!_gcWaiters.TryRemove(key, out waiter))
            {
                if (_cacheableGcMessageKeys.Contains(key))
                {
                    EnqueueCachedGcMessage(key, message);
                }

                return;
            }
        }

        waiter.TrySetResult(message);
    }

    private bool TryDequeueCachedGcMessage(string key, out SteamGcClientMessage message)
    {
        if (_gcMessageCache.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            message = queue.Dequeue();
            if (queue.Count == 0)
            {
                _gcMessageCache.Remove(key);
            }

            return true;
        }

        message = default!;
        return false;
    }

    private void EnqueueCachedGcMessage(string key, SteamGcClientMessage message)
    {
        if (!_gcMessageCache.TryGetValue(key, out var queue))
        {
            if (_gcMessageCache.Count >= MaxCachedGcMessageKeys)
            {
                return;
            }

            queue = new Queue<SteamGcClientMessage>();
            _gcMessageCache[key] = queue;
        }

        while (queue.Count >= MaxCachedGcMessagesPerKey)
        {
            queue.Dequeue();
        }

        queue.Enqueue(message);
    }

    private void ProcessMulti(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        uint unzippedSize = 0;
        byte[]? payload = null;

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    unzippedSize = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    payload = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (payload is null)
        {
            return;
        }

        if (unzippedSize > 0)
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)unzippedSize);
            gzip.CopyTo(output);
            payload = output.ToArray();
        }

        var offset = 0;
        while (offset + 4 <= payload.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;

            if (size < 0 || offset + size > payload.Length)
            {
                break;
            }

            HandleNetMessage(payload[offset..(offset + size)]);
            offset += size;
        }
    }

    private async Task DisconnectSocketAsync()
    {
        try
        {
            if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
        }
        catch
        {
            _socket?.Abort();
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
        }
    }

    private ulong NextJobId()
    {
        var jobId = Interlocked.Increment(ref _currentJobId);
        return jobId == JobIdNone ? Interlocked.Increment(ref _currentJobId) : jobId;
    }

    private static InvalidOperationException CreateLoggedOffException(byte[] body)
    {
        var reason = TryDecodeLoggedOffEresult(body);

        // Data["CmConflict"] 是语言中立标记，供 CsLoadoutService 判定会话冲突——
        // 不能用本地化后的 Message 文本做匹配（多语言下会失配）。
        if (reason is (int)SteamEresult.LoggedInElsewhere
            or (int)SteamEresult.LogonSessionReplaced
            or (int)SteamEresult.AlreadyLoggedInElsewhere)
        {
            var replaced = new InvalidOperationException(Loc.T("Cm_Error_SessionReplaced"));
            replaced.Data["CmConflict"] = "SessionReplaced";
            return replaced;
        }

        var loggedOff = new InvalidOperationException(
            Loc.T("Cm_Error_AccountLoggedOff") +
            (reason.HasValue ? Loc.Tf("Cm_Error_EresultSuffix_Format", reason.Value) : string.Empty));
        loggedOff.Data["CmConflict"] = "LoggedOff";
        return loggedOff;
    }

    private static int? TryDecodeLoggedOffEresult(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return (int)reader.ReadVarint(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private void FailPendingRequests(Exception ex)
    {
        _logonResponse?.TrySetException(ex);

        foreach (var (jobId, job) in _jobs)
        {
            if (_jobs.TryRemove(jobId, out _))
            {
                job.TrySetException(ex);
            }
        }

        List<TaskCompletionSource<SteamGcClientMessage>> gcWaiters;
        lock (_gcMessagesLock)
        {
            gcWaiters = _gcWaiters.Values.ToList();
            _gcWaiters.Clear();
            _gcMessageCache.Clear();
        }

        foreach (var waiter in gcWaiters)
        {
            waiter.TrySetException(ex);
        }
    }

    private static byte[] EncodeClientLogon(string refreshToken, ulong steamId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, ProtocolVersion);
            writer.WriteString(6, "english");
            writer.WriteUInt32(7, ClientOsWindows10);
            writer.WriteBool(8, true);
            writer.WriteBytes(11, SteamProtoWriter.Build(ip => ip.WriteFixed32(1, LogonId)));
            writer.WriteBytes(30, CreateMachineId(steamId.ToString(CultureInfo.InvariantCulture)));
            writer.WriteUInt32(33, 2);
            writer.WriteString(96, "unsub-all");
            writer.WriteBool(102, true);
            writer.WriteString(108, refreshToken);
        });
    }

    private static byte[] CreateMachineId(string accountIdentifier)
    {
        using var stream = new MemoryStream(155);
        WriteByte(stream, 0);
        WriteCString(stream, "MessageObject");
        WriteByte(stream, 1);
        WriteCString(stream, "BB3");
        WriteCString(stream, Sha1Hex($"SteamUser Hash BB3 {accountIdentifier}"));
        WriteByte(stream, 1);
        WriteCString(stream, "FF2");
        WriteCString(stream, Sha1Hex($"SteamUser Hash FF2 {accountIdentifier}"));
        WriteByte(stream, 1);
        WriteCString(stream, "3B3");
        WriteCString(stream, Sha1Hex($"SteamUser Hash 3B3 {accountIdentifier}"));
        WriteByte(stream, 8);
        WriteByte(stream, 8);
        return stream.ToArray();
    }

    private byte[] BuildGcProtobufPayload(uint appId, uint msgType, byte[] payload)
    {
        var jobId = _currentJobId++;
        var header = SteamProtoWriter.Build(writer =>
        {
            writer.WriteFixed64(1, _steamId);
            if (_sessionId != 0)
            {
                writer.WriteInt32(2, _sessionId);
            }

            writer.WriteUInt32(3, appId);
            writer.WriteFixed64(10, jobId);
        });

        var packet = new byte[8 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), msgType | ProtoMask);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), header.Length);
        header.CopyTo(packet.AsSpan(8));
        payload.CopyTo(packet.AsSpan(8 + header.Length));
        return packet;
    }

    private static byte[] ExtractGcMessageBody(uint rawMsgType, byte[] payload, out int? gcClientSessionId)
    {
        gcClientSessionId = null;
        var isProto = (rawMsgType & ProtoMask) != 0;
        if (isProto)
        {
            if (payload.Length < 8)
            {
                return Array.Empty<byte>();
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
            if (headerLength < 0 || payload.Length < 8 + headerLength)
            {
                return Array.Empty<byte>();
            }

            gcClientSessionId = TryReadGcClientSessionId(payload.AsSpan(8, headerLength));
            return payload[(8 + headerLength)..];
        }

        const int gcBinaryHeaderLength = 18;
        return payload.Length > gcBinaryHeaderLength
            ? payload[gcBinaryHeaderLength..]
            : Array.Empty<byte>();
    }

    private static int? TryReadGcClientSessionId(ReadOnlySpan<byte> headerBytes)
    {
        var reader = new SteamProtoReader(headerBytes.ToArray());
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 2)
            {
                var sessionId = (int)reader.ReadVarint(wireType);
                return sessionId == 0 ? null : sessionId;
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private static string GetGcWaiterKey(uint appId, uint msgType)
    {
        return FormattableString.Invariant($"{appId}:{msgType}");
    }

    private static ProtoHeader DecodeProtoHeader(byte[] body)
    {
        var header = new ProtoHeader();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    header.SteamId = reader.ReadFixed64(wireType);
                    break;

                case 2:
                    header.ClientSessionId = (int)reader.ReadVarint(wireType);
                    break;

                case 10:
                    header.JobIdSource = reader.ReadFixed64(wireType);
                    break;

                case 11:
                    header.JobIdTarget = reader.ReadFixed64(wireType);
                    break;

                case 12:
                    header.TargetJobName = reader.ReadString(wireType);
                    break;

                case 13:
                    header.Result = (int)reader.ReadVarint(wireType);
                    break;

                case 14:
                    header.ErrorMessage = reader.ReadString(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return header;
    }

    private static LogonResponse DecodeLogonResponse(byte[] body)
    {
        var response = new LogonResponse(2, 0);
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    response = response with { Result = (int)reader.ReadVarint(wireType) };
                    break;

                case 3:
                    response = response with { HeartbeatSeconds = (int)reader.ReadVarint(wireType) };
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return response;
    }

    private static string? DecodeAccessTokenForAppResponse(byte[] body)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return reader.ReadString(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private static bool DecodeAreFilesInSubscriptionListResponse(byte[] body, ulong publishedFileId)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 1)
            {
                reader.Skip(wireType);
                continue;
            }

            var nested = new SteamProtoReader(reader.ReadLengthDelimited(wireType));
            ulong? id = null;
            var inList = false;

            while (nested.TryReadTag(out var nestedField, out var nestedWireType))
            {
                switch (nestedField)
                {
                    case 1:
                        id = nested.ReadFixed64(nestedWireType);
                        break;

                    case 2:
                        inList = nested.ReadBool(nestedWireType);
                        break;

                    default:
                        nested.Skip(nestedWireType);
                        break;
                }
            }

            if (id == publishedFileId)
            {
                return inList;
            }
        }

        return false;
    }

    private static void EnsureOk(ServiceMethodResponse response, string message)
    {
        if (response.Result != (int)SteamEresult.Ok)
        {
            // 用 SteamCmException 抛：保留 EResult，调用方（如一键查询）才能按「令牌被 Steam 拒绝」给出正确提示；
            // 它继承自 InvalidOperationException，原有 catch(InvalidOperationException) 行为不变。
            throw new SteamCmException(
                response.Result,
                Loc.Tf("Cm_Error_EnsureOk_Format", message, response.Result, response.ErrorMessage));
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.TryGetDouble(out var value)
            ? value
            : double.MaxValue;
    }

    private static string Sha1Hex(string value)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void WriteCString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteByte(Stream stream, byte value)
    {
        stream.WriteByte(value);
    }

    private sealed record ProtoHeader
    {
        public ulong? SteamId { get; set; }
        public int? ClientSessionId { get; set; }
        public ulong? JobIdSource { get; set; }
        public ulong? JobIdTarget { get; set; }
        public string? TargetJobName { get; set; }
        public int? Result { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed record ServiceMethodResponse(
        int Result,
        string? ErrorMessage,
        byte[] Body);

    private sealed record LogonResponse(int Result, int HeartbeatSeconds);

    public sealed record SteamGcClientMessage(
        uint AppId,
        uint MessageType,
        byte[] Payload);
}
