using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class AboutPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private bool _isDownloadingUpdate;
    private long _downloadBytesReceived;
    private long? _downloadTotalBytes;
    private CancellationTokenSource? _downloadCts;

    public AboutPage()
    {
        InitializeComponent();
        AppState.UpdateStateChanged += Render;
        Loc.LanguageChanged += OnLanguageChanged;
        Render();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 进页面时若还没查过更新，补一次（启动时的那次可能已被跳过）。
        if (AppState.LatestUpdate is null && !AppState.IsCheckingForUpdates)
        {
            _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
        }
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；命令式文本（版本/更新状态）重跑 Render 即可换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            Render();
        });
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.CheckForUpdatesAsync(isAutomatic: false);
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var update = AppState.LatestUpdate;
        var url = update?.ArtifactUrl ?? update?.ReleaseUrl;
        if (update is null || string.IsNullOrWhiteSpace(url))
        {
            AppState.ShowStatus(Loc.T("About_NoDownloadInfo"), InfoBarSeverity.Warning);
            return;
        }

        if (!update.IsUpdateAvailable)
        {
            AppState.ShowStatus(Loc.Tf("About_Update_UpToDate_Format", update.LatestTag), InfoBarSeverity.Success);
            return;
        }

        if (_isDownloadingUpdate)
        {
            return;
        }

        // 若最新产物不是安装器，退化为打开发布页。
        if (!url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(update.ArtifactType, "exe-installer", StringComparison.OrdinalIgnoreCase))
        {
            await AppState.OpenUrlAsync(url);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("About_UpdateInstallConfirm_Title"),
            Content = Loc.Tf("About_UpdateInstallConfirm_Content_Format", update.LatestTag),
            PrimaryButtonText = Loc.T("About_UpdateInstallConfirm_Install"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _isDownloadingUpdate = true;
        _downloadBytesReceived = 0;
        _downloadTotalBytes = null;
        _downloadCts = new CancellationTokenSource();
        Render();

        try
        {
                AppState.ShowStatus(Loc.T("About_Update_Downloading"), InfoBarSeverity.Informational);

                var progress = new Progress<UpdateDownloadProgress>(p =>
                {
                    _downloadBytesReceived = p.BytesReceived;
                    _downloadTotalBytes = p.TotalBytes;
                    Render();
                });

                var installerPath = await AppState.UpdateInstallerService.DownloadInstallerAsync(
                    update, progress, _downloadCts.Token);
                AppState.ShowStatus(Loc.T("About_Update_Downloaded"), InfoBarSeverity.Success);

                if (!AppState.UpdateInstallerService.LaunchInstaller(installerPath))
                {
                    AppState.ShowStatus(Loc.T("About_Update_InstallerLaunchFailed"), InfoBarSeverity.Error);
                    return;
                }

                AppState.ShowStatus(Loc.T("About_Update_InstallerLaunched"), InfoBarSeverity.Warning);

                // 安装器已启动：先清掉残留实例，再强制结束当前进程，避免文件占用导致安装失败。
                var current = Process.GetCurrentProcess();
                AppState.UpdateInstallerService.ForceCloseOtherInstances(current.ProcessName, current.Id);
                current.Kill(entireProcessTree: true);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("About_Update_DownloadCanceled"), InfoBarSeverity.Informational);
        }
        catch (TimeoutException ex)
        {
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("About_Update_DownloadFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _isDownloadingUpdate = false;
            _downloadBytesReceived = 0;
            _downloadTotalBytes = null;
            Render();
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private async void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(AppState.LatestUpdate?.ReleaseUrl ?? GitHubUpdateService.ReleasesUrl);
    }

    /// <summary>B 站作者主页（「点点关注不迷路」卡片）。</summary>
    private const string BilibiliAuthorSpaceUrl = "https://space.bilibili.com/674984847";

    private async void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(GitHubUpdateService.RepositoryUrl);
    }

    private async void OpenBilibiliButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(BilibiliAuthorSpaceUrl);
    }

    private Storyboard? _downloadHighlightPulse;

    /// <summary>「下载更新」按钮的高亮提示：有新版时亮起主题色描边并轻微呼吸，否则收起。</summary>
    private void UpdateDownloadHighlightState(bool highlight)
    {
        if (!highlight)
        {
            _downloadHighlightPulse?.Stop();
            UpdateDownloadHighlight.Opacity = 1;
            // 只把描边收成透明：下载按钮本身必须一直看得见（之前折叠外圈把按钮一起藏了）。
            UpdateDownloadHighlight.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }

        // 高亮 = 描边换成当前主题色 + 轻微呼吸。
        UpdateDownloadHighlight.BorderBrush = Application.Current.Resources.TryGetValue("CustomUiBrush", out var accentBrush) &&
            accentBrush is Brush accent
            ? accent
            : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        try
        {
            if (_downloadHighlightPulse is null)
            {
                var pulse = new DoubleAnimation
                {
                    From = 1,
                    To = 0.4,
                    Duration = new Duration(TimeSpan.FromMilliseconds(700)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Storyboard.SetTarget(pulse, UpdateDownloadHighlight);
                Storyboard.SetTargetProperty(pulse, "Opacity");

                _downloadHighlightPulse = new Storyboard();
                _downloadHighlightPulse.Children.Add(pulse);
            }

            _downloadHighlightPulse.Begin();
        }
        catch (Exception ex)
        {
            // 动画失败只影响观感，绝不能让更新提示崩掉页面。
            AppLog.Warn($"更新按钮高亮动画启动失败（不影响使用）：{ex.Message}");
        }
    }

    /// <summary>刷新版本号、更新状态、最新成品、检查时间与更新日志。</summary>
    private void Render()
    {
        var update = AppState.LatestUpdate;
        var isChecking = AppState.IsCheckingForUpdates;

        UpdateCheckingRing.IsActive = isChecking;
        UpdateCheckingRing.Visibility = isChecking ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = !isChecking && !_isDownloadingUpdate;
        var updateAvailable = update is { IsUpdateAvailable: true };
        DownloadUpdateButton.IsEnabled = !isChecking &&
            !_isDownloadingUpdate &&
            updateAvailable &&
            !string.IsNullOrWhiteSpace(update?.ArtifactUrl);

        // 有新版：描边描起来 + 轻微呼吸，提示点这里下载（下载只由用户点击触发）。
        UpdateDownloadHighlightState(updateAvailable && !_isDownloadingUpdate);

        UpdateDownloadProgressPanel.Visibility = _isDownloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (_isDownloadingUpdate)
        {
            if (_downloadTotalBytes is > 0)
            {
                var percent = Math.Clamp(_downloadBytesReceived * 100d / _downloadTotalBytes.Value, 0d, 100d);
                UpdateDownloadProgressBar.IsIndeterminate = false;
                UpdateDownloadProgressBar.Value = percent;
                UpdateDownloadProgressText.Text = Loc.Tf(
                    "About_Update_DownloadProgress_Format",
                    percent.ToString("F1"),
                    FormatHelper.FormatFileSize(_downloadBytesReceived),
                    FormatHelper.FormatFileSize(_downloadTotalBytes.Value));
            }
            else
            {
                UpdateDownloadProgressBar.IsIndeterminate = true;
                UpdateDownloadProgressText.Text = Loc.T("About_Update_DownloadProgress_Unknown");
            }
        }

        AboutVersionText.Text = Loc.Tf("About_Version_Format", update?.CurrentVersion ?? GitHubUpdateService.CurrentVersion);

        if (isChecking)
        {
            AboutUpdateStatusText.Text = Loc.T("About_Update_Connecting");
            AboutUpdateCheckedText.Text = Loc.T("About_CheckedAt_Checking");
            return;
        }

        if (AppState.UpdateCheckError is { } error)
        {
            // 网络/代理不通这类失败给「查看网络或使用VPN」的提示，其它错误照实显示原因。
            AboutUpdateStatusText.Text = AppState.UpdateCheckFailedByNetwork
                ? Loc.T("About_Update_NetworkError")
                : Loc.Tf("About_Update_ConnectFail_Format", error);
            AboutArtifactText.Text = Loc.T("About_Artifact_ReadFail");
            AboutUpdateCheckedText.Text = AppState.UpdateCheckedAt.HasValue
                ? Loc.Tf("About_CheckedAt_Format", FormatHelper.FormatDateTime(AppState.UpdateCheckedAt.Value))
                : Loc.T("About_CheckedAt_Never");
            return;
        }

        if (update is null)
        {
            AboutUpdateStatusText.Text = Loc.T("About_Update_AutoHint");
            AboutArtifactText.Text = Loc.T("About_Artifact_Never");
            AboutUpdateCheckedText.Text = Loc.T("About_CheckedAt_Never");
            return;
        }

        // 兜底路径（仓库没发 latest.json、tag 又不是版本号）会给一句提示，优先显示它，
        // 避免把「无法比较」写成「已是最新」。
        AboutUpdateStatusText.Text = update.MetadataNotice is { Length: > 0 } notice
            ? notice
            : update.IsUpdateAvailable
                ? Loc.Tf("About_Update_Available_Format", update.LatestVersion)
                : Loc.Tf("About_Update_UpToDate_Format", update.LatestVersion);   // 显示版本号，不显示 tag
        AboutArtifactText.Text = string.IsNullOrWhiteSpace(update.ArtifactName)
            ? Loc.T("About_Artifact_NoAsset")
            : Loc.Tf("About_Artifact_Format", update.ArtifactName, FormatHelper.FormatFileSize(update.ArtifactSize));
        AboutUpdateCheckedText.Text = Loc.Tf("About_CheckedAt_Format", FormatHelper.FormatDateTime(update.CheckedAt));
    }
}
