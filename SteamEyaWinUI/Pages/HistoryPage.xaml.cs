using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SteamEyaWinUI.Pages;

public sealed partial class HistoryPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<SteamAccountHistoryItem> _viewItems = [];

    /// <summary>
    /// 当前从磁盘加载的完整列表快照（即 AppState.HistoryAccounts）。搜索/过滤只在此内存列表上做，
    /// 每个键击不再回读磁盘；仅 HistoryChanged 与进入页面时才刷新此快照。
    /// </summary>
    private IReadOnlyList<SteamAccountHistoryItem> _allItems = [];

    /// <summary>搜索框去抖计时器：输入停止约 300ms 后才执行一次过滤，避免逐键击全量重建。</summary>
    private readonly DispatcherQueueTimer _searchDebounceTimer;

    /// <summary>冷却倒计时刷新计时器：每秒通知在冷却中的账号刷新其倒计时绑定；无冷却账号时自动停表。</summary>
    private readonly DispatcherQueueTimer _cooldownTimer;

    /// <summary>上一 tick 仍在冷却的账号集合：用于在剩余归零那一 tick 补发一次刷新，把卡片刷成「无冷却」终态。</summary>
    private readonly HashSet<SteamAccountHistoryItem> _cooldownLiveLastTick = [];

    /// <summary>上一 tick 详情面板选中账号是否在冷却中（详情冷却行是命令式赋值，同样需要归零 tick 补刷一次）。</summary>
    private bool _detailCooldownLiveLastTick;

    /// <summary>当前详情面板备注框绑定的账号（失焦保存时据此定位，避免选择已切换后写错账号）。</summary>
    private SteamAccountHistoryItem? _noteAccount;

    /// <summary>「未分组」筛选项的 Tag 哨兵值（区别于 null=全部、具体分组 ID）。</summary>
    private const string UngroupedSentinel = "__ungrouped__";

    /// <summary>当前加载的分组定义（按 Order/名称排序），来自 settings.json。</summary>
    private List<AccountGroup> _groups = [];

    /// <summary>当前分组筛选：null=全部 / <see cref="UngroupedSentinel"/>=未分组 / 其它=分组 ID。</summary>
    private string? _groupFilter;

    /// <summary>重建筛选下拉时抑制 SelectionChanged 回调，避免重入重建。</summary>

    /// <summary>页面是否处于活动（已导航到、未离开）状态，用于不可见时延迟重建。</summary>
    private bool _isActive;

    /// <summary>
    /// 不可见期间收到 HistoryChanged 时只更新快照并记下待选中 SteamID（null 表示保持当前选择），
    /// 不做整列表重建；下次 OnNavigatedTo 经 ReloadHistory 统一重建一次。
    /// </summary>
    private string? _pendingSelectSteamId;

    /// <summary>
    /// 对话框流程重入门闩：同一 XamlRoot 同时只能打开一个 ContentDialog，二次 ShowAsync 直接抛异常。
    /// 对话框流程在等待用户选择期间存在挂起窗口，期间再点删除/清空等操作都必须拦下。
    /// </summary>
    private bool _isDialogFlowActive;

    /// <summary>
    /// 批量选择集（账号键）。与 ListView 的单选（详情焦点）解耦：勾选卡片左上角复选框进入此集，
    /// 驱动卡片黑框+对勾与底部批量操作栏。按键存储以便跨列表重建（换新实例）保留勾选。
    /// </summary>
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

    public HistoryPage()
    {
        InitializeComponent();
        HistoryAccountList.ItemsSource = _viewItems;

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += (_, _) => RebuildView(GetSelectedSteamId());

        _cooldownTimer = DispatcherQueue.CreateTimer();
        _cooldownTimer.Interval = TimeSpan.FromSeconds(1);
        _cooldownTimer.IsRepeating = true;
        _cooldownTimer.Tick += (_, _) => OnCooldownTick();

        AppState.HistoryChanged += OnHistoryChanged;
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        Loc.LanguageChanged += OnLanguageChanged;

        // 取用页面创建前积累的选中意图（首次构造时 _viewItems 为空，GetSelectedSteamId 必为 null）。
        var pending = AppState.PendingHistorySelection;
        AppState.PendingHistorySelection = null;
        _allItems = AppState.HistoryAccounts;
        LoadGroups();
        RebuildView(pending);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；命令式文本（详情/批量栏/摘要/控件状态）重跑对应方法即可换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            UpdateSummaryTexts();
            RebuildGroupFilterMenu();
            UpdateBatchBar();
            UpdateDetail();
            UpdateControlsEnabled();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;

        // 分组定义存于 settings.json（无变更事件），进入页面时重新加载以反映在别处的编辑。
        LoadGroups();

        // 不可见期间累积的待选中意图优先于当前选择；ReloadHistory 会刷新快照并触发重建。
        var select = _pendingSelectSteamId ?? GetSelectedSteamId();
        _pendingSelectSteamId = null;
        AppState.ReloadHistory(select);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
        // 停掉去抖与倒计时计时器，避免离开页面后仍对离屏页面触发无谓刷新/重建。
        _searchDebounceTimer.Stop();
        _cooldownTimer.Stop();

        // 悬停时被导航走，PointerExited 可能不触发；清掉残留悬停态，避免回来后空心勾选圈残留。
        foreach (var account in _allItems)
        {
            account.IsPointerOver = false;
        }
    }

    private void CancelHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ShowStatus(Loc.T("History_Status_Canceling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    private void OnHistoryChanged(string? selectSteamId)
    {
        // 进入页面时 OnNavigatedTo 触发的 ReloadHistory 会先于此处把 _isActive 置 true，正常重建；
        // 页面不可见时（其它页触发的后台刷新）只更新内存快照并记下待选中意图，下次 OnNavigatedTo 再重建。
        _allItems = AppState.HistoryAccounts;
        if (!_isActive)
        {
            _pendingSelectSteamId = selectSteamId ?? _pendingSelectSteamId;
            return;
        }

        RebuildView(selectSteamId ?? GetSelectedSteamId());
    }

    private void RebuildView(string? selectSteamId)
    {
        // 过滤只在内存快照上做，不回读磁盘。先按分组筛选，再按搜索词过滤。
        var source = _allItems;
        var filter = HistorySearchBox.Text.Trim();
        var filtered = source
            .Where(MatchesGroupFilter)
            .Where(account => string.IsNullOrEmpty(filter) || Matches(account, filter))
            .ToList();

        // 批量勾选集按账号键跨重建保留：先剔除已不存在的账号，再把勾选状态套用到（可能是新的）实例。
        var liveKeys = source
            .Select(AccountHistoryService.GetAccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.IntersectWith(liveKeys);
        foreach (var account in source)
        {
            account.IsSelected = _checkedKeys.Contains(AccountHistoryService.GetAccountKey(account));
        }

        // 记住当前单选（详情焦点）的账号键，重建后恢复——后台资料同步等延迟刷新不应丢失当前查看的账号。
        var activeKey = !string.IsNullOrWhiteSpace(selectSteamId)
            ? $"id:{selectSteamId}"
            : HistoryAccountList.SelectedItem is SteamAccountHistoryItem current
                ? AccountHistoryService.GetAccountKey(current)
                : null;

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var active = activeKey is null
            ? null
            : _viewItems.FirstOrDefault(account =>
                string.Equals(AccountHistoryService.GetAccountKey(account), activeKey, StringComparison.OrdinalIgnoreCase));
        // 没有选中意图（或原选中项已被过滤/删除）时保持未选中：不兜底选第一项，
        // 否则用户未点任何账号，详情面板就顶着第一个账号的头像与资料。
        HistoryAccountList.SelectedItem = active;

        HistoryEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummaryTexts();

        UpdateBatchBar();
        UpdateDetail();
        UpdateControlsEnabled();
        UpdateCooldownTimer();
    }

    /// <summary>按当前快照重算页头摘要与空状态文案（语言切换时也复用此处刷新已显示文本）。</summary>
    private void UpdateSummaryTexts()
    {
        var hasAny = _allItems.Count > 0;
        HistoryTitleText.Text = Loc.T("History_Title");
        HistoryEmptyText.Text = hasAny ? Loc.T("History_Empty_NoMatch") : Loc.T("History_Empty_Title");
        HistoryEmptyHintText.Text = hasAny ? Loc.T("History_Empty_NoMatch_Hint") : Loc.T("History_Empty_Hint");
        HistorySummaryText.Text = hasAny
            ? Loc.Tf("History_Subtitle_Count_Format", _allItems.Count)
            : Loc.T("History_Subtitle");
    }

    private static bool Matches(SteamAccountHistoryItem account, string filter)
    {
        return Contains(account.AccountName, filter) ||
            Contains(account.PersonaName, filter) ||
            Contains(account.SteamId, filter) ||
            Contains(account.Note, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrEmpty(value) &&
            value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // 去抖：连续输入只 Start/重置计时器，停止输入约 300ms 后才真正过滤重建。
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    // 「刷新」按钮的网络资料同步是否进行中（防连点叠加多轮抓取；UI 线程独占访问，无需同步）。
    private bool _profileRefreshInFlight;

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCheckedSelectionForRefresh();
        // 先秒级重读磁盘保持原有手感；随后后台重新抓取全部账号的昵称/头像——
        // 此前该按钮只重读磁盘，资料一旦落盘便再无任何入口更新，账号改名/换头像后界面永远停留旧值。
        AppState.ReloadHistory(GetSelectedSteamId());

        // 同步已在进行时不谎报「已刷新」成功：重新提示进行中状态（顺带恢复被其它消息顶掉的提示）。
        if (_profileRefreshInFlight)
        {
            AppState.ShowStatus(Loc.T("History_Status_ProfileSyncing"), InfoBarSeverity.Informational);
            return;
        }

        var steamIds = _allItems
            .Select(item => item.SteamId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (steamIds.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_Refreshed"), InfoBarSeverity.Success);
            return;
        }

        _profileRefreshInFlight = true;
        AppState.ShowStatus(Loc.T("History_Status_ProfileSyncing"), InfoBarSeverity.Informational);
        try
        {
            var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync(steamIds);
            if (refreshed > 0)
            {
                AppState.ReloadHistory(GetSelectedSteamId());
                AppState.ShowStatus(
                    Loc.Tf("History_Status_ProfileSyncDone_Format", refreshed), InfoBarSeverity.Success);
            }
            else
            {
                AppState.ShowStatus(Loc.T("History_Status_ProfileSyncNone"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("历史账号资料刷新失败。", ex);
            AppState.ShowStatus(
                Loc.Tf("History_Status_ProfileSyncFail_Format", ex.Message), InfoBarSeverity.Warning);
        }
        finally
        {
            _profileRefreshInFlight = false;
        }
    }

    private void HistoryAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private void ExportAccountsToClipboard(IReadOnlyList<SteamAccountHistoryItem> accounts)
    {
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToExport"), InfoBarSeverity.Error);
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            accounts.Select(account => $"{account.AccountName}----{account.EyaToken}"));

        var package = new DataPackage();
        package.SetText(text);

        try
        {
            // 导出内容为敏感凭据：不进 Win+V 剪贴板历史、不随云剪贴板漫游。
            var options = new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };
            if (!Clipboard.SetContentWithOptions(package, options))
            {
                // 个别系统配置下 SetContentWithOptions 可能返回 false；回退到普通写入保证导出可用。
                Clipboard.SetContent(package);
            }
        }
        catch (COMException)
        {
            AppState.ShowStatus(Loc.T("History_Status_ClipboardWriteFail"), InfoBarSeverity.Error);
            return;
        }

        try
        {
            // 不 Flush 的话内容由本进程延迟渲染，应用退出后剪贴板就空了；Flush 失败不影响本次粘贴。
            Clipboard.Flush();
        }
        catch (COMException)
        {
        }

        var statusMessage = accounts.Count == 1
            ? Loc.Tf("History_Status_Exported_One_Format", accounts[0].AccountTitle)
            : Loc.Tf("History_Status_Exported_Many_Format", accounts.Count);
        AppState.ShowStatus(statusMessage, InfoBarSeverity.Success);
    }

    private async Task DeleteAccountsWithConfirmAsync(IReadOnlyList<SteamAccountHistoryItem> accounts)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToDelete"), InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join("、", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? Loc.Tf("History_Delete_Confirm_Many_Format", nameText, accounts.Count)
            : Loc.Tf("History_Delete_Confirm_Few_Format", nameText, accounts.Count);

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_Delete_Dialog_Title"),
            Content = summary,
            PrimaryButtonText = Loc.T("Common_Delete"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // 删除前把这些键移出批量选择集，避免重建后残留在已选状态。
            foreach (var account in accounts)
            {
                _checkedKeys.Remove(AccountHistoryService.GetAccountKey(account));
            }

            var removed = AppState.AccountHistoryService.DeleteAccounts(accounts);
            AppState.ReloadHistory();
            AppState.ShowStatus(Loc.Tf("History_Status_Deleted_Format", removed), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_DeleteFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    // ---------- 卡片悬停 / 左上角勾选 / 单卡操作 ----------

    private static SteamAccountHistoryItem? CardItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as SteamAccountHistoryItem;

    private void HistoryCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = true;
        }
    }

    private void HistoryCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = false;
        }
    }

    private void HistoryCardCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        var key = AccountHistoryService.GetAccountKey(account);
        if (account.IsSelected)
        {
            account.IsSelected = false;
            _checkedKeys.Remove(key);
        }
        else
        {
            account.IsSelected = true;
            _checkedKeys.Add(key);
        }

        UpdateBatchBar();
    }

    private List<SteamAccountHistoryItem> GetCheckedAccounts() =>
        _viewItems
            .Where(account => _checkedKeys.Contains(AccountHistoryService.GetAccountKey(account)))
            .ToList();

    private void ClearCheckedSelectionForRefresh()
    {
        var currentKeys = _allItems
            .Select(AccountHistoryService.GetAccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.ExceptWith(currentKeys);
        foreach (var account in _allItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
    }

    private void UpdateBatchBar()
    {
        var checkedCount = GetCheckedAccounts().Count;
        var showBatchBar = checkedCount > 0;
        BatchActionBarTop.Visibility = showBatchBar ? Visibility.Visible : Visibility.Collapsed;
        BatchActionBarBottom.Visibility = showBatchBar ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = Loc.Tf("Common_Selected_Format", checkedCount);
        UpdateControlsEnabled();
    }

    private void CardExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        ExportAccountsToClipboard(new[] { account });
    }

    private async void CardDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            await DeleteAccountsWithConfirmAsync(new[] { account });
        }
    }

    /// <summary>
    /// 「Steam中查看」：用当前账号的 EYA 令牌登录 Steam 网页，
    /// 再弹出一个小窗口打开 https://steamcommunity.com/my/ ，打开即是已登录状态。
    /// </summary>
    private async void OpenSteamBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(account.EyaToken))
        {
            AppState.ShowStatus(Loc.T("History_SteamBrowser_NoCredential"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus(Loc.T("History_SteamBrowser_Logging"), InfoBarSeverity.Informational);
        try
        {
            var session = await SteamBrowserLoginService.CreateSessionFromTokenAsync(account.EyaToken, cancellationToken);
            new SteamBrowserWindow(session).Activate();
            AppState.ShowStatus(Loc.T("History_SteamBrowser_Opened"), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_QueryCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Steam 网页登录失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("History_SteamBrowser_Error_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void OneClickHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        await QueryHistoryAccountAsync(account);
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var count = _allItems.Count;
        if (count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToClear"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_ClearAll_Dialog_Title"),
            Content = Loc.Tf("History_ClearAll_Dialog_Content_Format", count),
            PrimaryButtonText = Loc.T("Common_Clear"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        try
        {
            var removed = AppState.AccountHistoryService.ClearAll();
            AppState.ReloadHistory();
            AppState.ShowStatus(
                Loc.Tf("History_Status_Cleared_Format", removed),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(
                Loc.Tf("History_Status_ClearFail_Format", ex.Message),
                InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 异常链里是不是「CM 会话被顶替」。判定用语言中立的标记 Data["CmConflict"]（与 SteamCmClient 里
    /// 抛出时写的一致），不去匹配本地化文案——多语言下文案会变，标记不会。
    /// </summary>
    private static bool IsCmSessionReplaced(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data["CmConflict"] is string conflict &&
                string.Equals(conflict, "SessionReplaced", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async void ClearInvalidAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var accounts = _allItems.ToList();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToTest"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_ClearInvalid_Dialog_Title"),
            Content = Loc.Tf("History_ClearInvalid_Dialog_Content_Format", accounts.Count) +
                Environment.NewLine +
                Environment.NewLine +
                Loc.T("History_ClearInvalid_Dialog_Content_Note"),
            PrimaryButtonText = Loc.T("History_ClearInvalid_Dialog_Primary"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        if (AppState.LoginPage is not { } loginPage)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginPageNotReady"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var invalid = new List<SteamAccountHistoryItem>();
        var tested = 0;
        string? networkError = null;
        var canceled = false;

        try
        {
            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                AppState.ShowStatus(
                    Loc.Tf("History_Status_Testing_Format", tested + 1, accounts.Count, account.AccountTitle),
                    InfoBarSeverity.Informational);

                try
                {
                    // 走「一键查询」完全相同的逻辑：查不动 = 这个账号一键查询也用不了；
                    // 成功时顺带把最新状态写回历史账号库。
                    await loginPage.QueryAndSaveCsStatusAsync(
                        account.AccountName,
                        account.EyaToken,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (SteamCmException ex) when (ex.IsTokenFailure)
                {
                    // 令牌被 Steam 拒绝：与「一键查询」同样的判定 → 视为无效账号。
                    tested++;
                    invalid.Add(account);
                    AppLog.Warn($"清空无效账号：{account.AccountTitle} 被判定无效（{ex.Message}）。");
                    continue;
                }
                catch (Exception ex) when (IsCmSessionReplaced(ex))
                {
                    // Steam 明确回了「会话被顶替」（账号正在别处跑，例如本机 Steam 客户端 / CS2 里）。
                    // 按用户要求：这种直接当无效账号删掉，并且继续测后面的账号，不再整批停在这一条上。
                    tested++;
                    invalid.Add(account);
                    AppLog.Warn($"清空无效账号：{account.AccountTitle} 的 CM 会话被顶替，按无效账号删除（{ex.Message}）。");
                    continue;
                }
                catch (Exception ex)
                {
                    // 其它失败（网络 / GC / 网页超时）不代表账号无效：不删，按既有文案停止剩余测试。
                    networkError = ex.Message;
                    break;
                }

                tested++;
            }

            var removed = invalid.Count > 0
                ? AppState.AccountHistoryService.DeleteAccounts(invalid)
                : 0;
            if (invalid.Count > 0)
            {
                AppLog.Info($"清空无效账号：测试 {tested} 个，判定无效 {invalid.Count} 个（{string.Join("、", invalid.Select(item => item.AccountTitle))}），实际删除 {removed} 个。");
            }
            if (removed > 0)
            {
                AppState.ReloadHistory();
            }

            if (networkError is not null)
            {
                AppLog.Warn("批量测试历史账号时遇到网络错误，已停止：" + networkError);
                AppState.ShowStatus(
                    removed > 0
                        ? Loc.Tf("History_Status_TestNetworkErr_WithRemoved_Format", tested, networkError, removed)
                        : Loc.Tf("History_Status_TestNetworkErr_Format", tested, networkError),
                    InfoBarSeverity.Error);
                return;
            }

            if (canceled)
            {
                AppState.ShowStatus(
                    removed > 0
                        ? Loc.Tf("History_Status_TestCanceled_WithRemoved_Format", tested, removed)
                        : Loc.Tf("History_Status_TestCanceled_Format", tested),
                    InfoBarSeverity.Informational);
                return;
            }

            AppState.ShowStatus(
                removed > 0
                    ? Loc.Tf("History_Status_TestDone_WithRemoved_Format", tested, removed)
                    : Loc.Tf("History_Status_TestDone_AllValid_Format", tested),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(
                Loc.Tf("History_Status_ClearInvalidFail_Format", ex.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void ClearCheckedSelection()
    {
        _checkedKeys.Clear();
        foreach (var account in _allItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
    }

    private void BatchSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var account in _viewItems)
        {
            account.IsSelected = true;
            _checkedKeys.Add(AccountHistoryService.GetAccountKey(account));
        }

        UpdateBatchBar();
    }

    private void BatchClearButton_Click(object sender, RoutedEventArgs e) => ClearCheckedSelection();

    private void BatchExportButton_Click(object sender, RoutedEventArgs e) =>
        ExportAccountsToClipboard(GetCheckedAccounts());

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e) =>
        await DeleteAccountsWithConfirmAsync(GetCheckedAccounts());

    private async Task<SteamAccountValidationResult> ValidateHistoryAccountAsync(
        SteamAccountHistoryItem account,
        CancellationToken cancellationToken,
        int current = 1,
        int total = 1)
    {
        if (string.IsNullOrWhiteSpace(account.Password))
        {
            throw new InvalidOperationException(Loc.T("Creds_Error_PasswordRequired"));
        }

        async Task<string?> GuardProvider(SteamGuardPrompt prompt, CancellationToken token)
        {
            if (prompt.Type == SteamGuardType.DeviceCode &&
                !string.IsNullOrWhiteSpace(account.SharedSecret))
            {
                var code = SteamTotp.GenerateAuthCode(account.SharedSecret);
                if (!string.IsNullOrEmpty(code))
                {
                    return code;
                }
            }

            var emailHint = prompt.Type == SteamGuardType.EmailCode ? account.Email : null;
            return await PromptGuardCodeAsync(prompt, token, emailHint);
        }

        var progress = new Progress<string>(message =>
        {
            var detail = string.IsNullOrWhiteSpace(message)
                ? account.AccountTitle
                : $"{account.AccountTitle} - {message}";
            var status = total > 1
                ? Loc.Tf("History_Status_BatchQuerying_Format", current, total, detail)
                : Loc.Tf("History_Status_Querying_Format", detail);
            AppState.ShowStatus(status, InfoBarSeverity.Informational);
        });
        var result = await AppState.AccountValidationService.QueryAsync(
            account.AccountName,
            account.Password,
            account.EyaToken,
            GuardProvider,
            progress,
            cancellationToken);

        DateTimeOffset? tokenExpiresAt = null;
        if (!string.IsNullOrWhiteSpace(result.RefreshToken))
        {
            tokenExpiresAt = AppState.JwtTokenService.Inspect(result.RefreshToken).ExpiresAt;
        }

        await Task.Run(
            () => AppState.AccountHistoryService.SaveValidationStatus(
                account.AccountName,
                result.SteamId,
                result.CooldownSeconds ?? 0,
                result.VacBanned,
                result.RefreshToken,
                tokenExpiresAt),
            cancellationToken);
        return result;
    }
    private async Task RefreshValidatedProfilesAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var ids = steamIds
            .Where(steamId => !string.IsNullOrWhiteSpace(steamId))
            .Select(steamId => steamId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        AppState.ShowStatus(Loc.T("History_Status_ProfileSyncing"), InfoBarSeverity.Informational);
        try
        {
            await AppState.AccountHistoryService.RefreshProfilesAsync(ids);
        }
        catch (Exception ex)
        {
            // 资料同步是查询的附加步骤，失败不能影响冷却/VAC结果落盘与界面刷新。
            AppLog.Warn($"查询后同步 Steam 资料失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 一键查询整体时限：Steam CM/GC 不通时底层会长时间重试（实测超过 10 分钟不返回），
    /// 这里给一个明确上限，到点提示超时，不让界面无限等待。
    /// </summary>
    private static readonly TimeSpan OneClickQueryTimeout = TimeSpan.FromSeconds(180);

    private async Task QueryHistoryAccountAsync(SteamAccountHistoryItem account)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        // 与全局取消（取消按钮）联动：用户点取消照样能中断，没点取消时到点由超时兜底。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(OneClickQueryTimeout);
        AppState.ShowStatus(Loc.Tf("History_Status_Querying_Format", account.AccountTitle), InfoBarSeverity.Informational);

        try
        {
            if (AppState.LoginPage is not { } loginPage)
            {
                AppState.ShowStatus(Loc.T("History_Status_LoginPageNotReady"), InfoBarSeverity.Error);
                return;
            }

            var score = await loginPage.QueryAndSaveCsStatusAsync(
                account.AccountName, account.EyaToken, timeoutCts.Token);
            AppState.ShowStatus(
                Loc.Tf("History_Status_QueryDone_Format", account.AccountTitle, score.DisplayText, score.PlayerLevelText, score.CooldownText, score.GcVacText),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 不是用户点取消，就是整体超时：给出可操作提示并留痕。
            AppLog.Warn($"一键查询超时：{account.AccountTitle}，等待 {OneClickQueryTimeout.TotalSeconds:0} 秒仍未返回。");
            AppState.ShowStatus(
                Loc.Tf("History_Status_QueryTimeout_Format", (int)OneClickQueryTimeout.TotalSeconds),
                InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_QueryCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void UseHistoryAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        MainWindow.Instance?.LoadAccountIntoLogin(account);
    }

    private string? GetSelectedSteamId()
    {
        return HistoryAccountList.SelectedItem is SteamAccountHistoryItem account
            ? account.SteamId
            : null;
    }

    private void UpdateDetail()
    {
        if (HistoryDetailAccountNameText is null)
        {
            return;
        }

        // 覆盖备注框前先保存正在编辑但尚未失焦的备注，避免后台 ReloadHistory/重建把改动冲掉。
        // 记录 flush 前备注框是否正被编辑（有焦点）及其绑定账号键：后台重建时选中账号常是磁盘快照读出的新实例，
        // 其 Note 仍是编辑前的旧值，若无条件回填会把用户正在输入的文本（连同光标）当场冲掉。
        // 此处已越过开头的 AccountNameText null 守卫（可视树已加载），备注框同批生成、必非 null，直接访问。
        var noteBoxHadFocus = HistoryDetailNoteBox.FocusState != FocusState.Unfocused;
        var editingKey = _noteAccount is null
            ? null
            : AccountHistoryService.GetAccountKey(_noteAccount);
        var inProgressNoteText = HistoryDetailNoteBox.Text;

        FlushPendingNote();

        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            HistoryDetailAvatar.ProfilePicture = null;
            // 空串才显示默认人像剪影：PersonPicture 按 DisplayName 取首字母，英文文案会显示„NS“之类的字母块。
            HistoryDetailAvatar.DisplayName = string.Empty;
            HistoryDetailAccountNameText.Text = Loc.T("History_Detail_NoAccountSelected");
            HistoryDetailPersonaText.Text = Loc.T("History_Detail_ProfileNotSynced");
            HistoryDetailSteamIdText.Text = Loc.T("History_Detail_Unparsed");
            HistoryDetailTokenExpiresText.Text = Loc.T("History_Detail_Unparsed");
            HistoryDetailLastLoginText.Text = Loc.T("History_Detail_NoRecord");
            HistoryDetailCompetitiveScoreText.Text = Loc.T("History_Detail_Pending");
            HistoryDetailCsLevelText.Text = Loc.T("History_Detail_Pending");
            SetCooldownStatusPresentation(null, Loc.T("History_Detail_Pending"));
            HistoryDetailAccountStatusText.Text = Loc.T("History_Detail_Pending");
            HistoryDetailCs2IsChinaText.Text = Loc.T("History_Detail_Pending");
            HistoryDetailNoteBox.Text = string.Empty;
            _noteAccount = null;
            return;
        }

        HistoryDetailAvatar.DisplayName = account.AccountTitle;
        HistoryDetailAvatar.ProfilePicture = account.AvatarImage;
        HistoryDetailAccountNameText.Text = account.AccountTitle;
        HistoryDetailPersonaText.Text = account.PersonaDisplayName;
        HistoryDetailSteamIdText.Text = account.SteamIdDisplay;
        HistoryDetailTokenExpiresText.Text = account.TokenExpiresText;
        HistoryDetailLastLoginText.Text = account.LastLoginText;
        HistoryDetailCompetitiveScoreText.Text = account.CompetitiveScoreText;
        HistoryDetailCsLevelText.Text = account.CsPlayerLevelText;
        SetCooldownStatusPresentation(account, account.RemainingCooldownStatusText);
        HistoryDetailAccountStatusText.Text = account.JwtAvailabilityText;
        HistoryDetailCs2IsChinaText.Text = account.Cs2IsChinaText;

        // 仍是同一账号且备注框正被编辑时，保留用户正在输入的文本（已在上面 flush 落盘），不用旧快照回填冲掉。
        var sameAccountBeingEdited = noteBoxHadFocus && editingKey is not null &&
            string.Equals(editingKey, AccountHistoryService.GetAccountKey(account), StringComparison.OrdinalIgnoreCase);
        if (sameAccountBeingEdited)
        {
            // 让新实例的 Note 与界面正在显示的文本一致，避免下次失焦时因“框≠实例”触发一次多余的重复保存。
            account.Note = string.IsNullOrWhiteSpace(inProgressNoteText) ? null : inProgressNoteText.Trim();
        }
        else
        {
            HistoryDetailNoteBox.Text = account.Note ?? string.Empty;
        }

        _noteAccount = account;
    }

    // ---------- 冷却倒计时（每秒刷新在冷却中的卡片与详情；全部到期后自动停表，省电） ----------

    private void OnCooldownTick()
    {
        var anyLive = false;
        var stillLive = new HashSet<SteamAccountHistoryItem>();
        foreach (var account in _viewItems)
        {
            if (account.HasLiveCooldown)
            {
                account.NotifyCooldownTick();
                stillLive.Add(account);
                anyLive = true;
            }
            else if (_cooldownLiveLastTick.Contains(account))
            {
                // 上一 tick 还在冷却、这一 tick 已归零：HasLiveCooldown 翻 false 会让常规分支跳过它，
                // 这里补发一次通知，把卡片从「1 秒」刷成「无冷却」终态（否则会永久停在最后的非零值）。
                account.NotifyCooldownTick();
            }
        }

        _cooldownLiveLastTick.Clear();
        foreach (var account in stillLive)
        {
            _cooldownLiveLastTick.Add(account);
        }

        // 详情面板冷却行是命令式赋值（不随绑定自动更新），选中账号在冷却中、或刚好本 tick 归零时都要刷一次。
        if (HistoryAccountList.SelectedItem is SteamAccountHistoryItem selected)
        {
            if (selected.HasLiveCooldown)
            {
                SetCooldownStatusPresentation(selected, selected.RemainingCooldownStatusText);
                _detailCooldownLiveLastTick = true;
                anyLive = true;
            }
            else if (_detailCooldownLiveLastTick)
            {
                SetCooldownStatusPresentation(selected, selected.RemainingCooldownStatusText);
                _detailCooldownLiveLastTick = false;
            }
        }

        if (!anyLive)
        {
            _cooldownTimer.Stop();
        }
    }

    private void SetCooldownStatusPresentation(SteamAccountHistoryItem? account, string text)
    {
        HistoryDetailCooldownStatusText.Text = text;
        HistoryDetailCooldownStatusText.Foreground = account?.GcVacBanned == true
            ? FormatHelper.GetStatusBrush(InfoBarSeverity.Error)
            : FormatHelper.GetPrimaryTextBrush();
    }

    private void UpdateCooldownTimer()
    {
        var anyLive = _isActive && _viewItems.Any(account => account.HasLiveCooldown);
        if (anyLive)
        {
            if (!_cooldownTimer.IsRunning)
            {
                _cooldownTimer.Start();
            }
        }
        else
        {
            _cooldownTimer.Stop();
        }
    }

    private void HistoryDetailNoteBox_LostFocus(object sender, RoutedEventArgs e)
    {
        FlushPendingNote();
    }

    // 把备注框里相对 _noteAccount 的未保存改动落盘（失焦时、以及每次覆盖备注框之前调用）。
    // 幂等：无改动时直接返回，不刷屏；就地更新内存实例（卡片备注标记随 INPC 立即刷新），不整表重建。
    private void FlushPendingNote()
    {
        var account = _noteAccount;
        if (account is null || HistoryDetailNoteBox is null)
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(HistoryDetailNoteBox.Text)
            ? null
            : HistoryDetailNoteBox.Text.Trim();
        if (string.Equals(normalized, account.Note, StringComparison.Ordinal))
        {
            return;
        }

        account.Note = normalized;
        try
        {
            AppState.AccountHistoryService.SetNote(account, normalized);
            AppState.ShowStatus(Loc.T("History_Status_NoteSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_NoteSaveFail_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    // 令牌验证器需要输码时弹窗索取邮箱/手机验证码（无 shared_secret 的账号）；取消返回 null。
    // emailHint：邮箱验证时把该账号绑定邮箱显示出来，方便去对应邮箱查收验证码。
    private async Task<string?> PromptGuardCodeAsync(
        SteamGuardPrompt prompt, CancellationToken cancellationToken, string? emailHint = null)
    {
        var isMobile = prompt.Type == SteamGuardType.DeviceCode;

        var codeBox = new TextBox
        {
            PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
            IsSpellCheckEnabled = false,
            MaxLength = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.AssociatedMessage)
                ? Loc.T(isMobile ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage")
                : prompt.AssociatedMessage,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(emailHint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Tf("Creds_Guard_EmailAt_Format", emailHint),
                TextWrapping = TextWrapping.Wrap,
                Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
            });
        }

        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            Title = Loc.T(isMobile ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
            Content = panel,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            PrimaryButtonStyle = (Style)Application.Current.Resources["ThemedDialogPrimaryButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["ThemedDialogCloseButtonStyle"],
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? codeBox.Text.Trim() : null;
    }

    // ---------- 批量一键查询（对所有已勾选账号依次查询，复用全局忙碌+取消机制） ----------

    private async void BatchQueryButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = GetCheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToQuery"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var succeeded = 0;
        var failed = 0;
        var canceled = false;
        var validatedSteamIds = new List<string>();

        try
        {
            for (var i = 0; i < accounts.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var account = accounts[i];
                AppState.ShowStatus(
                    Loc.Tf("History_Status_BatchQuerying_Format", i + 1, accounts.Count, account.AccountTitle),
                    InfoBarSeverity.Informational);

                try
                {
                    var validationResult = await ValidateHistoryAccountAsync(account, cancellationToken, i + 1, accounts.Count);
                    validatedSteamIds.Add(validationResult.SteamId);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"批量查询账号失败：{account.AccountTitle}，{ex.Message}");
                }
            }

            await RefreshValidatedProfilesAsync(validatedSteamIds, cancellationToken);

            if (succeeded > 0)
            {
                AppState.ReloadHistory(GetSelectedSteamId());
            }

            AppState.ShowStatus(
                canceled
                    ? Loc.Tf("History_Status_BatchQueryCanceled_Format", succeeded, failed)
                    : Loc.Tf("History_Status_BatchQueryDone_Format", succeeded, failed),
                canceled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        finally
        {
            await AppState.AccountValidationService.ReleaseReusedCmAsync();
            AppState.EndBusyOperation();
        }
    }

    // ---------- 账号分组（定义存 settings.json，成员关系存各账号 GroupIds） ----------

    private void LoadGroups()
    {
        var settings = AppState.SettingsService.Load();
        _groups = GetPersistedGroupDefinitions(settings)
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RebuildGroupFilterMenu();
    }

    private List<AccountGroup> GetPersistedGroupDefinitions(AppSettings settings) =>
        settings.Groups;

    private void RebuildGroupFilterMenu()
    {
        if (GroupFilterButton is null || GroupFilterFlyout is null)
        {
            return;
        }

        var target = BuildMenu(GroupFilterFlyout, _groupFilter);
        if (target is null)
        {
            return;
        }

        _groupFilter = target.Tag as string;
        GroupFilterText.Text = target.Text;

        MenuFlyoutItem? BuildMenu(MenuFlyout targetFlyout, string? current)
        {
            targetFlyout.Items.Clear();
            MenuFlyoutItem? selected = null;

            AddItem(targetFlyout, Loc.T("History_Group_Filter_All"), null);
            foreach (var group in _groups)
            {
                AddItem(targetFlyout, group.Name, group.Id);
            }

            AddItem(targetFlyout, Loc.T("History_Group_Filter_Ungrouped"), UngroupedSentinel);
            selected ??= targetFlyout.Items.FirstOrDefault() as MenuFlyoutItem;
            return selected;

            void AddItem(MenuFlyout menu, string text, string? tag)
            {
                var item = new MenuFlyoutItem
                {
                    Text = text,
                    Tag = tag
                };
                item.Click += GroupFilterMenuItem_Click;
                menu.Items.Add(item);
                if (selected is null && string.Equals(tag, current, StringComparison.Ordinal))
                {
                    selected = item;
                }
            }
        }
    }

    private void GroupFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
        {
            return;
        }

        _groupFilter = item.Tag as string;
        GroupFilterText.Text = item.Text;
        RebuildView(GetSelectedSteamId());
    }
    private bool MatchesGroupFilter(SteamAccountHistoryItem account)
    {
        if (_groupFilter is null)
        {
            return true;
        }

        if (_groupFilter == UngroupedSentinel)
        {
            return account.GroupIds is not { Count: > 0 };
        }

        return account.GroupIds is { Count: > 0 } && account.GroupIds.Contains(_groupFilter);
    }

    private void BatchGroupFlyout_Opening(object sender, object e)
    {
        BatchGroupFlyout.Items.Clear();
        var accounts = GetCheckedAccounts();

        foreach (var group in _groups)
        {
            var allMembers = accounts.Count > 0 && accounts.All(account => account.GroupIds.Contains(group.Id));
            var toggle = new ToggleMenuFlyoutItem { Text = group.Name, IsChecked = allMembers };
            var groupId = group.Id;
            var add = !allMembers;
            toggle.Click += (_, _) => ApplyBatchGroup(groupId, add);
            BatchGroupFlyout.Items.Add(toggle);
        }

        if (_groups.Count > 0)
        {
            BatchGroupFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        var newItem = new MenuFlyoutItem { Text = Loc.T("History_Group_NewAndAdd") };
        newItem.Click += async (_, _) => await CreateGroupAndAddSelectedAsync();
        BatchGroupFlyout.Items.Add(newItem);

        var manageItem = new MenuFlyoutItem { Text = Loc.T("History_Group_Manage") };
        manageItem.Click += async (_, _) => await ManageGroupsAsync();
        BatchGroupFlyout.Items.Add(manageItem);
    }

    private void ApplyBatchGroup(string groupId, bool add)
    {
        var accounts = GetCheckedAccounts();
        if (accounts.Count == 0)
        {
            return;
        }

        try
        {
            var changed = AppState.AccountHistoryService.SetGroupMembership(accounts, groupId, add);
            AppState.ReloadHistory(GetSelectedSteamId());
            var groupName = _groups.FirstOrDefault(group => group.Id == groupId)?.Name ?? string.Empty;
            AppState.ShowStatus(
                add
                    ? Loc.Tf("History_Status_GroupAdded_Format", changed, groupName)
                    : Loc.Tf("History_Status_GroupRemoved_Format", changed, groupName),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            // accounts.json 被占用/损坏时 SetGroupMembership 会抛（与删除/清空/导入一致），
            // 事件处理器里不接就会经未处理异常终止进程；这里与其它变更路径一样降级为状态栏报错。
            AppLog.Warn($"批量分组变更失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CreateGroupAndAddSelectedAsync()
    {
        var name = await PromptGroupNameAsync(Loc.T("History_Group_NewDialog_Title"), string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var group = CreateGroup(name);
            var accounts = GetCheckedAccounts();
            if (accounts.Count > 0)
            {
                AppState.AccountHistoryService.SetGroupMembership(accounts, group.Id, true);
            }

            AppState.ReloadHistory(GetSelectedSteamId());
            AppState.ShowStatus(Loc.Tf("History_Status_GroupCreated_Format", group.Name), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"新建分组并加入失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private AccountGroup CreateGroup(string name)
    {
        var settings = AppState.SettingsService.Load();
        var groups = GetPersistedGroupDefinitions(settings);
        var group = new AccountGroup { Name = name.Trim(), Order = groups.Count };
        groups.Add(group);
        AppState.SettingsService.Save(settings);
        return group;
    }


    private async void ManageGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        await ManageGroupsAsync();
    }

    private async Task<string?> PromptGroupNameAsync(string title, string initial)
    {
        if (_isDialogFlowActive)
        {
            return null;
        }

        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = Loc.T("History_Group_Name_Placeholder"),
            AcceptsReturn = false
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            PrimaryButtonStyle = (Style)Application.Current.Resources["ThemedDialogPrimaryButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["ThemedDialogCloseButtonStyle"],
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private async Task ManageGroupsAsync()
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        LoadGroups();

        var newNameBox = new TextBox
        {
            PlaceholderText = Loc.T("History_Group_Name_Placeholder"),
            AcceptsReturn = false
        };
        var addButton = new Button { Content = Loc.T("History_Group_Add") };
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(newNameBox);
        addRow.Children.Add(addButton);

        var listPanel = new StackPanel { Spacing = 6 };
        var root = new StackPanel { Spacing = 12, MinWidth = 380 };
        root.Children.Add(addRow);
        root.Children.Add(listPanel);

        void RebuildRows()
        {
            listPanel.Children.Clear();
            LoadGroups();

            if (_groups.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("History_Group_Manage_Empty"),
                    Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
                });
                return;
            }

            foreach (var group in _groups)
            {
                var count = _allItems.Count(account =>
                    account.GroupIds is { Count: > 0 } && account.GroupIds.Contains(group.Id));
                var groupId = group.Id;

                var nameBox = new TextBox { Text = group.Name, VerticalAlignment = VerticalAlignment.Center };
                nameBox.LostFocus += (_, _) => RenameGroup(groupId, nameBox.Text);

                var countText = new TextBlock
                {
                    Text = Loc.Tf("History_Group_Manage_Count_Format", count),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
                };

                var deleteButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
                deleteButton.Click += (_, _) =>
                {
                    DeleteGroup(groupId);
                    RebuildRows();
                };

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(countText, 1);
                Grid.SetColumn(deleteButton, 2);
                row.Children.Add(nameBox);
                row.Children.Add(countText);
                row.Children.Add(deleteButton);
                listPanel.Children.Add(row);
            }
        }

        addButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newNameBox.Text))
            {
                return;
            }

            CreateGroup(newNameBox.Text);
            newNameBox.Text = string.Empty;
            RebuildRows();
        };

        RebuildRows();

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_Group_Manage"),
            Content = new ScrollViewer { Content = root, MaxHeight = 420 },
            CloseButtonText = Loc.T("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        // 对话框里可能改了名/删了组：刷新筛选下拉与列表。
        LoadGroups();
        RebuildView(GetSelectedSteamId());
    }

    private void RenameGroup(string id, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var groups = GetPersistedGroupDefinitions(settings);
        var group = groups.FirstOrDefault(item => item.Id == id);
        if (group is null || string.Equals(group.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        group.Name = newName;
        AppState.SettingsService.Save(settings);
    }

    private void DeleteGroup(string id)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            var groups = GetPersistedGroupDefinitions(settings);
            groups.RemoveAll(item => item.Id == id);
            AppState.SettingsService.Save(settings);
            AppState.AccountHistoryService.RemoveGroupFromAllAccounts(id);
            AppState.ReloadHistory(GetSelectedSteamId());
        }
        catch (Exception ex)
        {
            // RemoveGroupFromAllAccounts 在 accounts.json 不可读时会抛，管理分组对话框里不接会崩掉整个应用。
            AppLog.Warn($"删除分组失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        var hasActive = HistoryAccountList.SelectedItem is SteamAccountHistoryItem;
        var hasChecked = GetCheckedAccounts().Count > 0;
        HistoryAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        RefreshHistoryButton.IsEnabled = !isBusy;
        HistorySearchBox.IsEnabled = !isBusy;
        ClearHistoryButton.IsEnabled = !isBusy && _allItems.Count > 0;
        ClearInvalidAccountsButton.IsEnabled = !isBusy && _allItems.Count > 0;
        OneClickHistoryQueryButton.IsEnabled = !isBusy && hasActive;
        // 「Steam中查看」与「一键查询」同一规则：没选中账号（或正忙）时不可点。
        OpenSteamBrowserButton.IsEnabled = !isBusy && hasActive;
        UseHistoryAccountButton.IsEnabled = !isBusy && hasActive;
        BatchSelectAllButton.IsEnabled = !isBusy && _viewItems.Count > 0;
        BatchClearButton.IsEnabled = !isBusy && hasChecked;
        BatchQueryButton.IsEnabled = !isBusy && hasChecked;
        BatchGroupButton.IsEnabled = !isBusy && hasChecked;
        BatchExportButton.IsEnabled = !isBusy && hasChecked;
        BatchDeleteButton.IsEnabled = !isBusy && hasChecked;
        GroupFilterButton.IsEnabled = !isBusy;
        ManageGroupsButton.IsEnabled = !isBusy;

        // 取消按钮仅忙碌时出现且保持可用，让用户中断本页发起的一键查询。
        CancelHistoryQueryButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ToolbarCancelHistoryQueryButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }
}
