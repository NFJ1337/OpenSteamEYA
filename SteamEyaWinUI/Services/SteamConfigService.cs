using System.Globalization;
using Microsoft.Win32;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed class SteamConfigService
{
    public IReadOnlyList<CachedSteamLoginAccount> GetLoginAccounts(SteamPaths paths)
    {
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        var loginUsers = VdfDocument.LoadOrEmpty(loginUsersPath);
        var accounts = new List<CachedSteamLoginAccount>();

        var activeAccount = GetActiveLoginAccount(paths, loginUsers);
        if (activeAccount is not null)
        {
            accounts.Add(activeAccount);
        }

        accounts.AddRange(GetLoginUsersAccounts(loginUsers));
        accounts.AddRange(GetConfigAccounts(Path.Combine(paths.ConfigPath, "config.vdf")));

        var normalized = NormalizeLoginAccounts(accounts);
        PopulateConnectCacheTokens(paths, normalized);
        return normalized;
    }

    /// <summary>
    /// 取 Steam 自己记的「每个账号最近一次登录时间」：loginusers.vdf 里每个 Steam64 下的 Timestamp（unix 秒）。
    /// 这是 Steam 客户端写的时间，不是本程序的查询时间；没在本机登录过的账号不会出现在里面。
    /// </summary>
    public Dictionary<string, DateTimeOffset> GetLoginUsersLastLogin(SteamPaths paths)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var loginUsers = VdfDocument.LoadOrEmpty(Path.Combine(paths.ConfigPath, "loginusers.vdf"));
        if (!TryGetUsers(loginUsers, out var users))
        {
            return result;
        }

        foreach (var (steamId, value) in users)
        {
            if (value is not Dictionary<string, object> user)
            {
                continue;
            }

            var raw = GetString(user, "Timestamp");
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                result[steamId] = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            }
        }

        return result;
    }

    public void SetAutoLoginUser(string accountName)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Valve\Steam", writable: true);
            if (key is null)
            {
                AppLog.Warn("无法打开 HKCU\\Software\\Valve\\Steam，未写入 AutoLoginUser。");
                return;
            }

            key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
            AppLog.Info($"已写入 AutoLoginUser：\"{accountName}\"");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入 Steam AutoLoginUser 失败：{ex.Message}");
        }
    }
    public void UpdateLoginFiles(
        SteamPaths paths,
        string accountName,
        string steamId,
        string encryptedJwt,
        string accountCrc32)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        UpdateConfigVdf(configPath, accountName, steamId);
        AppLog.Info($"已写入 config.vdf（{FileLength(configPath)} 字节）：\"{configPath}\"");

        UpdateLoginUsersVdf(loginUsersPath, accountName, steamId);
        AppLog.Info($"已写入 loginusers.vdf（{FileLength(loginUsersPath)} 字节）：\"{loginUsersPath}\"");

        UpdateLocalVdf(paths.LocalVdfPath, accountCrc32, encryptedJwt);
        AppLog.Info($"已写入 local.vdf（{FileLength(paths.LocalVdfPath)} 字节）：\"{paths.LocalVdfPath}\"");
    }

    public void RestoreLoginFiles(SteamPaths paths, CachedSteamLoginAccount account)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        UpdateConfigVdf(configPath, account.AccountName, account.SteamId);
        AppLog.Info($"已恢复 config.vdf（{FileLength(configPath)} 字节）：\"{configPath}\"");

        RestoreLoginUsersVdf(loginUsersPath, account);
        AppLog.Info($"已恢复 loginusers.vdf（{FileLength(loginUsersPath)} 字节）：\"{loginUsersPath}\"");

        // 写回原账号的 ConnectCache 令牌：有它 Steam 才能免密自动登录；缺失则只能预选账号、仍需手动登录。
        if (!string.IsNullOrWhiteSpace(account.ConnectCacheToken))
        {
            UpdateLocalVdf(
                paths.LocalVdfPath,
                Crc32.ComputeSteamAccountKey(account.AccountName),
                account.ConnectCacheToken);
            AppLog.Info($"已恢复 local.vdf ConnectCache（{FileLength(paths.LocalVdfPath)} 字节）：\"{paths.LocalVdfPath}\"");
        }
        else
        {
            AppLog.Warn("缓存账号缺少 ConnectCache 令牌，恢复后 Steam 可能需要手动登录。");
        }
    }

    public SteamLoginStateSnapshot CaptureLoginState(SteamPaths paths)
    {
        var snapshot = new SteamLoginStateSnapshot();
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        var loginUsers = VdfDocument.LoadOrEmpty(loginUsersPath);
        if (TryGetUsers(loginUsers, out var users))
        {
            foreach (var (steamId, value) in users)
            {
                if (value is Dictionary<string, object> user)
                {
                    snapshot.LoginUsers[steamId] = (Dictionary<string, object>)CloneVdfValue(user)
                        ;
                }
            }
        }

        var connectCache = GetPath(
            VdfDocument.LoadOrEmpty(paths.LocalVdfPath),
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        if (connectCache is not null)
        {
            foreach (var (key, value) in connectCache)
            {
                var token = value.ToString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    snapshot.ConnectCache[key] = token;
                }
            }
        }

        return snapshot;
    }

    public void RestorePreservedLoginState(
        SteamPaths paths,
        SteamLoginStateSnapshot snapshot,
        string targetAccountName)
    {
        var targetKey = Crc32.ComputeSteamAccountKey(targetAccountName);
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        var loginUsers = VdfDocument.LoadOrEmpty(loginUsersPath);
        var users = EnsureObject(loginUsers, "users");

        foreach (var (steamId, savedUser) in snapshot.LoginUsers)
        {
            var savedAccountName = GetString(savedUser, "AccountName");
            if (string.Equals(savedAccountName, targetAccountName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (users.TryGetValue(steamId, out var currentValue) &&
                currentValue is Dictionary<string, object> currentUser)
            {
                foreach (var key in new[] { "AccountName", "RememberPassword", "AllowAutoLogin", "WantsOfflineMode", "SkipOfflineModeWarning" })
                {
                    if (savedUser.TryGetValue(key, out var savedValue))
                    {
                        currentUser[key] = CloneVdfValue(savedValue);
                    }
                }

                currentUser["MostRecent"] = "0";
            }
            else
            {
                var restored = (Dictionary<string, object>)CloneVdfValue(savedUser);
                restored["MostRecent"] = "0";
                users[steamId] = restored;
            }
        }

        VdfDocument.Save(loginUsersPath, loginUsers);

        var local = VdfDocument.LoadOrEmpty(paths.LocalVdfPath);
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        foreach (var (key, token) in snapshot.ConnectCache)
        {
            if (!string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                connectCache[key] = token;
            }
        }

        VdfDocument.Save(paths.LocalVdfPath, local);
        AppLog.Info($"已恢复旧账号的 Steam 登录缓存（跳过目标账号 {targetAccountName}）。");
    }

    private static object CloneVdfValue(object value) => value switch
    {
        Dictionary<string, object> dictionary => dictionary.ToDictionary(
            pair => pair.Key,
            pair => CloneVdfValue(pair.Value),
            StringComparer.Ordinal),
        _ => value
    };

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>读取 Steam 注册表当前 ActiveUser，用于确认密码登录是否已经完成。</summary>
    public CachedSteamLoginAccount? GetActiveLoginAccount(SteamPaths paths)
    {
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        return GetActiveLoginAccount(paths, VdfDocument.LoadOrEmpty(loginUsersPath));
    }

    /// <summary>
    /// 更新 config.vdf：**保留其它账号**，只增改当前账号 —— 对齐旧版 Python 版
    /// （_write_vdf_config："更新 config.vdf，保留已有账号并追加/更新当前 EYA 账号"）。
    ///
    /// 早先这里是从零生成、整体覆盖的最小模板，虽然能规避 VDF 往返损坏，副作用却是把用户
    /// config.vdf 里其它账号的登录记录一并抹掉（用户反馈：登录后缓存被清空）。
    /// 现在改成读旧文件再合并；解析不了就中止（见 LoadForRewrite），绝不拿空文档覆盖别人的账号。
    /// </summary>
    private static void UpdateConfigVdf(string path, string accountName, string steamId)
    {
        var config = LoadForRewrite(path);
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

        // 同一个 SteamID 换了登录名（改名/换号）时清掉旧条目；其它账号一律保留。
        foreach (var oldName in accounts
                     .Where(pair => pair.Key != accountName &&
                         pair.Value is Dictionary<string, object> old &&
                         string.Equals(old.GetValueOrDefault("SteamID")?.ToString(), steamId, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            accounts.Remove(oldName);
        }

        accounts[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId
        };

        BackupBeforeWrite(path);
        VdfDocument.Save(path, config);
    }

    /// <summary>
    /// 读取要改写的 VDF；解析不了就抛错、**中止这次写入**（对齐旧版 Python：
    /// "无法解析 xxx，已中止登录以避免覆盖其它 Steam 账号"）。
    /// 早先用的是 LoadOrEmpty：解析失败当空文档继续，等于把别的账号全抹掉。
    /// </summary>
    private static Dictionary<string, object> LoadForRewrite(string path)
    {
        try
        {
            return VdfDocument.Load(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                Loc.Tf("Steam_Error_VdfAbort_Format", Path.GetFileName(path), ex.Message),
                ex);
        }
    }

    /// <summary>改写前留一份同名 .bak：万一我们手写的 VDF 序列化有问题，还能手动还原。（旧版 EYA 那条链路也共用）</summary>
    internal static void BackupBeforeWrite(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"备份 {Path.GetFileName(path)} 失败（继续写入）：{ex.Message}");
        }
    }
    /// <summary>
    /// 更新 loginusers.vdf：保留其它已记住的账号，只增改当前账号 —— 旧版 Python
    /// _write_vdf_loginusers 的语义（解析不了就中止，绝不拿空文档覆盖别人的账号）。
    /// </summary>
    private static void UpdateLoginUsersVdf(string path, string accountName, string steamId)
    {
        var loginUsers = LoadForRewrite(path);
        var users = EnsureObject(loginUsers, "users");

        // 同一登录名换了 SteamID（改名/换号）时清掉旧条目；其它账号一律保留。
        foreach (var oldId in users
                     .Where(pair => pair.Key != steamId &&
                         pair.Value is Dictionary<string, object> old &&
                         string.Equals(old.GetValueOrDefault("AccountName")?.ToString(), accountName, StringComparison.OrdinalIgnoreCase))
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
            ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };

        BackupBeforeWrite(path);
        VdfDocument.Save(path, loginUsers);
    }
    private static void RestoreLoginUsersVdf(string path, CachedSteamLoginAccount account)
    {
        var loginUsers = LoadForRewrite(path);
        var users = EnsureObject(loginUsers, "users");

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        var restoredUser = EnsureObject(users, account.SteamId);
        restoredUser["AccountName"] = account.AccountName;
        // 优先保留 loginusers.vdf 里既有昵称，其次用缓存到的 Steam 昵称，最后才退回登录名。
        var existingPersona = GetString(restoredUser, "PersonaName");
        restoredUser["PersonaName"] = !string.IsNullOrWhiteSpace(existingPersona)
            ? existingPersona
            : !string.IsNullOrWhiteSpace(account.PersonaName)
                ? account.PersonaName
                : account.AccountName;
        restoredUser["RememberPassword"] = "1";
        restoredUser["WantsOfflineMode"] = "0";
        restoredUser["SkipOfflineModeWarning"] = "0";
        restoredUser["AllowAutoLogin"] = "1";
        restoredUser["MostRecent"] = "1";
        restoredUser["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

        BackupBeforeWrite(path);
        VdfDocument.Save(path, loginUsers);
    }
    private static void UpdateLocalVdf(string path, string accountCrc32, string encryptedJwt)
    {
        var local = LoadForRewrite(path);
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");

        connectCache[accountCrc32] = encryptedJwt;
        BackupBeforeWrite(path);
        VdfDocument.Save(path, local);
    }
    private static Dictionary<string, object> EnsurePath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            current = EnsureObject(current, key);
        }

        return current;
    }

    private static Dictionary<string, object> EnsureObject(
        Dictionary<string, object> parent,
        string key)
    {
        if (parent.TryGetValue(key, out var value) && value is Dictionary<string, object> existing)
        {
            return existing;
        }

        var created = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[key] = created;
        return created;
    }

    private static CachedSteamLoginAccount? GetActiveLoginAccount(
        SteamPaths paths,
        Dictionary<string, object> loginUsers)
    {
        var accountId = ReadActiveUserAccountId();
        if (!accountId.HasValue)
        {
            return null;
        }

        var steamId = ToSteam64(accountId.Value);
        var accountName = FindAccountNameBySteamId(loginUsers, steamId) ??
            FindAccountNameBySteamId(Path.Combine(paths.ConfigPath, "config.vdf"), steamId) ??
            ReadSteamRegistryString("AutoLoginUser");

        if (string.IsNullOrWhiteSpace(accountName))
        {
            AppLog.Warn($"找到活动 Steam 用户 {steamId}，但无法解析其账户名。");
            return null;
        }

        return new CachedSteamLoginAccount
        {
            AccountName = accountName,
            SteamId = steamId,
            CachedAt = DateTimeOffset.Now
        };
    }

    // 在 EYA 登录覆盖 local.vdf 之前，把每个账号的 ConnectCache 令牌（crc32(账户名)+"1"）抓出来随账号缓存，
    // 恢复时写回 local.vdf 才能让 Steam 免密自动登录。读不到（账号已被 Steam 忘记/令牌已轮换）时保持 null，
    // 恢复退化为仅预选账号。local.vdf 解析失败时 GetPath 返回 null，整体跳过，best-effort。
    private static void PopulateConnectCacheTokens(
        SteamPaths paths,
        IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        var connectCache = GetPath(
            VdfDocument.LoadOrEmpty(paths.LocalVdfPath),
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        if (connectCache is null)
        {
            return;
        }

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AccountName))
            {
                continue;
            }

            var token = GetString(connectCache, Crc32.ComputeSteamAccountKey(account.AccountName));
            if (!string.IsNullOrWhiteSpace(token))
            {
                account.ConnectCacheToken = token;
            }
        }
    }

    // internal：SteamAccountNameService 离线补全 CS2 来源账号显示名时复用同一份解析。
    internal static IEnumerable<CachedSteamLoginAccount> GetLoginUsersAccounts(
        Dictionary<string, object> loginUsers)
    {
        if (!TryGetUsers(loginUsers, out var users))
        {
            yield break;
        }

        foreach (var (steamId, value) in users)
        {
            if (value is not Dictionary<string, object> user)
            {
                continue;
            }

            var accountName = GetString(user, "AccountName");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                PersonaName = GetString(user, "PersonaName"),
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    // internal：同 GetLoginUsersAccounts，供 SteamAccountNameService 复用。
    internal static IEnumerable<CachedSteamLoginAccount> GetConfigAccounts(string configPath)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            yield break;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is not Dictionary<string, object> account)
            {
                continue;
            }

            var steamId = GetString(account, "SteamID");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    private static IReadOnlyList<CachedSteamLoginAccount> NormalizeLoginAccounts(
        IEnumerable<CachedSteamLoginAccount> accounts)
    {
        return accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.SteamId))
            .GroupBy(account => account.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryGetUsers(
        Dictionary<string, object> loginUsers,
        out Dictionary<string, object> users)
    {
        if (VdfDocument.GetObject(loginUsers, "users") is { } existingUsers)
        {
            users = existingUsers;
            return true;
        }

        users = [];
        return false;
    }

    private static string? GetString(Dictionary<string, object> values, string key)
    {
        return VdfDocument.GetValue(values, key)?.ToString();
    }

    private static uint? ReadActiveUserAccountId()
    {
        return ReadSteamRegistryUInt32(@"Software\Valve\Steam\ActiveProcess", "ActiveUser") ??
            ReadSteamRegistryUInt32(@"Software\Valve\Steam", "ActiveUser");
    }

    private static string ToSteam64(uint accountId)
    {
        const ulong individualAccountUniverseBase = 76561197960265728UL;
        return (individualAccountUniverseBase + accountId).ToString(CultureInfo.InvariantCulture);
    }

    private static string? FindAccountNameBySteamId(
        Dictionary<string, object> loginUsers,
        string steamId)
    {
        if (!TryGetUsers(loginUsers, out var users) ||
            !users.TryGetValue(steamId, out var value) ||
            value is not Dictionary<string, object> user)
        {
            return null;
        }

        return GetString(user, "AccountName");
    }

    private static string? FindAccountNameBySteamId(string configPath, string steamId)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            return null;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is Dictionary<string, object> account &&
                string.Equals(GetString(account, "SteamID"), steamId, StringComparison.OrdinalIgnoreCase))
            {
                return accountName;
            }
        }

        return null;
    }

    private static Dictionary<string, object>? GetPath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            if (VdfDocument.GetObject(current, key) is not { } child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private static uint? ReadSteamRegistryUInt32(string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(keyPath);
            var value = key?.GetValue(valueName);
            return value switch
            {
                int intValue when intValue > 0 => unchecked((uint)intValue),
                uint uintValue when uintValue > 0 => uintValue,
                long longValue when longValue is > 0 and <= uint.MaxValue => (uint)longValue,
                string stringValue when uint.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSteamRegistryString(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}


internal sealed class SteamLoginStateSnapshot
{
    public Dictionary<string, Dictionary<string, object>> LoginUsers { get; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string> ConnectCache { get; } =
        new(StringComparer.Ordinal);
}
