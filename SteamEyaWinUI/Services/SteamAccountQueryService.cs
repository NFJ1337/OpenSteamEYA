using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>账号查询页的一次查询选项；空值表示不修改对应资料。</summary>
internal sealed record SteamAccountQueryOptions(
    bool QueryRank,
    bool QueryUserInfo,
    bool QueryBanTime,
    string? ChangeNameTo,
    string? ChangeAvatarFrom,
    string? ChangeSummaryTo);

/// <summary>账号查询结果。Checked 标志决定是否允许用空值覆盖本地旧结果。</summary>
internal sealed class SteamAccountQueryResult
{
    public string AccountName { get; set; } = string.Empty;
    public string SteamId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public bool LoginSucceeded { get; set; }
    public bool IsSuccess { get; set; }
    public string StatusCode { get; set; } = "failed";
    public string Message { get; set; } = string.Empty;

    public bool RankChecked { get; set; }
    public int? CsProfileRank { get; set; }
    public string? LastGameLoginText { get; set; }

    public bool UserInfoChecked { get; set; }
    public string? PersonaName { get; set; }
    public string? Region { get; set; }

    public bool ChinaSteamChecked { get; set; }
    public bool? HasChinaSteam { get; set; }

    public bool BanChecked { get; set; }
    public bool? VacBanned { get; set; }
    public DateTimeOffset? BanExpiresAt { get; set; }
    public uint? CooldownSeconds { get; set; }

    public bool NameApplied { get; set; }
    public bool AvatarApplied { get; set; }
    public string? AppliedPersonaName { get; set; }
}

/// <summary>
/// 「账号查询」工作流：复用本项目的 refresh token / CM / Web 会话能力，批量读取 Steam 资料，
/// 可选修改昵称、头像和简介，并把结果写回白号账号库。
/// </summary>
internal sealed partial class SteamAccountQueryService
{
    private const string RankPageFormat = "https://steamcommunity.com/profiles/{0}/gcpd/730/";
    private const string UserInfoPageFormat = "https://steamcommunity.com/profiles/{0}/friends/add";
    private const string ChinaSteamPage = "https://store.steampowered.com/account/";
    private const string CooldownPage =
        "https://help.steampowered.com/zh-cn/wizard/HelpWithGameIssue/?appid=730&issueid=131";
    private const string VacPage =
        "https://help.steampowered.com/zh-cn/wizard/VacBans";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false,
        UseProxy = true
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<SteamAccountQueryResult> QueryAsync(
        SteamAccountHistoryItem account,
        SteamAccountQueryOptions options,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SteamAccountQueryResult
        {
            AccountName = account.AccountName
        };

        try
        {
            progress?.Report(Loc.T("Legacy_Query_StatusLoggingIn"));
            var auth = await ResolveSessionAsync(
                account,
                guardCodeProvider,
                progress,
                cancellationToken);

            result.LoginSucceeded = true;
            result.SteamId = auth.SteamId;
            result.RefreshToken = auth.RefreshToken;
            result.TokenExpiresAt = auth.TokenExpiresAt;

            var failures = new List<string>();

            if (options.QueryRank)
            {
                progress?.Report(Loc.T("Legacy_Query_StatusRank"));
                try
                {
                    var (rank, lastLogin) = await ReadRankInfoAsync(
                        auth.Session,
                        auth.SteamId,
                        cancellationToken);
                    result.RankChecked = true;
                    result.CsProfileRank = rank;
                    result.LastGameLoginText = lastLogin;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(Loc.Tf("Legacy_Query_Error_Rank_Format", ex.Message));
                }
            }

            if (options.QueryUserInfo)
            {
                progress?.Report(Loc.T("Legacy_Query_StatusUserInfo"));
                try
                {
                    var (personaName, region) = await ReadUserInfoAsync(
                        auth.Session,
                        auth.SteamId,
                        cancellationToken);
                    result.UserInfoChecked = true;
                    result.PersonaName = personaName;
                    result.Region = region;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(Loc.Tf("Legacy_Query_Error_UserInfo_Format", ex.Message));
                }

                progress?.Report(Loc.T("Legacy_Query_StatusChinaSteam"));
                try
                {
                    result.HasChinaSteam = await ReadChinaSteamAsync(
                        auth.Session,
                        cancellationToken);
                    result.ChinaSteamChecked = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(Loc.Tf("Legacy_Query_Error_ChinaSteam_Format", ex.Message));
                }
            }

            if (options.QueryBanTime)
            {
                progress?.Report(Loc.T("Legacy_Query_StatusBanTime"));
                try
                {
                    var ban = await ReadBanStateAsync(auth.Session, cancellationToken);
                    result.BanChecked = true;
                    result.VacBanned = ban.VacBanned;
                    result.BanExpiresAt = ban.ExpiresAt;
                    result.CooldownSeconds = ban.CooldownSeconds;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(Loc.Tf("Legacy_Query_Error_BanTime_Format", ex.Message));
                }
            }

            if (!string.IsNullOrWhiteSpace(options.ChangeNameTo) ||
                !string.IsNullOrWhiteSpace(options.ChangeAvatarFrom) ||
                !string.IsNullOrWhiteSpace(options.ChangeSummaryTo))
            {
                progress?.Report(Loc.T("Legacy_Query_StatusApplyingProfile"));
                try
                {
                    var profileResult = await AppState.ProfileService.ApplyAsync(
                        result.RefreshToken,
                        new SteamProfileApplyRequest(
                            string.IsNullOrWhiteSpace(options.ChangeNameTo) ? null : options.ChangeNameTo.Trim(),
                            null,
                            string.IsNullOrWhiteSpace(options.ChangeSummaryTo) ? null : options.ChangeSummaryTo.Trim(),
                            string.IsNullOrWhiteSpace(options.ChangeAvatarFrom) ? null : options.ChangeAvatarFrom,
                            ClearAliasHistory: false),
                        progress,
                        cancellationToken);

                    result.NameApplied = profileResult.NameApplied;
                    result.AvatarApplied = profileResult.AvatarApplied;
                    result.AppliedPersonaName = profileResult.NameApplied
                        ? options.ChangeNameTo?.Trim()
                        : null;
                    if (!profileResult.IsFullSuccess)
                    {
                        failures.Add(string.IsNullOrWhiteSpace(profileResult.ProfileError)
                            ? profileResult.AvatarError ?? Loc.T("Legacy_Query_Error_Profile")
                            : profileResult.ProfileError);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{Loc.T("Legacy_Query_Error_Profile")}: {ex.Message}");
                }
            }

            result.IsSuccess = failures.Count == 0;
            result.StatusCode = failures.Count == 0 ? "completed" : "partial";
            result.Message = string.Join("; ", failures.Where(message => !string.IsNullOrWhiteSpace(message)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.StatusCode = "cancelled";
            result.Message = Loc.T("Legacy_Query_StatusCancelled");
        }
        catch (OperationCanceledException ex)
        {
            result.StatusCode = "failed";
            result.Message = ex.Message;
        }
        catch (SteamCredentialsAuthException ex) when (ex.EResult == 5)
        {
            result.StatusCode = "invalidCredentials";
            result.Message = ex.Message;
        }
        catch (SteamCredentialsAuthException ex) when (ex.EResult is 84 or 87)
        {
            result.StatusCode = "rateLimited";
            result.Message = ex.Message;
        }
        catch (Exception ex)
        {
            result.StatusCode = "failed";
            result.Message = ex.Message;
            AppLog.Warn($"账号查询失败（{account.AccountName}）：{ex.Message}");
        }

        if (result.LoginSucceeded && !result.IsSuccess)
        {
            AppLog.Warn($"账号查询部分失败（{account.AccountName}）：{result.Message}");
        }

        return result;
    }

    private static async Task<AuthenticatedWebSession> ResolveSessionAsync(
        SteamAccountHistoryItem account,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (TryGetUsableCachedToken(account.EyaToken, out var cachedToken, out var cachedSteamId))
        {
            try
            {
                return await CreateSessionAsync(cachedToken, cachedSteamId, cancellationToken);
            }
            catch (SteamCmException ex) when (ex.IsTokenFailure)
            {
                AppLog.Warn($"账号查询：缓存令牌不可用，回退账号密码登录（{account.AccountName}）：{ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(account.Password))
        {
            throw new InvalidOperationException(Loc.T("Creds_Error_PasswordRequired"));
        }

        var auth = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
            account.AccountName,
            account.Password,
            guardCodeProvider,
            progress,
            cancellationToken);
        var refreshToken = FormatHelper.NormalizeToken(auth.RefreshToken);
        return await CreateSessionAsync(refreshToken, auth.SteamId, cancellationToken);
    }

    private static async Task<AuthenticatedWebSession> CreateSessionAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        var session = await SteamWebSession.BuildAsync(
            cmClient,
            refreshToken,
            steamId,
            cancellationToken);

        var tokenInfo = AppState.JwtTokenService.Inspect(refreshToken);
        return new AuthenticatedWebSession(steamId, refreshToken, tokenInfo.ExpiresAt, session);
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
        var tokenInfo = AppState.JwtTokenService.Inspect(refreshToken);
        if (!tokenInfo.IsValid || string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return false;
        }

        steamId = tokenInfo.SteamId;
        return true;
    }

    private static async Task<(int? Rank, string? LastLogin)> ReadRankInfoAsync(
        SteamWebSession session,
        string steamId,
        CancellationToken cancellationToken)
    {
        var (html, finalUri) = await GetHtmlAsync(
            string.Format(CultureInfo.InvariantCulture, RankPageFormat, steamId),
            session,
            "en-US,en;q=0.9",
            cancellationToken);
        EnsureAuthenticatedPage(finalUri);

        int? rank = null;
        var rankMatch = ProfileRankRegex().Match(html);
        if (!rankMatch.Success)
        {
            rankMatch = ProfileRankFallbackRegex().Match(html);
        }

        if (rankMatch.Success &&
            int.TryParse(
                rankMatch.Groups["rank"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedRank))
        {
            rank = parsedRank;
        }

        string? lastLogin = null;
        var lastLoginMatch = LastGameLoginRegex().Match(html);
        if (lastLoginMatch.Success)
        {
            lastLogin = WebUtility.HtmlDecode(
                    HtmlTagRegex().Replace(lastLoginMatch.Groups["value"].Value, " "))
                .Trim();
            lastLogin = WhitespaceRegex().Replace(lastLogin, " ");
        }

        return (rank, lastLogin);
    }

    private static async Task<(string? PersonaName, string? Region)> ReadUserInfoAsync(
        SteamWebSession session,
        string steamId,
        CancellationToken cancellationToken)
    {
        var (html, finalUri) = await GetHtmlAsync(
            string.Format(CultureInfo.InvariantCulture, UserInfoPageFormat, steamId),
            session,
            "en-US,en;q=0.9",
            cancellationToken);
        EnsureAuthenticatedPage(finalUri);

        string? personaName = null;
        var personaMatch = PersonaNameRegex().Match(html);
        if (personaMatch.Success)
        {
            personaName = WebUtility.HtmlDecode(personaMatch.Groups["name"].Value).Trim();
        }

        string? region = null;
        var userInfoMatch = UserInfoJsonRegex().Match(html);
        if (userInfoMatch.Success)
        {
            try
            {
                var json = WebUtility.HtmlDecode(userInfoMatch.Groups["json"].Value);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("country_code", out var country) &&
                    country.ValueKind == JsonValueKind.String)
                {
                    region = country.GetString();
                }
            }
            catch (JsonException)
            {
                // 页面结构变化时只放弃地区字段，不影响其它查询。
            }
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            var countryMatch = CountryCodeRegex().Match(html);
            if (countryMatch.Success)
            {
                region = countryMatch.Groups["code"].Value;
            }
        }

        return (string.IsNullOrWhiteSpace(personaName) ? null : personaName, region);
    }

    private static async Task<bool?> ReadChinaSteamAsync(
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var (html, finalUri) = await GetHtmlAsync(
            ChinaSteamPage,
            session,
            "en-US,en;q=0.9",
            cancellationToken);
        EnsureAuthenticatedPage(finalUri);

        var match = ChinaSteamDeleteLinkRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(match.Groups["text"].Value).Trim();
        return text.Contains("蒸汽平台", StringComparison.Ordinal) ||
               text.Contains("Steam China", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Perfect World", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<BanState> ReadBanStateAsync(
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var (cooldownHtml, cooldownUri) = await GetHtmlAsync(
            CooldownPage,
            session,
            "zh-CN,zh;q=0.9",
            cancellationToken);
        EnsureAuthenticatedPage(cooldownUri);

        var cooldownText = ExtractCooldownExpiration(cooldownHtml);
        var expiresAt = string.IsNullOrWhiteSpace(cooldownText)
            ? null
            : ParseCooldownEnd(cooldownHtml, cooldownText);
        var remaining = expiresAt.HasValue && expiresAt.Value > DateTimeOffset.Now
            ? (uint)Math.Ceiling((expiresAt.Value - DateTimeOffset.Now).TotalSeconds)
            : 0u;

        if (remaining > 0)
        {
            return new BanState(false, expiresAt, remaining);
        }

        var (vacHtml, vacUri) = await GetHtmlAsync(
            VacPage,
            session,
            "zh-CN,zh;q=0.9",
            cancellationToken);
        EnsureAuthenticatedPage(vacUri);

        var permanentVac =
            IsRedLockPermanentVac(vacHtml) ||
            vacHtml.Contains("Counter-Strike 2", StringComparison.OrdinalIgnoreCase);
        return new BanState(permanentVac, null, 0);
    }

    private static async Task<(string Html, Uri FinalUri)> GetHtmlAsync(
        string url,
        SteamWebSession session,
        string acceptLanguage,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await GetHtmlCoreAsync(url, session, acceptLanguage, cancellationToken);
            }
            catch (Exception ex) when (
                attempt < maxAttempts &&
                IsRetryableAccountQueryException(ex, cancellationToken))
            {
                lastError = ex;
                var baseError = ex.GetBaseException();
                AppLog.Warn(
                    $"账号查询网页请求失败（{attempt}/{maxAttempts}）：{url}：" +
                    $"{baseError.GetType().Name}: {baseError.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Steam 页面请求失败：{lastError?.GetBaseException().Message ?? Loc.T("Legacy_Query_Error_RedirectLoop")}",
            lastError);
    }

    private static async Task<(string Html, Uri FinalUri)> GetHtmlCoreAsync(
        string url,
        SteamWebSession session,
        string acceptLanguage,
        CancellationToken cancellationToken)
    {
        var current = new Uri(url);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/123 Safari/537.36");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 &&
                response.Headers.Location is not null)
            {
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(cancellationToken), current);
        }

        throw new InvalidOperationException(Loc.T("Legacy_Query_Error_RedirectLoop"));
    }

    private static bool IsRetryableAccountQueryException(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            TaskCanceledException => true,
            IOException => true,
            System.Net.Sockets.SocketException => true,
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout } => true,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => true,
            _ => false
        };
    }

    private static void EnsureAuthenticatedPage(Uri uri)
    {
        if (uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Loc.T("Legacy_Query_Error_SessionExpired"));
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
        var serverMatch = ServerTimeRegex().Match(html);
        if (!serverMatch.Success ||
            !long.TryParse(
                serverMatch.Groups["value"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var timestamp))
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
        var match = CooldownDateRegex().Match(cooldownText);
        if (!match.Success)
        {
            return null;
        }

        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var period = match.Groups["period"].Value;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        if (period == "下午" && hour != 12)
        {
            hour += 12;
        }
        else if (period == "上午" && hour == 12)
        {
            hour = 0;
        }

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
                // 夏令时不存在的时间，尝试相邻年份即可。
            }
        }

        return best?.ToLocalTime();
    }

    private static bool IsRedLockPermanentVac(string html) =>
        html.Contains("暂时强制将您的个人资料设置为私密状态", StringComparison.Ordinal) ||
        html.Contains("怀疑您的帐户被他人登录过", StringComparison.Ordinal) ||
        html.Contains("暫時強制將您的個人資料設定為私密狀態", StringComparison.Ordinal) ||
        html.Contains("懷疑您的帳戶被他人登入過", StringComparison.Ordinal) ||
        html.Contains("账号锁定", StringComparison.Ordinal) ||
        html.Contains("帐号锁定", StringComparison.Ordinal) ||
        html.Contains("账户锁定", StringComparison.Ordinal) ||
        html.Contains("帳號鎖定", StringComparison.Ordinal) ||
        html.Contains("帳戶鎖定", StringComparison.Ordinal) ||
        html.Contains("account locked", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"CS:GO\s+Profile\s+Rank(?:[\s\S]{0,800}?<td[^>]*>\s*|\s*:?\s*)(?<rank>\d{1,2})",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProfileRankRegex();

    [GeneratedRegex(
        @"CS:GO\s+Profile\s+Rank[\s\S]{0,800}?:\s*(?<rank>\d{1,2})",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProfileRankFallbackRegex();

    [GeneratedRegex(
        @"Logged\s+out\s+of\s+CS:GO[\s\S]{0,1000}?<td[^>]*>(?<value>[\s\S]*?)</td>",
        RegexOptions.IgnoreCase)]
    private static partial Regex LastGameLoginRegex();

    [GeneratedRegex(
        @"<div\s+class=""friends_header_name""[^>]*>[\s\S]*?<a[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase)]
    private static partial Regex PersonaNameRegex();

    [GeneratedRegex(@"data-userinfo=""(?<json>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfoJsonRegex();

    [GeneratedRegex(@"""country_code""\s*:\s*""(?<code>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CountryCodeRegex();

    [GeneratedRegex(
        @"<a\s+class=""account_manage_link\s+delete_link""\s+href=""https://help\.steampowered\.com/[^/]+/wizard/HelpDeleteAccount"">(?<text>[^<]+)</a>",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChinaSteamDeleteLinkRegex();

    [GeneratedRegex(@"g_ServerTime\s*=\s*(?<value>\d+)")]
    private static partial Regex ServerTimeRegex();

    [GeneratedRegex(
        @"(?<month>\d+)\s*月\s*(?<day>\d+)\s*日\s*(?<period>上午|下午)\s*(?<hour>\d+):(?<minute>\d+)")]
    private static partial Regex CooldownDateRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record AuthenticatedWebSession(
        string SteamId,
        string RefreshToken,
        DateTimeOffset? TokenExpiresAt,
        SteamWebSession Session);

    private sealed record BanState(bool? VacBanned, DateTimeOffset? ExpiresAt, uint CooldownSeconds);
}
