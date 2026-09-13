using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>某账号的离线可读名：昵称 + 登录账号名，两者都可能缺。</summary>
internal sealed record OfflineAccountName(string? PersonaName, string? AccountName);

/// <summary>
/// 离线把 SteamID64 映射为可读账号名，用于 CS2 设置同步「来源账号」下拉：
/// 来源账号来自 userdata 目录扫描，多数不在应用历史里，只显示 SteamID64 很难辨认。
/// 名称来源按优先级逐字段合并（先到先得）：
///   1. Steam config/loginusers.vdf——本机登录过且记住的账号（昵称+账号名）；
///   2. 应用缓存 cached-login.json——上号器缓存过的本机账号；
///   3. Steam config/config.vdf Accounts——只有登录账号名；
///   4. userdata/&lt;accountId&gt;/config/localconfig.vdf——好友区里自己条目的昵称，
///      兜底覆盖已被 loginusers.vdf 清掉的老账号（文件可达数 MB，仅对仍无昵称的账号解析）。
/// 全程 best-effort：任何来源读不动只记日志跳过，绝不让设置页刷新失败。
/// </summary>
internal static class SteamAccountNameService
{
    public static IReadOnlyDictionary<string, OfflineAccountName> BuildOfflineNames(
        SteamPaths paths, IReadOnlyList<Cs2SettingsSource> sources)
    {
        var map = new Dictionary<string, OfflineAccountName>(StringComparer.OrdinalIgnoreCase);

        Merge(map, () => SteamConfigService.GetLoginUsersAccounts(
                VdfDocument.LoadOrEmpty(Path.Combine(paths.ConfigPath, "loginusers.vdf")))
            .Select(account =>
            {
                // 上号流程写 loginusers.vdf 时会把 PersonaName 占位成登录名（见 UpdateLoginUsersVdf），
                // 不算真昵称：置空让 cached-login / localconfig 兜底有机会补上真的。
                // 真昵称恰好等于登录名的账号不受影响——展示层会再回退到账号名，结果一样。
                if (string.Equals(account.PersonaName, account.AccountName, StringComparison.OrdinalIgnoreCase))
                {
                    account.PersonaName = null;
                }

                return account;
            }), "loginusers.vdf");
        Merge(map, () => new SteamLoginCacheService().LoadAll(), "cached-login.json");
        Merge(map, () => SteamConfigService.GetConfigAccounts(
            Path.Combine(paths.ConfigPath, "config.vdf")), "config.vdf");

        foreach (var source in sources)
        {
            map.TryGetValue(source.SteamId64, out var existing);
            if (!string.IsNullOrWhiteSpace(existing?.PersonaName))
            {
                continue;
            }

            var persona = TryReadLocalConfigPersona(paths.UserdataPath, source.AccountId);
            if (!string.IsNullOrWhiteSpace(persona))
            {
                map[source.SteamId64] = new OfflineAccountName(persona, existing?.AccountName);
            }
        }

        return map;
    }

    private static void Merge(
        Dictionary<string, OfflineAccountName> map,
        Func<IEnumerable<CachedSteamLoginAccount>> read,
        string sourceName)
    {
        List<CachedSteamLoginAccount> accounts;
        try
        {
            accounts = read().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取账号名来源失败（{sourceName}）：{ex.Message}");
            return;
        }

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.SteamId))
            {
                continue;
            }

            map.TryGetValue(account.SteamId, out var existing);
            map[account.SteamId] = new OfflineAccountName(
                FirstNonEmpty(existing?.PersonaName, account.PersonaName),
                FirstNonEmpty(existing?.AccountName, account.AccountName));
        }
    }

    /// <summary>读 userdata/&lt;accountId&gt;/config/localconfig.vdf 里自己的昵称；读不到返回 null。</summary>
    private static string? TryReadLocalConfigPersona(string? userdataPath, uint accountId)
    {
        if (string.IsNullOrWhiteSpace(userdataPath))
        {
            return null;
        }

        var path = Path.Combine(userdataPath, accountId.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(path))
        {
            return null;
        }

        // LoadOrEmpty 已吞掉 IO/解析异常并记日志，这里只需处理“结构不是预期形状”。
        var document = VdfDocument.LoadOrEmpty(path);
        if (VdfDocument.GetObject(document, "UserLocalConfigStore") is not { } store ||
            VdfDocument.GetObject(store, "friends") is not { } friends)
        {
            return null;
        }

        // 自己的条目：friends/<accountId>/name；部分 Steam 版本在 friends 下直接写 PersonaName。
        if (VdfDocument.GetObject(friends, accountId.ToString()) is { } self &&
            VdfDocument.GetString(self, "name") is { } name && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return VdfDocument.GetString(friends, "PersonaName");
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;
}
