using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 旧版（Qt「账号管理系统」）风格的账号页，两个标签：账号管理 / VAC查询。
///   · 布局照老版来：一行工具栏（添加/导出/刷新 + 5 个 ID 生成）+ 一行筛选 + 紧凑表格；
///   · 数据与新版共用同一份账号库（白号），查询走既有的「账号 + 密码登录 Steam 读冷却 / VAC 页」链路；
///   · 老版特有、而新数据模型里没有的字段（快捷方式、隐藏）不还原，避免为了外观塞进无意义的字段。
/// </summary>
public sealed partial class LegacyAccountsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public LegacyAccountsPage()
    {
        InitializeComponent();
        BuildGenIdMenu();

        // 行高亮统一在这里处理：行上的 Tapped 在点在复选框等自己处理指针的子控件上时收不到，
        // handledEventsToo: true 才能保证「点哪一行都算」。
        LegacyAccountList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(LegacyAccountList_PointerPressed),
            handledEventsToo: true);

        // 右键菜单在「松开」这一刻弹：框架的 RightTapped 还要等手势判定，实测慢约 110 ms。
        LegacyAccountList.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(LegacyAccountList_PointerReleased),
            handledEventsToo: true);
        Loc.LanguageChanged += OnLanguageChanged;
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    /// <summary>表格行（勾选 + 展示用文本）。</summary>
    public ObservableCollection<LegacyAccountRow> Rows { get; } = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshHighlightBrush();
        AppState.ReloadWhiteAccounts();
        UpdateBatchButtonsState();
        RebuildRows();


    }

    private void OnLanguageChanged() => _dispatcherQueue.TryEnqueue(() =>
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
        UpdateGenIdMenuTexts();
        RebuildRows();
    });

    // ---------- 视图：两个标签 / 搜索 / 筛选 ----------

    private void LegacySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildRows();

    /// <summary>表格右上角搜索框的关键字。</summary>
    private string SearchText => LegacySearchBox?.Text?.Trim() ?? string.Empty;

    private void LegacyFilter_Changed(object sender, RoutedEventArgs e) => RebuildRows();

    /// <summary>「显示隐藏」：两列一直在，只是默认用 ******** 遮住内容；勾选后显示真实值。</summary>
    private void LegacyShowHiddenCheck_Changed(object sender, RoutedEventArgs e) => RebuildRows();

    /// <summary>是否显示密码 / 其他两列的真实内容（默认遮住）。</summary>
    private bool RevealHiddenContent => LegacyShowHiddenCheck?.IsChecked == true;

    private void RebuildRows()
    {
        var keyword = SearchText;
        var availableOnly = LegacyShowAvailableCheck?.IsChecked == true;
        var remarkedOnly = LegacyShowRemarkedCheck?.IsChecked == true;

        Rows.Clear();
        var index = 0;
        foreach (var account in AppState.WhiteAccounts)
        {
            if (keyword.Length > 0 && !Matches(account, keyword))
            {
                continue;
            }

            if (availableOnly && !IsAvailable(account))
            {
                continue;
            }

            if (remarkedOnly && string.IsNullOrWhiteSpace(account.Note))
            {
                continue;
            }

            index++;
            var row = new LegacyAccountRow(account, index, RevealHiddenContent);
            row.PropertyChanged += LegacyAccountRow_PropertyChanged;
            Rows.Add(row);
        }

        UpdateBatchButtonsState();

        // 重建后默认高亮第一行（贴顶显示）。
        if (Rows.Count > 0)
        {
            HighlightRow(Rows[0]);
        }
        else
        {
            _highlightedRow = null;
        }
    }

    private void LegacyAccountRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LegacyAccountRow.IsChecked))
        {
            UpdateBatchButtonsState();
        }
    }

    /// <summary>没有勾选任何账号时，「批量备注」与「删除」不可点；勾选任一行或点「全选」后恢复可用。</summary>
    private void UpdateBatchButtonsState()
    {
        var hasChecked = Rows.Any(row => row.IsChecked);
        if (LegacyBatchQueryButton is not null)
        {
            LegacyBatchQueryButton.IsEnabled = hasChecked;
        }

        if (LegacyBatchNoteButton is not null)
        {
            LegacyBatchNoteButton.IsEnabled = hasChecked;
        }

        if (LegacyDeleteButton is not null)
        {
            LegacyDeleteButton.IsEnabled = hasChecked;
        }
    }

    private static bool Matches(SteamAccountHistoryItem account, string keyword) =>
        account.AccountName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        (account.PersonaName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (account.SteamId?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (account.Note?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>老版「显示可用」= 既没在冷却、也没有 VAC 标记。</summary>
    private static bool IsAvailable(SteamAccountHistoryItem account) =>
        account.GcVacBanned != true && (account.CooldownSeconds ?? 0) == 0;

    /// <summary>
    /// 重建列表后把视图定位回刚才操作的那一行：重新选中（高亮）并滚动到可见，
    /// 否则菜单操作一次就会丢失位置、高亮也没了。
    /// </summary>
    /// <summary>当前高亮行（自己管理，不用 ListView 的选中态：它会在列表刷新时被清掉）。</summary>
    private LegacyAccountRow? _highlightedRow;

    /// <summary>高亮画刷：颜色取自主题色（半透明，叠在行背景上）。</summary>
    private readonly SolidColorBrush _highlightBrush = new();

    private void RefreshHighlightBrush()
    {
        var accent = AppState.UiColorService.BaseColor;
        _highlightBrush.Color = Color.FromArgb(0x66, accent.R, accent.G, accent.B);
    }

    /// <summary>鼠标右键也在 PointerReleased 里立刻弹过菜单，RightTapped 只留给触屏/手写笔兜底；
    /// 这是压掉「同一次右键又走一遍 RightTapped」的时间窗（毫秒）。</summary>
    private const long ContextMenuSuppressMs = 600;

    /// <summary>上一次由指针事件直接弹出右键菜单的时间（TickCount64）。</summary>
    private long _contextMenuShownAt;

    /// <summary>
    /// 列表上的指针按下：左键、右键一视同仁，都把那行切成高亮（不滚动列表），
    /// 这样右键也能「选中」行，和左键行为一致。
    /// </summary>
    private void LegacyAccountList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LegacyAccountList);
        var props = point.Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;

        // 勾选复选框时只做勾选，不切高亮（用户要求）。
        if (IsCheckBoxClick(source))
        {
            return;
        }

        var row = ResolveRowAt(e.GetCurrentPoint(null).Position, source);
        if (row is not null)
        {
            HighlightRow(row, scrollToTop: false);
        }
    }

    /// <summary>
    /// 右键松开：立刻弹出该列对应的菜单。
    /// 不用等 RightTapped —— 框架要等手势判定，实测比松开晚约 110 ms，右键就「慢半拍」。
    /// </summary>
    private void LegacyAccountList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LegacyAccountList);
        if (point.Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (source is not FrameworkElement element)
        {
            return;
        }

        // 命中测试的坐标是根元素坐标系（窗口内坐标），不是列表自己的坐标。
        var rootPosition = e.GetCurrentPoint(null).Position;
        var row = ResolveRowAt(rootPosition, source);
        if (row is null)
        {
            return;
        }

        var column = ResolveColumnAt(rootPosition, source, row);
        if (column is null)
        {
            return;   // 这一列没有菜单：既不弹，也不吃掉事件，交给后面的 RightTapped 兜底逻辑
        }

        // 先把时间戳打上：框架的 RightTapped 有时会跟这次「松开」挤在同一毫秒里到，
        // 不压掉就会连开两个菜单（前一个被后一个挤掉，白闪一下）。
        _contextMenuShownAt = Environment.TickCount64;

        // 关键：不要在这次「松开」的事件里直接 ShowAt —— 弹层会在同一次按键抬起里被当成外部点击，
        // 刚开就关。排到本轮输入处理之后（同一帧，实测十几毫秒）再弹，既跟手又稳。
        // 位置是相对锚点元素的：ShowAt 的 Position 参数用的是锚点坐标系。
        var position = e.GetCurrentPoint(element).Position;
        _dispatcherQueue.TryEnqueue(() => ShowColumnMenu(column, row, element, position));
        e.Handled = true;
    }

    /// <summary>
    /// 定位点的是哪一行：先顺着事件源往上找；行与行之间的空隙、行内上下留白这些地方，
    /// 事件源是行的容器而不是行里的控件，就再按坐标做一次命中测试。
    /// </summary>
    private LegacyAccountRow? ResolveRowAt(Windows.Foundation.Point rootPosition, DependencyObject? source)
    {
        if (FindRowFromSource(source) is { } fromSource)
        {
            return fromSource;
        }

        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(rootPosition, LegacyAccountList))
        {
            if (hit is FrameworkElement { DataContext: LegacyAccountRow row })
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// 定位点的是哪一列：先顺着事件源往上找（点中的就是单元格文本时最准）。
    /// 行与行之间的空隙、行内上下留白命中的是行容器、拿不到列，这时改到「这一行的中间高度」
    /// 那条线上按 x 命中 —— 行的中间一定有单元格，列就定得下来，于是空隙处点右键也能弹出对应列的菜单。
    /// </summary>
    private string? ResolveColumnAt(Windows.Foundation.Point rootPosition, DependencyObject? source, LegacyAccountRow row)
    {
        if (FindColumnTag(source) is { } fromSource)
        {
            return fromSource;
        }

        if (FindRowElement(row) is { } rowElement)
        {
            var top = rowElement.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            var midLine = new Windows.Foundation.Point(rootPosition.X, top.Y + rowElement.ActualHeight / 2);
            if (FindColumnTagAt(midLine) is { } fromMidLine)
            {
                return fromMidLine;
            }
        }

        return FindColumnTagAt(rootPosition);
    }

    /// <summary>
    /// 按坐标在列表里命中一次，找出该点下面带列标记的单元格。
    /// 注意：坐标是根元素坐标系（窗口内坐标）——实测传列表自己的坐标会命中不到任何元素。
    /// </summary>
    private string? FindColumnTagAt(Windows.Foundation.Point rootPosition)
    {
        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(rootPosition, LegacyAccountList))
        {
            if (FindColumnTag(hit) is { } fromHit)
            {
                return fromHit;
            }
        }

        return null;
    }

    /// <summary>取这一行在列表里的容器（列表虚拟化，没滚到的行没有容器）。</summary>
    private FrameworkElement? FindRowElement(LegacyAccountRow row)
    {
        var index = Rows.IndexOf(row);
        return index >= 0 ? LegacyAccountList.ContainerFromIndex(index) as FrameworkElement : null;
    }

    /// <summary>从被点元素往上找最近的列标记（XAML 里给各列 TextBlock 的 Tag 标了列名）。</summary>
    private static string? FindColumnTag(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: string tag })
            {
                return tag;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 按列弹右键菜单：账号列 = 登录 / 查询VAC；备注列 = 清空备注 / 自定义备注；冷却时间列 = 预设时长 / 自定义。
    /// 返回 true 表示这一列有菜单（调用方据此决定要不要吃掉这次右键事件）。
    /// </summary>
    private bool ShowColumnMenu(string? column, LegacyAccountRow row, FrameworkElement element, Windows.Foundation.Point position)
    {
        var flyout = new MenuFlyout();

        switch (column)
        {
            case "account":
                _contextAccount = row.Item;

                // 与备注列右键完全同一套菜单样式：MenuFlyout + 带图标的菜单项。
                var login = new MenuFlyoutItem
                {
                    Text = Loc.T("WhiteAccounts_Btn_Login"),
                    Icon = new FontIcon { Glyph = "\uE8D4" }
                };
                login.Click += LegacyRowLogin_Click;
                flyout.Items.Add(login);

                var queryVac = new MenuFlyoutItem
                {
                    Text = Loc.T("Legacy_Btn_QueryVac"),
                    Icon = new FontIcon { Glyph = "\uE9D9" }
                };
                queryVac.Click += LegacyRowQueryVac_Click;
                flyout.Items.Add(queryVac);
                break;

            case "note":
                AddNoteMenuItems(flyout, row);
                break;

            case "cooldownEnd":
                AddCooldownMenuItems(flyout, row);
                break;

            default:
                return false;
        }

        if (flyout.Items.Count == 0)
        {
            return false;
        }

        flyout.ShowAt(element, new FlyoutShowOptions { Position = position });
        return true;
    }


    /// <summary>被点中的是不是行前面那个勾选框。</summary>
    private static bool IsCheckBoxClick(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is CheckBox)
            {
                return true;
            }

            if (current is FrameworkElement { DataContext: LegacyAccountRow })
            {
                return false;   // 已经走到行本身还没遇到勾选框 → 不是勾选框上的点击
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>从被点中的元素往上找它所属的行（子控件也带同一个 DataContext）。</summary>
    private static LegacyAccountRow? FindRowFromSource(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: LegacyAccountRow row })
            {
                return row;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                // 非可视元素（例如文本内联）没有父级：就此结束。
                return null;
            }
        }

        return null;
    }

    /// <summary>把高亮切到指定行（其余行取消），并滚到最上方显示。</summary>
    private void HighlightRow(LegacyAccountRow row, bool scrollToTop = true)
    {
        foreach (var item in Rows)
        {
            item.HighlightBrush = ReferenceEquals(item, row) ? _highlightBrush : null;
        }

        _highlightedRow = row;

        if (scrollToTop && LegacyAccountList is not null)
        {
            LegacyAccountList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        }
    }

    private void FocusRowForAccount(SteamAccountHistoryItem account)
    {
        if (LegacyAccountList is null || Rows.Count == 0)
        {
            return;
        }

        var row = Rows.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(account.AccountName) &&
             string.Equals(item.Item.AccountName, account.AccountName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(account.SteamId) &&
             string.Equals(item.Item.SteamId, account.SteamId, StringComparison.OrdinalIgnoreCase)));

        if (row is null)
        {
            return;
        }

        HighlightRow(row);
    }

    private List<SteamAccountHistoryItem> CheckedAccounts() =>
        Rows.Where(row => row.IsChecked).Select(row => row.Item).ToList();

    // ---------- 账号管理标签 ----------

    private void LegacyRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        AppState.ShowStatus(Loc.Tf("Legacy_Status_Refreshed_Format", Rows.Count), InfoBarSeverity.Success);
    }

    private void LegacySelectAllButton_Click(object sender, RoutedEventArgs e) => ToggleSelectAll();


    private void ToggleSelectAll()
    {
        var select = Rows.Any(row => !row.IsChecked);
        foreach (var row in Rows)
        {
            row.IsChecked = select;
        }

        UpdateBatchButtonsState();
    }

    private void LegacyExportButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = CheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Status_NoSelection"), InfoBarSeverity.Warning);
            return;
        }

        // 老版的导出格式：每行「账号----密码」。
        var text = string.Join(Environment.NewLine, accounts.Select(a => $"{a.AccountName}----{a.Password}"));
        CopyToClipboard(text);
        AppState.ShowStatus(Loc.Tf("Legacy_Status_Exported_Format", accounts.Count), InfoBarSeverity.Success);
    }

    private async void LegacyAddButton_Click(object sender, RoutedEventArgs e)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 160,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            PlaceholderText = Loc.T("Legacy_Dialog_Add_Placeholder")
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Legacy_Dialog_Add_Title"),
            Content = box,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var parsed = WhiteAccountLineParser.Parse(box.Text, allowMissingPassword: true);
        var entries = parsed.Select(entry => new SteamAccountHistoryItem
        {
            AccountName = entry.AccountName,
            Password = entry.Password,
            SharedSecret = entry.SharedSecret,
            Email = entry.Email,
            EmailPassword = entry.EmailPassword,
            IsWhiteAccount = true,
            LastLoginAt = DateTimeOffset.Now
        }).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var added = AppState.WhiteAccountService.ImportWhiteAccounts(entries);
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        AppState.ShowStatus(Loc.Tf("Legacy_Status_Imported_Format", added), InfoBarSeverity.Success);
    }

    /// <summary>
    /// 「一键查询」：对勾选的账号逐个优先复用令牌，没有可用令牌才走「账号 + 密码登录 Steam」，
    /// 再读冷却/VAC；结果与登录成功后拿到的令牌一并写回账号，刷新表格。需要 Steam Guard 会弹验证码框。
    /// </summary>
    private async void LegacyBatchQueryButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = CheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Status_NoSelection"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var succeeded = 0;
        var failed = 0;
        var loginThrottled = false;

        try
        {
            for (var i = 0; i < accounts.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var account = accounts[i];
                if (string.IsNullOrWhiteSpace(account.Password) &&
                    string.IsNullOrWhiteSpace(account.EyaToken))
                {
                    failed++;
                    continue;
                }

                // 进度计数（如 1/61）始终跟在状态栏最前面：查询过程自己的提示会覆盖状态栏，
                // 这里把计数拼在每条提示前，避免用户看不出进行到第几个。
                var position = i + 1;
                var total = accounts.Count;
                AppState.ShowStatus(
                    Loc.Tf("Legacy_Status_BatchQuerying_Format", position, total),
                    InfoBarSeverity.Informational);

                try
                {
                    var progress = new Progress<string>(message =>
                        AppState.ShowStatus(
                            Loc.Tf("Legacy_Status_BatchProgress_Format", position, total, message),
                            InfoBarSeverity.Informational));
                    var result = await AppState.AccountValidationService.QueryAsync(
                        account.AccountName,
                        account.Password,
                        account.EyaToken,
                        (prompt, token) => PromptGuardCodeAsync(prompt, token, account),
                        progress,
                        cancellationToken);

                    SaveValidationResult(account, result);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SteamCredentialsAuthException ex) when (ex.EResult == 87)
                {
                    failed++;
                    loginThrottled = true;
                    AppLog.Warn($"一键查询被 Steam 限流：{account.AccountName}，{ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"一键查询失败（{account.AccountName}）：{ex.Message}");
                }
            }
        }
        finally
        {
            AppState.EndBusyOperation();
        }

        AppState.ReloadWhiteAccounts();
        RebuildRows();

        // 结束提示同样带进度（如 61/61），一眼能看出整轮跑完了。
        var doneText = loginThrottled
            ? Loc.T("Creds_Error_LoginThrottled")
            : Loc.Tf("Legacy_Status_BatchDone_Format", succeeded, failed);
        AppState.ShowStatus(
            Loc.Tf("Legacy_Status_BatchProgress_Format", accounts.Count, accounts.Count, doneText),
            failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void LegacyBatchNoteButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = CheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Status_NoSelection"), InfoBarSeverity.Warning);
            return;
        }

        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var box = new TextBox { IsSpellCheckEnabled = false };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Legacy_Dialog_Note_Title"),
            Content = box,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var note = box.Text?.Trim();
        foreach (var account in accounts)
        {
            AppState.WhiteAccountService.SetNote(account, note);
        }

        AppState.ReloadWhiteAccounts();
        RebuildRows();
        AppState.ShowStatus(Loc.Tf("Legacy_Status_NoteSaved_Format", accounts.Count), InfoBarSeverity.Success);
    }

    private async void LegacyDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var accounts = CheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Status_NoSelection"), InfoBarSeverity.Warning);
            return;
        }

        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Legacy_Btn_Delete"),
            Content = Loc.Tf("Legacy_Dialog_Delete_Format", accounts.Count),
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removed = AppState.WhiteAccountService.DeleteAccounts(accounts);
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        AppState.ShowStatus(Loc.Tf("Legacy_Status_Deleted_Format", removed), InfoBarSeverity.Success);
    }

    // ---------- 右键行：账号 + 密码登录（与「账号查询」页卡片登录按钮同一条链路） ----------

    /// <summary>右键点中的那一行（菜单项点击时用它取账号）。</summary>
    private SteamAccountHistoryItem? _contextAccount;

    /// <summary>触屏/手写笔的右键手势兜底（鼠标右键已经在 PointerReleased 里弹过了）。</summary>
    private void LegacyAccountRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LegacyAccountRow row)
        {
            return;
        }

        if (Environment.TickCount64 - _contextMenuShownAt < ContextMenuSuppressMs)
        {
            e.Handled = true;
            return;
        }

        if (ShowColumnMenu("account", row, element, e.GetPosition(element)))
        {
            _contextMenuShownAt = Environment.TickCount64;
            e.Handled = true;
        }
    }


    // ---------- 右键：备注列 / 冷却（可用时间）列 / 其他列 —— 照旧版 Qt 版的右键菜单 ----------

    /// <summary>
    /// 旧版右键菜单对应关系：备注列 = 清空备注 / 自定义备注（弹窗，可选历史备注）；
    /// 可用时间列 = 立即可用 / 20 小时 / 7 天 / 31 天 / 181 天 / 自定义天+小时。
    /// 新数据模型里冷却存的是「剩余秒数」，所以这里改的是剩余时长（效果与旧版设置可用时间一致）。
    /// </summary>
    private void LegacyAccountCell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column } element ||
            element.DataContext is not LegacyAccountRow row)
        {
            return;
        }

        if (Environment.TickCount64 - _contextMenuShownAt < ContextMenuSuppressMs)
        {
            e.Handled = true;
            return;
        }

        if (ShowColumnMenu(column, row, element, e.GetPosition(element)))
        {
            _contextMenuShownAt = Environment.TickCount64;
            e.Handled = true;
        }
    }


    private void AddNoteMenuItems(MenuFlyout flyout, LegacyAccountRow row)
    {
        var clear = new MenuFlyoutItem
        {
            Text = Loc.T("Legacy_Menu_ClearNote"),
            Icon = new FontIcon { Glyph = "\uE75C" }
        };
        clear.Click += (_, _) => ApplyNote(row, null);
        flyout.Items.Add(clear);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var custom = new MenuFlyoutItem
        {
            Text = Loc.T("Legacy_Menu_CustomNote"),
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        custom.Click += async (_, _) => await PromptCustomNoteAsync(row);
        flyout.Items.Add(custom);
    }

    private void AddCooldownMenuItems(MenuFlyout flyout, LegacyAccountRow row)
    {
        // 旧版对已标记 VAC 的账号不给改可用时间。
        if (row.Item.GcVacBanned == true)
        {
            var blocked = new MenuFlyoutItem
            {
                Text = Loc.T("Legacy_Status_VacNoEdit"),
                IsEnabled = false
            };
            flyout.Items.Add(blocked);
            return;
        }

        void Add(string key, string glyph, uint seconds)
        {
            var item = new MenuFlyoutItem
            {
                Text = Loc.T(key),
                Icon = new FontIcon { Glyph = glyph }
            };
            item.Click += (_, _) => ApplyCooldown(row, seconds);
            flyout.Items.Add(item);
        }

        Add("Legacy_Menu_AvailableNow", "\uE73E", 0);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Legacy_Menu_Cooldown20h", "\uE823", 20 * 3600);
        Add("Legacy_Menu_Cooldown7d", "\uE823", 7 * 24 * 3600);
        Add("Legacy_Menu_Cooldown31d", "\uE823", 31 * 24 * 3600);
        Add("Legacy_Menu_Cooldown181d", "\uE823", 181 * 24 * 3600);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var custom = new MenuFlyoutItem
        {
            Text = Loc.T("Legacy_Menu_CustomCooldown"),
            Icon = new FontIcon { Glyph = "\uE916" }
        };
        custom.Click += async (_, _) => await PromptCustomCooldownAsync(row);
        flyout.Items.Add(custom);
    }

    private void ApplyNote(LegacyAccountRow row, string? note)
    {
        var account = row.Item;
        AppState.WhiteAccountService.SetNote(account, note);
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        FocusRowForAccount(account);
        AppState.ShowStatus(
            note is null || note.Length == 0
                ? Loc.Tf("Legacy_Status_NoteCleared_Format", row.Item.AccountName)
                : Loc.Tf("Legacy_Status_NoteUpdated_Format", row.Item.AccountName, note),
            InfoBarSeverity.Success);
    }

    private async Task PromptCustomNoteAsync(LegacyAccountRow row)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        // 旧版的「自定义备注」弹窗里能直接挑之前用过的备注。
        var history = AppState.WhiteAccounts
            .Select(account => account.Note?.Trim())
            .Where(note => !string.IsNullOrEmpty(note))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(note => note, StringComparer.Ordinal)
            .ToList();

        var box = new TextBox
        {
            Text = row.Item.Note ?? string.Empty,
            IsSpellCheckEnabled = false,
            Header = Loc.T("Legacy_Dialog_Note_Header")
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(box);

        if (history.Count > 0)
        {
            var list = new ListView
            {
                MaxHeight = 160,
                SelectionMode = ListViewSelectionMode.Single
            };
            foreach (var note in history)
            {
                list.Items.Add(new TextBlock { Text = note, TextTrimming = TextTrimming.CharacterEllipsis });
            }

            // 点历史备注即填进输入框，可直接确定。
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is TextBlock { Text: { } picked })
                {
                    box.Text = picked;
                }
            };

            content.Children.Add(new TextBlock
            {
                Text = Loc.T("Legacy_Dialog_Note_Saved"),
                Style = (Style)Application.Current.Resources["FieldLabelTextStyle"]
            });
            content.Children.Add(list);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Legacy_Dialog_CustomNote_Title"),
            Content = content,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ApplyNote(row, box.Text?.Trim());
    }

    private void ApplyCooldown(LegacyAccountRow row, uint seconds)
    {
        var account = row.Item;
        AppState.WhiteAccountService.SaveValidationStatus(
            account.AccountName,
            account.SteamId,
            seconds,
            account.GcVacBanned);
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        FocusRowForAccount(account);
        AppState.ShowStatus(
            seconds == 0
                ? Loc.Tf("Legacy_Status_CooldownCleared_Format", row.Item.AccountName)
                : Loc.Tf("Legacy_Status_CooldownUpdated_Format", row.Item.AccountName, FormatHelper.FormatRemaining(TimeSpan.FromSeconds(seconds))),
            InfoBarSeverity.Success);
    }

    private async Task PromptCustomCooldownAsync(LegacyAccountRow row)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var remaining = TimeSpan.FromSeconds(row.Item.CooldownSeconds ?? 0);
        var days = new NumberBox
        {
            Header = Loc.T("Legacy_Dialog_Cooldown_Days"),
            Minimum = 0,
            Maximum = 3650,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = Math.Floor(remaining.TotalDays)
        };
        var hours = new NumberBox
        {
            Header = Loc.T("Legacy_Dialog_Cooldown_Hours"),
            Minimum = 0,
            Maximum = 23,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = remaining.Hours
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(days);
        content.Children.Add(hours);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Legacy_Dialog_Cooldown_Title"),
            Content = content,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var totalDays = double.IsNaN(days.Value) ? 0 : days.Value;
        var totalHours = double.IsNaN(hours.Value) ? 0 : hours.Value;
        var seconds = (uint)Math.Max(0, Math.Round((totalDays * 24 + totalHours) * 3600));
        ApplyCooldown(row, seconds);
    }

    /// <summary>
    /// 双击账号 / 密码 / 其他三列：把该单元格的<b>真实内容</b>复制到剪贴板。
    /// 密码与邮箱列平时显示 ********，但复制的是真值 —— 否则复制没有意义。
    /// 状态栏只确认「复制了什么列」，不回显密码与邮箱明文。
    /// </summary>
    private void LegacyAccountCell_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column } element ||
            element.DataContext is not LegacyAccountRow row)
        {
            return;
        }

        switch (column)
        {
            case "account":
                if (string.IsNullOrWhiteSpace(row.Item.AccountName))
                {
                    return;
                }

                CopyToClipboard(row.Item.AccountName);
                AppState.ShowStatus(
                    Loc.Tf("Legacy_Status_CopiedAccount_Format", row.Item.AccountName),
                    InfoBarSeverity.Success);
                break;

            case "password":
                if (string.IsNullOrWhiteSpace(row.Item.Password))
                {
                    return;
                }

                CopyToClipboard(row.Item.Password);
                AppState.ShowStatus(Loc.T("Legacy_Status_CopiedPassword"), InfoBarSeverity.Success);
                break;

            case "others":
                if (string.IsNullOrWhiteSpace(row.Others))
                {
                    return;
                }

                CopyToClipboard(row.Others);
                AppState.ShowStatus(Loc.T("Legacy_Status_CopiedOthers"), InfoBarSeverity.Success);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>右键弹窗里的「查询VAC」：优先复用令牌，没有可用令牌才用账号+密码登录 Steam 读冷却/VAC 页。</summary>
    private async void LegacyRowQueryVac_Click(object sender, RoutedEventArgs e)
    {
        if (_contextAccount is not { } account)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(account.Password) &&
            string.IsNullOrWhiteSpace(account.EyaToken))
        {
            AppState.ShowStatus(Loc.T("Creds_Error_PasswordRequired"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus(Loc.Tf("Legacy_Status_VacQuerying_Format", 1, 1), InfoBarSeverity.Informational);
        var progress = new Progress<string>(message => AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var result = await AppState.AccountValidationService.QueryAsync(
                account.AccountName,
                account.Password,
                account.EyaToken,
                (prompt, token) => PromptGuardCodeAsync(prompt, token, account),
                progress,
                cancellationToken);

            SaveValidationResult(account, result);

            AppState.ReloadWhiteAccounts();
            RebuildRows();
            FocusRowForAccount(account);
            AppState.ShowStatus(
                Loc.Tf("Legacy_Status_VacDoneOne_Format", account.AccountName, result.SummaryText),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("查询 VAC 失败。", ex);
            AppState.ShowStatus(Loc.Tf("Legacy_Status_VacFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private static void SaveValidationResult(
        SteamAccountHistoryItem account,
        SteamAccountValidationResult result)
    {
        DateTimeOffset? tokenExpiresAt = null;
        if (!string.IsNullOrWhiteSpace(result.RefreshToken))
        {
            tokenExpiresAt = AppState.JwtTokenService.Inspect(result.RefreshToken).ExpiresAt;
        }

        AppState.WhiteAccountService.SaveValidationStatus(
            account.AccountName,
            result.SteamId,
            result.CooldownSeconds ?? 0,
            result.VacBanned,
            result.RefreshToken,
            tokenExpiresAt);
    }

    /// <summary>需要 Steam Guard 时弹框要验证码（与历史页同一套交互，页面各自持有）。</summary>
    private async Task<string?> PromptGuardCodeAsync(SteamGuardPrompt prompt, CancellationToken cancellationToken, SteamAccountHistoryItem account)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        if (prompt.Type == SteamGuardType.DeviceCode && !string.IsNullOrWhiteSpace(account.SharedSecret))
        {
            var code = SteamTotp.GenerateAuthCode(account.SharedSecret);
            if (!string.IsNullOrEmpty(code))
            {
                return code;
            }
        }

        var isMobile = prompt.Type == SteamGuardType.DeviceCode;
        var box = new TextBox
        {
            PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
            IsSpellCheckEnabled = false
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.AssociatedMessage)
                ? Loc.T(isMobile ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage")
                : prompt.AssociatedMessage,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T(isMobile ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
            Content = content,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return box.Text?.Trim();
    }

    private async void LegacyRowLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_contextAccount is not { } account)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(account.AccountName))
        {
            AppState.ShowStatus(Loc.T("Creds_Error_AccountRequired"), InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(account.Password))
        {
            AppState.ShowStatus(Loc.T("Creds_Error_PasswordRequired"), InfoBarSeverity.Error);
            return;
        }

        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            AppState.ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus(
            Loc.Tf("History_Status_LoggingIn_Format", account.AccountTitle),
            InfoBarSeverity.Informational);
        var progress = new Progress<string>(message =>
            AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            await Task.Run(
                () => AppState.LoginService.LoginWithPassword(
                    account.AccountName,
                    account.Password,
                    progress,
                    account.SteamId),
                cancellationToken);
            AppState.ShowStatus(
                Loc.Tf("History_Status_LoginStarted_Format", account.AccountTitle, account.SteamId),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("账号密码登录失败。", ex);
            AppState.ShowStatus(
                Loc.Tf("History_Status_LoginFail_Format", ex.Message, AppLog.LogFilePath),
                InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // ---------- 生成 ID（合并成下拉框：点开选类型，生成后复制到剪贴板） ----------

    /// <summary>菜单项文案键 → 生成规则标记。</summary>
    private static readonly (string Key, string Tag)[] GenIdItems =
    [
        ("Legacy_Btn_GenEn", "en"),
        ("Legacy_Btn_GenJa", "ja"),
        ("Legacy_Btn_GenRu", "ru"),
        ("Legacy_Btn_GenNum", "num"),
        ("Legacy_Btn_GenAlnum", "alnum")
    ];

    private void BuildGenIdMenu()
    {
        foreach (var (key, tag) in GenIdItems)
        {
            var item = new MenuFlyoutItem
            {
                Text = Loc.T(key),
                Tag = tag
            };
            item.Click += LegacyGenIdMenuItem_Click;
            LegacyGenIdFlyout.Items.Add(item);
        }
    }

    private void UpdateGenIdMenuTexts()
    {
        foreach (var item in LegacyGenIdFlyout.Items.OfType<MenuFlyoutItem>())
        {
            var match = GenIdItems.FirstOrDefault(entry => entry.Tag == (item.Tag as string));
            if (match.Key is not null)
            {
                item.Text = Loc.T(match.Key);
            }
        }
    }

    private void LegacyGenIdMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag })
        {
            return;
        }

        var id = tag switch
        {
            "en" => RandomId(EnglishChars, Random.Shared.Next(4, 10), upperFirst: true),
            "ja" => RandomId(JapaneseChars, Random.Shared.Next(4, 10)),
            "ru" => RandomId(RussianChars, Random.Shared.Next(4, 10)),
            "num" => RandomId("0123456789", Random.Shared.Next(7, 10)),
            _ => RandomId(EnglishChars, 5) + RandomId("0123456789", 5)
        };

        CopyGeneratedId(id);
    }

    private const string EnglishChars = "abcdefghijklmnopqrstuvwxyz";
    private const string RussianChars = "абвгдежзиклмнопрстуфхцчшщыэюя";
    private const string JapaneseChars = "ぁあぃいぅうぇえぉおかがきぎくぐけげこごさざしじすずせぜそぞただちぢっつづてでとどなにぬねのはばぱひびぴふぶぺへべぽほぼまみむめもやゆよらりるれろわをん";

    private static string RandomId(string pool, int length, bool upperFirst = false)
    {
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = pool[Random.Shared.Next(pool.Length)];
        }

        if (upperFirst && buffer.Length > 0)
        {
            buffer[0] = char.ToUpperInvariant(buffer[0]);
        }

        return new string(buffer);
    }

    private void CopyGeneratedId(string id)
    {
        CopyToClipboard(id);
        AppState.ShowStatus(Loc.Tf("Legacy_Status_Generated_Format", id), InfoBarSeverity.Success);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        try
        {
            // 账号/生成的 ID 都可能被当凭据用：不进 Win+V 历史、不随云剪贴板漫游。
            var options = new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };
            if (!Clipboard.SetContentWithOptions(package, options))
            {
                Clipboard.SetContent(package);
            }
        }
        catch (COMException)
        {
            AppState.ShowStatus(Loc.T("History_Status_ClipboardWriteFail"), InfoBarSeverity.Error);
        }
    }

    // ---------- VAC查询标签 ----------


}

/// <summary>旧版表格的一行：勾选状态 + 展示文本，包住真实账号对象。</summary>
public sealed partial class LegacyAccountRow : INotifyPropertyChanged
{
    private bool _isChecked;

    /// <summary>遮住内容时统一用 8 个星号（列本身始终显示）。</summary>
    private const string Mask = "********";

    internal LegacyAccountRow(SteamAccountHistoryItem item, int index, bool revealHiddenContent)
    {
        Item = item;
        IndexText = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        AccountName = item.AccountName;
        Password = item.Password;
        PasswordText = revealHiddenContent ? item.Password : Mask;
        Note = item.Note ?? string.Empty;
        StatusText = item.GcVacBanned == true
            ? Loc.T("Legacy_Status_Vac")
            : (item.CooldownSeconds ?? 0) > 0 ? Loc.T("Legacy_Status_Cooldown") : Loc.T("Legacy_Status_Available");

        // 状态列按状态上色：可用=绿、冷却中=橙、VAC 封禁=红。
        // 画刷走 FormatHelper 的主题感知取法，深浅色模式下都取得到对应变体。
        StatusBrush = item.GcVacBanned == true
            ? FormatHelper.GetStatusBrush(InfoBarSeverity.Error)
            : (item.CooldownSeconds ?? 0) > 0
                ? FormatHelper.GetWarningBrush()
                : FormatHelper.GetStatusBrush(InfoBarSeverity.Success);

        // 老版「可用时间」：VAC 封禁直接显示 VAC，冷却中显示剩余时间，否则留空。
        // 冷却时间列：冷却结束的绝对时间（本地时间）；VAC 直接写 VAC。
        CooldownEndText = item.GcVacBanned == true
            ? "VAC"
            : (item.CooldownSeconds ?? 0) > 0
                ? DateTimeOffset.Now.AddSeconds(item.CooldownSeconds!.Value).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;

        // 老版「其他」列放邮箱信息：邮箱----邮箱密码。
        Others = string.IsNullOrWhiteSpace(item.Email)
            ? string.Empty
            : string.IsNullOrWhiteSpace(item.EmailPassword) ? item.Email! : $"{item.Email}----{item.EmailPassword}";
        OthersText = string.IsNullOrEmpty(Others) ? string.Empty : revealHiddenContent ? Others : Mask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SteamAccountHistoryItem Item { get; }

    private Brush? _highlightBrush;

    /// <summary>
    /// 行背景（高亮时用主题色画刷，否则为 null = 透明）。
    /// 必须触发 PropertyChanged：XAML 用的是 x:Bind OneWay，不通知就不会重绘（表现为点了没高亮）。
    /// </summary>
    public Brush? HighlightBrush
    {
        get => _highlightBrush;
        set
        {
            if (ReferenceEquals(_highlightBrush, value))
            {
                return;
            }

            _highlightBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBrush)));
        }
    }

    public string IndexText { get; }

    public string AccountName { get; }

    public string Password { get; }

    /// <summary>密码列显示内容：默认 ********，勾选「显示隐藏」后是真实密码。</summary>
    public string PasswordText { get; }

    public string StatusText { get; }

    /// <summary>状态列的文字颜色：可用=绿、冷却中=橙、VAC 封禁=红。</summary>
    public Brush StatusBrush { get; }

    public string Note { get; }

    /// <summary>「冷却时间」列：冷却结束的绝对时间（VAC 显示 VAC，无冷却为空）。</summary>
    public string CooldownEndText { get; }

    public string Others { get; }

    /// <summary>其他列显示内容：默认 ********，勾选后是「邮箱----邮箱密码」。</summary>
    public string OthersText { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}
