using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 「Clash」页：原先挤在设置页里的 VPN（Clash Verge）卡片单独成页，
/// 订阅链接、代理方式（系统代理 / 虚拟网卡）、模式（规则 / 全局）、节点与延迟、内核路径都在这里。
/// 逻辑与设置页时期完全一致（整段搬移），只是换了个住处。
/// </summary>
public sealed partial class VpnPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // 代码设置控件（ToggleSwitch / RadioButtons / 下拉菜单）会触发事件，置位以区分「用户操作」与「初始同步」。
    private bool _syncing;
    private bool _pickingVpnPath;
    private bool _vpnConnecting;
    private bool _vpnNodeMenuBuilt;
    private IReadOnlyList<string> _vpnNodeMenuNodes = [];
    private IReadOnlyDictionary<string, int> _vpnNodeMenuDelays = new Dictionary<string, int>(StringComparer.Ordinal);

    // 更新站点（原先在设置页的独立卡片）：检查更新 / 下载安装包走直连还是 GitHub 代理站。
    private static readonly string[] UpdateProxyCodes = ["direct", "gh-proxy.org", "v4.gh-proxy.org", "v6.gh-proxy.org", "cdn.gh-proxy.org"];
    private string _selectedUpdateProxyCode = "direct";

    public VpnPage()
    {
        // XAML 初始化 ToggleSwitch 时会触发 Toggled；先屏蔽，避免首帧用默认值覆盖已保存设置。
        _syncing = true;
        InitializeComponent();
        // 更新站点下拉：XAML 里留的是空 MenuFlyout，项在代码里按语言建一次。
        BuildUpdateProxyMenu();
        Loc.LanguageChanged += OnLanguageChanged;
        _syncing = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UpdateVpnControls();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            UpdateVpnControls();
            UpdateUpdateProxyMenuTexts();
        });
    }

    // ---------- VPN（Clash Verge）：仅本程序访问 GitHub 时走它的本地代理端口 ----------

    /// <summary>刷新 VPN 卡片：路径文本、开关状态、状态说明。</summary>
    private void UpdateVpnControls()
    {
        if (VpnPathText is null)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        // 更新站点卡片的选中项跟随已保存设置（进页面 / 切语言都会重算文案）。
        _selectedUpdateProxyCode = UpdateProxyCodes.Contains(settings.UpdateProxySite, StringComparer.OrdinalIgnoreCase)
            ? settings.UpdateProxySite
            : "direct";
        UpdateProxyButtonText.Text = UpdateProxyDisplayName(_selectedUpdateProxyCode);

        VpnPathText.Text = VpnProxyService.IsValidExecutable(settings.VpnExecutablePath)
            ? settings.VpnExecutablePath!
            : Loc.T("Settings_Vpn_Path_None");

        // 正在输入时不要打断用户；否则用设置里的链接回填。
        if (VpnSubscriptionBox.FocusState == FocusState.Unfocused &&
            VpnSubscriptionBox.Text != (settings.VpnSubscriptionUrl ?? string.Empty))
        {
            VpnSubscriptionBox.Text = settings.VpnSubscriptionUrl ?? string.Empty;
        }

        SetVpnProxyToggle(settings.VpnProxyEnabled);

        UpdateVpnChoiceControls(settings);
        UpdateVpnNodeControl(settings);

        VpnProxyStatusText.Text = DescribeVpnProxy(settings);

        // 端口探测放到后台：界面立即刷新，探完再补一次状态（绝不阻塞 UI 线程）。
        if (!_vpnConnecting)
        {
            _ = RefreshVpnPortAsync();
        }
    }

    /// <summary>
    /// 同步下方「启用 VPN 代理」开关。<c>_syncing</c> 屏蔽掉 Toggled：
    /// 否则这次赋值会被当成用户操作，反过来又触发连接/断开。
    /// </summary>
    private void SetVpnProxyToggle(bool isOn)
    {
        if (VpnProxyToggle is null || VpnProxyToggle.IsOn == isOn)
        {
            return;
        }

        _syncing = true;
        try
        {
            VpnProxyToggle.IsOn = isOn;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task RefreshVpnPortAsync()
    {
        await VpnProxyService.RefreshProxyPortAsync();
        VpnProxyStatusText.Text = DescribeVpnProxy(AppState.SettingsService.Load());
    }

    /// <summary>状态说明：未启用 / 内核在跑（显示端口 + 当前接管方式与模式）/ 已启用但内核没起来。</summary>
    private static string DescribeVpnProxy(AppSettings settings)
    {
        if (!settings.VpnProxyEnabled)
        {
            return Loc.T("Settings_Vpn_Proxy_Off");
        }

        if (VpnCoreService.IsRunning)
        {
            // 内核在跑就是已连接：端口探测偶尔会慢/失败，用配置端口兜底，避免显示成「未连接」误导用户。
            var port = VpnProxyService.CachedPort > 0 ? VpnProxyService.CachedPort : VpnCoreService.ConfiguredPort;
            return Loc.Tf(
                "Settings_Vpn_Proxy_On_Format",
                port,
                TakeoverName(settings.VpnTakeover),
                ModeName(settings.VpnMode));
        }

        return VpnCoreService.HasSubscription
            ? Loc.T("Settings_Vpn_Status_NotConnected")
            : Loc.T("Settings_Vpn_Error_SubscriptionRequired");
    }

    /// <summary>接管方式显示名（与两个单选按钮共用同一套文案，避免界面与状态行说法不一致）。</summary>
    private static string TakeoverName(string? takeover) =>
        VpnCoreService.NormalizeTakeover(takeover) == VpnCoreService.TakeoverTun
            ? Loc.T("Settings_Vpn_Takeover_Tun")
            : Loc.T("Settings_Vpn_Takeover_System");

    /// <summary>代理模式显示名。</summary>
    private static string ModeName(string? mode) =>
        VpnCoreService.NormalizeMode(mode) == VpnCoreService.ModeGlobal
            ? Loc.T("Settings_Vpn_Mode_Global")
            : Loc.T("Settings_Vpn_Mode_Rule");

    /// <summary>
    /// 代理方式 / 代理模式两个单选控件：按设置回填选中项，并随语言刷新文案与说明。
    /// 选择本身上面两个事件里立即写回设置（用户要求：选完就保存，下次启动按上次的选择来）。
    /// </summary>
    private void UpdateVpnChoiceControls(AppSettings settings)
    {
        if (VpnTakeoverRadios is null || VpnModeRadios is null || VpnTakeoverHintText is null)
        {
            return;
        }

        VpnTakeoverSystemRadio.Content = Loc.T("Settings_Vpn_Takeover_System");
        VpnTakeoverTunRadio.Content = Loc.T("Settings_Vpn_Takeover_Tun");
        VpnModeRuleRadio.Content = Loc.T("Settings_Vpn_Mode_Rule");
        VpnModeGlobalRadio.Content = Loc.T("Settings_Vpn_Mode_Global");

        var takeover = VpnCoreService.NormalizeTakeover(settings.VpnTakeover);
        VpnTakeoverHintText.Text = takeover == VpnCoreService.TakeoverTun
            ? Loc.T("Settings_Vpn_Takeover_Hint_Tun")
            : Loc.T("Settings_Vpn_Takeover_Hint_System");

        _syncing = true;
        try
        {
            VpnTakeoverRadios.SelectedItem = takeover == VpnCoreService.TakeoverTun
                ? VpnTakeoverTunRadio
                : VpnTakeoverSystemRadio;
            VpnModeRadios.SelectedItem = VpnCoreService.NormalizeMode(settings.VpnMode) == VpnCoreService.ModeGlobal
                ? VpnModeGlobalRadio
                : VpnModeRuleRadio;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 节点下拉：按钮文字 = 当前节点（或「自动」），菜单项 = 自动 + 订阅里的节点。
    /// 节点列表没变就不重建菜单，免得用户正拉开菜单时被清掉。
    /// </summary>
    private void UpdateVpnNodeControl(AppSettings settings)
    {
        if (VpnNodeButton is null || VpnNodeFlyout is null || VpnNodeButtonText is null)
        {
            return;
        }

        var nodes = VpnCoreService.ListNodes();
        var selected = string.IsNullOrWhiteSpace(settings.VpnNode) ? null : settings.VpnNode.Trim();
        if (selected is not null && !nodes.Contains(selected, StringComparer.Ordinal))
        {
            // 换过订阅：原来选的节点已经不在了，回到自动。
            selected = null;
        }

        VpnNodeButtonText.Text = selected ?? Loc.T("Settings_Vpn_Node_Auto");

        // 延迟也是这样：自动连回来的那次测量发生在设置页创建之前，进页面时必须把数字补上。
        var delays = VpnCoreService.NodeDelays;
        if (!_vpnNodeMenuBuilt ||
            !_vpnNodeMenuNodes.SequenceEqual(nodes, StringComparer.Ordinal) ||
            !DelaysEqual(_vpnNodeMenuDelays, delays))
        {
            _vpnNodeMenuNodes = nodes;
            _vpnNodeMenuDelays = delays;
            VpnNodeFlyout.Items.Clear();
            VpnNodeFlyout.Items.Add(BuildNodeMenuItem(null, Loc.T("Settings_Vpn_Node_Auto")));

            // 节点按地区归拢：同一地区的节点永远连在一起（组间用分隔线隔开），组内保持订阅原顺序。
            var firstGroup = true;
            foreach (var group in GroupNodesByRegion(nodes))
            {
                if (!firstGroup)
                {
                    VpnNodeFlyout.Items.Add(new MenuFlyoutSeparator());
                }

                firstGroup = false;
                foreach (var node in group)
                {
                    VpnNodeFlyout.Items.Add(BuildNodeMenuItem(node, BuildNodeLabel(node)));
                }
            }

            _vpnNodeMenuBuilt = true;
        }
        else if (VpnNodeFlyout.Items.FirstOrDefault() is MenuFlyoutItem autoItem)
        {
            autoItem.Text = Loc.T("Settings_Vpn_Node_Auto");   // 语言切换后「自动」项文案要跟着变
        }
    }

    /// <summary>两份延迟快照是否一致（数量 + 每个节点的数值）。</summary>
    private static bool DelaysEqual(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>菜单项文案：节点名 + 最近一次测到的延迟（没测到或测不通显示 —）。</summary>
    private static string BuildNodeLabel(string node)
    {
        var delays = VpnCoreService.NodeDelays;
        return delays.TryGetValue(node, out var delay) ? $"{node}  ·  {delay} ms" : $"{node}  ·  —";
    }

    /// <summary>延迟变了：强制重建节点菜单，让数字刷新出来。</summary>
    private void RebuildVpnNodeMenu()
    {
        _vpnNodeMenuBuilt = false;
        UpdateVpnNodeControl(AppState.SettingsService.Load());
    }

    /// <summary>刷新节点延迟：内核把它那个分流组里的节点都测一遍（失败的按 — 显示）。</summary>
    private async void RefreshVpnNodeDelayButton_Click(object sender, RoutedEventArgs e)
    {
        var nodes = VpnCoreService.ListNodes();
        if (nodes.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Error_DelayNoNodes"), InfoBarSeverity.Warning);
            return;
        }

        RefreshVpnNodeDelayButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_TestingDelay"), InfoBarSeverity.Informational);
        try
        {
            await VpnCoreService.MeasureNodeDelaysAsync(nodes);
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_DelayUpdated"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新节点延迟失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_DelayFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            RefreshVpnNodeDelayButton.IsEnabled = true;
            RebuildVpnNodeMenu();
        }
    }

    /// <summary>
    /// 连上之后自动测一轮延迟——「保存并连接」和打开下方开关都会走到这里。
    /// 刚起来的内核控制口可能还没完全就绪（上一次日志里就是这里失败的），所以先等一拍，
    /// 失败隔一会儿再试一次；两次都不行就只写日志，不弹提示打扰用户。
    /// </summary>
    private async Task RefreshNodeDelaysQuietlyAsync()
    {
        await VpnCoreService.RefreshNodeDelaysAsync();
        RebuildVpnNodeMenu();
    }

    private MenuFlyoutItem BuildNodeMenuItem(string? node, string text)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = node };
        item.Click += VpnNodeMenuItem_Click;
        return item;
    }

    /// <summary>
    /// 地区关键字表（顺序 = 菜单里的排序）：中文名、常用英文写法、两位国家代码、国旗 emoji 都认。
    /// 纯 ASCII 字母的关键字要求词边界，否则 "US" 会命中 "russia"、"IN" 会命中 "singapore"。
    /// </summary>
    private static readonly string[][] RegionKeywords =
    [
        ["香港", "港", "HK", "HKG", "Hong Kong", "HongKong", "🇭🇰"],
        ["台湾", "台灣", "台", "TW", "Taiwan", "台北", "🇹🇼"],
        ["日本", "日", "JP", "Japan", "东京", "東京", "大阪", "🇯🇵"],
        ["韩国", "韓國", "韩", "KR", "Korea", "首尔", "首爾", "🇰🇷"],
        ["新加坡", "狮城", "SG", "Singapore", "🇸🇬"],
        ["美国", "美", "US", "USA", "United States", "America", "洛杉矶", "洛杉磯", "圣何塞", "西雅图", "达拉斯", "🇺🇸"],
        ["英国", "英", "UK", "GB", "United Kingdom", "Britain", "伦敦", "倫敦", "🇬🇧"],
        ["德国", "德", "DE", "Germany", "法兰克福", "法蘭克福", "🇩🇪"],
        ["法国", "法", "FR", "France", "🇫🇷"],
        ["荷兰", "荷", "NL", "Netherlands", "🇳🇱"],
        ["俄罗斯", "俄", "RU", "Russia", "莫斯科", "🇷🇺"],
        ["加拿大", "加", "CA", "Canada", "🇨🇦"],
        ["澳大利亚", "澳洲", "澳", "AU", "Australia", "悉尼", "雪梨", "🇦🇺"],
        ["马来西亚", "馬來西亞", "马来", "MY", "Malaysia", "🇲🇾"],
        ["泰国", "泰國", "泰", "TH", "Thailand", "🇹🇭"],
        ["越南", "越", "VN", "Vietnam", "🇻🇳"],
        ["菲律宾", "菲律賓", "菲", "PH", "Philippines", "🇵🇭"],
        ["印尼", "印度尼西亚", "ID", "Indonesia", "🇮🇩"],
        ["印度", "印", "IN", "India", "🇮🇳"],
        ["土耳其", "土", "TR", "Turkey", "🇹🇷"],
        ["巴西", "巴", "BR", "Brazil", "🇧🇷"],
        ["阿根廷", "AR", "Argentina", "🇦🇷"],
    ];

    /// <summary>把节点按地区分组：组内保持订阅里的原始顺序，组间按 <see cref="RegionKeywords"/> 的顺序。</summary>
    private static List<List<string>> GroupNodesByRegion(IReadOnlyList<string> nodes)
    {
        var groups = new Dictionary<int, List<string>>();
        var ranks = new List<int>();
        foreach (var node in nodes)
        {
            var rank = RegionRank(node);
            if (!groups.TryGetValue(rank, out var list))
            {
                list = [];
                groups[rank] = list;
                ranks.Add(rank);
            }

            list.Add(node);
        }

        return ranks.OrderBy(rank => rank).Select(rank => groups[rank]).ToList();
    }

    /// <summary>节点名 → 地区序号；识别不出的一律排最后。</summary>
    private static int RegionRank(string nodeName)
    {
        for (var index = 0; index < RegionKeywords.Length; index++)
        {
            foreach (var keyword in RegionKeywords[index])
            {
                if (MatchesRegionKeyword(nodeName, keyword))
                {
                    return index;
                }
            }
        }

        return RegionKeywords.Length;
    }

    /// <summary>关键字匹配：纯字母关键字要词边界（"US" 不该命中 "russia"），中文/emoji 直接包含即可。</summary>
    private static bool MatchesRegionKeyword(string name, string keyword)
    {
        var needsBoundary = keyword.Length > 0 && keyword.All(ch => ch < 128 && char.IsLetter(ch));
        var start = 0;
        while (true)
        {
            var index = name.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (!needsBoundary)
            {
                return true;
            }

            var beforeOk = index == 0 || !char.IsLetter(name[index - 1]);
            var afterIndex = index + keyword.Length;
            var afterOk = afterIndex >= name.Length || !char.IsLetter(name[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = index + 1;
        }
    }

    /// <summary>选节点：立即保存，已连接时按新节点重写配置并重启内核。</summary>
    private async void VpnNodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
        {
            return;
        }

        var node = item.Tag as string;   // null = 自动
        var settings = AppState.SettingsService.Load();
        var current = string.IsNullOrWhiteSpace(settings.VpnNode) ? null : settings.VpnNode.Trim();
        if (string.Equals(current, node, StringComparison.Ordinal))
        {
            return;
        }

        settings.VpnNode = node;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 节点已选择：{node ?? "自动"}");
        UpdateVpnControls();
        AppState.ShowStatus(
            Loc.Tf("Settings_Vpn_Status_NodeSaved_Format", node ?? Loc.T("Settings_Vpn_Node_Auto")),
            InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    /// <summary>
    /// 切换代理方式：立即保存；已连接时按新方式重连——接管方式写在生成的内核配置里，
    /// 不重启内核的话切换不会生效（系统代理还要额外改写/还原 WinINET 代理）。
    /// </summary>
    private async void VpnTakeoverRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VpnTakeoverRadios.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var takeover = VpnCoreService.NormalizeTakeover(tag);
        if (VpnCoreService.NormalizeTakeover(settings.VpnTakeover) == takeover)
        {
            return;
        }

        settings.VpnTakeover = takeover;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 代理方式已切换为：{takeover}");
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_ChoiceSaved"), InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    /// <summary>切换代理模式（规则 / 全局）：同样立即保存，已连接时重启内核让新配置生效。</summary>
    private async void VpnModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VpnModeRadios.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var mode = VpnCoreService.NormalizeMode(tag);
        if (VpnCoreService.NormalizeMode(settings.VpnMode) == mode)
        {
            return;
        }

        settings.VpnMode = mode;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 代理模式已切换为：{mode}");
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_ChoiceSaved"), InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    private void DetectVpnButton_Click(object sender, RoutedEventArgs e)
    {
        var detected = VpnProxyService.AutoDetectExecutablePath();
        if (detected is null)
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_DetectFail"), InfoBarSeverity.Warning);
            UpdateVpnControls();
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnExecutablePath = detected;
        AppState.SettingsService.Save(settings);
        AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_Detected_Format", detected), InfoBarSeverity.Success);
        UpdateVpnControls();
    }

    private async void ChangeVpnPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickingVpnPath)
        {
            return;
        }

        _pickingVpnPath = true;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

            StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开 VPN 选择器失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Vpn_Status_PathInvalid"), InfoBarSeverity.Error);
                return;
            }

            if (file is null)
            {
                return;
            }

            if (!VpnProxyService.IsValidExecutable(file.Path))
            {
                AppState.ShowStatus(Loc.T("Settings_Vpn_Status_PathInvalid"), InfoBarSeverity.Error);
                return;
            }

            var settings = AppState.SettingsService.Load();
            settings.VpnExecutablePath = file.Path;
            AppState.SettingsService.Save(settings);
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_PathSaved_Format", file.Path), InfoBarSeverity.Success);
            UpdateVpnControls();
        }
        finally
        {
            _pickingVpnPath = false;
        }
    }

    private void LaunchVpnButton_Click(object sender, RoutedEventArgs e)
    {
        if (VpnProxyService.IsRunning())
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_AlreadyRunning"), InfoBarSeverity.Informational);
            UpdateVpnControls();
            return;
        }

        if (VpnProxyService.TryLaunch(out var error))
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Launched"), InfoBarSeverity.Success);
        }
        else
        {
            AppState.ShowStatus(
                error is null ? Loc.T("Settings_Vpn_Error_NotFound") : Loc.Tf("Settings_Vpn_Status_LaunchFail_Format", error),
                InfoBarSeverity.Error);
        }

        UpdateVpnControls();
    }

    private async void VpnProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = VpnProxyToggle.IsOn;
        AppState.SettingsService.Save(settings);

        if (!settings.VpnProxyEnabled)
        {
            VpnCoreService.Stop();
            await VpnProxyService.RefreshProxyPortAsync();
            UpdateVpnControls();
            return;
        }

        // 打开开关 = 拉起内核（不需要用户启动 Clash 软件）：整个过程异步，不阻塞界面。
        await ConnectVpnAsync();
    }

    /// <summary>保存订阅并连接内核（按钮与开关共用）。</summary>
    private async void SaveVpnSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        var url = VpnSubscriptionBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Error_SubscriptionRequired"), InfoBarSeverity.Warning);
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnSubscriptionUrl = url;
        settings.VpnProxyEnabled = true;
        AppState.SettingsService.Save(settings);
        await ConnectVpnAsync(forceRefreshSubscription: true);
    }

    /// <summary>
    /// 「清空」：①清空文本框（并保存空值）→ ②断开连接 → ③关掉下方 VPN 开关，最后抹掉本机留存的订阅数据。
    /// 断开走 <see cref="VpnCoreService.Stop"/>，它内部会还原被接管的系统代理。
    /// </summary>
    private async void ClearVpnSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        // ① 先清文本（同时把已保存的订阅链接置空）
        VpnSubscriptionBox.Text = string.Empty;
        var settings = AppState.SettingsService.Load();
        settings.VpnSubscriptionUrl = null;
        AppState.SettingsService.Save(settings);

        // ② 断开连接
        VpnCoreService.Stop();

        // ③ 关掉下方 VPN 开关（设置 + 界面都显式置为关闭）
        settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = false;
        AppState.SettingsService.Save(settings);
        SetVpnProxyToggle(false);
        AppLog.Info("已清空 VPN 订阅链接、断开连接并关闭 VPN 代理。");

        VpnCoreService.ClearLocalSubscription();
        await VpnProxyService.RefreshProxyPortAsync();
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Cleared"), InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 「断开」：结束内核并把下方 VPN 开关一并关掉；**不动订阅链接**（下次还能直接连回来）。
    /// </summary>
    private async void StopVpnCoreButton_Click(object sender, RoutedEventArgs e)
    {
        // ① 断开连接
        VpnCoreService.Stop();

        // ② 关掉下方 VPN 开关：设置 + 界面都显式置为关闭（不等 UpdateVpnControls 的间接路径）
        var settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = false;
        AppState.SettingsService.Save(settings);
        SetVpnProxyToggle(false);
        AppLog.Info("已点击「断开」：内核已结束，VPN 开关已关闭（订阅链接保留）。");

        await VpnProxyService.RefreshProxyPortAsync();
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Disconnected"), InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 连接流程：拉订阅（可选）→ 启动内核 → 等端口就绪 → 按所选方式接管（系统代理 / TUN）→ 刷新界面。
    /// <paramref name="restart"/> = true 时先停掉现有内核：接管方式与模式都写在内核配置里，不重启不会生效。
    /// </summary>
    private async Task ConnectVpnAsync(bool forceRefreshSubscription = false, bool restart = false)
    {
        if (_vpnConnecting)
        {
            return;
        }

        _vpnConnecting = true;
        VpnProxyToggle.IsEnabled = false;
        SaveVpnSubscriptionButton.IsEnabled = false;
        VpnProxyStatusText.Text = Loc.T("Settings_Vpn_Status_Connecting");
        try
        {
            if (restart)
            {
                VpnCoreService.Stop();
                // 换节点 / 换模式只是改配置：用本地订阅副本立刻重写，不必再下一次订阅。
                await VpnCoreService.RewriteFromLocalSubscriptionAsync();
            }

            IProgress<string> progress = new Progress<string>(message => VpnProxyStatusText.Text = message);
            if (forceRefreshSubscription)
            {
                progress.Report(Loc.T("Settings_Vpn_Status_FetchingSubscription"));
                await VpnCoreService.RefreshSubscriptionAsync(VpnSubscriptionBox.Text.Trim());
            }

            await VpnCoreService.EnsureRunningAsync(progress);
            await VpnProxyService.RefreshProxyPortAsync();

            // 系统代理 = 把 WinINET 代理写到内核端口；TUN = 内核自己接管全部流量，系统代理必须还原，
            // 否则流量会「系统代理 → 内核 → TUN」绕一圈，还会在断开后留给用户一个死代理。
            var port = VpnProxyService.CachedPort > 0 ? VpnProxyService.CachedPort : VpnCoreService.ConfiguredPort;
            var current = AppState.SettingsService.Load();
            if (VpnCoreService.CurrentTakeover == VpnCoreService.TakeoverTun)
            {
                SystemProxyService.RestoreIfApplied();
            }
            else
            {
                SystemProxyService.Apply(port, VpnCoreService.CoreProcessId);
            }

            UpdateVpnControls();
            AppState.ShowStatus(
                Loc.Tf("Settings_Vpn_Proxy_On_Format", port, TakeoverName(current.VpnTakeover), ModeName(current.VpnMode)),
                InfoBarSeverity.Success);

            // 连上后顺手测一轮节点延迟：菜单里的节点会带上 ms 数（后台进行，不阻塞界面）。
            _ = RefreshNodeDelaysQuietlyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"连接 VPN 失败：{ex.Message}");
            UpdateVpnControls();
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_ConnectFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _vpnConnecting = false;
            VpnProxyToggle.IsEnabled = true;
            SaveVpnSubscriptionButton.IsEnabled = true;
        }
    }

    // ---------- 更新站点：可选直连或代理站，手动检测当前选择站点延迟 ----------

    private void BuildUpdateProxyMenu()
    {
        foreach (var code in UpdateProxyCodes)
        {
            var item = new MenuFlyoutItem
            {
                Text = UpdateProxyDisplayName(code),
                Tag = code
            };
            item.Click += UpdateProxyMenuItem_Click;
            UpdateProxyFlyout.Items.Add(item);
        }
    }

    private void UpdateProxyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string proxyCode })
        {
            return;
        }

        _selectedUpdateProxyCode = proxyCode;
        UpdateProxyButtonText.Text = UpdateProxyDisplayName(proxyCode);

        var settings = AppState.SettingsService.Load();
        settings.UpdateProxySite = proxyCode;
        AppState.SettingsService.Save(settings);
        AppState.UpdateService.SetProxySite(proxyCode);
    }

    private void UpdateUpdateProxyMenuTexts()
    {
        foreach (var item in UpdateProxyFlyout.Items.OfType<MenuFlyoutItem>())
        {
            if (item.Tag is string code)
            {
                item.Text = UpdateProxyDisplayName(code);
            }
        }
    }

    private static string UpdateProxyDisplayName(string code) => code switch
    {
        "gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrg"),
        "v4.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgV4"),
        "v6.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgV6"),
        "cdn.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgCdn"),
        _ => Loc.T("Settings_UpdateProxy_Direct")
    };

    private async void UpdateProxyLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var proxyCode = _selectedUpdateProxyCode;
        if (string.IsNullOrWhiteSpace(proxyCode))
        {
            return;
        }

        UpdateProxyLatencyButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_UpdateProxy_Testing"), InfoBarSeverity.Informational);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var elapsed = await AppState.UpdateService.ProbeLatencyAsync(proxyCode, cts.Token);
            AppState.ShowStatus(
                Loc.Tf("Settings_UpdateProxy_LatencyResult_Format", Math.Round(elapsed.TotalMilliseconds)),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("Settings_UpdateProxy_LatencyTimeout"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Settings_UpdateProxy_LatencyFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            UpdateProxyLatencyButton.IsEnabled = true;
        }
    }

}
