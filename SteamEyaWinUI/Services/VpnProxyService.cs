using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 本程序专用的 VPN 直连（Clash Verge）：
///   · 自动探测 clash-verge.exe（注册表卸载项 + 各固定磁盘常见目录），也允许用户手动指定；
///   · 「仅本程序」：只把本程序访问 GitHub（更新检查 / 下载安装包）的那两个 HttpClient 指向 VPN 的
///     本地代理端口（Clash 的混合端口）。
///     —— 不改系统代理（WinINET/WinHTTP）、不设 HttpClient.DefaultProxy、不动注册表，
///        所以对本机其它软件零影响；开关关闭时代理返回 null，本程序直接直连，
///        连系统里开着的加速器/系统代理也不会被本程序采用（handler.Proxy 一旦赋值就不再走系统代理）；
///     —— Steam 相关的连接（CM/WebSocket 等）不走这里，完全不受影响；
///   · 代理地址在每次请求时实时读取设置（见 <see cref="DynamicProxy"/>），开关改完立即生效。
/// </summary>
internal static class VpnProxyService
{
    /// <summary>Clash Verge 常见进程名（用于判断是否已在运行）。</summary>
    // 兼容 Clash Verge / Clash Verge Rev，以及它们实际拉起的 cores（mihomo 内核进程名各不相同）。
    private static readonly string[] ProcessNames =
    [
        "clash-verge", "clash-verge-rev", "clash verge",
        "verge-mihomo", "mihomo", "clash-meta", "clash-win64", "clash"
    ];

    /// <summary>可执行文件候选名（Clash Verge / Clash Verge Rev 的差异）。</summary>
    private static readonly string[] ExecutableNames = ["clash-verge.exe", "clash-verge-rev.exe", "clash verge.exe"];

    /// <summary>常见混合代理端口（与脚本 scripts\publish-source.ps1 的探测列表一致）。</summary>
    private static readonly int[] CommonProxyPorts = [7897, 7890, 7899, 10809, 10808, 1080, 8889, 2080];

    /// <summary>运行期动态代理：每个请求现读设置，开关/端口改动无需重建 HttpClient。</summary>
    private static readonly DynamicProxy SharedProxy = new();

    /// <summary>程序退出时收掉内核（异常一律吞掉，退出路径不能抛）。</summary>
    public static void SafeStopCore()
    {
        try
        {
            VpnCoreService.Stop();
        }
        catch
        {
            // 退出路径：忽略一切异常
        }
    }

    /// <summary>把「动态代理」挂到某个 HttpClientHandler 上（挂一次即可，之后随设置变化自动生效）。</summary>
    public static void Attach(HttpClientHandler handler)
    {
        handler.UseProxy = true;
        handler.Proxy = SharedProxy;
    }

    /// <summary>当前是否启用本程序走 VPN。</summary>
    public static bool IsEnabled => AppState.SettingsService.Load().VpnProxyEnabled;

    /// <summary>探测到的端口缓存（异步探测后写入，请求路径只读缓存，绝不在 UI 线程阻塞）。</summary>
    private static int _cachedPort;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>当前实际使用的代理地址；未启用、或端口未知时返回 null（= 直连）。</summary>
    public static string? CurrentProxyAddress()
    {
        var settings = AppState.SettingsService.Load();
        if (!settings.VpnProxyEnabled)
        {
            return null;
        }

        // 优先用「订阅内核」自带的端口，其次用户配置的端口，最后用探测缓存（缓存由 UI 侧异步刷新）。
        var port = VpnCoreService.ConfiguredPort;
        if (port <= 0)
        {
            port = settings.VpnProxyPort > 0 ? settings.VpnProxyPort : CachedPort;
        }

        return port <= 0 ? null : $"http://127.0.0.1:{port}";
    }

    /// <summary>最近一次探测到的端口（0 = 还没探到）；请求路径只读它，不做任何阻塞探测。</summary>
    public static int CachedPort => _cachedPort;

    /// <summary>
    /// 异步探测端口（结果写入缓存），必须从 UI 之外/异步调用。
    /// 先认本程序内核自己的端口：它起来后就是唯一正确的答案；下面的常见端口只是
    /// 「用户自己开着 Clash Verge」时的兜底（内核端口不在这份常见列表里）。
    /// </summary>
    public static async Task<int> RefreshProxyPortAsync(int timeoutPerPortMs = 300)
    {
        var corePort = VpnCoreService.ConfiguredPort;
        if (corePort > 0 && await ProbeAsync(corePort, Math.Max(timeoutPerPortMs, 500)))
        {
            _cachedPort = corePort;
            _cachedAt = DateTime.Now;
            return corePort;
        }

        var tasks = CommonProxyPorts.Select(port => ProbeAsync(port, timeoutPerPortMs)).ToArray();
        var results = await Task.WhenAll(tasks);
        var found = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i])
            {
                found = CommonProxyPorts[i];
                break;
            }
        }

        _cachedPort = found;
        _cachedAt = DateTime.Now;
        return found;
    }

    /// <summary>同步探测（只给后台/非 UI 场景用；UI 请用 RefreshProxyPortAsync，避免卡住界面）。</summary>
    public static int? DetectProxyPort()
    {
        var corePort = VpnCoreService.ConfiguredPort;
        if (corePort > 0 && ProbeAsync(corePort, 400).GetAwaiter().GetResult())
        {
            _cachedPort = corePort;
            _cachedAt = DateTime.Now;
            return corePort;
        }

        foreach (var port in CommonProxyPorts)
        {
            if (ProbeAsync(port, 220).GetAwaiter().GetResult())
            {
                _cachedPort = port;
                _cachedAt = DateTime.Now;
                return port;
            }
        }

        _cachedPort = 0;
        _cachedAt = DateTime.Now;
        return null;
    }

    /// <summary>端口是否可连（异步、带超时，不阻塞线程）。</summary>
    public static async Task<bool> ProbeAsync(int port, int timeoutMs = 300)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
    /// <summary>持久化路径是否仍是可用的可执行文件。</summary>
    public static bool IsValidExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前该用的 exe 路径：优先用户指定（仍有效），否则自动探测（探测到就写回设置）。</summary>
    public static string? ResolveExecutablePath(bool persistDetected = true)
    {
        var settings = AppState.SettingsService.Load();
        if (IsValidExecutable(settings.VpnExecutablePath))
        {
            return settings.VpnExecutablePath;
        }

        var detected = AutoDetectExecutablePath();
        if (detected is not null && persistDetected)
        {
            settings.VpnExecutablePath = detected;
            AppState.SettingsService.Save(settings);
            AppLog.Info($"已自动探测并记录 VPN（Clash Verge）路径：\"{detected}\"");
        }

        return detected;
    }

    /// <summary>
    /// 自动探测 clash-verge.exe：
    /// 1) 注册表卸载项（DisplayName 含 Clash Verge）的 InstallLocation / DisplayIcon；
    /// 2) 各固定磁盘的常见目录（%ProgramFiles%、%LOCALAPPDATA%\Programs、X:\APP\… 等）。
    /// 只做存在性判断，不启动任何进程。
    /// </summary>
    public static string? AutoDetectExecutablePath()
    {
        AppLog.Info("开始自动探测 VPN（Clash Verge）路径。");

        foreach (var candidate in EnumerateCandidates())
        {
            if (IsValidExecutable(candidate))
            {
                AppLog.Info($"自动探测选定 VPN 路径：\"{candidate}\"");
                return candidate;
            }
        }

        AppLog.Warn("未能自动探测到 clash-verge.exe。");
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        // 1) 注册表：卸载项里的安装位置 / 显示图标
        foreach (var key in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                 })
        {
            using var root = Registry.LocalMachine.OpenSubKey(key) ?? Registry.CurrentUser.OpenSubKey(key);
            if (root is null)
            {
                continue;
            }

            foreach (var subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                if (sub?.GetValue("DisplayName") is not string displayName ||
                    !displayName.Contains("clash", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sub.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                {
                    foreach (var name in ExecutableNames)
                    {
                        yield return Path.Combine(location, name);
                    }
                }

                if (sub.GetValue("DisplayIcon") is string icon)
                {
                    yield return icon.Trim('"').Split(',')[0];
                }
            }
        }

        // 2) 固定磁盘上的常见目录
        var folders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            folders.Add(Path.Combine(drive.RootDirectory.FullName, "APP"));
            folders.Add(drive.RootDirectory.FullName);
        }

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var sub in new[] { "Clash Verge", "Clash Verge Rev", "ClashVerge", "clash-verge" })
            {
                foreach (var name in ExecutableNames)
                {
                    yield return Path.Combine(folder, sub, name);
                }
            }

            // 目录本身就直接放着 exe 的情况
            foreach (var name in ExecutableNames)
            {
                yield return Path.Combine(folder, name);
            }
        }
    }

    /// <summary>Clash Verge 相关目录候选（安装目录等）：给内核/规则数据探测复用。</summary>
    public static IEnumerable<string> AutoDetectClashVergeFolders()
    {
        var folders = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        folders.Add(Path.Combine(programFiles, "Clash Verge"));
        folders.Add(Path.Combine(programFilesX86, "Clash Verge"));
        folders.Add(Path.Combine(localAppData, "Programs", "Clash Verge"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            folders.Add(Path.Combine(drive.RootDirectory.FullName, "APP", "Clash Verge"));
            folders.Add(Path.Combine(drive.RootDirectory.FullName, "Clash Verge"));
        }

        return folders.Where(Directory.Exists);
    }

    /// <summary>VPN 程序是否已在运行。</summary>
    public static bool IsRunning() =>
        ProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);

    /// <summary>启动 VPN（已在运行则直接返回成功）。</summary>
    public static bool TryLaunch(out string? error)
    {
        error = null;
        if (IsRunning())
        {
            return true;
        }

        var path = ResolveExecutablePath();
        if (!IsValidExecutable(path))
        {
            error = Loc.T("Settings_Vpn_Error_NotFound");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path!,
                WorkingDirectory = Path.GetDirectoryName(path!) ?? string.Empty,
                UseShellExecute = true
            });
            AppLog.Info($"已启动 VPN：\"{path}\"");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Warn($"启动 VPN 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>每次请求现读设置的动态代理；返回 null 表示该请求直连。</summary>
    private sealed class DynamicProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            var address = CurrentProxyAddress();
            return address is null ? null : new Uri(address);
        }

        public bool IsBypassed(Uri host) => false;
    }
}
