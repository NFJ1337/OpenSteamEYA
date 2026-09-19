using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 旧版 SteamAccountManager 的 EYA Token 登录流程。仅用于「旧版 EYA 登录」页，
/// 不改动 OpenSteamEYA 现有的自动、手动和账密登录。
/// </summary>
internal sealed class LegacyEyaLoginService
{
    private readonly JwtTokenService _jwtTokenService = new();
    private readonly SteamCryptoService _steamCryptoService = new();
    private readonly SteamConfigService _steamConfigService = new();
    private readonly SteamProcessService _steamProcessService = new();

    public LoginResult Login(string accountName, string eyaToken, IProgress<string>? progress = null)
    {
        accountName = accountName.Trim().ToLowerInvariant();
        eyaToken = eyaToken.Trim();
        AppLog.Info($"==== 开始旧版 EYA Token 上号：账号=\"{accountName}\" ====");

        try
        {
            progress?.Report(Loc.T("Steam_Progress_ValidatingToken"));
            var token = _jwtTokenService.Validate(eyaToken);

            progress?.Report(Loc.T("Steam_Progress_LocatingInstall"));
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();

            progress?.Report(Loc.T("Steam_Progress_EncryptingToken"));
            var encryptedJwt = _steamCryptoService.EncryptToHex(eyaToken, accountName);
            var accountCrc32 = Crc32.ComputeSteamAccountKey(accountName);

            _steamProcessService.EnsureSteamStopped(paths, progress);
            AppState.LoginService.TrySyncCs2UserdataFolders(paths, token.SteamId, progress);

            progress?.Report(Loc.T("Steam_Progress_WritingConfig"));
            WriteLoginFiles(paths, accountName, token.SteamId, encryptedJwt, accountCrc32);
            _steamConfigService.SetAutoLoginUser(accountName);

            progress?.Report(Loc.T("Steam_Progress_StartingSteam"));
            _steamProcessService.LaunchSteamWithLogin(paths, accountName);

            AppState.LoginService.TryPushCs2Cloud(paths, token.SteamId, progress);

            AppLog.Info("==== 旧版 EYA Token 上号流程完成 ====");
            return new LoginResult(accountName, token.SteamId, token.ExpiresAt);
        }
        catch (Exception ex)
        {
            AppLog.Error("旧版 EYA Token 上号失败。", ex);
            throw;
        }
    }

    private static void WriteLoginFiles(
        SteamPaths paths,
        string accountName,
        string steamId,
        string encryptedJwt,
        string accountKey)
    {
        Directory.CreateDirectory(paths.ConfigPath);
        UpdateConfigVdfPreservingExisting(Path.Combine(paths.ConfigPath, "config.vdf"), accountName, steamId);
        UpdateLoginUsersVdf(Path.Combine(paths.ConfigPath, "loginusers.vdf"), accountName, steamId);
        UpdateLocalVdfPreservingExisting(paths.LocalVdfPath, accountKey, encryptedJwt);
    }

    private static void UpdateConfigVdfPreservingExisting(string path, string accountName, string steamId)
    {
        var config = VdfDocument.Load(path);
        var steam = EnsurePath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam.GetValueOrDefault("AutoUpdateWindowEnabled") is not string)
        {
            steam["AutoUpdateWindowEnabled"] = "0";
        }

        if (steam.GetValueOrDefault("MTBF") is not string)
        {
            steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();
        }

        var accounts = EnsureObject(steam, "Accounts");
        foreach (var oldName in accounts
                     .Where(pair => pair.Key != accountName &&
                         pair.Value is Dictionary<string, object> old &&
                         old.GetValueOrDefault("SteamID")?.ToString() == steamId)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            accounts.Remove(oldName);
        }

        accounts[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId
        };
        SteamConfigService.BackupBeforeWrite(path);
        VdfDocument.Save(path, config);
    }

    private static void UpdateLoginUsersVdf(string path, string accountName, string steamId)
    {
        var loginUsers = VdfDocument.Load(path);
        var users = EnsureObject(loginUsers, "users");

        foreach (var oldId in users
                     .Where(pair => pair.Key != steamId &&
                         pair.Value is Dictionary<string, object> old &&
                         string.Equals(
                             old.GetValueOrDefault("AccountName")?.ToString(),
                             accountName,
                             StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            users.Remove(oldId);
        }

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        users[steamId] = new Dictionary<string, object>
        {
            ["AccountName"] = accountName,
            ["PersonaName"] = accountName,
            ["RememberPassword"] = "1",
            ["WantsOfflineMode"] = "0",
            ["SkipOfflineModeWarning"] = "0",
            ["AllowAutoLogin"] = "1",
            ["MostRecent"] = "1",
            ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        };
        SteamConfigService.BackupBeforeWrite(path);
        VdfDocument.Save(path, loginUsers);
    }

    private static void UpdateLocalVdfPreservingExisting(string path, string accountKey, string encryptedJwt)
    {
        var local = VdfDocument.Load(path);
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        connectCache[accountKey] = encryptedJwt;
        SteamConfigService.BackupBeforeWrite(path);
        VdfDocument.Save(path, local);
    }

    private static Dictionary<string, object> EnsurePath(
        Dictionary<string, object> parent,
        params string[] keys)
    {
        var current = parent;
        foreach (var key in keys)
        {
            if (current.GetValueOrDefault(key) is not Dictionary<string, object> child)
            {
                child = new Dictionary<string, object>(StringComparer.Ordinal);
                current[key] = child;
            }

            current = child;
        }

        return current;
    }

    private static Dictionary<string, object> EnsureObject(
        Dictionary<string, object> parent,
        string key)
    {
        if (parent.GetValueOrDefault(key) is Dictionary<string, object> child)
        {
            return child;
        }

        child = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[key] = child;
        return child;
    }
}
