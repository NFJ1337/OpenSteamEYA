using System.Net.Http;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed record EyaLicenseParseResult(string AccountName, string SteamId, string Token);

/// <summary>
/// 旧版 SteamAccountManager 的 EYA 卡密解析：固定请求奶味上游的 keygettoken 接口。
/// 输入格式兼容「账号名----卡密」；响应格式兼容「SteamID----Token」。
/// </summary>
internal sealed class LegacyEyaLicenseClient
{
    private const string TokenEndpoint = "http://111.170.18.37:9099/keygettoken?key=";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<EyaLicenseParseResult> ParseLicenseKeyAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var raw = licenseKey.Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        var separatorIndex = raw.IndexOf("----", StringComparison.Ordinal);
        var accountName = separatorIndex >= 0 ? raw[..separatorIndex].Trim().ToLowerInvariant() : string.Empty;
        var apiKey = separatorIndex >= 0 ? raw[(separatorIndex + 4)..].Trim() : raw;
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        var response = await Client.GetStringAsync(
            TokenEndpoint + Uri.EscapeDataString(apiKey),
            cancellationToken);
        response = response.Trim().Trim('\uFEFF');

        var responseSeparator = response.IndexOf("----", StringComparison.Ordinal);
        var steamId = responseSeparator >= 0 ? response[..responseSeparator].Trim() : string.Empty;
        var token = responseSeparator >= 0 ? response[(responseSeparator + 4)..].Trim() : response;
        if (token.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LegacyEyaTokenMissing"));
        }

        if (steamId.Length == 0)
        {
            steamId = AppState.JwtTokenService.Inspect(token).SteamId ?? string.Empty;
        }

        return new EyaLicenseParseResult(accountName, steamId, token);
    }

    public async Task<SteamAccountData> GetAccountDataAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var result = await ParseLicenseKeyAsync(licenseKey, cancellationToken);
        var accountName = result.AccountName;
        if (accountName.Length == 0)
        {
            accountName = result.SteamId;
        }

        if (accountName.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LegacyEyaAccountMissing"));
        }

        return new SteamAccountData(result.Token, accountName, result.SteamId);
    }
}