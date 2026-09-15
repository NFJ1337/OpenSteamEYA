using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http;
using SteamEyaWinUI.Localization;
using System.Text;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「VPN 直连」的订阅内核（mihomo / Clash 内核）——用户不需要启动 Clash 软件：
///   1) 用订阅链接拉取配置（mihomo 的 config.yaml 格式，订阅本身即一份完整配置）；
///   2) 改写成「本程序专用」：只监听 127.0.0.1、独立端口、关掉外部控制口与局域网；
///   3) 用 Clash Verge 自带的内核（verge-mihomo.exe，自动探测）在无窗口模式下拉起；
///   4) 接管方式由用户在设置页选（见 <see cref="TakeoverSystem"/>/<see cref="TakeoverTun"/>）：
///      · system = 系统代理：只写当前用户的 WinINET 代理，指向内核端口，全系统流量都走代理；
///      · tun = 虚拟网卡：把内核配成 TUN 接管全部流量（需要管理员权限 + wintun.dll）；
///      另外本程序自己访问 GitHub 的请求也一并走这个端口；
///   5) 代理模式（规则 / 全局）同样由用户在设置页选，写进内核配置的 mode。
/// 关闭开关或程序退出时：结束我们自己启动的内核，并还原被接管的系统代理。
/// 与 Clash Verge 互不干扰：独立端口、独立工作目录、不改 Clash Verge 自己的配置。
/// </summary>
internal static class VpnCoreService
{
    /// <summary>本程序专用内核端口（避开 Clash Verge 默认的 7897，避免与其同时运行冲突）。</summary>
    public const int DefaultPort = 17897;

    /// <summary>接管方式：系统代理（把 Windows 的 WinINET 代理指向本程序内核端口）。</summary>
    public const string TakeoverSystem = "system";

    /// <summary>接管方式：虚拟网卡（TUN；需要管理员权限与 wintun.dll）。</summary>
    public const string TakeoverTun = "tun";

    /// <summary>代理模式：规则（按订阅 rules 分流）。</summary>
    public const string ModeRule = "rule";

    /// <summary>代理模式：全局（所有流量走选中的节点，不看规则）。</summary>
    public const string ModeGlobal = "global";

    /// <summary>TUN 模式必需的文件名（mihomo 从这里加载虚拟网卡驱动）。</summary>
    private const string WintunDllName = "wintun.dll";

    /// <summary>我们自己的虚拟网卡名（不用 mihomo 默认的 "Meta"，避免与 Clash Verge 等撞名）。</summary>
    private const string TunDeviceName = "SteamEYA";

    /// <summary>提权启动时内核自己的日志（UseShellExecute 下没法重定向 stdout，只能让 cmd 转发到文件）。</summary>
    private static string CoreLogPath => Path.Combine(DataFolder, "core.log");

    /// <summary>提权启动用的批处理（把内核日志转存到 <see cref="CoreLogPath"/>）。</summary>
    private static string CoreLauncherPath => Path.Combine(DataFolder, "start-core.cmd");

    /// <summary>设置里的接管方式字符串 → 合法值（未知一律回落到系统代理）。</summary>
    public static string NormalizeTakeover(string? value) =>
        string.Equals(value, TakeoverTun, StringComparison.OrdinalIgnoreCase) ? TakeoverTun : TakeoverSystem;

    /// <summary>设置里的模式字符串 → 合法值（未知一律回落到规则模式）。</summary>
    public static string NormalizeMode(string? value) =>
        string.Equals(value, ModeGlobal, StringComparison.OrdinalIgnoreCase) ? ModeGlobal : ModeRule;

    /// <summary>当前生效的接管方式（每次现读设置，改完立即生效）。</summary>
    public static string CurrentTakeover => NormalizeTakeover(AppState.SettingsService.Load().VpnTakeover);

    /// <summary>当前生效的代理模式（每次现读设置，改完立即生效）。</summary>
    public static string CurrentMode => NormalizeMode(AppState.SettingsService.Load().VpnMode);

    /// <summary>内核可执行文件候选名（Clash Verge 自带的 mihomo 内核）。</summary>
    private static readonly string[] CoreExecutableNames =
        ["verge-mihomo.exe", "verge-mihomo-alpha.exe", "mihomo.exe", "clash-meta.exe", "clash.exe"];

    /// <summary>需要从 Clash Verge 借用的规则数据文件（GEOSITE/GEOIP 规则要用）。</summary>
    private static readonly string[] GeoFileNames = ["geosite.dat", "geoip.dat", "Country.mmdb"];

    /// <summary>
    /// 改写成我们的监听参数时，需要先抹掉的同名顶层键（连同它们的缩进子行一起丢）。
    /// dns 不在这里：只有 TUN 模式才连 DNS 一起接管（见 <see cref="RewriteConfig"/>）。
    /// </summary>
    private static readonly string[] ManagedTopLevelKeys =
        ["mixed-port", "port", "socks-port", "allow-lan", "bind-address", "external-controller", "secret", "log-level", "mode", "tun"];

    // 订阅站普遍按 User-Agent 区分返回格式：带 Clash 客户端 UA 才返回 Clash/mihomo 的 YAML 配置，
    // 否则给的是 base64 的 vmess/ss 节点列表（内核读不了）。所以固定用 Clash 内核的 UA。
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Process? _coreProcess;

    /// <summary>内核 PID 记录文件：程序重启/异常退出后仍能清理残留内核（不依赖 WMI）。</summary>
    private static string PidFilePath => Path.Combine(DataFolder, "core.pid");

    public static string DataFolder => Path.Combine(AppPaths.DataRoot, "vpn");
    public static string ConfigPath => Path.Combine(DataFolder, "config.yaml");

    /// <summary>订阅原文的本地副本：换节点 / 换模式时据此重写配置，不必为了改一行再下一次订阅。</summary>
    private static string SubscriptionPath => Path.Combine(DataFolder, "subscription.yaml");

    /// <summary>内核本地控制口（= 内核端口 + 1）：只监听 127.0.0.1，用来查节点延迟。</summary>
    public static int ControllerPort => ConfiguredPort + 1;

    /// <summary>测延迟用的探测地址（Clash 生态默认的 204 探测点）。</summary>
    private const string DelayTestUrl = "http://www.gstatic.com/generate_204";

    /// <summary>查控制口用的 HttpClient（本地回环，超时给宽一点，节点多时一次请求要测完所有节点）。</summary>
    private static readonly HttpClient ControllerClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly Dictionary<string, int> NodeDelayCache = new(StringComparer.Ordinal);

    /// <summary>最近一次测到的节点延迟（节点名 → 毫秒）；失败/未测的节点不在里面。</summary>
    public static IReadOnlyDictionary<string, int> NodeDelays
    {
        get
        {
            lock (NodeDelayCache)
            {
                return new Dictionary<string, int>(NodeDelayCache, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>控制口 secret：没有就生成一个存进设置（本机其它程序不读我们配置就拿不到）。</summary>
    private static string ControllerSecret()
    {
        var settings = AppState.SettingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.VpnControllerSecret))
        {
            return settings.VpnControllerSecret!;
        }

        settings.VpnControllerSecret = Guid.NewGuid().ToString("N");
        AppState.SettingsService.Save(settings);
        return settings.VpnControllerSecret!;
    }

    /// <summary>
    /// 批量测节点延迟：一次请求让内核把它那个分流组里的所有节点都测一遍
    /// （失败的节点不会出现在返回里，调用方按「—」显示）。
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> MeasureNodeDelaysAsync(
        IReadOnlyList<string> nodes,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_DelayNoCore"));
        }

        var url = $"http://127.0.0.1:{ControllerPort}/group/{Uri.EscapeDataString(GitHubGroupName)}/delay" +
                  $"?timeout={timeoutMs}&url={Uri.EscapeDataString(DelayTestUrl)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ControllerSecret()}");

        using var response = await ControllerClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        var measured = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(payload))
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.TryGetInt32(out var delay) && delay > 0)
                {
                    measured[property.Name] = delay;
                }
            }
        }

        lock (NodeDelayCache)
        {
            // 本次没返回的节点就是测不通：把旧值清掉，别让上一次的数字留着误导人。
            foreach (var node in nodes)
            {
                if (measured.TryGetValue(node, out var delay))
                {
                    NodeDelayCache[node] = delay;
                }
                else
                {
                    NodeDelayCache.Remove(node);
                }
            }

            return new Dictionary<string, int>(NodeDelayCache, StringComparer.Ordinal);
        }
    }

    /// <summary>本程序内核监听的端口：设置里显式配过就用它，否则用默认端口。</summary>
    public static int ConfiguredPort
    {
        get
        {
            var port = AppState.SettingsService.Load().VpnCorePort;
            return port > 0 ? port : DefaultPort;
        }
    }

    public static bool HasSubscription =>
        !string.IsNullOrWhiteSpace(AppState.SettingsService.Load().VpnSubscriptionUrl);

    /// <summary>当前选中的节点名；null = 自动（按订阅里的顺序）。</summary>
    public static string? CurrentNode
    {
        get
        {
            var node = AppState.SettingsService.Load().VpnNode;
            return string.IsNullOrWhiteSpace(node) ? null : node.Trim();
        }
    }

    /// <summary>内核是否在运行（本进程启动的，或上次残留的同目录内核）。</summary>
    public static bool IsRunning => _coreProcess is { HasExited: false } || FindCoreProcess() is not null;

    /// <summary>已启动内核的 PID（读 PID 文件）；没有或进程已退出返回 0。</summary>
    public static int TrackedCorePid
    {
        get
        {
            try
            {
                if (!File.Exists(PidFilePath) ||
                    !int.TryParse(File.ReadAllText(PidFilePath).Trim(), out var pid) || pid <= 0)
                {
                    return 0;
                }

                using var process = Process.GetProcessById(pid);
                return process.HasExited ? 0 : pid;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 下载订阅并落盘为内核配置。订阅即 mihomo 配置（clash 格式 YAML），
    /// 这里只覆盖监听相关的顶层键，其余（代理、分组、规则）原样保留。
    /// </summary>
    public static async Task RefreshSubscriptionAsync(string subscriptionUrl, CancellationToken cancellationToken = default)
    {
        var url = subscriptionUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_SubscriptionInvalid"));
        }

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var yaml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(yaml) ||
            (!yaml.Contains("proxies:", StringComparison.Ordinal) &&
             !yaml.Contains("proxy-providers:", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_SubscriptionInvalid"));
        }

        Directory.CreateDirectory(DataFolder);
        // 留一份原始订阅：切节点 / 切模式时直接拿它重写，省一次下载。
        await File.WriteAllTextAsync(SubscriptionPath, yaml, new UTF8Encoding(false), cancellationToken);
        var rewritten = RewriteConfig(yaml, ConfiguredPort, CurrentTakeover, CurrentMode, CurrentNode);
        await File.WriteAllTextAsync(ConfigPath, rewritten, new UTF8Encoding(false), cancellationToken);
        EnsureGeoFiles();
        AppLog.Info(
            $"VPN 订阅已保存：{ConfigPath}（来源 {yaml.Length} 字符 → 配置 {rewritten.Length} 字符，" +
            $"端口 {ConfiguredPort}，方式 {CurrentTakeover}，模式 {CurrentMode}）");
    }

    /// <summary>确保内核在跑：必要时拉订阅、写配置、启动内核并等端口就绪。</summary>
    public static async Task EnsureRunningAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning && await VpnProxyService.ProbeAsync(ConfiguredPort, 400))
            {
                return;
            }

            if (!HasSubscription)
            {
                throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_SubscriptionRequired"));
            }

            // 配置不存在，或不是「当前接管方式 / 模式 / 端口 / 节点」生成的 → 重写一份。
            if (NeedsRewrite(ConfiguredPort, CurrentTakeover, CurrentMode, CurrentNode))
            {
                progress?.Report(Loc.T("Settings_Vpn_Status_ApplyingConfig"));
                // 先试本地订阅副本（换节点/换模式不必重新下载）；没有副本才联网拉订阅。
                if (!await RewriteFromLocalSubscriptionAsync(cancellationToken))
                {
                    progress?.Report(Loc.T("Settings_Vpn_Status_FetchingSubscription"));
                    await RefreshSubscriptionAsync(AppState.SettingsService.Load().VpnSubscriptionUrl!, cancellationToken);
                }
            }

            var core = ResolveCoreExecutable()
                ?? throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_CoreNotFound"));

            // TUN 要 wintun.dll，而且内核必须以管理员权限运行（创建虚拟网卡）：先备好驱动，再决定是否提权启动。
            var tunMode = string.Equals(CurrentTakeover, TakeoverTun, StringComparison.OrdinalIgnoreCase);
            if (tunMode)
            {
                progress?.Report(Loc.T("Settings_Vpn_Status_PreparingTun"));
                EnsureWintunDll();
            }

            progress?.Report(Loc.T("Settings_Vpn_Status_StartingCore"));
            StartCore(core, elevate: tunMode && !IsAdministrator());

            // 等端口起来（最多 ~20 秒；内核首次启动要下载 rule-providers，慢一点也正常）。
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (await VpnProxyService.ProbeAsync(ConfiguredPort, 500))
                {
                    // 端口通 ≠ TUN 建好了：内核可能报「设备已存在」而虚拟网卡始终没起来，
                    // 此时路由已被改了一半（表现为整机断网），所以必须确认网卡真的在，否则立刻断开。
                    if (tunMode && !await WaitForTunAdapterAsync(cancellationToken))
                    {
                        Stop();
                        throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_TunAdapterFailed"));
                    }

                    AppLog.Info($"VPN 内核已就绪：127.0.0.1:{ConfiguredPort}");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken);
            }

            throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_CoreTimeout"));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>结束本程序启动的内核（含上次残留的同目录内核）。</summary>
    public static void Stop()
    {
        try
        {
            if (_coreProcess is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                // 等进程真的退出再往下走：TUN 模式上网卡要几百毫秒才释放，
                // 立刻起新内核会撞上同名设备（ERROR_ALREADY_EXISTS）。
                if (!process.WaitForExit(5000))
                {
                    AppLog.Warn("VPN 内核在 5 秒内没有退出，继续按已结束处理。");
                }

                AppLog.Info("已结束本程序启动的 VPN 内核。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"结束 VPN 内核失败：{ex.Message}");
        }
        finally
        {
            _coreProcess = null;
        }

        // 清掉上次残留的内核（程序异常退出时 _coreProcess 拿不到，用 PID 文件兜底）。
        var trackedPid = TrackedCorePid;
        if (trackedPid > 0)
        {
            try
            {
                using var stray = Process.GetProcessById(trackedPid);
                stray.Kill(entireProcessTree: true);
                AppLog.Info($"已清理残留的 VPN 内核进程（PID {trackedPid}）。");
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        try
        {
            if (File.Exists(PidFilePath))
            {
                File.Delete(PidFilePath);
            }
        }
        catch
        {
            // 删除失败无副作用
        }

        // 内核停了系统代理就成了死代理（全网打不开），所以断开路径一律还原成接管前的设置。
        SystemProxyService.RestoreIfApplied();
    }
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("clash-verge/v2.0.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>
    /// 启动内核。<paramref name="elevate"/> = true（TUN 模式且当前不是管理员）时走 UAC 提权启动：
    /// 提权后拿不到子进程的 stdout/stderr（UseShellExecute 下不能重定向），内核日志只靠这里的启动记录与
    /// 端口探测结果；用户点了 UAC 的「否」会抛 <see cref="InvalidOperationException"/>，由调用方提示。
    /// </summary>
    private static void StartCore(string corePath, bool elevate)
    {
        Directory.CreateDirectory(DataFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = corePath,
            Arguments = $"-d \"{DataFolder}\" -f \"{ConfigPath}\"",
            WorkingDirectory = DataFolder,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate
        };

        if (elevate)
        {
            // 提权后拿不到 stdout，改由 cmd 把输出转存到 core.log——否则 TUN 出问题永远查不到原因。
            File.WriteAllText(
                CoreLauncherPath,
                $"@echo off\r\n\"{corePath}\" -d \"{DataFolder}\" -f \"{ConfigPath}\" >> \"{CoreLogPath}\" 2>&1\r\n",
                new UTF8Encoding(false));
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.Arguments = $"/c \"{CoreLauncherPath}\"";
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!elevate)
        {
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    AppLog.Info("[vpn-core] " + args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    AppLog.Warn("[vpn-core] " + args.Data);
                }
            };
        }

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗里点了「否」。
            throw new InvalidOperationException(Loc.T("Settings_Vpn_Error_TunElevationCancelled"), ex);
        }

        if (!elevate)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        _coreProcess = process;
        try
        {
            File.WriteAllText(PidFilePath, process.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入 VPN 内核 PID 失败：{ex.Message}");
        }

        AppLog.Info($"已启动 VPN 内核：\"{corePath}\"（PID {process.Id}，端口 {ConfiguredPort}，方式 {CurrentTakeover}，模式 {CurrentMode}）");
    }

    /// <summary>等虚拟网卡就绪（最多 ~15 秒）：TUN 建不起来时返回 false，由调用方断开内核。</summary>
    private static async Task<bool> WaitForTunAdapterAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (IsTunAdapterUp())
            {
                AppLog.Info($"虚拟网卡已就绪：{TunDeviceName}");
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
        }

        AppLog.Warn($"等待虚拟网卡（{TunDeviceName}）超时：内核可能没能创建 TUN 适配器。");
        return false;
    }

    /// <summary>
    /// 系统里是否已有我们这张处于启用状态的虚拟网卡。只认我们自己的设备名：
    /// 若换成宽松匹配（比如「描述里有 Wintun/Tunnel」），Clash Verge 等其它 TUN 程序
    /// 的网卡会被误判成「我们的 TUN 已就绪」，那就等于把问题藏起来了。
    /// </summary>
    private static bool IsTunAdapterUp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                nic.Name.Equals(TunDeviceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前进程是否已提权（TUN 需要管理员权限才能创建虚拟网卡）。</summary>
    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TUN 模式需要 wintun.dll：数据目录里已有就用现成的，否则从 Clash Verge / 内核目录找一份复制过来。
    /// 找不到直接抛错——静默降级成「看起来连上了但其实没接管」比失败更坑。
    /// </summary>
    private static void EnsureWintunDll()
    {
        var target = Path.Combine(DataFolder, WintunDllName);
        if (File.Exists(target))
        {
            return;
        }

        foreach (var source in EnumerateWintunCandidates())
        {
            try
            {
                if (!File.Exists(source))
                {
                    continue;
                }

                Directory.CreateDirectory(DataFolder);
                File.Copy(source, target, overwrite: true);
                AppLog.Info($"已复制 wintun.dll：{source} → {target}");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"复制 wintun.dll 失败（{source}）：{ex.Message}");
            }
        }

        throw new InvalidOperationException(Loc.Tf("Settings_Vpn_Error_WintunMissing_Format", DataFolder));
    }

    /// <summary>wintun.dll 的候选位置：本程序目录、内核目录（含 resources）、Clash Verge 安装目录、服务数据目录。</summary>
    private static IEnumerable<string> EnumerateWintunCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, WintunDllName);

        if (ResolveCoreExecutable() is { } core && Path.GetDirectoryName(core) is { Length: > 0 } coreFolder)
        {
            yield return Path.Combine(coreFolder, WintunDllName);
            yield return Path.Combine(coreFolder, "resources", WintunDllName);
        }

        foreach (var folder in VpnProxyService.AutoDetectClashVergeFolders())
        {
            yield return Path.Combine(folder, WintunDllName);
            yield return Path.Combine(folder, "resources", WintunDllName);
        }

        // Clash Verge 的服务模式会自己留一份内核 + 驱动的工作目录。
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(programData, "clash-verge-service", WintunDllName);
        yield return Path.Combine(programData, "Clash Verge", WintunDllName);
    }

    /// <summary>我们自己注入的代理分组名（配置里出现它说明已按新规则生成）。</summary>
    public const string GitHubGroupName = "SteamEYA-GitHub";

    /// <summary>全局模式用的出口组名（mihomo 的内置全局组就叫这个名字）。</summary>
    public const string GlobalGroupName = "GLOBAL";

    /// <summary>
    /// 改写订阅配置：
    ///   1) 抹掉同名顶层键（连同它们的缩进子行），写入本程序专用的监听设置（只监听 127.0.0.1、独立端口）；
    ///   2) 写入用户在设置里选的 mode（rule = 规则 / global = 全局）；TUN 模式额外写 tun: 段并接管 dns: 段；
    ///   3) 注入一个「GitHub 专用」分组 + 规则：很多订阅默认把 GitHub 走直连（境内 403），
    ///      这里在 rules 最前面把 GitHub / gh-proxy 域名强制指到真实节点上，其余仍按订阅规则走。
    /// 首行写入生成标记（接管方式 + 模式 + 端口）：选择变了 <see cref="NeedsRewrite"/> 就会发现配置过期并重新生成。
    /// </summary>
    private static string RewriteConfig(string yaml, int port, string takeover, string mode, string? node)
    {
        var normalized = yaml.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();
        var tunEnabled = string.Equals(takeover, TakeoverTun, StringComparison.OrdinalIgnoreCase);
        var globalMode = string.Equals(mode, ModeGlobal, StringComparison.OrdinalIgnoreCase);

        // TUN 接管全部流量，DNS 必须一起接管：否则解析仍走系统 DNS，fake-ip 与域名分流都会失效。
        string[] managedKeys = tunEnabled ? [.. ManagedTopLevelKeys, "dns"] : ManagedTopLevelKeys;

        // 节点名（用于自建分组；把「剩余流量/套餐到期」这类占位条目排除）
        var usableNodes = ExtractProxyNames(lines)
            .Where(name => !IsPlaceholderNode(name))
            .ToList();

        // 用户选的节点排到分组首位：mihomo 的 select 组默认选中第一个成员，这样「节点选择」才生效。
        var preferred = node;
        if (preferred is not null && !usableNodes.Contains(preferred, StringComparer.Ordinal))
        {
            AppLog.Warn($"订阅里没有节点「{preferred}」，本次按订阅默认顺序。");
            preferred = null;
        }

        var orderedNodes = preferred is null
            ? usableNodes
            : [preferred, .. usableNodes.Where(name => !string.Equals(name, preferred, StringComparison.Ordinal))];

        var builder = new StringBuilder();
        // 标记写用户实际选的值（哪怕订阅里没有这个节点也一样）：否则每次连接都会重写一次配置。
        builder.AppendLine(BuildMarker(port, takeover, mode, node));
        builder.AppendLine("# 由 SteamEYA 生成：仅本程序使用的 mihomo 配置（只监听 127.0.0.1）");
        builder.AppendLine($"mixed-port: {port}");
        builder.AppendLine("allow-lan: false");
        builder.AppendLine("bind-address: 127.0.0.1");
        builder.AppendLine($"mode: {mode}");
        builder.AppendLine("log-level: warning");
        // 控制口只监听本机并带 secret：节点延迟要靠它查（关掉的话就测不了延迟）。
        builder.AppendLine($"external-controller: 127.0.0.1:{ControllerPort}");
        builder.AppendLine($"secret: '{EscapeYaml(ControllerSecret())}'");
        // 非 TUN 时也显式写 enable: false，免得订阅里的 tun 段（或被删剩的残行）让内核抢网卡。
        builder.AppendLine("tun:");
        builder.AppendLine($"  enable: {(tunEnabled ? "true" : "false")}");

        if (tunEnabled)
        {
            // 固定成自己的设备名：mihomo 默认叫 "Meta"，和 Clash Verge 等其它 mihomo 内核重名时，
            // TUN 创建会直接失败（ERROR_ALREADY_EXISTS），表现为「怎么都起不来、还可能断网」。
            builder.AppendLine($"  device: {TunDeviceName}");
            builder.AppendLine("  stack: mixed");
            builder.AppendLine("  auto-route: true");
            builder.AppendLine("  auto-detect-interface: true");
            builder.AppendLine("  dns-hijack:");
            builder.AppendLine("    - 'any:53'");
            builder.Append(BuildDnsSection());
        }

        builder.AppendLine();

        // 注入的分组/规则必须与订阅里同级项的缩进一致：同一序列里各项缩进不同 YAML 直接解析失败，
        // 而订阅既有 2 空格的也有 4 空格的，所以按订阅自己的缩进来写。
        var groupIndent = DetectItemIndent(lines, "proxy-groups");
        var ruleIndent = DetectItemIndent(lines, "rules");

        var injectedGroup = usableNodes.Count == 0;
        var injectedRules = usableNodes.Count == 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                var key = trimmed.Split(':', 2)[0].Trim();
                if (managedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    // 顶层同名键由我们接管：连同它的缩进子行一起丢掉（订阅里的 tun:/dns: 往往是多行块）。
                    while (index + 1 < lines.Count)
                    {
                        var next = lines[index + 1];
                        if (next.StartsWith(' ') || next.StartsWith('\t'))
                        {
                            index++;
                            continue;
                        }

                        // 段里夹着空行时，后面还有缩进行就一起丢，免得留下悬空的子行。
                        if (next.Length == 0 && index + 2 < lines.Count &&
                            (lines[index + 2].StartsWith(' ') || lines[index + 2].StartsWith('\t')))
                        {
                            index++;
                            continue;
                        }

                        break;
                    }

                    continue;
                }
            }

            builder.AppendLine(line);

            // 在 proxy-groups 段的第一行前插入我们的分组
            if (!injectedGroup && trimmed.StartsWith("proxy-groups:", StringComparison.Ordinal))
            {
                builder.AppendLine(BuildGitHubGroup(orderedNodes, groupIndent));
                if (globalMode && !HasGroupNamed(lines, GlobalGroupName))
                {
                    // 全局模式走内核的 GLOBAL 组：显式定义它，用户选的节点才会成为默认出口。
                    builder.AppendLine(BuildGlobalGroup(orderedNodes, groupIndent));
                }

                injectedGroup = true;
            }

            // 在 rules 段的第一行前插入 GitHub 规则（rules 是列表，第一行紧随其后）
            if (!injectedRules && trimmed.StartsWith("rules:", StringComparison.Ordinal))
            {
                foreach (var rule in BuildGitHubRules(ruleIndent))
                {
                    builder.AppendLine(rule);
                }

                injectedRules = true;
            }
        }

        if (usableNodes.Count == 0)
        {
            AppLog.Warn("订阅里没解析出可用节点，跳过 GitHub 强制代理分组。");
        }

        return builder.ToString();
    }

    /// <summary>配置首行的生成标记：接管方式 / 模式 / 端口 / 节点任一变化都要重新生成配置。</summary>
    private static string BuildMarker(int port, string takeover, string mode, string? node) =>
        $"# SteamEYA-Generated: takeover={takeover} mode={mode} port={port} node={node ?? "-"}";

    /// <summary>配置是否不是「当前接管方式 / 模式 / 端口 / 节点」生成的（是就重写配置）。</summary>
    private static bool NeedsRewrite(int port, string takeover, string mode, string? node)
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return true;
            }

            var content = File.ReadAllText(ConfigPath);
            // 控制口 secret 也要对得上：否则内核认的是旧 secret，查延迟会 401。
            return !content.Contains(BuildMarker(port, takeover, mode, node), StringComparison.Ordinal) ||
                   !content.Contains($"secret: '{EscapeYaml(ControllerSecret())}'", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 VPN 配置失败，按需要重新生成处理：{ex.Message}");
            return true;
        }
    }

    /// <summary>订阅里可选的节点名（占位条目已排除）；没有订阅副本时读已生成的配置。没有可用节点时返回空表。</summary>
    public static IReadOnlyList<string> ListNodes()
    {
        foreach (var path in new[] { SubscriptionPath, ConfigPath })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var nodes = ExtractProxyNames(File.ReadAllLines(path))
                    .Where(name => !IsPlaceholderNode(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (nodes.Count > 0)
                {
                    return nodes;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取 VPN 节点列表失败（{path}）：{ex.Message}");
            }
        }

        return [];
    }

    /// <summary>
    /// 用本地订阅副本按当前设置（接管方式 / 模式 / 节点）重写内核配置，不联网。
    /// 返回 false 表示没有副本可用，需要走联网拉订阅的路径。
    /// </summary>
    public static async Task<bool> RewriteFromLocalSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SubscriptionPath))
        {
            return false;
        }

        var yaml = await File.ReadAllTextAsync(SubscriptionPath, cancellationToken);
        var rewritten = RewriteConfig(yaml, ConfiguredPort, CurrentTakeover, CurrentMode, CurrentNode);
        await File.WriteAllTextAsync(ConfigPath, rewritten, new UTF8Encoding(false), cancellationToken);
        AppLog.Info($"已按本地订阅副本重写内核配置（方式 {CurrentTakeover}，模式 {CurrentMode}，节点 {CurrentNode ?? "自动"}）。");
        return true;
    }

    /// <summary>
    /// 清空本地订阅数据：删掉订阅副本与按它生成的配置。
    /// 这两个文件里有节点地址/端口/口令，属于用户自己的东西；用户点「清空」就是要把它们从本机抹掉。
    /// </summary>
    public static void ClearLocalSubscription()
    {
        foreach (var path in new[] { SubscriptionPath, ConfigPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    AppLog.Info($"已删除本地 VPN 文件：{path}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"删除本地 VPN 文件失败（{path}）：{ex.Message}");
            }
        }
    }

    /// <summary>订阅里是否已定义同名分组：同名重复定义会让内核直接报错，注入前先查一下。</summary>
    private static bool HasGroupNamed(IReadOnlyList<string> lines, string groupName)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            $@"name\s*:\s*['""]?(?:{System.Text.RegularExpressions.Regex.Escape(groupName)})['""]?\s*[,}}]");
        return lines.Any(line => pattern.IsMatch(line));
    }

    /// <summary>TUN 模式的 DNS：fake-ip + 国内解析，避免系统 DNS 污染导致分流把域名判错。</summary>
    private static string BuildDnsSection()
    {
        var builder = new StringBuilder();
        builder.AppendLine("dns:");
        builder.AppendLine("  enable: true");
        builder.AppendLine("  ipv6: false");
        builder.AppendLine("  listen: 0.0.0.0:1053");
        builder.AppendLine("  enhanced-mode: fake-ip");
        builder.AppendLine("  fake-ip-range: 198.18.0.1/16");
        builder.AppendLine("  fake-ip-filter:");
        builder.AppendLine("    - '*.lan'");
        builder.AppendLine("    - '*.local'");
        builder.AppendLine("    - 'localhost.ptlogin2.qq.com'");
        builder.AppendLine("  default-nameserver:");
        builder.AppendLine("    - 223.5.5.5");
        builder.AppendLine("    - 119.29.29.29");
        builder.AppendLine("  nameserver:");
        builder.AppendLine("    - 223.5.5.5");
        builder.AppendLine("    - 119.29.29.29");
        return builder.ToString();
    }

    /// <summary>自建的 GitHub 分组：type=select，第一个可用节点即默认选中（避免订阅的 url-test 选到 DIRECT）。</summary>
    private static string BuildGitHubGroup(IReadOnlyList<string> nodes, string indent)
    {
        var members = string.Join(", ", nodes.Select(node => "'" + EscapeYaml(node) + "'"));
        return $"{indent}- {{ name: '{GitHubGroupName}', type: select, proxies: [{members}] }}";
    }

    /// <summary>全局模式下的出口组：显式定义 GLOBAL，让选中的节点成为默认出口（否则内核会自己建一组）。</summary>
    private static string BuildGlobalGroup(IReadOnlyList<string> nodes, string indent)
    {
        var members = string.Join(", ", nodes.Select(node => "'" + EscapeYaml(node) + "'"));
        return $"{indent}- {{ name: '{GlobalGroupName}', type: select, proxies: [{members}] }}";
    }

    /// <summary>强制走节点的域名：GitHub 本体 + 更新站点用到的 gh-proxy 加速域名。</summary>
    private static IEnumerable<string> BuildGitHubRules(string indent)
    {
        foreach (var domain in new[]
                 {
                     "github.com", "api.github.com", "codeload.github.com", "objects.githubusercontent.com",
                     "raw.githubusercontent.com", "githubusercontent.com", "github.io", "gh-proxy.org"
                 })
        {
            yield return $"{indent}- 'DOMAIN-SUFFIX,{domain},{GitHubGroupName}'";
        }
    }

    /// <summary>取某个顶层段里第一个列表项的缩进；段不存在（或里面没有列表项）时按 2 空格处理。</summary>
    private static string DetectItemIndent(IReadOnlyList<string> lines, string section)
    {
        var inSection = false;
        var sectionKey = section + ":";
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                inSection = line.StartsWith(sectionKey, StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                return new string(' ', line.Length - trimmed.Length);
            }
        }

        return "  ";
    }

    /// <summary>从 proxies 段里抽节点名（支持 "- { name: 'X', ... }" 与多行 "- name: X" 两种写法）。</summary>
    private static List<string> ExtractProxyNames(IEnumerable<string> lines)
    {
        var names = new List<string>();
        var inProxies = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!line.StartsWith(' ') && !line.StartsWith('-'))
            {
                inProxies = trimmed.StartsWith("proxies:", StringComparison.Ordinal);
                continue;
            }

            if (!inProxies || !trimmed.StartsWith('-'))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^-\s*\{?\s*name\s*:\s*(?:'([^']*)'|\""([^\""]*)\""|([^,}]+))");
            if (!match.Success)
            {
                continue;
            }

            var name = (match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value).Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>占位/信息类「节点」（剩余流量、套餐到期…）不进自建分组。</summary>
    private static bool IsPlaceholderNode(string name) =>
        new[] { "剩余流量", "距离下次", "套餐到期", "防失联", "官网", "流量", "重置", "过期", "续费" }
            .Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string EscapeYaml(string value) => value.Replace("'", "''");

    /// <summary>从 Clash Verge 目录借 GEOSITE/GEOIP 规则数据（订阅规则依赖它们）。</summary>
    private static void EnsureGeoFiles()
    {
        foreach (var name in GeoFileNames)
        {
            var target = Path.Combine(DataFolder, name);
            if (File.Exists(target))
            {
                continue;
            }

            foreach (var source in EnumerateGeoCandidates(name))
            {
                try
                {
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    File.Copy(source, target, overwrite: true);
                    AppLog.Info($"已复制 VPN 规则数据：{name} ← {source}");
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"复制 VPN 规则数据失败（{name}）：{ex.Message}");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateGeoCandidates(string fileName)
    {
        // Clash Verge 安装目录（内核同级 resources）
        foreach (var dir in EnumerateClashVergeFolders())
        {
            yield return Path.Combine(dir, "resources", fileName);
            yield return Path.Combine(dir, fileName);
        }

        // Clash Verge 数据目录（运行期下载的规则数据）
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", fileName);
        yield return Path.Combine(appData, "clash-verge", fileName);
    }

    /// <summary>Clash Verge 相关目录：先用设置里的 exe 所在目录，再做一次候选扫描。</summary>
    private static IEnumerable<string> EnumerateClashVergeFolders()
    {
        var persisted = AppState.SettingsService.Load().VpnExecutablePath;
        if (VpnProxyService.IsValidExecutable(persisted))
        {
            var folder = Path.GetDirectoryName(persisted!);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                yield return folder;
            }
        }

        foreach (var candidate in VpnProxyService.AutoDetectClashVergeFolders())
        {
            yield return candidate;
        }
    }

    /// <summary>找内核可执行文件：优先 Clash Verge 目录下的 verge-mihomo.exe 等。</summary>
    public static string? ResolveCoreExecutable()
    {
        foreach (var dir in EnumerateClashVergeFolders())
        {
            foreach (var name in CoreExecutableNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }

                var inResources = Path.Combine(dir, "resources", name);
                if (File.Exists(inResources))
                {
                    return inResources;
                }
            }
        }

        AppLog.Warn("未找到 mihomo 内核（verge-mihomo.exe），无法启用订阅直连。");
        return null;
    }

    /// <summary>PID 文件里记录的内核进程（拿不到即视为没有）。</summary>
    private static Process? FindCoreProcess()
    {
        var pid = TrackedCorePid;
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            return null;
        }
    }
}

