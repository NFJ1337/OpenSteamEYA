using System.Security.Cryptography;

namespace SteamEyaWinUI.Services;

/// <summary>
/// steamcommunity.com 的已认证 Web 会话凭据：在「已登录的 CM 会话」上用 EYA refresh token 换取
/// access token，据此拼出 steamLoginSecure / sessionid cookie。
/// 注意：必须经 CM 已登录会话来换——GenerateAccessTokenForApp / finalizelogin 在匿名 web 上一律
/// AccessDenied(15)，这类 token 需要一个已认证会话才放行。
/// FileUploader、profile 表单等接口要求请求体里的 sessionid 与 cookie 里的一致，故同时暴露 SessionId。
/// </summary>
internal sealed record SteamWebSession(string CookieHeader, string SessionId, string AccessToken)
{
    public static async Task<SteamWebSession> BuildAsync(
        SteamCmClient cmClient,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        var accessToken = await cmClient.GenerateAccessTokenForAppAsync(refreshToken, cancellationToken);
        var steamLoginSecure = Uri.EscapeDataString($"{steamId}||{accessToken}");
        var sessionId = RandomHex(12);
        var clientSessionId = RandomHex(8);

        var cookieHeader =
            $"steamLoginSecure={steamLoginSecure}; sessionid={sessionId}; clientsessionid={clientSessionId}";

        return new SteamWebSession(cookieHeader, sessionId, accessToken);
    }

    private static string RandomHex(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }
}
