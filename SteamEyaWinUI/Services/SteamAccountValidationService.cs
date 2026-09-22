using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed record SteamAccountValidationResult(
    string SteamId,
    uint? CooldownSeconds,
    bool? VacBanned,
    string SummaryText,
    string? RefreshToken = null);

/// <summary>
/// 与本地 SteamAccountManager 的账号管理验证保持一致：
/// 优先复用已保存令牌；没有可用令牌时使用账号名 + 密码登录 Steam，
/// 再访问 help.steampowered.com 的冷却/VAC 页面。不复用 EYA Token 的 GC 查询逻辑。
/// </summary>
internal sealed class SteamAccountValidationService
{
    private const string CooldownUrl =
        "https://help.steampowered.com/zh-cn/wizard/HelpWithGameIssue/?appid=730&issueid=131";
    private const string VacUrl =
        "https://help.steampowered.com/zh-cn/wizard/VacBans";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
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

    // 批内复用同一条 CM 连接：每账号一次 WebSocket 握手实测 0.8~2.2 秒，换账号只重新登录即可。
    // 空闲超过 IdleReleaseAfter，或调用方在批次结束时显式释放（ReleaseReusedCmAsync）。
    private static readonly TimeSpan IdleReleaseAfter = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _cmGate = new(1, 1);
    private SteamCmClient? _reusedCmClient;
    private DateTimeOffset _reusedCmUsedAt;

    public Task<SteamAccountValidationResult> QueryAsync(
        string accountName,
        string password,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        QueryAsync(
            accountName: accountName,
            password: password,
            cachedRefreshToken: null,
            guardCodeProvider: guardCodeProvider,
            progress: progress,
            cancellationToken: cancellationToken);

    /// <summary>
    /// 优先复用账号库里已有的 refresh token；令牌不可用时才回退到账号密码登录。
    /// 成功后返回本次实际使用的令牌，供调用方保存，避免每次查 VAC 都重复登录触发 Steam 限流。
    /// </summary>
    public async Task<SteamAccountValidationResult> QueryAsync(
        string accountName,
        string password,
        string? cachedRefreshToken,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException(Loc.T("Creds_Error_AccountRequired"));
        }

        if (TryGetUsableCachedToken(cachedRefreshToken, out var cachedToken, out var cachedSteamId))
        {
            progress?.Report(Loc.T("Login_Status_QueryingAccount"));
            try
            {
                return await QueryWithTokenAsync(cachedToken, cachedSteamId, cancellationToken);
            }
            catch (SteamCmException ex) when (ex.IsTokenFailure)
            {
                AppLog.Warn($"账号管理验证：缓存令牌不可用，回退账号密码登录（{accountName.Trim()}）：{ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(Loc.T("Creds_Error_PasswordRequired"));
        }

        progress?.Report(Loc.T("Creds_Progress_Authenticating"));
        var auth = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
            accountName.Trim(),
            password,
            guardCodeProvider,
            progress,
            cancellationToken);

        progress?.Report(Loc.T("Login_Status_QueryingAccount"));
        var refreshToken = FormatHelper.NormalizeToken(auth.RefreshToken);
        return await QueryWithTokenAsync(refreshToken, auth.SteamId, cancellationToken);
    }

    private static bool TryGetUsableCachedToken(
        string? cachedRefreshToken,
        out string refreshToken,
        out string steamId)
    {
        refreshToken = string.Empty;
        steamId = string.Empty;
        if (string.IsNullOrWhiteSpace(cachedRefreshToken))
        {
            return false;
        }

        refreshToken = FormatHelper.NormalizeToken(cachedRefreshToken.Trim());
        var tokenInfo = new JwtTokenService().Inspect(refreshToken);
        if (!tokenInfo.IsValid || string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return false;
        }

        steamId = tokenInfo.SteamId;
        return true;
    }

    private async Task<SteamAccountValidationResult> QueryWithTokenAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        // 整段查询独占这条 CM 连接：同一个 socket 上并发换账号会把别的账号的请求搅乱。
        // 查完不关连接，留给下一个账号复用；批次结束时由 ReleaseReusedCmAsync 统一释放。
        await _cmGate.WaitAsync(cancellationToken);
        try
        {
            var cmClient = await AcquireCmClientAsync(refreshToken, steamId, cancellationToken);
            var session = await SteamWebSession.BuildAsync(
                cmClient,
                refreshToken,
                steamId,
                cancellationToken);

            var cooldownHtml = await GetHtmlAsync(CooldownUrl, session, cancellationToken);
            var cooldownText = ExtractCooldownExpiration(cooldownHtml);
            uint? activeCooldownSeconds = null;
            if (!string.IsNullOrWhiteSpace(cooldownText))
            {
                var endsAt = ParseCooldownEnd(cooldownHtml, cooldownText);
                if (endsAt.HasValue)
                {
                    var remainingSeconds = (endsAt.Value - DateTimeOffset.Now).TotalSeconds;
                    if (remainingSeconds > 0)
                    {
                        activeCooldownSeconds = (uint)Math.Ceiling(remainingSeconds);
                    }
                }
            }

            // VAC 冷却就只记录为 CS2 冷却状态，绝不能写成永久 VAC 封禁。
            if (activeCooldownSeconds is > 0)
            {
                return new SteamAccountValidationResult(
                    steamId,
                    activeCooldownSeconds,
                    false,
                    Loc.Tf("Account_Cooldown_Summary_Format", cooldownText),
                    refreshToken);
            }

            var vacHtml = await GetHtmlAsync(VacUrl, session, cancellationToken);
            var vacBanned = vacHtml.Contains("Counter-Strike 2", StringComparison.OrdinalIgnoreCase);
            return new SteamAccountValidationResult(
                steamId,
                0,
                vacBanned,
                vacBanned ? Loc.T("WhiteAccounts_Filter_Vac") : Loc.T("Cs_Premier_NoRestrictions"),
                refreshToken);
        }
        finally
        {
            _reusedCmUsedAt = DateTimeOffset.Now;
            _cmGate.Release();
        }
    }

    /// <summary>
    /// 取一条已登录的 CM 连接：优先复用上一条（只换账号登录），复用不了才重新握手。
    /// 调用方必须已持有 <c>_cmGate</c>，并在整段查询结束后交还。
    /// </summary>
    private async Task<SteamCmClient> AcquireCmClientAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        if (_reusedCmClient is not null && DateTimeOffset.Now - _reusedCmUsedAt > IdleReleaseAfter)
        {
            await ReleaseReusedCmAsyncCore();
        }

        if (_reusedCmClient is not null && await _reusedCmClient.TryRelogOnAsync(refreshToken, steamId, cancellationToken))
        {
            return _reusedCmClient;
        }

        if (_reusedCmClient is not null)
        {
            // 复用失败（连接已断/服务端拒绝）：丢掉旧的，重新握手。
            await ReleaseReusedCmAsyncCore();
        }

        var created = new SteamCmClient(HttpClient);
        await created.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        _reusedCmClient = created;
        _reusedCmUsedAt = DateTimeOffset.Now;
        return created;
    }

    /// <summary>
    /// 放掉复用的 CM 连接（批次结束/取消时调用）。不调用也会在空闲超时后自行释放。
    /// 等 30 秒拿不到独占权就放弃：说明还有查询占着它，让它自己闲着释放即可。
    /// </summary>
    public async ValueTask ReleaseReusedCmAsync()
    {
        if (!await _cmGate.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            AppLog.Warn("释放复用 CM 连接超时：仍有查询占用，等它空闲后自行释放。");
            return;
        }

        try
        {
            await ReleaseReusedCmAsyncCore();
        }
        finally
        {
            _cmGate.Release();
        }
    }

    /// <summary>真正关掉复用连接。必须在持有 <c>_cmGate</c> 时调用。</summary>
    private async ValueTask ReleaseReusedCmAsyncCore()
    {
        var client = _reusedCmClient;
        _reusedCmClient = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    private static string? ExtractCooldownExpiration(string html)
    {
        const string marker = "help_game_cooldown_expirationtime\">";
        var start = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = html.IndexOf("</span>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : WebUtility.HtmlDecode(html[start..end]).Trim();
    }

    private static DateTimeOffset? ParseCooldownEnd(string html, string cooldownText)
    {
        var serverMatch = Regex.Match(html, @"g_ServerTime\s*=\s*(\d+)");
        if (!serverMatch.Success ||
            !long.TryParse(serverMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return null;
        }

        var serverUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        TimeZoneInfo pacific;
        try
        {
            pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch
        {
            return null;
        }

        var serverPacific = TimeZoneInfo.ConvertTime(serverUtc, pacific);
        var match = Regex.Match(
            cooldownText,
            @"(\d+)\s*月\s*(\d+)\s*日\s*(上午|下午)\s*(\d+):(\d+)");
        if (!match.Success)
        {
            return null;
        }

        var month = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var period = match.Groups[3].Value;
        var hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        if (period == "下午" && hour != 12)
        {
            hour += 12;
        }
        else if (period == "上午" && hour == 12)
        {
            hour = 0;
        }

        // Steam 的冷却日期不带年份。选择距离服务器时间最近的一年：
        // 已结束的冷却保持为过去时间，而不是被强行滚到下一年形成新的假倒计时。
        DateTimeOffset? best = null;
        var bestDistance = TimeSpan.MaxValue;
        for (var year = serverPacific.Year - 1; year <= serverPacific.Year + 1; year++)
        {
            try
            {
                var unspecified = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
                var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, pacific);
                var candidate = new DateTimeOffset(utc, TimeSpan.Zero);
                var distance = (candidate - serverUtc).Duration();
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            catch (ArgumentException)
            {
                // 无效的太平洋夏令时时间，尝试相邻年份即可。
            }
        }

        return best?.ToLocalTime();
    }

    private static async Task<string> GetHtmlAsync(
        string url,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 SteamEYA");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 &&
                response.Headers.Location is not null)
            {
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.AbsoluteUri
                    : new Uri(new Uri(current), response.Headers.Location).AbsoluteUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new InvalidOperationException("Steam 验证页面重定向次数过多。");
    }
}
