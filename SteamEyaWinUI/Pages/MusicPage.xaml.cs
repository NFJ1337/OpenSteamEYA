using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 「轻松音乐」：内嵌各音乐平台的<b>官方网页</b>。
///   · 登录、会员与播放全部由平台官方页面处理，本程序不介入账号与付费，也不保存账号密码；
///   · 登录态由 WebView2 保存在数据目录（%APPDATA%\SteamEYA\webview2），下次打开仍是登录状态；
///   · 平台选择用与设置页「选择语言」同一套下拉样式（DropDownButton + MenuFlyout）。
/// </summary>
public sealed partial class MusicPage : Page, INotifyPropertyChanged
{
    // 只列「网页里能直接听歌」的平台（实测过）：
    //   网易云音乐 / QQ 音乐：网页版有真正的播放器，登录自己账号即可在线听；
    //   汽水音乐：网页只有下载引导（没有播放器）—— 想听只能装它的 PC 客户端；
    //   酷狗音乐：网页是门户页（排行榜/精选集/MV），同样没有播放器。
    // 这两个保留在清单里但 Enabled=false（下拉不显示）；哪天它们上了网页播放器，
    // 把 false 改成 true 就能用，界面与逻辑都不用动。
    private static readonly MusicPlatform[] MusicPlatforms =
    [
        new("netease", "Personalization_Music_Platform_Netease", "https://music.163.com", true),
        new("qq", "Personalization_Music_Platform_QQ", "https://y.qq.com", true),
        new("qishui", "Personalization_Music_Platform_Qishui", "https://qishui.douyin.com", false),
        new("kugou", "Personalization_Music_Platform_Kugou", "https://www.kugou.com", false)
    ];

    private sealed record MusicPlatform(string Key, string NameKey, string Url, bool Enabled);

    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 2.0;

    // 默认缩放：音乐平台的网页版大多按固定宽度排版（约 980~1100px），100% 时窗口一窄就出横向滚动条。
    // 默认压到 85%，等效宽度变大，正常窗口下横向滚动条就不出现了；还能用工具条的放大/缩小微调。
    private const double DefaultZoom = 0.85;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private string _platformKey = string.Empty;
    private double _zoom = DefaultZoom;
    private bool _muted;
    private bool _platformMenuBuilt;
    private bool _webViewReady;
    private bool _webViewLoading;

    public MusicPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        BuildPlatformMenu();

        // 进入本页才初始化网页：不拖慢启动，也不在别的页面白占资源。
        _ = EnsureWebViewAsync();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            _platformMenuBuilt = false;
            BuildPlatformMenu();
        });
    }

    /// <summary>构建平台下拉（菜单项文本按当前语言），并回填当前选中平台。</summary>
    private void BuildPlatformMenu()
    {
        if (MusicPlatformFlyout is null || MusicPlatformButtonText is null)
        {
            return;
        }

        if (!_platformMenuBuilt)
        {
            var enabled = MusicPlatforms.Where(item => item.Enabled).ToArray();
            if (enabled.Length == 0)
            {
                return;
            }

            // 默认选中：优先「设为默认」记住的平台，其次清单里的第一个。
            var preferred = AppState.SettingsService.Load().MusicPlatform;
            _platformKey = enabled.Any(item => item.Key == preferred)
                ? preferred!
                : enabled[0].Key;

            MusicPlatformFlyout.Items.Clear();
            foreach (var platform in enabled)
            {
                var item = new MenuFlyoutItem
                {
                    Text = Loc.T(platform.NameKey),
                    Tag = platform.Key
                };
                item.Click += MusicPlatformMenuItem_Click;
                MusicPlatformFlyout.Items.Add(item);
            }

            _platformMenuBuilt = true;
        }

        MusicPlatformButtonText.Text = Loc.T(CurrentPlatform(_platformKey).NameKey);
        UpdateSetDefaultState();
    }

    private static MusicPlatform CurrentPlatform(string? key)
    {
        var enabled = MusicPlatforms.Where(item => item.Enabled).ToArray();
        return enabled.FirstOrDefault(item => item.Key == key) ?? enabled[0];
    }

    private void MusicPlatformMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string key } || string.Equals(key, _platformKey, StringComparison.Ordinal))
        {
            return;
        }

        _platformKey = key;
        MusicPlatformButtonText.Text = Loc.T(CurrentPlatform(key).NameKey);
        UpdateSetDefaultState();
        Navigate();
    }

    /// <summary>把当前平台记为默认：下次进本页自动选中它。</summary>
    private void MusicSetDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_platformKey))
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.MusicPlatform = _platformKey;
        AppState.SettingsService.Save(settings);
        UpdateSetDefaultState();
        AppState.ShowStatus(
            Loc.Tf("Music_Status_DefaultSaved_Format", Loc.T(CurrentPlatform(_platformKey).NameKey)),
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// 刷新「设为默认」按钮的前置图标：当前平台就是默认平台时显示<b>锁定</b>（已设为默认），
    /// 选了别的平台则回到图钉（可以再点一次把它设为默认）。
    /// </summary>
    private void UpdateSetDefaultState()
    {
        if (MusicSetDefaultIcon is null || MusicSetDefaultButton is null)
        {
            return;
        }

        var defaultKey = AppState.SettingsService.Load().MusicPlatform;
        var isDefault = !string.IsNullOrEmpty(_platformKey) &&
                        string.Equals(_platformKey, defaultKey, StringComparison.Ordinal);

        MusicSetDefaultIcon.Glyph = isDefault ? "\uE72E" : "\uE718";   // 锁定 / 图钉
        ToolTipService.SetToolTip(
            MusicSetDefaultButton,
            Loc.T(isDefault ? "Music_Btn_IsDefault_Tip" : "Music_Btn_SetDefault_Tip"));
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady || _webViewLoading)
        {
            return;
        }

        _webViewLoading = true;
        try
        {
            // 登录态要落在自己的数据目录里：默认位置在 exe 旁边，安装目录不可写而且会污染。
            var userDataFolder = System.IO.Path.Combine(AppPaths.DataRoot, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                userDataFolder,
                new CoreWebView2EnvironmentOptions());
            await MusicWebView.EnsureCoreWebView2Async(environment);

            // 页面内跳转会把 body 的 zoom 丢掉，导航结束后重新套用一次。
            MusicWebView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                if (Math.Abs(_zoom - 1.0) > 0.001)
                {
                    await ApplyZoomScriptAsync();
                }
            };

            _webViewReady = true;
            Navigate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"初始化音乐网页失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("Personalization_Music_Error_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _webViewLoading = false;
        }
    }

    private void Navigate()
    {
        if (!_webViewReady || string.IsNullOrEmpty(_platformKey))
        {
            return;
        }

        MusicWebView.CoreWebView2.Navigate(CurrentPlatform(_platformKey).Url);
    }

    // ---------- 工具条：刷新 / 后退 / 回首页 / 静音 / 缩放 ----------

    private void MusicRefreshButton_Click(object sender, RoutedEventArgs e) => MusicWebView.CoreWebView2?.Reload();

    private void MusicBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (MusicWebView.CoreWebView2?.CanGoBack == true)
        {
            MusicWebView.CoreWebView2.GoBack();
        }
    }

    /// <summary>回当前平台的首页（和刚进来时一样）。</summary>
    private void MusicHomeButton_Click(object sender, RoutedEventArgs e) => Navigate();

    private void MusicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (MusicWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        _muted = !_muted;
        core.IsMuted = _muted;
        MusicMuteIcon.Glyph = _muted ? "\uE74F" : "\uE767";        // 静音 / 有声
        ToolTipService.SetToolTip(MusicMuteButton, Loc.T(_muted ? "Music_Btn_Unmute" : "Music_Btn_Mute"));
    }

    private async void MusicZoomOutButton_Click(object sender, RoutedEventArgs e) => await ApplyZoomAsync(-ZoomStep);

    private async void MusicZoomInButton_Click(object sender, RoutedEventArgs e) => await ApplyZoomAsync(ZoomStep);

    /// <summary>
    /// 网页缩放（不是窗口缩放）：0.5~2.0，方便把歌单/歌词放大看。
    /// WinUI 的 WebView2 控件没有暴露 ZoomFactor，所以用页面自身的 zoom 实现；
    /// 页面内跳转会重置，所以每次导航结束会重新套用（见 EnsureWebViewAsync）。
    /// </summary>
    private async Task ApplyZoomScriptAsync()
    {
        if (MusicWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        var value = _zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await core.ExecuteScriptAsync($"document.body.style.zoom='{value}'");
    }

    private async Task ApplyZoomAsync(double delta)
    {
        _zoom = Math.Clamp(Math.Round(_zoom + delta, 2), MinZoom, MaxZoom);
        await ApplyZoomScriptAsync();
    }
}