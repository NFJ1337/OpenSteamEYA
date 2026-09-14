using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 奶味平台上号器的新版卡密接口客户端（服务端 111.170.18.37:9095）：
///   · GET /api/v1/health              —— 可用性与公告（纯 JSON）
///   · GET /api/v1/redeem?key=&lt;卡密&gt; —— 取名（Server-Sent Events 流）
///
/// redeem 响应是 SSE：先若干条 <c>event: progress</c>（percent/stage/message），
/// 最后一条 <c>event: result</c>：成功为 {"ok":true,"code":"OK","eya":"SteamID64----JWT"}，
/// 失败为 {"ok":false,"code":"CARD_INVALID"}（HTTP 状态码仍为 200，失败要从事件里判断）。
/// eya 的 `SteamID64----Token` 与本程序旧版卡密格式同构，可直接喂给现有登录链路。
/// </summary>
internal sealed class NaiweiRedeemClient
{
    internal const string BaseUrl = "http://111.170.18.37:9095";

    internal const string SuccessCode = "OK";

    private static readonly HttpClient Client = CreateHttpClient();

    /// <summary>取名成功：已拆出 SteamID64 与 EYA 令牌。</summary>
    internal sealed record RedeemAccount(string SteamId, string Token, bool FromCache);

    /// <summary>
    /// 用卡密取名。<paramref name="progress"/> 会收到服务端推送的阶段消息（percent/stage/message）。
    /// 失败时抛 <see cref="NaiweiRedeemException"/>（带服务端 code），网络/协议问题抛其它异常。
    /// </summary>
    public async Task<RedeemAccount> RedeemAsync(
        string licenseKey,
        IProgress<NaiweiRedeemProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var key = licenseKey.Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/api/v1/redeem?key={Uri.EscapeDataString(key)}");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Loc.Tf("Login_Verify_Error_Http_Format", (int)response.StatusCode));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var eventName = string.Empty;
        var dataLines = new List<string>();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            // SSE：空行结束一个事件；"event:"/"data:"/"retry:" 为字段，其余行忽略。
            if (line.Length == 0)
            {
                if (TryHandleEvent(eventName, dataLines, progress, out var account))
                {
                    return account;
                }

                eventName = string.Empty;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line[5..].Trim());
            }
        }

        // 流结束前没有 result 事件：按协议异常处理（服务端可能中途断开）。
        throw new InvalidOperationException(Loc.T("Login_Redeem_Error_NoResult"));
    }

    /// <summary>健康检查/公告；失败返回 null（仅用于可用性提示，不阻断取名）。</summary>
    public async Task<NaiweiHealthDto?> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync($"{BaseUrl}/api/v1/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize(body, NaiweiRedeemJsonContext.Default.NaiweiHealthDto);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>处理一个完整 SSE 事件；返回 true 表示已经拿到 result（成功）。</summary>
    private static bool TryHandleEvent(
        string eventName,
        List<string> dataLines,
        IProgress<NaiweiRedeemProgress>? progress,
        out RedeemAccount account)
    {
        account = null!;
        if (dataLines.Count == 0)
        {
            return false;
        }

        var payload = string.Join("\n", dataLines);
        switch (eventName)
        {
            case "progress":
                var stage = JsonSerializer.Deserialize(payload, NaiweiRedeemJsonContext.Default.NaiweiProgressDto);
                if (stage is not null)
                {
                    progress?.Report(new NaiweiRedeemProgress(stage.Percent ?? 0, stage.Stage ?? string.Empty, stage.Message ?? string.Empty));
                }

                return false;

            case "result":
                var result = JsonSerializer.Deserialize(payload, NaiweiRedeemJsonContext.Default.NaiweiResultDto)
                    ?? throw new InvalidOperationException(Loc.T("Login_Redeem_Error_InvalidResponse"));

                if (result.Ok != true || string.IsNullOrWhiteSpace(result.Eya))
                {
                    throw new NaiweiRedeemException(result.Code, DescribeCode(result.Code));
                }

                account = ParseEya(result.Eya);
                return true;

            default:
                return false;
        }
    }

    /// <summary>拆 `SteamID64----Token`；SteamID 缺失时从令牌的 sub 字段兜底。</summary>
    private static RedeemAccount ParseEya(string eya)
    {
        var separator = eya.IndexOf("----", StringComparison.Ordinal);
        var steamId = separator >= 0 ? eya[..separator].Trim() : string.Empty;
        var token = separator >= 0 ? eya[(separator + 4)..].Trim() : eya.Trim();
        if (token.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LegacyEyaTokenMissing"));
        }

        if (steamId.Length == 0)
        {
            steamId = AppState.JwtTokenService.Inspect(token).SteamId ?? string.Empty;
        }

        return new RedeemAccount(steamId, token, FromCache: false);
    }

    /// <summary>服务端 code → 用户可读文案；未知 code 原样带出，便于排查。</summary>
    private static string DescribeCode(string? code) => code switch
    {
        "CARD_INVALID" => Loc.T("Login_Redeem_Error_CardInvalid"),
        "NO_SIGN" => Loc.T("Login_Redeem_Error_NoSign"),
        "UPSTREAM_ERROR" => Loc.T("Login_Redeem_Error_Upstream"),
        null or "" => Loc.T("Login_Redeem_Error_Unknown"),
        _ => Loc.Tf("Login_Redeem_Error_Code_Format", code)
    };

    private static HttpClient CreateHttpClient()
    {
        // 冷启动取名要在上游分配/释放账号，给足超时；流式读取由 ReadLineAsync 逐事件推进。
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Redeem");
        return client;
    }
}

/// <summary>取名进度（对应 redeem 的 progress 事件）。</summary>
internal sealed record NaiweiRedeemProgress(int Percent, string Stage, string Message);

/// <summary>服务端返回的业务失败（带 code，调用方可据此决定是否回退到旧接口）。</summary>
internal sealed class NaiweiRedeemException : Exception
{
    public NaiweiRedeemException(string? code, string message)
        : base(message)
    {
        Code = code;
    }

    public string? Code { get; }
}

// ---- 上游 DTO（SSE data 行内的 JSON，camelCase）----

internal sealed record NaiweiProgressDto(int? Percent, string? Stage, string? Message);

internal sealed record NaiweiResultDto(bool? Ok, string? Code, string? Eya);

internal sealed record NaiweiHealthDto(bool? Ok, string? Version, string? Announcement);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NaiweiProgressDto))]
[JsonSerializable(typeof(NaiweiResultDto))]
[JsonSerializable(typeof(NaiweiHealthDto))]
internal sealed partial class NaiweiRedeemJsonContext : JsonSerializerContext;