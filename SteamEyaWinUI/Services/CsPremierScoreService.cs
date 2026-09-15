using System.Diagnostics;
using System.Globalization;
using System.Net;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class CsPremierScoreService
{
    private const uint ClientRequestPlayersProfile = 9127;
    private const uint PlayersProfile = 9128;
    private const uint MatchmakingClient2GCHello = 9109;
    private const uint MatchmakingGC2ClientHello = 9110;
    private const uint PremierRankTypeId = 11;
    // 9110 可能在 GC welcome 前后主动下发；等待必须覆盖握手窗口，接收层也会缓存早到消息。
    // 仍保留“断开 GC 再重连”循环触发（与 cooldown.js 一致：6 轮、每轮等 11 秒、
    // 断开后 2.5 秒再重连），另设总时限兜底防止单轮 GC welcome 重试拖长整体耗时。
    private const int MaxHelloCycles = 6;

    // 单轮等 9110 的上限。实测：9110 一旦会来，**在 GC 连上后 300ms 内就到**；不来则整轮都不会来
    // （补发 9109 也没用，只有「断开重连」那条路有效）。连续多次实测首轮都没等到，
    // 所以单轮只等 1 秒就转重连，省掉的都是纯等待。
    private static readonly TimeSpan HelloWaitTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CachedHelloPollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GcReconnectDelay = TimeSpan.FromSeconds(0.4);

    // 国服判定只是附加信息，给它独立的短上限：授权页慢/被墙时按「未知」返回，
    // 不能让它把整次查询拖住（「清空无效账号」批量跑时每个账号都要走这一趟）。
    private static readonly TimeSpan Cs2IsChinaTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HelloTotalBudget = TimeSpan.FromSeconds(100);

    // 用 SteamProxyBypass：我们自己开着 VPN 时 Steam 请求直连（走节点会把登录往返拖到十几秒）。
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new SteamProxyBypass()
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly HttpClient LicensesHttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false,
        UseProxy = true,
        Proxy = new SteamProxyBypass()
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// 预热 Steam 侧连接：CM 服务器列表 + 各相关域名（api / store）的 DNS 与 TLS。
    /// 供软件启动后后台调用；失败只写日志，绝不影响正常查询。
    /// </summary>
    public static async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await SteamCmClient.PrewarmAsync(HttpClient, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://store.steampowered.com/");
            using var response = await LicensesHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            AppLog.Info($"[query] 预热完成：CM 列表与 Steam 侧连接已就绪（{watch.ElapsedMilliseconds} ms）。");
        }
        catch (Exception ex)
        {
            AppLog.Info($"[query] 预热未完成（不影响后续查询）：{ex.Message}");
        }
    }

    public async Task<CsPremierScoreResult> QueryAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_BadSteam64"));
        }

        var accountId = CsGcSession.GetAccountId(steamId64);
        var queryWatch = Stopwatch.StartNew();
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        AppLog.Info($"[query] 总耗时里程碑：CM 连接+登录完成 {queryWatch.ElapsedMilliseconds} ms");

        try
        {
            var helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);

            var webSession = await SteamWebSession.BuildAsync(cmClient, refreshToken, steamId, cancellationToken);
            var cs2IsChinaTask = CheckCs2IsChinaAsync(webSession, cancellationToken);
            AppLog.Info($"[query] 总耗时里程碑：Web 会话完成 {queryWatch.ElapsedMilliseconds} ms");

            // 先明确「退出 730」再「进入 730」：GC 只有看到完整的状态变化才会跑完 matchmaking 初始化
            // 并下发 9110。只发一次「进入」时，若 Steam 端认为该账号上一会话还挂着 730，
            // 就不会重跑初始化 —— 日志里就是首轮 10 秒都等不到 9110，反而要等重连循环
            // （那轮正是先退出再进入）才拿到，白白多花十几秒。
            await cmClient.SetGamesPlayedAsync([], cancellationToken);
            await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
            await CsGcSession.ConnectAsync(cmClient, cancellationToken);
            AppLog.Info($"[query] 总耗时里程碑：进入 CS2 GC {queryWatch.ElapsedMilliseconds} ms");

            // 冷却/VAC 只能从 GC 的 MatchmakingGC2ClientHello(9110) 拿：PlayersProfile 对自己
            // 账号的 penalty 字段永远为空。9110 waiter 已在进 730 前挂好，避免 welcome 阶段
            // 主动下发的 9110 被错过；这里再发 9109 请求，随后照常请求 PlayersProfile 取优先分/等级。
            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingClient2GCHello,
                [],
                cancellationToken);

            var profileTask = cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                PlayersProfile,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                ClientRequestPlayersProfile,
                EncodePlayersProfileRequest(accountId),
                cancellationToken);

            var profileMessage = await profileTask;
            var profile = DecodePlayersProfile(accountId, profileMessage.Payload);
            AppLog.Info($"[query] 总耗时里程碑：拿到 PlayersProfile {queryWatch.ElapsedMilliseconds} ms");

            var helloData = await helloTask;
            if (helloData is null)
            {
                // 首轮没等到：实测（日志）补发 9109 完全无效 —— 能拿到 9110 的只有
                // 「SetGamesPlayed([]) → [730] → 重连 GC → 发 9109」这条完整重来一遍的路径。
                // 所以这里不再补发、也不干等，直接进下面的重连循环。
                AppLog.Info($"[query] 首轮没等到 9110（{queryWatch.ElapsedMilliseconds} ms），转入重连重试");
            }

            helloData ??= await WaitForMatchmakingHelloAsync(
                cmClient,
                CachedHelloPollTimeout,
                cancellationToken);
            AppLog.Info($"[query] 总耗时里程碑：拿到 9110（{(helloData is null ? "没拿到，将重连重试" : "成功")}）{queryWatch.ElapsedMilliseconds} ms");
            var helloDeadline = DateTimeOffset.UtcNow + HelloTotalBudget;

            for (var cycle = 2;
                helloData is null && cycle <= MaxHelloCycles && DateTimeOffset.UtcNow < helloDeadline;
                cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.SetGamesPlayedAsync([], cancellationToken);
                    await Task.Delay(GcReconnectDelay, cancellationToken);
                    helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);
                    await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                    await CsGcSession.ConnectAsync(cmClient, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // GC 重连失败：优先分已拿到，冷却按未知返回。
                    break;
                }

                await cmClient.SendGcProtobufMessageAsync(
                    CsGcSession.Cs2AppId,
                    MatchmakingClient2GCHello,
                    [],
                    cancellationToken);
                helloData = await helloTask;
                helloData ??= await WaitForMatchmakingHelloAsync(
                    cmClient,
                    CachedHelloPollTimeout,
                    cancellationToken);
            }

            var premier = profile.Rankings.FirstOrDefault(ranking =>
                ranking.RankTypeId == PremierRankTypeId);

            var cs2IsChina = await cs2IsChinaTask;
            AppLog.Info($"[query] 总耗时 {queryWatch.ElapsedMilliseconds} ms（国服判定：{cs2IsChina}）");

            return new CsPremierScoreResult(
                steamId,
                accountId,
                premier,
                profile.Rankings,
                helloData?.PenaltySeconds,
                helloData?.PenaltyReason,
                helloData?.VacBanned,
                profile.PlayerLevel,
                profile.InMatch,
                cs2IsChina);
        }
        finally
        {
            try
            {
                await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
            }
            catch
            {
                // Best-effort cleanup before logoff.
            }
        }
    }

    /// <summary>等待一轮 9110；超时返回 null（由调用方断开 GC 重连再试）。9110 与 PlayersProfile
    /// 的账号条目是同一个 proto 消息类型，直接复用 DecodeAccountProfile。</summary>
    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return await WaitForMatchmakingHelloAsync(cmClient, HelloWaitTimeout, cancellationToken);
    }

    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingGC2ClientHello,
                timeout,
                cancellationToken,
                cacheUnmatched: true);
            return DecodeAccountProfile(message.Payload);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判定该账号的 CS:GO 授权是否含国服（Steam China PW Grant）：查询账号授权页
    /// （store.steampowered.com/account/licenses）并检查“Steam China PW Grant”授权条目。
    /// 返回 null 表示未能判定。
    /// </summary>
    private static async Task<bool?> CheckCs2IsChinaAsync(
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Cs2IsChinaTimeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://store.steampowered.com/account/licenses/?l=schinese");
            request.Headers.Add("Cookie", session.CookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            using var response = await LicensesHttpClient.SendAsync(request, timeoutCts.Token);

            // 3xx：会话不被接受时会被导向登录页等；不跟随，按未知处理。
            if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(html) ||
                html.Contains("<TITLE>Access Denied</TITLE>", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return html.Contains("Steam China PW Grant", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消：照旧往上抛。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 只是国服判定自己超时：按「未知」处理，不影响本次查询结果。
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodePlayersProfileRequest(uint accountId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(3, accountId);
            writer.WriteUInt32(4, 32);
        });
    }

    private static CsAccountProfile DecodePlayersProfile(uint requestedAccountId, byte[] body)
    {
        var profiles = new List<CsAccountProfile>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    profiles.Add(DecodeAccountProfile(reader.ReadLengthDelimited(wireType)));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        var profile = profiles.FirstOrDefault(value => value.AccountId == requestedAccountId)
            ?? profiles.FirstOrDefault();

        if (profile is null)
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_NoProfile"));
        }

        return profile;
    }

    private static CsAccountProfile DecodeAccountProfile(byte[] body)
    {
        uint accountId = 0;
        uint penaltySeconds = 0;
        uint penaltyReason = 0;
        var vacBanned = 0;
        int? playerLevel = null;
        var inMatch = false;
        var rankings = new List<CsRankingInfo>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    inMatch = true;
                    reader.Skip(wireType);
                    break;

                case 4:
                {
                    // penalty_seconds 实为有符号：冷却已过期时 GC 下发“负的剩余秒数”，protobuf 把它符号扩展成
                    // 64 位变长整型。直接当 uint 读会截成 ~2^32 的天文数字（曾把刚解封的账号显示成“49707天…”）。
                    // 按 int32 解读，≤0（已过期 / 无冷却）一律归零。
                    var penalty = unchecked((int)reader.ReadVarint(wireType));
                    penaltySeconds = penalty > 0 ? (uint)penalty : 0;
                    break;
                }

                case 5:
                    penaltyReason = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    vacBanned = (int)reader.ReadVarint(wireType);
                    break;

                case 7:
                case 20:
                    rankings.Add(DecodeRanking(reader.ReadLengthDelimited(wireType)));
                    break;

                case 17:
                    playerLevel = (int)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsAccountProfile(
            accountId,
            rankings,
            penaltySeconds,
            penaltyReason,
            vacBanned,
            playerLevel,
            inMatch);
    }

    private static CsRankingInfo DecodeRanking(byte[] body)
    {
        uint rankTypeId = 0;
        uint rankId = 0;
        uint wins = 0;
        uint? mapId = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    rankId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    wins = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    rankTypeId = (uint)reader.ReadVarint(wireType);
                    break;

                case 13:
                    mapId ??= DecodePerMapRankMapId(reader.ReadLengthDelimited(wireType));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsRankingInfo(rankTypeId, rankId, wins, mapId);
    }

    private static uint? DecodePerMapRankMapId(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return (uint)reader.ReadVarint(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private sealed record CsAccountProfile(
        uint AccountId,
        IReadOnlyList<CsRankingInfo> Rankings,
        uint PenaltySeconds,
        uint PenaltyReason,
        int VacBanned,
        int? PlayerLevel,
        bool InMatch);
}
