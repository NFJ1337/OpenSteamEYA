using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用级共享状态：服务单例、历史账号缓存、全局忙碌状态与更新检查协调。
/// 页面通过事件订阅状态变化（页面均为 NavigationCacheMode=Required 的常驻实例，无需退订）。
/// </summary>
internal static class AppState
{
    public static SteamLoginService LoginService { get; } = new();
    public static SteamWorkshopService WorkshopService { get; } = new();
    public static SteamLicenseClient LicenseClient { get; } = new();
    public static LegacyEyaLicenseClient LegacyEyaLicenseClient { get; } = new();
    public static LegacyEyaLoginService LegacyEyaLoginService { get; } = new();
    public static JwtTokenService JwtTokenService { get; } = new();
    public static SteamTokenOnlineValidationService TokenOnlineValidationService { get; } = new();
    public static AccountHistoryService AccountHistoryService { get; } = new();
    public static AccountHistoryService WhiteAccountService { get; } = new(string.Empty, "white-accounts.json", "white-avatars", sortOldestFirst: true);
    public static CsPremierScoreService PremierScoreService { get; } = new();
    public static CsLoadoutService LoadoutService { get; } = new();
    public static SteamProfileService ProfileService { get; } = new();
    public static SteamAccountValidationService AccountValidationService { get; } = new();
    public static SteamCredentialsAuthService CredentialsAuthService { get; } = new();
    public static GitHubUpdateService UpdateService { get; } = new();
    public static UpdateInstallerService UpdateInstallerService { get; } = new();
    public static SettingsService SettingsService { get; } = new();
    public static Cs2CloudService Cs2CloudService { get; } = new();
    public static UiColorService UiColorService { get; } = new();
    public static SteamVerifyService VerifyService { get; } = new();

    /// <summary>由 MainWindow 注入，向全局状态栏输出消息。</summary>
    public static Action<string, InfoBarSeverity>? StatusReporter { get; set; }

    /// <summary>
    /// 登录触发的缓存账号后台资料刷新已落盘（SteamLoginService.StartCachedProfileRefresh 完成且有更新）。
    /// 可能在后台线程触发，订阅方自行回 UI 线程。缓存账号页订阅以自动重载，否则登录后立即打开该页
    /// 会一直停留在「未同步」的旧快照上。
    /// </summary>
    public static event Action? CachedLoginAccountsRefreshed;

    public static void NotifyCachedLoginAccountsRefreshed()
    {
        CachedLoginAccountsRefreshed?.Invoke();
    }

    /// <summary>登录页常驻实例，供历史页复用一键查询流程（保持与旧版一致的联动行为）。</summary>
    public static Pages.LoginPage? LoginPage { get; set; }

    public static void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusReporter?.Invoke(message, severity);
    }

    // ---------- 全局忙碌状态 ----------

    public static bool IsBusy { get; private set; }

    public static event Action<bool>? BusyChanged;

    public static void SetBusy(bool isBusy)
    {
        if (IsBusy == isBusy)
        {
            return;
        }

        IsBusy = isBusy;
        BusyChanged?.Invoke(isBusy);
    }

    // 当前忙碌操作的取消源。全部由 UI 线程访问（页面长流程的开始/取消/结束都在 UI 线程），
    // 无需额外同步。
    private static CancellationTokenSource? _busyCts;

    /// <summary>开始一段可取消的忙碌操作：置忙并新建 CTS，返回其 Token 供长任务传入。</summary>
    public static CancellationToken BeginBusyOperation()
    {
        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        SetBusy(true);
        return _busyCts.Token;
    }

    /// <summary>取消当前忙碌操作；无操作进行时为 no-op（取消按钮点击调用）。</summary>
    public static void CancelBusyOperation()
    {
        _busyCts?.Cancel();
    }

    /// <summary>结束忙碌操作：解忙并释放 CTS。幂等，可在 finally 中安全调用。</summary>
    public static void EndBusyOperation()
    {
        SetBusy(false);
        _busyCts?.Dispose();
        _busyCts = null;
    }

    // ---------- 历史账号缓存 ----------

    public static IReadOnlyList<SteamAccountHistoryItem> HistoryAccounts { get; private set; } = [];

    /// <summary>
    /// 希望历史页选中的 SteamID。历史页是懒加载的，登录/查询时发出的选中意图
    /// 在历史页首次构造前没有订阅者，存在这里供历史页构造时取用。
    /// </summary>
    public static string? PendingHistorySelection { get; set; }

    /// <summary>历史账号已重新加载；参数为希望选中的 SteamID（null 表示保持当前选择）。</summary>
    public static event Action<string?>? HistoryChanged;

    public static IReadOnlyList<SteamAccountHistoryItem> WhiteAccounts { get; private set; } = [];

    public static event Action<string?>? WhiteAccountsChanged;

    public static void ReloadWhiteAccounts(string? selectSteamId = null)
    {
        try
        {
            WhiteAccounts = WhiteAccountService.Load();
        }
        catch (Exception ex)
        {
            WhiteAccounts = [];
            ShowStatus(Loc.Tf("AppState_HistoryLoadFailed_Format", ex.Message), InfoBarSeverity.Warning);
        }

        WhiteAccountsChanged?.Invoke(selectSteamId);
    }
    public static void ReloadHistory(string? selectSteamId = null)
    {
        try
        {
            HistoryAccounts = AccountHistoryService.Load();
        }
        catch (Exception ex)
        {
            HistoryAccounts = [];
            ShowStatus(Loc.Tf("AppState_HistoryLoadFailed_Format", ex.Message), InfoBarSeverity.Warning);
        }

        if (!string.IsNullOrWhiteSpace(selectSteamId))
        {
            PendingHistorySelection = selectSteamId;
        }

        HistoryChanged?.Invoke(selectSteamId);
    }

    /// <summary>
    /// 历史账号重载（后台读盘版）：磁盘读取 + 逐字段解密 + JSON 解析都放到线程池，
    /// UI 线程只接收结果并派发事件。
    /// 一键查询完成后的重载走这条路径——旧实现把整段读盘留在 UI 线程，
    /// 后台资料刷新任务正持有账号文件锁时，界面会整段卡住（表现为「查询后卡死」）。
    /// </summary>
    public static async Task ReloadHistoryAsync(string? selectSteamId = null)
    {
        IReadOnlyList<SteamAccountHistoryItem> accounts;
        try
        {
            accounts = await Task.Run(AccountHistoryService.Load);
        }
        catch (Exception ex)
        {
            accounts = [];
            ShowStatus(Loc.Tf("AppState_HistoryLoadFailed_Format", ex.Message), InfoBarSeverity.Warning);
        }

        HistoryAccounts = accounts;
        if (!string.IsNullOrWhiteSpace(selectSteamId))
        {
            PendingHistorySelection = selectSteamId;
        }

        HistoryChanged?.Invoke(selectSteamId);
    }

    public static SteamAccountHistoryItem? FindHistoryAccount(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        return HistoryAccounts.FirstOrDefault(item =>
                   string.Equals(item.SteamId, steamId, StringComparison.OrdinalIgnoreCase))
               ?? WhiteAccounts.FirstOrDefault(item =>
                   string.Equals(item.SteamId, steamId, StringComparison.OrdinalIgnoreCase));    }

    // ---------- 更新检查协调 ----------

    public static GitHubUpdateInfo? LatestUpdate { get; private set; }

    public static bool IsCheckingForUpdates { get; private set; }

    public static string? UpdateCheckError { get; private set; }

    /// <summary>上次检查失败是否属于「网络/代理不通」这类原因（UI 据此给「查看网络或使用VPN」的提示）。</summary>
    public static bool UpdateCheckFailedByNetwork { get; private set; }

    /// <summary>网络类失败判定：连接失败、超时、DNS/代理不通都算。</summary>
    internal static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or System.Net.Sockets.SocketException ||
        exception.InnerException is System.Net.Sockets.SocketException;

    public static DateTimeOffset? UpdateCheckedAt { get; private set; }

    public static event Action? UpdateStateChanged;

    /// <summary>检查完更新后该做什么。</summary>
    internal enum UpdateAction
    {
        /// <summary>版本一致，只提示。</summary>
        SameVersion,

        /// <summary>远端更新：只提示（「关于」页会高亮下载按钮），下载要用户点按钮。</summary>
        UpdateAvailable,

        /// <summary>远端比本机旧（例如刚回滚过 release）：不自动降级，只提示。</summary>
        LocalNewer,

        /// <summary>版本信息不足以比较（例如 tag 不是版本号）：只提示。</summary>
        Unknown,
    }

    /// <summary>
    /// 依据一次检查结果决定后续动作：
    /// 版本一致 → SameVersion；远端更新 → UpdateAvailable（只提示，不自动下载）；
    /// 远端更旧 → LocalNewer；无法比较 → Unknown。
    /// </summary>
    internal static UpdateAction DecideUpdateAction(GitHubUpdateInfo update)
    {
        if (update.MetadataNotice is { Length: > 0 })
        {
            return UpdateAction.Unknown;
        }

        if (string.Equals(update.LatestVersion, update.CurrentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateAction.SameVersion;
        }

        return update.IsUpdateAvailable ? UpdateAction.UpdateAvailable : UpdateAction.LocalNewer;
    }

    public static async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStateChanged?.Invoke();

        if (!isAutomatic)
        {
            ShowStatus(Loc.T("AppState_Update_Checking"), InfoBarSeverity.Informational);
        }

        try
        {
            var update = await UpdateService.CheckLatestAsync();
            LatestUpdate = update;
            UpdateCheckError = null;
            UpdateCheckFailedByNetwork = false;
            UpdateCheckedAt = update.CheckedAt;

            switch (DecideUpdateAction(update))
            {
                case UpdateAction.SameVersion:
                    // 手动检查才提示，避免每次启动都刷一条状态。
                    if (!isAutomatic)
                    {
                        ShowStatus(Loc.Tf("AppState_Update_UpToDate_Format", update.LatestVersion), InfoBarSeverity.Success);
                    }

                    break;

                case UpdateAction.UpdateAvailable:
                    // 只提示，不自动下载：「关于」页会把「下载更新」按钮高亮，等用户点了才下载。
                    ShowStatus(
                        Loc.Tf("AppState_Update_Available_Format", update.LatestVersion),
                        InfoBarSeverity.Warning);
                    break;

                case UpdateAction.LocalNewer:
                    ShowStatus(Loc.Tf("AppState_Update_LocalNewer_Format", update.LatestVersion), InfoBarSeverity.Warning);
                    break;

                default:
                    // 版本无法比较（如仓库没发 latest.json 且 tag 不是版本号）：只把提示带给用户。
                    if (!isAutomatic && update.MetadataNotice is { Length: > 0 } notice)
                    {
                        ShowStatus(notice, InfoBarSeverity.Warning);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateCheckError = ex.Message;
            UpdateCheckFailedByNetwork = IsNetworkFailure(ex);
            UpdateCheckedAt = DateTimeOffset.Now;

            if (!isAutomatic)
            {
                ShowStatus(
                    UpdateCheckFailedByNetwork
                        ? Loc.T("AppState_Update_NetworkError")
                        : Loc.Tf("AppState_Update_CheckFailed_Format", ex.Message),
                    InfoBarSeverity.Error);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateStateChanged?.Invoke();
        }
    }

    public static async Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowStatus(Loc.T("AppState_Url_Invalid"), InfoBarSeverity.Error);
            return;
        }

        var opened = await Windows.System.Launcher.LaunchUriAsync(uri);
        ShowStatus(
            opened ? Loc.T("AppState_Url_Opened") : Loc.T("AppState_Url_OpenFailed"),
            opened ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
