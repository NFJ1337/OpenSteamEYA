using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「系统代理」接管（接管方式选「系统代理」时使用）：
///   · 把当前用户的 WinINET 代理（HKCU\...\Internet Settings 的 ProxyEnable / ProxyServer / ProxyOverride）
///     指向本程序内核的本地端口，再通知系统立即生效（InternetSetOption），浏览器等程序无需重启；
///   · 第一次写之前先把原值备份到 vpn\system-proxy-backup.txt，断开连接 / 退出程序时原样还原；
///   · 只动当前用户、只动这几个值：不碰 WinHTTP、不碰别的注册表键。
/// 异常退出的保护：<see cref="RestoreIfApplied"/> 在程序启动时也会被调用——若上次没还原成功，
/// 系统代理会一直指向已经结束的内核（表现为上不了网），所以启动时必须先兜底还原。
/// </summary>
internal static partial class SystemProxyService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // WinINET：改完注册表要通知系统，否则已运行的程序（浏览器等）会继续用旧设置。
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>备份里代表「这个值本来就不存在」的标记（空字符串是合法值，不能混用）。</summary>
    private const string MissingValue = "<missing>";

    private static string BackupPath => Path.Combine(VpnCoreService.DataFolder, "system-proxy-backup.txt");

    /// <summary>系统代理当前是否由本程序接管（存在待还原的备份）。</summary>
    public static bool IsApplied
    {
        get
        {
            try
            {
                return File.Exists(BackupPath);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 把系统代理指向本程序内核端口（127.0.0.1:port）。重复调用只更新端口，不会覆盖最初的备份。
    /// <paramref name="corePid"/> 会被记进备份文件：看门狗据此判断「这份备份是不是本次内核会话留下的」，
    /// 避免旧会话的看门狗把新会话的接管误还原掉。
    /// </summary>
    public static void Apply(int port, int corePid = 0)
    {
        if (port <= 0)
        {
            throw new InvalidOperationException("系统代理没有可用的本地端口。");
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("打不开 WinINET 代理注册表键。");

        EnsureBackup(key, corePid);

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
        // 本机地址不走代理：否则访问 127.0.0.1 的调试服务也会被塞进内核。
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        // 自动配置脚本优先级高于固定代理，留着会让系统代理形同虚设。
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

        NotifySystem();
        AppLog.Info($"已接管系统代理：127.0.0.1:{port}");
    }

    /// <summary>
    /// 这份备份是不是 <paramref name="corePid"/> 这次内核会话留下的。
    /// 看门狗用它做「该不该还原」的判据：旧会话的看门狗不该动新会话的接管。
    /// </summary>
    public static bool IsAppliedFor(int corePid)
    {
        if (!IsApplied)
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadAllLines(BackupPath))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && parts[0] == "corepid")
                {
                    return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                           pid == corePid;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取系统代理备份失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>还原成接管前的系统代理设置；没有备份（= 不是我们改的）时什么也不做。</summary>
    public static void RestoreIfApplied()
    {
        if (!IsApplied)
        {
            return;
        }

        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(BackupPath))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2)
                {
                    values[parts[0]] = parts[1];
                }
            }

            using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
            {
                if (key is not null)
                {
                    RestoreValue(key, "ProxyEnable", values, "enable", RegistryValueKind.DWord);
                    RestoreValue(key, "ProxyServer", values, "server", RegistryValueKind.String);
                    RestoreValue(key, "ProxyOverride", values, "override", RegistryValueKind.String);
                    RestoreValue(key, "AutoConfigURL", values, "autoconfig", RegistryValueKind.String);
                }
            }

            File.Delete(BackupPath);
            NotifySystem();
            AppLog.Info("已还原系统代理设置。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"还原系统代理失败：{ex.Message}");
        }
    }

    /// <summary>首次接管前把原值写进备份文件；已有备份就保留（多次开关也不会把我们的值当成原始值存下来）。</summary>
    private static void EnsureBackup(RegistryKey key, int corePid)
    {
        if (IsApplied)
        {
            return;
        }

        var lines = new[]
        {
            $"enable\t{ReadValue(key, "ProxyEnable")}",
            $"server\t{ReadValue(key, "ProxyServer")}",
            $"override\t{ReadValue(key, "ProxyOverride")}",
            $"autoconfig\t{ReadValue(key, "AutoConfigURL")}",
            $"corepid\t{corePid.ToString(CultureInfo.InvariantCulture)}"
        };

        Directory.CreateDirectory(VpnCoreService.DataFolder);
        File.WriteAllLines(BackupPath, lines, new System.Text.UTF8Encoding(false));
    }

    private static string ReadValue(RegistryKey key, string name) =>
        key.GetValue(name) is { } value
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : MissingValue;

    private static void RestoreValue(
        RegistryKey key,
        string valueName,
        IReadOnlyDictionary<string, string> backup,
        string backupKey,
        RegistryValueKind kind)
    {
        if (!backup.TryGetValue(backupKey, out var raw) || raw == MissingValue)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        if (kind == RegistryValueKind.DWord &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            key.SetValue(valueName, number, RegistryValueKind.DWord);
            return;
        }

        key.SetValue(valueName, raw, RegistryValueKind.String);
    }

    /// <summary>通知 WinINET 重读代理设置（两个选项都要发，只发一个部分程序不会刷新）。</summary>
    private static void NotifySystem()
    {
        InternetSetOption(nint.Zero, InternetOptionSettingsChanged, nint.Zero, 0);
        InternetSetOption(nint.Zero, InternetOptionRefresh, nint.Zero, 0);
    }

    // 必须写全 EntryPoint：LibraryImport（源生成）默认按 ExactSpelling 精确匹配，
    // 而 wininet.dll 只导出 InternetSetOptionA / InternetSetOptionW，没有无后缀的名字。
    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);
}