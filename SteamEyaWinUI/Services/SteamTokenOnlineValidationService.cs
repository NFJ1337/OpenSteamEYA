using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamTokenOnlineValidationService
{
    private readonly JwtTokenService _jwtTokenService = new();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<SteamTokenOnlineValidationResult> ValidateAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var tokenInfo = _jwtTokenService.Inspect(refreshToken);
        if (!tokenInfo.IsValid)
        {
            return new SteamTokenOnlineValidationResult(false, tokenInfo.Status);
        }

        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return new SteamTokenOnlineValidationResult(false, Loc.T("Jwt_Status_MissingSteamId"));
        }

        await using var cmClient = new SteamCmClient(HttpClient);
        try
        {
            await cmClient.ConnectAndLogOnAsync(refreshToken, tokenInfo.SteamId, cancellationToken);
            return new SteamTokenOnlineValidationResult(true, Loc.T("Token_Result_Accepted"));
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            return new SteamTokenOnlineValidationResult(false, ex.Message);
        }
    }

    /// <summary>
    /// 「登录口径」的在线校验：CM 登录成功之外，还要求能换取 App 访问令牌（上号/一键查询都依赖它）。
    /// 只做 CM 登录时，Steam 可能放行登录却拒绝换取 App 令牌（常见 EResult 15 AccessDenied）——
    /// 那种令牌实际并不能用，历史页「清空无效账号」必须按这个口径判，否则会和「一键查询」结论不一致。
    /// </summary>
    public async Task<SteamTokenOnlineValidationResult> ValidateForLoginAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var tokenInfo = _jwtTokenService.Inspect(refreshToken);
        if (!tokenInfo.IsValid)
        {
            return new SteamTokenOnlineValidationResult(false, tokenInfo.Status);
        }

        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return new SteamTokenOnlineValidationResult(false, Loc.T("Jwt_Status_MissingSteamId"));
        }

        await using var cmClient = new SteamCmClient(HttpClient);
        try
        {
            await cmClient.ConnectAndLogOnAsync(refreshToken, tokenInfo.SteamId, cancellationToken);
            await cmClient.GenerateAccessTokenForAppAsync(refreshToken, cancellationToken);
            return new SteamTokenOnlineValidationResult(true, Loc.T("Token_Result_Accepted"));
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            return new SteamTokenOnlineValidationResult(false, ex.Message);
        }
    }
}
