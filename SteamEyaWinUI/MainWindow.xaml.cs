using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using SteamEyaWinUI.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Pages;
using SteamEyaWinUI.Services;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SteamEyaWinUI;

public sealed partial class MainWindow : Window
{
    public const int DefaultWindowWidth = 1380;
    public const int DefaultWindowHeight = 810;
    public const int MinimumWindowWidth = 1180;
    public const int MinimumWindowHeight = 700;
    public const int MaximumWindowWidth = 10000;
    public const int MaximumWindowHeight = 10000;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint WindowSubclassId = 1;
    private const double IntroVideoVolume = 0.1;

    private static nint s_hwnd;

    // 状态栏提示的自动收起时间：用户要求所有页面、所有等级（含警告/错误）统一 3 秒。
    // 详情不会丢：警告/错误同时写入 logs\steameya.log，状态栏只做即时反馈。
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(3);
    // 提示条底色画刷：不透明，颜色 = 对话框底色 与 UI 主题色 的混合（每次显示前重算，换主题色立即生效）。
    private readonly SolidColorBrush _statusPanelBrush = new();
    private bool _statusPanelBrushAttached;
    private readonly DispatcherQueueTimer _statusDismissTimer;
    private MediaPlayer? _introMediaPlayer;
    private bool _introVideoActive;
    private bool _startupInitializationStarted;
    private TaskCompletionSource<bool>? _introPlaybackCompletion;
    private bool _introPlaybackStartsApp;

    public static MainWindow? Instance { get; private set; }

    /// <summary>主窗口句柄，供文件/目录选择器等 WinRT 互操作（InitializeWithWindow）使用；在 ConfigureWindowSize 中赋值。</summary>
    public static nint Hwnd => s_hwnd;

    /// <summary>
    /// 启动时按上次状态连回 VPN：稍等一下再连，避免连接流程（TUN 还需要 UAC）挤在窗口弹出的瞬间。
    /// </summary>
    private static async Task RestoreVpnOnStartupAsync()
    {
        try
        {
            await Task.Delay(1500);
            await VpnCoreService.TryRestoreOnStartupAsync();
        }
        catch (Exception ex)
        {
            AppLog.Info($"启动时自动连回 VPN 未完成：{ex.Message}");
        }
    }

    /// <summary>
    /// 打开软件就预热 Steam 侧连接（CM 列表 / DNS / TLS），让「一键查询 / 清空无效账号」
    /// 的第一次点击也不用等冷启动。后台进行、失败只写日志，不占用窗口初始化。
    /// </summary>
    private static async Task PrewarmSteamConnectionsAsync()
    {
        try
        {
            await CsPremierScoreService.PrewarmAsync();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Steam 连接预热异常：{ex.Message}");
        }
    }

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();

        // 背景视频的播放器：MediaPlayerElement.MediaPlayer 不会自动创建（只读属性，XAML 里也赋不了），
        // 必须像入场动画那样显式 new 一个再 SetMediaPlayer，否则永远是 null、视频播不出来。
        _backgroundPlayer = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true
        };
        CustomBackgroundMedia.SetMediaPlayer(_backgroundPlayer);

        // 关窗口时停掉背景视频：及时释放解码器与文件句柄。
        Closed += (_, _) => StopBackgroundVideo();

        // 关闭软件 = 断开 VPN：窗口真正关闭时显式收掉内核并还原系统代理。
        // 不依赖 ProcessExit —— 它只是进程级兜底，触发时机在窗口关闭之后，被强杀时更不会跑。
        Closed += (_, _) => VpnProxyService.SafeStopCore();

        // 上次退出时 VPN 是开启状态 → 启动后自动连回来。
        _ = RestoreVpnOnStartupAsync();

        // VPN 兜底自愈：开关是「已启用」但内核不在了就自动连回（见 EnsureVpnAliveAsync）。
        // 间隔压到 5 秒：多实例场景下，前一个窗口退出会把内核一起带走（Job 保险），
        // 这个间隔就是「另一个窗口要等多久才把 VPN 接回来」，越短越接近无感。
        _vpnWatchTimer = DispatcherQueue.CreateTimer();
        _vpnWatchTimer.Interval = TimeSpan.FromSeconds(5);
        _vpnWatchTimer.Tick += async (_, _) => await EnsureVpnAliveAsync();
        _vpnWatchTimer.Start();


        // 后台预热 Steam 侧连接：首次点「一键查询 / 清空无效账号」不用再等冷启动。
        _ = PrewarmSteamConnectionsAsync();
        StatusInfoBar.RegisterPropertyChangedCallback(
            InfoBar.IsOpenProperty,
            (_, _) => StatusOverlay.Visibility = StatusInfoBar.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        RootLayoutGrid.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnIntroPointerPressed),
            handledEventsToo: true);
        RootLayoutGrid.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnIntroKeyDown),
            handledEventsToo: true);

        // 背景层按系统能力挑：Windows 11 用 Mica；Windows 10 没有 Mica（实测 IsSupported=False），
        // 这时若仍设 MicaBackdrop，窗口边框/内容缝隙会透出「黑色」底 —— 表现为四周黑边。
        // 退一档到桌面亚克力（Win10 1809+ 支持），两边都不可用就不设背景层，由根 Grid 的渐变兜底。
        SystemBackdrop = MicaController.IsSupported()
            ? new MicaBackdrop()
            : DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicBackdrop()
                : null;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        TitleVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";

        var startupSettings = AppState.SettingsService.Load();
        ApplyTheme(ParseTheme(startupSettings.Theme, startupSettings.UiColor));
        ApplyCustomBackground(startupSettings);
        RefreshNavText();
        Loc.LanguageChanged += RefreshNavText;

        _statusDismissTimer = DispatcherQueue.CreateTimer();
        _statusDismissTimer.Interval = StatusAutoDismissDelay;
        _statusDismissTimer.IsRepeating = false;
        _statusDismissTimer.Tick += (_, _) => StatusInfoBar.IsOpen = false;

        AppState.StatusReporter = ShowStatus;
        AppState.BusyChanged += OnBusyChanged;
        AppState.UpdateStateChanged += RefreshUpdateBadge;
        RefreshUpdateBadge();

        ConfigureWindowSize();

        // 预载历史账号，登录页的头像/资料复用依赖该缓存。
        AppState.ReloadHistory();

        // 启动时预构造登录页并保留在 Frame 缓存中，供历史页快速登录和查询复用。
        EnsureLoginPageInitialized();
        // 启动后默认进入账号管理页，登录页仍可从导航或历史账号快捷进入。
        RootNavigationView.SelectedItem = ManagedAccountsNavItem;

        // 首次启动即解析并持久化 Steam 安装路径（之后上号直接复用，不再每次探测）。
        // 等内容进入可视树（XamlRoot 就绪）后再跑，检测失败才需要弹框。
        RootLayoutGrid.Loaded += OnRootLayoutGridLoaded;

        _ = RunStartupUpdateCheckAsync();
    }

    /// <summary>
    /// 启动时的更新检查。
    ///   · 先等一拍再查：VPN 自动连回是「先接管系统代理、内核随后就绪」，这个窗口里发请求会直接被拒
    ///     （日志里的「连接被拒 (127.0.0.1:17897)」就是这么来的），等内核起来再查成功率最高；
    ///   · 自动检查原本只在失败时写日志、界面上完全静默，用户会误以为「没检测到新版本」——
    ///     所以这里失败会多试两轮（6 秒 / 15 秒），并把每一轮结果都记进日志，方便排查。
    /// 注意整个过程留在 UI 线程（async/await 不切上下文），UpdateStateChanged 的订阅方才能安全刷 UI。
    /// </summary>
    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
        }
        catch
        {
            // 取消/异常都不影响后续检查
        }

        var delays = new[] { TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15) };
        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            await AppState.CheckForUpdatesAsync(isAutomatic: true);

            if (AppState.UpdateCheckError is null)
            {
                var latest = AppState.LatestUpdate;
                AppLog.Info(
                    $"更新检查完成：本机 {GitHubUpdateService.CurrentVersion}，" +
                    $"线上 {latest?.LatestVersion ?? "未知"}（有更新：{latest?.IsUpdateAvailable == true}）。");
                return;
            }

            AppLog.Warn($"启动更新检查第 {attempt + 1} 次失败：{AppState.UpdateCheckError}");

            if (attempt >= delays.Length)
            {
                break;
            }

            try
            {
                await Task.Delay(delays[attempt]);
            }
            catch
            {
                return;
            }
        }
    }

    private void OnRootLayoutGridLoaded(object sender, RoutedEventArgs e)
    {
        RootLayoutGrid.Loaded -= OnRootLayoutGridLoaded;
        if (!TryStartIntroVideo())
        {
            StartStartupInitialization();
        }
    }

    /// <summary>启动内置入场视频；文件不存在或初始化失败时直接进入软件。</summary>
    private bool TryStartIntroVideo()
    {
        var settings = AppState.SettingsService.Load();
        if (!settings.IntroVideoEnabled)
        {
            return false;
        }

        var videoPath = AppState.SettingsService.GetCustomIntroVideoPath(settings)
            ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Startup.mp4");
        if (!File.Exists(videoPath))
        {
            AppLog.Warn($"未找到启动视频：{videoPath}");
            return false;
        }

        return TryStartVideoPlayback(videoPath, completion: null, startAppAfterPlayback: true);
    }

    /// <summary>百宝箱专用：强制播放原神机密动画，返回是否完整播放结束。</summary>
    internal Task<bool> PlaySecretVideoAsync()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    dispatched.TrySetResult(await PlaySecretVideoAsync());
                }
                catch (Exception ex)
                {
                    AppLog.Error("调度原神机密动画失败。", ex);
                    dispatched.TrySetResult(false);
                }
            }))
            {
                dispatched.TrySetResult(false);
            }

            return dispatched.Task;
        }

        var videoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "GenshinSecret.mp4");
        if (!File.Exists(videoPath))
        {
            AppLog.Warn($"未找到原神机密动画：{videoPath}");
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryStartVideoPlayback(videoPath, completion, startAppAfterPlayback: false))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private bool TryStartVideoPlayback(
        string videoPath,
        TaskCompletionSource<bool>? completion,
        bool startAppAfterPlayback)
    {
        if (_introVideoActive)
        {
            completion?.TrySetResult(false);
            return false;
        }

        try
        {
            _introPlaybackCompletion = completion;
            _introPlaybackStartsApp = startAppAfterPlayback;
            _introMediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = false,
                IsMuted = false,
                Volume = IntroVideoVolume
            };
            _introMediaPlayer.MediaEnded += OnIntroMediaEnded;
            _introMediaPlayer.MediaFailed += OnIntroMediaFailed;

            IntroMediaPlayerElement.SetMediaPlayer(_introMediaPlayer);
            _introMediaPlayer.Source = MediaSource.CreateFromUri(new Uri(videoPath, UriKind.Absolute));
            IntroVideoOverlay.Visibility = Visibility.Visible;
            _introVideoActive = true;
            _introMediaPlayer.Play();
            IntroSkipButton.Focus(FocusState.Programmatic);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("播放动画失败。", ex);
            EndIntroVideo(completed: false);
            return false;
        }
    }

    private void OnIntroPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_introVideoActive)
        {
            return;
        }

        e.Handled = true;
        EndIntroVideo(completed: false);
    }

    private void OnIntroKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_introVideoActive)
        {
            return;
        }

        e.Handled = true;
        EndIntroVideo(completed: false);
    }

    private void IntroSkipButton_Click(object sender, RoutedEventArgs e) => EndIntroVideo(completed: false);

    private void OnIntroMediaEnded(MediaPlayer sender, object args)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => EndIntroVideo(completed: true));
            return;
        }

        EndIntroVideo(completed: true);
    }

    private void OnIntroMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        AppLog.Warn($"动画播放失败：{args.ErrorMessage}");
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => EndIntroVideo(completed: false));
            return;
        }

        EndIntroVideo(completed: false);
    }

    /// <summary>关闭动画层并释放媒体资源；重复调用安全。</summary>
    private void EndIntroVideo(bool completed)
    {
        var completion = _introPlaybackCompletion;
        var startAppAfterPlayback = _introPlaybackStartsApp;
        _introPlaybackCompletion = null;
        _introPlaybackStartsApp = false;

        if (!_introVideoActive && IntroVideoOverlay.Visibility == Visibility.Collapsed && completion is null)
        {
            return;
        }

        _introVideoActive = false;
        IntroVideoOverlay.Visibility = Visibility.Collapsed;
        DisposeIntroMediaPlayer();

        if (startAppAfterPlayback)
        {
            StartStartupInitialization();
        }

        completion?.TrySetResult(completed);
    }
    private void DisposeIntroMediaPlayer()
    {
        IntroMediaPlayerElement.SetMediaPlayer(null);
        if (_introMediaPlayer is not { } player)
        {
            return;
        }

        player.MediaEnded -= OnIntroMediaEnded;
        player.MediaFailed -= OnIntroMediaFailed;
        player.Source = null;
        player.Dispose();
        _introMediaPlayer = null;
    }

    private void StartStartupInitialization()
    {
        if (_startupInitializationStarted)
        {
            return;
        }

        _startupInitializationStarted = true;
        _ = ResolveSteamPathAfterStartupAsync();

        // 凭据库启用了口令层（第 4 层）时，启动后提示输入口令；未启用时该方法立即返回。
        _ = PromptVaultUnlockAfterStartupAsync();
    }

    /// <summary>
    /// 启动时若凭据库启用了口令层（第 4 层）且本次会话还没解锁，提示输入口令。
    /// 取消则本次不载入账号（仍可在设置页解锁）；未启用口令时不做任何事。
    /// </summary>
    private async Task PromptVaultUnlockAfterStartupAsync()
    {
        try
        {
            if (!CredentialVault.IsPassphraseEnabled || !CredentialVault.IsLocked)
            {
                return;
            }

            while (CredentialVault.IsLocked)
            {
                var passphrase = await VaultPassphraseDialog.AskAsync(
                    RootLayoutGrid.XamlRoot,
                    Loc.T("VaultPw_Unlock_Title"),
                    Loc.T("VaultPw_Unlock_Desc"),
                    Loc.T("Common_Confirm"));

                if (passphrase is null)
                {
                    ShowStatus(Loc.T("VaultPw_Status_LockedHint"), InfoBarSeverity.Warning);
                    return;
                }

                if (!CredentialVault.TryUnlock(passphrase))
                {
                    ShowStatus(Loc.T("VaultPw_Error_Wrong"), InfoBarSeverity.Error);
                    continue;
                }

                AppState.ReloadHistory();
                AppState.ReloadWhiteAccounts();
                ShowStatus(Loc.T("VaultPw_Status_UnlockedOk"), InfoBarSeverity.Success);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("启动时解锁凭据库失败。", ex);
        }
    }

    /// <summary>启动后台解析 Steam 安装路径（失败只记日志，不打扰用户）。</summary>
    private async Task ResolveSteamPathAfterStartupAsync()
    {
        try
        {
            await SteamPathCoordinator.EnsureResolvedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("启动时解析 Steam 安装路径失败。", ex);
        }
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        // 状态可能来自后台线程（如登录后的 CS2 云推送进度），统一封送到 UI 线程。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatus(message, severity));
            return;
        }

        UpdateStatusPanelBrush();
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;

        // 不分等级：任何提示都在 3 秒后自动收起（含警告/错误，避免常驻占位）。
        // 例外：批量查询/清空无效账号这类长流程整段挂在忙碌状态上，单步（CM 连接、逐页抓取）常超过 3 秒，
        // 期间让提示常驻，否则每查一个账号都会「消失一块」再重新弹出来；
        // 流程结束由 OnBusyChanged(false) 再按原规则收起。
        _statusDismissTimer.Stop();
        if (!AppState.IsBusy)
        {
            _statusDismissTimer.Start();
        }
    }

    /// <summary>
    /// 提示条底色：取对话框底色与当前 UI 主题色做不透明混合（不吃透背后的内容），
    /// 每次显示提示前重算，因此在设置里改主题色后立刻生效。混合比例固定 22% 主题色。
    /// </summary>
    private void UpdateStatusPanelBrush()
    {
        var baseColor = Application.Current.Resources.TryGetValue("CustomUiDialogBrush", out var value) && value is SolidColorBrush dialog
            ? dialog.Color
            : Microsoft.UI.Colors.Black;
        var accent = AppState.UiColorService.BaseColor;
        const double tint = 0.22;

        _statusPanelBrush.Color = Windows.UI.Color.FromArgb(
            255,
            (byte)Math.Round(baseColor.R * (1 - tint) + accent.R * tint),
            (byte)Math.Round(baseColor.G * (1 - tint) + accent.G * tint),
            (byte)Math.Round(baseColor.B * (1 - tint) + accent.B * tint));

        if (!_statusPanelBrushAttached)
        {
            _statusPanelBrushAttached = true;
            StatusPanel.Background = _statusPanelBrush;
        }
    }
    /// <summary>有新版本时在「关于」导航项上亮红点，代替曾经常驻底部的更新横幅。</summary>
    private void RefreshUpdateBadge()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshUpdateBadge);
            return;
        }

        AboutNavItem.InfoBadge = AppState.LatestUpdate?.IsUpdateAvailable == true
            ? new InfoBadge()
            : null;
    }

    /// <summary>历史页“载入到登录页”：切到登录页并在导航完成后填充账号。</summary>
    public void LoadAccountIntoLogin(SteamAccountHistoryItem account)
    {
        RootNavigationView.SelectedItem = LoginNavItem;
        NavigateTo("login");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is LoginPage loginPage)
            {
                loginPage.LoadHistoryAccount(account);
                return;
            }

            AppState.LoginPage?.LoadHistoryAccount(account);
        });
    }

    private void EnsureLoginPageInitialized()
    {
        if (AppState.LoginPage is not null)
        {
            return;
        }

        ContentFrame.Navigate(
            typeof(LoginPage),
            null,
            new SuppressNavigationTransitionInfo());
        ContentFrame.BackStack.Clear();
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string pageName)
        {
            NavigateTo(pageName);
        }
    }

    private string? _currentPageName;

    private void NavigateTo(string pageName)
    {
        var pageType = pageName switch
        {
            "history" => typeof(HistoryPage),
            "whiteAccounts" => typeof(HistoryPage),
            "cachedAccounts" => typeof(CachedAccountsPage),
            "loadout" => typeof(LoadoutPage),
            "personalization" => typeof(PersonalizationPage),
            "music" => typeof(MusicPage),
            "clash" => typeof(VpnPage),
            "treasureBox" => typeof(TreasureBoxPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LoginPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType ||
            !string.Equals(_currentPageName, pageName, StringComparison.Ordinal))
        {
            object? parameter = pageName == "whiteAccounts"
                ? HistoryPageScope.WhiteAccounts
                : null;
            ContentFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
            _currentPageName = pageName;
        }
    }

    private void OnBusyChanged(bool isBusy)
    {
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;

        // 长流程收尾：最后一条提示（如「批量查询完成」）按原规则再留 3 秒再收起。
        if (!isBusy && StatusInfoBar.IsOpen)
        {
            _statusDismissTimer.Stop();
            _statusDismissTimer.Start();
        }
    }

    /// <summary>把主题套用到内容根（无打包下 Application.RequestedTheme 不可后置，故走根元素 RequestedTheme）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    /// <summary>让背景图的布局尺寸始终跟随窗口客户区，GIF 和大图也能实时缩放。</summary>
    private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        CustomBackgroundImage.Width = e.NewSize.Width;
        CustomBackgroundImage.Height = e.NewSize.Height;
    }

    /// <summary>按当前设置加载自定义背景图片。</summary>
    internal void ApplyCustomBackground(AppSettings settings)
    {
        ApplyCustomBackground(
            AppState.SettingsService.GetCustomBackgroundImagePath(settings),
            settings.CustomBackgroundEnabled,
            settings.CustomBackgroundOpacity);
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _vpnWatchTimer;

    /// <summary>
    /// VPN 兜底自愈：开关是「已启用」但内核已经不在了（最常见的是另一个窗口退出时把内核一起带走，
    /// 或内核自己崩了）→ 静默重新连回。开关关着、内核在跑、或正在连接时都不动手。
    /// </summary>
    private static async Task EnsureVpnAliveAsync()
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            if (!settings.VpnProxyEnabled || VpnCoreService.IsRunning)
            {
                return;
            }

            AppLog.Info("VPN 开关处于启用状态但内核不在运行，自动连回…");
            await VpnCoreService.TryRestoreOnStartupAsync(ignoreOtherInstances: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"VPN 自愈检查失败：{ex.Message}");
        }
    }

    /// <summary>自定义背景视频的播放器（静音循环）。</summary>
    private readonly MediaPlayer? _backgroundPlayer;

    /// <summary>视频类背景扩展名（与设置页文件选择器、SettingsService 白名单保持一致）。</summary>
    private static readonly string[] CustomBackgroundVideoExtensions =
        [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    /// <summary>应用或隐藏自定义背景：图片走 Image（保留 GIF 动画），视频走 MediaPlayerElement（静音循环）。</summary>
    internal void ApplyCustomBackground(string? path, bool enabled, double opacity)
    {
        // 两层先都收起来：图片与视频互斥，切换后不能叠在一起。
        HideBackgroundImage();
        StopBackgroundVideo();

        if (!enabled || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var clamped = Math.Clamp(opacity, 0.1, 0.9);
        if (CustomBackgroundVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            PlayBackgroundVideo(path, clamped);
            return;
        }

        try
        {
            CustomBackgroundImage.Source = new BitmapImage
            {
                UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute),
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            CustomBackgroundImage.Opacity = clamped;
            CustomBackgroundImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            HideBackgroundImage();
            AppLog.Warn($"加载自定义背景失败：{ex.Message}");
        }
    }

    private void PlayBackgroundVideo(string path, double opacity)
    {
        try
        {
            var player = _backgroundPlayer
                ?? throw new InvalidOperationException("背景视频的播放器未初始化。");

            player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path), UriKind.Absolute));

            CustomBackgroundMedia.Opacity = opacity;
            CustomBackgroundMedia.Visibility = Visibility.Visible;
            player.Play();
        }
        catch (Exception ex)
        {
            StopBackgroundVideo();
            AppLog.Warn($"加载自定义背景视频失败：{ex.Message}");
        }
    }

    private void StopBackgroundVideo()
    {
        try
        {
            if (_backgroundPlayer is { } player)
            {
                player.Pause();
                player.Source = null;   // 释放文件句柄，否则换背景/删文件会被占用
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止背景视频失败：{ex.Message}");
        }

        CustomBackgroundMedia.Opacity = 0;
        CustomBackgroundMedia.Visibility = Visibility.Collapsed;
    }

    private void HideBackgroundImage()
    {
        CustomBackgroundImage.Source = null;
        CustomBackgroundImage.Opacity = 0;
        CustomBackgroundImage.Visibility = Visibility.Collapsed;
    }

    private static ElementTheme ParseTheme(string theme, string? customColor) =>
        UiColorService.ResolveElementTheme(theme, customColor);
    /// <summary>本地化导航项文字；语言切换时由 Loc.LanguageChanged 再次调用。</summary>
    private void RefreshNavText()
    {
        LoginNavItem.Content = Loc.T("Nav_Login");
        HistoryNavItem.Content = Loc.T("Nav_History");
        ManagedAccountsNavItem.Content = Loc.T("Nav_ManagedAccounts");
        CachedAccountsNavItem.Content = Loc.T("Nav_CachedAccounts");
        LoadoutNavItem.Content = Loc.T("Nav_Loadout");
        PersonalizationNavItem.Content = Loc.T("Nav_Personalization");
        MusicNavItem.Content = Loc.T("Nav_Music");
        ClashNavItem.Content = Loc.T("Nav_Clash");
        TreasureBoxNavItem.Content = Loc.T("Nav_TreasureBox");
        SettingsNavItem.Content = Loc.T("Nav_Settings");
        AboutNavItem.Content = Loc.T("Nav_About");
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private unsafe void ConfigureWindowSize()
    {
        s_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var settings = AppState.SettingsService.Load();
        ApplyWindowSizeCore(
            settings.WindowWidth ?? DefaultWindowWidth,
            settings.WindowHeight ?? DefaultWindowHeight);
        if (settings.RememberWindowPosition && settings.WindowX is int savedX && settings.WindowY is int savedY)
        {
            TryMoveToPosition(savedX, savedY);
        }

        // 用 WM_GETMINMAXINFO 子类化实时按当前 DPI 计算最小尺寸，
        // 跨多显示器 / DPI 变化时 OverlappedPresenter.PreferredMinimum*（启动时固定的物理像素）会失效。
        SetWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId, 0);
        Closed += OnClosed;
    }

    /// <summary>按逻辑像素（DIP）应用主窗口宽高，并限制在当前显示器可用区域内。</summary>
    public (int Width, int Height) ApplyWindowSize(int width, int height)
    {
        if (s_hwnd == 0)
        {
            return (width, height);
        }

        return ApplyWindowSizeCore(width, height);
    }

    private (int Width, int Height) ApplyWindowSizeCore(int width, int height)
    {
        var logicalWidth = Math.Clamp(width, MinimumWindowWidth, MaximumWindowWidth);
        var logicalHeight = Math.Clamp(height, MinimumWindowHeight, MaximumWindowHeight);
        var scale = GetDpiForWindow(s_hwnd) / 96.0;

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var minPhysicalWidth = Math.Min((int)Math.Ceiling(MinimumWindowWidth * scale), workArea.Width);
        var minPhysicalHeight = Math.Min((int)Math.Ceiling(MinimumWindowHeight * scale), workArea.Height);
        var targetPhysicalWidth = (int)Math.Ceiling(logicalWidth * scale);
        var targetPhysicalHeight = (int)Math.Ceiling(logicalHeight * scale);
        var physicalWidth = Math.Clamp(targetPhysicalWidth, minPhysicalWidth, workArea.Width);
        var physicalHeight = Math.Clamp(targetPhysicalHeight, minPhysicalHeight, workArea.Height);

        var x = workArea.X + Math.Max(0, (workArea.Width - physicalWidth) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - physicalHeight) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, physicalWidth, physicalHeight));

        var actualWidth = Math.Max(MinimumWindowWidth, (int)Math.Round(physicalWidth / scale));
        var actualHeight = Math.Max(MinimumWindowHeight, (int)Math.Round(physicalHeight / scale));
        return (actualWidth, actualHeight);
    }

    public (int X, int Y) GetCurrentWindowPosition()
    {
        var position = AppWindow.Position;
        return (position.X, position.Y);
    }

    private void TryMoveToPosition(int x, int y)
    {
        var displayArea = DisplayArea.GetFromPoint(
            new PointInt32(x, y),
            DisplayAreaFallback.None);
        if (displayArea is null)
        {
            return;
        }

        var workArea = displayArea.WorkArea;
        var size = AppWindow.Size;
        var maxX = workArea.X + Math.Max(0, workArea.Width - size.Width);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - size.Height);
        var clampedX = Math.Clamp(x, workArea.X, maxX);
        var clampedY = Math.Clamp(y, workArea.Y, maxY);
        AppWindow.Move(new PointInt32(clampedX, clampedY));
    }

    private unsafe void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            if (settings.RememberWindowPosition)
            {
                var position = AppWindow.Position;
                settings.WindowX = position.X;
                settings.WindowY = position.Y;
                AppState.SettingsService.Save(settings);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存窗口位置失败：{ex.Message}");
        }

        _introVideoActive = false;
        _introPlaybackCompletion?.TrySetResult(false);
        _introPlaybackCompletion = null;
        _introPlaybackStartsApp = false;
        DisposeIntroMediaPlayer();
        RemoveWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint SubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var info = (MinMaxInfo*)lParam;
            info->MinTrackSize.X = (int)Math.Ceiling(MinimumWindowWidth * scale);
            info->MinTrackSize.Y = (int)Math.Ceiling(MinimumWindowHeight * scale);
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
