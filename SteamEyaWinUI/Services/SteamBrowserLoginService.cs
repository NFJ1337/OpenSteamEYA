using System.Net.Http;

namespace SteamEyaWinUI.Services;

/// <summary>Steam 网页会话：登录后可直接注入 WebView2 的 cookie 列表。</summary>
internal sealed record SteamBrowserSession(string SteamId, IReadOnlyList<KeyValuePair<string, string>> Cookies);

/// <summary>
/// 「Steam中查看」用的网页会话：拿账号+密码走项目已有的登录链路
/// （账号密码 → CM 登录换 refresh token → 换 access token → 拼 steamLoginSecure / sessionid），
/// 再把 cookie 注入 WebView2，这样小窗口一打开就是已登录的「我的页面」，不用手填表单、也不会撞上网页版的人机校验。
/// 注意：账号开了手机令牌（2FA）时这里拿不到验证码，会直接失败并提示。
/// </summary>
internal static class SteamBrowserLoginService
{
    public static async Task<SteamBrowserSession> CreateSessionAsync(
        string accountName,
        string password,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 无 2FA 的账号不需要验证码；需要时这里返回空，由上游把错误抛出来提示用户。
        static Task<string?> NoGuardCodeAsync(SteamGuardPrompt _, CancellationToken __) => Task.FromResult<string?>(null);

        var auth = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
            accountName,
            password,
            NoGuardCodeAsync,
            progress,
            cancellationToken);

        await using var cmClient = new SteamCmClient(httpClient);
        await cmClient.ConnectAndLogOnAsync(auth.RefreshToken, auth.SteamId, cancellationToken);

        var session = await SteamWebSession.BuildAsync(cmClient, auth.RefreshToken, auth.SteamId, cancellationToken);

        return new SteamBrowserSession(auth.SteamId, ParseCookies(session.CookieHeader));
    }

    /// <summary>
    /// 历史账号没有密码、只有 EYA 令牌：令牌（JWT）本身就是 refresh token，可以直接换网页会话。
    /// 与「一键查询」用的是同一条链路，所以能不能打开网页与那条链路的结论一致。
    /// </summary>
    public static async Task<SteamBrowserSession> CreateSessionFromTokenAsync(
        string eyaToken,
        CancellationToken cancellationToken)
    {
        var normalized = FormatHelper.NormalizeToken(eyaToken);
        var tokenInfo = AppState.JwtTokenService.Inspect(normalized);
        if (!tokenInfo.IsValid || string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            throw new InvalidOperationException(tokenInfo.Status);
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await using var cmClient = new SteamCmClient(httpClient);
        await cmClient.ConnectAndLogOnAsync(normalized, tokenInfo.SteamId, cancellationToken);

        var session = await SteamWebSession.BuildAsync(cmClient, normalized, tokenInfo.SteamId, cancellationToken);
        return new SteamBrowserSession(tokenInfo.SteamId, ParseCookies(session.CookieHeader));
    }

    /// <summary>把 "k=v; k2=v2" 形式的 CookieHeader 拆成键值对。</summary>
    private static List<KeyValuePair<string, string>> ParseCookies(string cookieHeader) =>
        cookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
            .Select(parts => new KeyValuePair<string, string>(parts[0].Trim(), parts[1].Trim()))
            .ToList();
}