using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
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

    // Informational/Success 状态若干秒后自动收起；Warning/Error 常驻，直到被替换或用户手动关闭。
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(6);
    private readonly DispatcherQueueTimer _statusDismissTimer;
    private MediaPlayer? _introMediaPlayer;
    private bool _introVideoActive;
    private bool _startupInitializationStarted;
    private TaskCompletionSource<bool>? _introPlaybackCompletion;
    private bool _introPlaybackStartsApp;

    public static MainWindow? Instance { get; private set; }

    /// <summary>主窗口句柄，供文件/目录选择器等 WinRT 互操作（InitializeWithWindow）使用；在 ConfigureWindowSize 中赋值。</summary>
    public static nint Hwnd => s_hwnd;

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();
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

        SystemBackdrop = new MicaBackdrop();
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

        _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
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
    }

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

        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;

        _statusDismissTimer.Stop();
        if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            _statusDismissTimer.Start();
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

    /// <summary>应用或隐藏自定义背景；BitmapImage 会保留 GIF 的动画播放。</summary>
    internal void ApplyCustomBackground(string? imagePath, bool enabled, double opacity)
    {
        if (!enabled || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Opacity = 0;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            CustomBackgroundImage.Source = new BitmapImage
            {
                UriSource = new Uri(Path.GetFullPath(imagePath), UriKind.Absolute),
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            CustomBackgroundImage.Opacity = Math.Clamp(opacity, 0.1, 0.9);
            CustomBackgroundImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Opacity = 0;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            AppLog.Warn($"加载自定义背景失败：{ex.Message}");
        }
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
