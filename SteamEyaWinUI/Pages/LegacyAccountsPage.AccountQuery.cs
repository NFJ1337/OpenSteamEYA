using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SteamEyaWinUI.Pages;

public sealed partial class LegacyAccountsPage
{
    private const int AccountQueryDefaultThreadCount = 3;
    private readonly SemaphoreSlim _accountQueryGuardDialogGate = new(1, 1);
    private bool _accountQueryRunning;
    private bool _accountQueryInitializing = true;
    private bool _accountQuerySyncingThreadInputs;
    private string? _accountQueryAvatarPath;
    private int _accountQueryRunSuccessCount;
    private int _accountQueryRunFailedCount;
    private LegacyAccountQueryRow? _accountQueryHighlightedRow;
    private LegacyAccountQueryRow? _accountQueryCheckboxDragAnchor;
    private bool _accountQueryCheckboxDragActive;
    private bool _accountQueryCheckboxDragSelectValue;
    private uint _accountQueryCheckboxDragPointerId;

    /// <summary>账号查询标签的独立行集合，不改变原账号管理表的筛选与选择。</summary>
    public ObservableCollection<LegacyAccountQueryRow> AccountQueryRows { get; } = [];

    private void InitializeAccountQueryTab()
    {
        _accountQueryInitializing = true;
        try
        {
            var settings = AppState.SettingsService.Load();
            SyncAccountQueryThreadInputs(Math.Clamp(settings.AccountQueryThreadCount, 1, 100));
            UpdateAccountQueryStartButton();
            UpdateAccountQueryAvatarButton();
            UpdateAccountQueryFooter();
            RegisterAccountQueryCheckboxDragHandlers();
        }
        finally
        {
            _accountQueryInitializing = false;
        }
    }

    private void RefreshAccountQueryRows()
    {
        if (_accountQueryRunning)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(LegacyAccountQueryList);
        (double Vertical, double Horizontal)? scrollOffset = scrollViewer is null
            ? null
            : (Vertical: scrollViewer.VerticalOffset, Horizontal: scrollViewer.HorizontalOffset);
        var selectedAccounts = AccountQueryRows
            .Where(row => row.IsSelected)
            .Select(row => row.AccountName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var highlightedAccountName = _accountQueryHighlightedRow?.AccountName;

        foreach (var oldRow in AccountQueryRows)
        {
            oldRow.PropertyChanged -= OnAccountQueryRowPropertyChanged;
        }

        var failedOnly = LegacyAccountQueryFailedOnlyCheck?.IsChecked == true;
        var accounts = failedOnly
            ? AppState.WhiteAccounts.Where(account =>
                IsFailedAccountQueryStatus(account.AccountQueryStatus))
            : AppState.WhiteAccounts;

        AccountQueryRows.Clear();
        foreach (var account in accounts)
        {
            var row = new LegacyAccountQueryRow(
                account,
                _statusAvailableBrush,
                _statusCooldownBrush,
                _statusVacBrush,
                (Brush)Resources["LegacyTextMutedBrush"])
            {
                IsSelected = selectedAccounts.Contains(account.AccountName)
            };
            if (!string.IsNullOrWhiteSpace(highlightedAccountName) &&
                string.Equals(
                    row.AccountName,
                    highlightedAccountName,
                    StringComparison.OrdinalIgnoreCase))
            {
                row.HighlightBrush = _highlightBrush;
                _accountQueryHighlightedRow = row;
            }

            row.PropertyChanged += OnAccountQueryRowPropertyChanged;
            AccountQueryRows.Add(row);
        }

        if (_accountQueryHighlightedRow is not null &&
            !AccountQueryRows.Contains(_accountQueryHighlightedRow))
        {
            _accountQueryHighlightedRow = null;
        }

        UpdateAccountQueryFooter();
        if (scrollOffset is { } offset)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var currentScrollViewer = FindDescendant<ScrollViewer>(LegacyAccountQueryList);
                currentScrollViewer?.ChangeView(
                    offset.Horizontal,
                    offset.Vertical,
                    null,
                    disableAnimation: true);
            });
        }
    }

    private void OnAccountQueryRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LegacyAccountQueryRow.IsSelected) or
            nameof(LegacyAccountQueryRow.StatusCode))
        {
            UpdateAccountQueryFooter();
        }
    }

    private void RegisterAccountQueryCheckboxDragHandlers()
    {
        LegacyAccountQueryList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(LegacyAccountQueryList_PointerPressed),
            handledEventsToo: true);
        LegacyAccountQueryList.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(LegacyAccountQueryList_PointerMoved),
            handledEventsToo: true);
        LegacyAccountQueryList.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(LegacyAccountQueryList_PointerReleased),
            handledEventsToo: true);
        LegacyAccountQueryList.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(LegacyAccountQueryList_PointerCanceled),
            handledEventsToo: true);
        LegacyAccountQueryList.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(LegacyAccountQueryList_PointerCaptureLost),
            handledEventsToo: true);
    }

    private void LegacyAccountQueryList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LegacyAccountQueryList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var rootPosition = e.GetCurrentPoint(null).Position;
        if (TryResolveAccountQueryCheckboxRow(rootPosition, source, out var checkboxRow))
        {
            BeginAccountQueryCheckboxDrag(checkboxRow, e);
            e.Handled = true;
            return;
        }

        var row = ResolveAccountQueryRowAt(rootPosition, source);
        if (row is not null)
        {
            HighlightAccountQueryRow(row);
        }
    }

    private bool TryResolveAccountQueryCheckboxRow(
        Windows.Foundation.Point rootPosition,
        DependencyObject? source,
        out LegacyAccountQueryRow row)
    {
        row = null!;
        var hitElements = VisualTreeHelper
            .FindElementsInHostCoordinates(rootPosition, LegacyAccountQueryList)
            .OfType<FrameworkElement>()
            .ToList();
        if (!hitElements.Any(element =>
                string.Equals(element.Tag as string, "checkbox", StringComparison.Ordinal)))
        {
            return false;
        }

        row = FindAccountQueryRowFromSource(source)
            ?? hitElements
                .Select(element => element.DataContext as LegacyAccountQueryRow)
                .FirstOrDefault(candidate => candidate is not null)!;
        return row is not null;
    }

    private static LegacyAccountQueryRow? FindAccountQueryRowFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: LegacyAccountQueryRow row })
            {
                return row;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private LegacyAccountQueryRow? ResolveAccountQueryRowAt(
        Windows.Foundation.Point rootPosition,
        DependencyObject? source)
    {
        return FindAccountQueryRowFromSource(source)
            ?? VisualTreeHelper
                .FindElementsInHostCoordinates(rootPosition, LegacyAccountQueryList)
                .OfType<FrameworkElement>()
                .Select(element => element.DataContext as LegacyAccountQueryRow)
                .FirstOrDefault(candidate => candidate is not null);
    }

    private void HighlightAccountQueryRow(LegacyAccountQueryRow row)
    {
        foreach (var item in AccountQueryRows)
        {
            item.HighlightBrush = ReferenceEquals(item, row) ? _highlightBrush : null;
        }

        _accountQueryHighlightedRow = row;
    }

    private void BeginAccountQueryCheckboxDrag(
        LegacyAccountQueryRow row,
        PointerRoutedEventArgs e)
    {
        _accountQueryCheckboxDragAnchor = row;
        _accountQueryCheckboxDragActive = true;
        _accountQueryCheckboxDragSelectValue = !row.IsSelected;
        _accountQueryCheckboxDragPointerId = e.Pointer.PointerId;
        LegacyAccountQueryList.CapturePointer(e.Pointer);
        ApplyAccountQueryCheckboxDragRange(row);
    }

    private void ApplyAccountQueryCheckboxDragRange(LegacyAccountQueryRow row)
    {
        if (!_accountQueryCheckboxDragActive || _accountQueryCheckboxDragAnchor is null)
        {
            return;
        }

        var startIndex = AccountQueryRows.IndexOf(_accountQueryCheckboxDragAnchor);
        var endIndex = AccountQueryRows.IndexOf(row);
        if (startIndex < 0 || endIndex < 0)
        {
            return;
        }

        var first = Math.Min(startIndex, endIndex);
        var last = Math.Max(startIndex, endIndex);
        for (var index = first; index <= last; index++)
        {
            AccountQueryRows[index].IsSelected = _accountQueryCheckboxDragSelectValue;
        }
    }

    private void EndAccountQueryCheckboxDrag()
    {
        if (!_accountQueryCheckboxDragActive)
        {
            return;
        }

        _accountQueryCheckboxDragActive = false;
        _accountQueryCheckboxDragAnchor = null;
        LegacyAccountQueryList.ReleasePointerCaptures();
        UpdateAccountQueryFooter();
    }

    private void LegacyAccountQueryList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_accountQueryCheckboxDragActive ||
            e.Pointer.PointerId != _accountQueryCheckboxDragPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(LegacyAccountQueryList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndAccountQueryCheckboxDrag();
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var rootPosition = e.GetCurrentPoint(null).Position;
        if (TryResolveAccountQueryCheckboxRow(rootPosition, source, out var row))
        {
            ApplyAccountQueryCheckboxDragRange(row);
            e.Handled = true;
        }
    }

    private void LegacyAccountQueryList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_accountQueryCheckboxDragActive ||
            e.Pointer.PointerId != _accountQueryCheckboxDragPointerId)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var rootPosition = e.GetCurrentPoint(null).Position;
        if (TryResolveAccountQueryCheckboxRow(rootPosition, source, out var row))
        {
            ApplyAccountQueryCheckboxDragRange(row);
        }

        EndAccountQueryCheckboxDrag();
        e.Handled = true;
    }

    private void LegacyAccountQueryList_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_accountQueryCheckboxDragActive &&
            e.Pointer.PointerId == _accountQueryCheckboxDragPointerId)
        {
            EndAccountQueryCheckboxDrag();
            e.Handled = true;
        }
    }

    private void LegacyAccountQueryList_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_accountQueryCheckboxDragActive &&
            e.Pointer.PointerId == _accountQueryCheckboxDragPointerId)
        {
            EndAccountQueryCheckboxDrag();
        }
    }

    private void UpdateAccountQueryLocalizedChrome()
    {
        UpdateAccountQueryStartButton();
        UpdateAccountQueryAvatarButton();
        UpdateAccountQueryFooter();
    }

    private void ApplyLegacyPageMode(bool showAccountQuery)
    {
        LegacyAccountsTabContent.Visibility =
            showAccountQuery ? Visibility.Collapsed : Visibility.Visible;
        LegacyAccountQueryTabContent.Visibility =
            showAccountQuery ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LegacyAccountQueryStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountQueryRunning)
        {
            return;
        }

        if (AppState.IsBusy)
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_OtherBusy"), InfoBarSeverity.Warning);
            return;
        }

        if (LegacyAccountQueryFailedOnlyCheck.IsChecked != true &&
            AccountQueryRows.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_NoAccounts"), InfoBarSeverity.Warning);
            return;
        }

        List<LegacyAccountQueryRow> rows;
        if (LegacyAccountQueryFailedOnlyCheck.IsChecked == true)
        {
            rows = AccountQueryRows
                .Where(row => IsFailedAccountQueryStatus(row.StatusCode))
                .ToList();
            if (rows.Count == 0)
            {
                AppState.ShowStatus(Loc.T("Legacy_Query_NoFailedAccounts"), InfoBarSeverity.Informational);
                return;
            }
        }
        else
        {
            rows = AccountQueryRows.Where(row => row.IsSelected).ToList();
            if (rows.Count == 0)
            {
                if (!await ConfirmQueryAllWithoutSelectionAsync())
                {
                    return;
                }

                rows = AccountQueryRows.ToList();
            }
        }

        if (!await EnsureAccountQueryActionsSelectedAsync())
        {
            return;
        }

        if (!TryBuildAccountQueryOptions(out var options))
        {
            return;
        }

        await RunAccountQueryAsync(rows, options);
    }

    private void LegacyAccountQueryStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_accountQueryRunning)
        {
            return;
        }

        AppState.ShowStatus(Loc.T("Legacy_Query_Cancelling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    private async void LegacyAccountQueryContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountQueryRunning || AppState.IsBusy)
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_OtherBusy"), InfoBarSeverity.Warning);
            return;
        }

        List<LegacyAccountQueryRow> rows;
        if (LegacyAccountQueryFailedOnlyCheck.IsChecked == true)
        {
            rows = AccountQueryRows
                .Where(row => IsFailedAccountQueryStatus(row.StatusCode))
                .ToList();
            if (rows.Count == 0)
            {
                AppState.ShowStatus(Loc.T("Legacy_Query_NoFailedAccounts"), InfoBarSeverity.Informational);
                return;
            }
        }
        else
        {
            var allUnfinishedRows = AccountQueryRows
                .Where(row => !LegacyAccountQueryRow.IsTerminal(row.StatusCode))
                .ToList();
            if (allUnfinishedRows.Count == 0)
            {
                AppState.ShowStatus(Loc.T("Legacy_Query_ContinueEmpty"), InfoBarSeverity.Informational);
                return;
            }

            if (AccountQueryRows.Any(row => row.IsSelected))
            {
                rows = allUnfinishedRows.Where(row => row.IsSelected).ToList();
                if (rows.Count == 0)
                {
                    AppState.ShowStatus(Loc.T("Legacy_Query_ContinueEmpty"), InfoBarSeverity.Informational);
                    return;
                }
            }
            else
            {
                if (!await ConfirmQueryAllWithoutSelectionAsync())
                {
                    return;
                }

                rows = allUnfinishedRows;
            }
        }

        if (!await EnsureAccountQueryActionsSelectedAsync())
        {
            return;
        }

        if (!TryBuildAccountQueryOptions(out var options))
        {
            return;
        }

        await RunAccountQueryAsync(rows, options);
    }

    private async Task<bool> EnsureAccountQueryActionsSelectedAsync()
    {
        var hasQuery =
            LegacyAccountQueryRankCheck.IsChecked == true ||
            LegacyAccountQueryUserInfoCheck.IsChecked == true ||
            LegacyAccountQueryBanTimeCheck.IsChecked == true;
        var hasProfileChange =
            LegacyAccountQueryChangeNameCheck.IsChecked == true ||
            LegacyAccountQueryChangeAvatarCheck.IsChecked == true ||
            LegacyAccountQueryChangeSummaryCheck.IsChecked == true;
        if (hasQuery || hasProfileChange)
        {
            return true;
        }

        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Legacy_Query_NoActionTitle"),
            Content = Loc.T("Legacy_Query_NoActionMessage"),
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        LegacyAccountQueryRankCheck.IsChecked = true;
        LegacyAccountQueryUserInfoCheck.IsChecked = true;
        LegacyAccountQueryBanTimeCheck.IsChecked = true;
        return true;
    }

    private async Task<bool> ConfirmQueryAllWithoutSelectionAsync()
    {
        if (AccountQueryRows.Any(row => row.IsSelected))
        {
            return true;
        }

        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Legacy_Query_NoSelectionConfirmTitle"),
            Content = Loc.T("Legacy_Query_NoSelectionConfirmMessage"),
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private bool TryBuildAccountQueryOptions(out SteamAccountQueryOptions options)
    {
        options = default!;
        var changeName = LegacyAccountQueryChangeNameCheck.IsChecked == true;
        var changeAvatar = LegacyAccountQueryChangeAvatarCheck.IsChecked == true;
        var changeSummary = LegacyAccountQueryChangeSummaryCheck.IsChecked == true;
        var newName = LegacyAccountQueryNewNameBox.Text?.Trim();
        var newSummary = LegacyAccountQueryNewSummaryBox.Text?.Trim();

        if (changeName && string.IsNullOrWhiteSpace(newName))
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_NewNameRequired"), InfoBarSeverity.Warning);
            LegacyAccountQueryNewNameBox.Focus(FocusState.Programmatic);
            return false;
        }

        if (changeAvatar &&
            (string.IsNullOrWhiteSpace(_accountQueryAvatarPath) || !File.Exists(_accountQueryAvatarPath)))
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_AvatarRequired"), InfoBarSeverity.Warning);
            return false;
        }

        if (changeSummary && string.IsNullOrWhiteSpace(newSummary))
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_NewSummaryRequired"), InfoBarSeverity.Warning);
            LegacyAccountQueryNewSummaryBox.Focus(FocusState.Programmatic);
            return false;
        }

        options = new SteamAccountQueryOptions(
            LegacyAccountQueryRankCheck.IsChecked == true,
            LegacyAccountQueryUserInfoCheck.IsChecked == true,
            LegacyAccountQueryBanTimeCheck.IsChecked == true,
            changeName ? newName : null,
            changeAvatar ? _accountQueryAvatarPath : null,
            changeSummary ? newSummary : null);
        return true;
    }

    private async Task RunAccountQueryAsync(
        IReadOnlyList<LegacyAccountQueryRow> rows,
        SteamAccountQueryOptions options)
    {
        _accountQueryRunning = true;
        _accountQueryRunSuccessCount = 0;
        _accountQueryRunFailedCount = 0;
        UpdateAccountQueryButtonState();
        foreach (var row in rows)
        {
            row.SetPending();
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var threadCount = GetAccountQueryThreadCount();
        using var throttle = new SemaphoreSlim(threadCount, threadCount);
        LegacyAccountQueryStatusText.Text = Loc.Tf("Legacy_Query_Running_Format", rows.Count);

        try
        {
            var tasks = rows.Select(row => RunOneAccountQueryAsync(
                row,
                options,
                throttle,
                cancellationToken)).ToList();
            await Task.WhenAll(tasks);
        }
        finally
        {
            var wasCancelled = cancellationToken.IsCancellationRequested;
            _accountQueryRunning = false;
            AppState.EndBusyOperation();

            if (!wasCancelled)
            {
                foreach (var row in AccountQueryRows)
                {
                    row.IsSelected = false;
                }

                var settings = AppState.SettingsService.Load();
                settings.AccountQueryLastRunAt = DateTimeOffset.Now;
                AppState.SettingsService.Save(settings);
            }

            AppState.ReloadWhiteAccounts();
            RefreshAccountQueryRows();
            UpdateAccountQueryButtonState();
            UpdateAccountQueryFooter();
            LegacyAccountQueryStatusText.Text = wasCancelled
                ? Loc.Tf(
                    "Legacy_Query_Cancelled_Format",
                    _accountQueryRunSuccessCount,
                    _accountQueryRunFailedCount)
                : Loc.Tf(
                    "Legacy_Query_Completed_Format",
                    _accountQueryRunSuccessCount,
                    _accountQueryRunFailedCount);
        }
    }

    private async Task RunOneAccountQueryAsync(
        LegacyAccountQueryRow row,
        SteamAccountQueryOptions options,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await throttle.WaitAsync(cancellationToken);
            acquired = true;
            var progress = new Progress<string>(row.SetProgress);
            var result = await AppState.AccountQueryService.QueryAsync(
                row.Item,
                options,
                (prompt, token) => PromptAccountQueryGuardCodeAsync(prompt, token, row.Item),
                progress,
                cancellationToken);

            // Progress<T> 通过 UI 队列异步派发；先让已排队的最后一条进度落地，再写最终状态。
            await Task.Yield();
            AppState.WhiteAccountService.SaveAccountQueryResult(row.Item, result);
            if (result.NameApplied || result.AvatarApplied)
            {
                AppState.WhiteAccountService.UpdateProfileLocally(
                    result.SteamId,
                    result.NameApplied ? result.AppliedPersonaName : null,
                    result.AvatarApplied ? options.ChangeAvatarFrom : null);
            }

            row.ApplyResult(result);
            if (result.IsSuccess)
            {
                _accountQueryRunSuccessCount++;
            }
            else
            {
                _accountQueryRunFailedCount++;
            }
        }
        catch (OperationCanceledException)
        {
            var result = new SteamAccountQueryResult
            {
                AccountName = row.AccountName,
                SteamId = row.Item.SteamId,
                StatusCode = "cancelled",
                Message = Loc.T("Legacy_Query_StatusCancelled")
            };
            AppState.WhiteAccountService.SaveAccountQueryResult(row.Item, result);
            row.ApplyResult(result);
            _accountQueryRunFailedCount++;
        }
        catch (Exception ex)
        {
            var result = new SteamAccountQueryResult
            {
                AccountName = row.AccountName,
                SteamId = row.Item.SteamId,
                StatusCode = "failed",
                Message = ex.Message
            };
            AppState.WhiteAccountService.SaveAccountQueryResult(row.Item, result);
            row.ApplyResult(result);
            _accountQueryRunFailedCount++;
            AppLog.Warn($"账号查询未完成（{row.AccountName}）：{ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                throttle.Release();
            }

            UpdateAccountQueryFooter();
        }
    }

    private async Task<string?> PromptAccountQueryGuardCodeAsync(
        SteamGuardPrompt prompt,
        CancellationToken cancellationToken,
        SteamAccountHistoryItem account)
    {
        await _accountQueryGuardDialogGate.WaitAsync(cancellationToken);
        try
        {
            return await PromptGuardCodeAsync(prompt, cancellationToken, account);
        }
        finally
        {
            _accountQueryGuardDialogGate.Release();
        }
    }

    private void LegacyAccountQueryChangeNameCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LegacyAccountQueryNewNameBox is not null)
        {
            LegacyAccountQueryNewNameBox.IsEnabled = LegacyAccountQueryChangeNameCheck.IsChecked == true;
        }
    }

    private void LegacyAccountQueryOptionsScrollViewer_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (LegacyAccountQueryOptionsScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(LegacyAccountQueryOptionsScrollViewer)
            .Properties.MouseWheelDelta;
        var target = Math.Clamp(
            LegacyAccountQueryOptionsScrollViewer.HorizontalOffset - delta * 1.5,
            0,
            LegacyAccountQueryOptionsScrollViewer.ScrollableWidth);
        LegacyAccountQueryOptionsScrollViewer.ChangeView(
            target,
            null,
            null,
            disableAnimation: true);
        e.Handled = true;
    }

    private void LegacyAccountQueryChangeAvatarCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LegacyAccountQueryAvatarButton is not null)
        {
            LegacyAccountQueryAvatarButton.IsEnabled = LegacyAccountQueryChangeAvatarCheck.IsChecked == true;
        }
    }

    private void LegacyAccountQueryChangeSummaryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LegacyAccountQueryNewSummaryBox is not null)
        {
            LegacyAccountQueryNewSummaryBox.IsEnabled = LegacyAccountQueryChangeSummaryCheck.IsChecked == true;
        }
    }

    private async void LegacyAccountQueryAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

        StorageFile? file;
        try
        {
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Legacy_Query_AvatarPickFailed_Format", ex.Message), InfoBarSeverity.Error);
            return;
        }

        if (file is null)
        {
            return;
        }

        _accountQueryAvatarPath = file.Path;
        UpdateAccountQueryAvatarButton();
    }

    private void UpdateAccountQueryAvatarButton()
    {
        if (LegacyAccountQueryAvatarButton is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_accountQueryAvatarPath))
        {
            LegacyAccountQueryAvatarButton.Content = Loc.T("Legacy_Query_SelectAvatar");
            ToolTipService.SetToolTip(LegacyAccountQueryAvatarButton, null);
            return;
        }

        LegacyAccountQueryAvatarButton.Content = Loc.T("Legacy_Query_AvatarSelected");
        ToolTipService.SetToolTip(LegacyAccountQueryAvatarButton, _accountQueryAvatarPath);
    }

    private void LegacyAccountQueryThreadIncrementButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAccountQueryThreadTextBox();
        var value = Math.Clamp(GetAccountQueryThreadCount() + 1, 1, 100);
        SyncAccountQueryThreadInputs(value);
        SaveAccountQueryThreadCount(value);
    }

    private void LegacyAccountQueryThreadDecrementButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAccountQueryThreadTextBox();
        var value = Math.Clamp(GetAccountQueryThreadCount() - 1, 1, 100);
        SyncAccountQueryThreadInputs(value);
        SaveAccountQueryThreadCount(value);
    }

    private void LegacyAccountQueryThreadTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_accountQueryInitializing || _accountQueryRunning || _accountQuerySyncingThreadInputs)
        {
            return;
        }

        if (!int.TryParse(LegacyAccountQueryThreadTextBox.Text, out var value) ||
            value is < 1 or > 100)
        {
            return;
        }

        SyncAccountQueryThreadInputs(value, updateTextBox: false);
        SaveAccountQueryThreadCount(value);
    }

    private void LegacyAccountQueryThreadTextBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitAccountQueryThreadTextBox();

    private void LegacyAccountQueryThreadTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        CommitAccountQueryThreadTextBox();
        e.Handled = true;
    }

    private void CommitAccountQueryThreadTextBox()
    {
        if (_accountQueryInitializing || _accountQueryRunning)
        {
            return;
        }

        var value = int.TryParse(LegacyAccountQueryThreadTextBox.Text, out var parsed)
            ? Math.Clamp(parsed, 1, 100)
            : GetAccountQueryThreadCount();
        SyncAccountQueryThreadInputs(value);
        SaveAccountQueryThreadCount(value);
    }

    private void SyncAccountQueryThreadInputs(
        int value,
        bool updateDisplay = true,
        bool updateTextBox = true)
    {
        value = Math.Clamp(value, 1, 100);
        _accountQuerySyncingThreadInputs = true;
        try
        {
            if (updateDisplay && LegacyAccountQueryThreadDisplay is not null)
            {
                LegacyAccountQueryThreadDisplay.Text = value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (updateTextBox && LegacyAccountQueryThreadTextBox is not null)
            {
                LegacyAccountQueryThreadTextBox.Text = value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _accountQuerySyncingThreadInputs = false;
        }
    }

    private static void SaveAccountQueryThreadCount(int value)
    {
        value = Math.Clamp(value, 1, 100);
        var settings = AppState.SettingsService.Load();
        if (settings.AccountQueryThreadCount == value)
        {
            return;
        }

        settings.AccountQueryThreadCount = value;
        AppState.SettingsService.Save(settings);
    }

    private int GetAccountQueryThreadCount()
    {
        if (LegacyAccountQueryThreadTextBox is not null &&
            int.TryParse(LegacyAccountQueryThreadTextBox.Text, out var textValue))
        {
            return Math.Clamp(textValue, 1, 100);
        }

        if (LegacyAccountQueryThreadDisplay is null ||
            !int.TryParse(LegacyAccountQueryThreadDisplay.Text, out var displayValue))
        {
            return 3;
        }

        return Math.Clamp(displayValue, 1, 100);
    }

    private void LegacyAccountQuerySelectAll_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(_ => true);

    private void LegacyAccountQuerySelectInvert_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => !row.IsSelected);

    private void LegacyAccountQuerySelectNone_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(_ => false);

    private void LegacyAccountQueryRefreshButton_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(_ => false);

    private void LegacyAccountQueryFailedOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_accountQueryInitializing || _accountQueryRunning)
        {
            return;
        }

        RefreshAccountQueryRows();
    }

    private void LegacyAccountQueryResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountQueryRunning)
        {
            return;
        }

        _accountQueryInitializing = true;
        try
        {
            LegacyAccountQueryRankCheck.IsChecked = false;
            LegacyAccountQueryUserInfoCheck.IsChecked = false;
            LegacyAccountQueryBanTimeCheck.IsChecked = false;
            LegacyAccountQueryChangeNameCheck.IsChecked = false;
            LegacyAccountQueryChangeAvatarCheck.IsChecked = false;
            LegacyAccountQueryChangeSummaryCheck.IsChecked = false;
            LegacyAccountQueryFailedOnlyCheck.IsChecked = false;
            LegacyAccountQueryNewNameBox.Text = string.Empty;
            LegacyAccountQueryNewSummaryBox.Text = string.Empty;
            _accountQueryAvatarPath = null;
            SyncAccountQueryThreadInputs(AccountQueryDefaultThreadCount);
            SaveAccountQueryThreadCount(AccountQueryDefaultThreadCount);
            UpdateAccountQueryAvatarButton();
        }
        finally
        {
            _accountQueryInitializing = false;
        }

        RefreshAccountQueryRows();
    }

    private void LegacyAccountQuerySelectUnbanned_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => !row.IsBanned);

    private void LegacyAccountQuerySelectBanned_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => row.IsBanned);

    private void LegacyAccountQuerySelectLevel2_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => row.IsLevel2);

    private void LegacyAccountQuerySelectAvailableLevel2_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => row.IsLevel2 && !row.IsBanned);

    private void LegacyAccountQuerySelectBannedLevel2_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => row.IsLevel2 && row.IsBanned);

    private void LegacyAccountQuerySelectQueried_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => LegacyAccountQueryRow.IsTerminal(row.StatusCode));

    private void LegacyAccountQuerySelectNotQueried_Click(object sender, RoutedEventArgs e) =>
        SetAccountQuerySelection(row => !LegacyAccountQueryRow.IsTerminal(row.StatusCode));

    private static bool IsFailedAccountQueryStatus(string? statusCode) =>
        statusCode is "failed" or "partial" or "rateLimited";

    private void SetAccountQuerySelection(Func<LegacyAccountQueryRow, bool> predicate)
    {
        if (_accountQueryRunning)
        {
            return;
        }

        foreach (var row in AccountQueryRows)
        {
            row.IsSelected = predicate(row);
        }

        UpdateAccountQueryFooter();
    }

    private async void LegacyAccountQueryExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AccountQueryRows.Where(row => row.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_SelectRequired"), InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"accounts_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        picker.FileTypeChoices.Add(Loc.T("Legacy_Query_TextFile"), [".txt"]);
        InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var lines = selected.Select(row => $"{row.AccountName}----{row.Item.Password}");
        await FileIO.WriteTextAsync(file, string.Join(Environment.NewLine, lines));
        AppState.ShowStatus(
            Loc.Tf("Legacy_Query_Exported_Format", selected.Count),
            InfoBarSeverity.Success);
    }

    private async void LegacyAccountQueryDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountQueryRunning)
        {
            return;
        }

        var selected = AccountQueryRows.Where(row => row.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Legacy_Query_SelectRequired"), InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Legacy_Query_DeleteConfirmTitle"),
            Content = Loc.Tf("Legacy_Query_DeleteConfirm_Format", selected.Count),
            PrimaryButtonText = Loc.T("Legacy_Btn_Delete"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removed = AppState.WhiteAccountService.DeleteAccounts(
            selected.Select(row => row.Item).ToList());
        AppState.ReloadWhiteAccounts();
        RebuildRows();
        RefreshAccountQueryRows();
        AppState.ShowStatus(Loc.Tf("Legacy_Query_Deleted_Format", removed), InfoBarSeverity.Success);
    }

    private void LegacyAccountQueryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LegacyAccountQueryRow row)
        {
            UpdateAccountQueryFooter();
        }
    }

    private void LegacyAccountQueryAccountCell_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: LegacyAccountQueryRow row
            } element)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var login = new MenuFlyoutItem
        {
            Text = Loc.T("WhiteAccounts_Btn_Login"),
            Icon = new FontIcon { Glyph = "\uE8D4" }
        };
        login.Click += (_, _) =>
        {
            _contextAccount = row.Item;
            LegacyRowLogin_Click(login, new RoutedEventArgs());
        };
        flyout.Items.Add(login);
        flyout.ShowAt(
            element,
            new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private void UpdateAccountQueryButtonState()
    {
        if (LegacyAccountQueryStartText is null || LegacyAccountQueryStartIcon is null)
        {
            return;
        }

        LegacyAccountQueryStartText.Text = Loc.T("Legacy_Query_Start");
        LegacyAccountQueryStartIcon.Glyph = "\uE768";
        LegacyAccountQueryStartButton.IsEnabled = !_accountQueryRunning && !AppState.IsBusy;
        LegacyAccountQueryStopButton.IsEnabled = _accountQueryRunning;
        LegacyAccountQueryContinueButton.IsEnabled = !_accountQueryRunning;
        if (LegacyAccountQueryExportButton is not null)
        {
            LegacyAccountQueryExportButton.IsEnabled = !_accountQueryRunning;
            LegacyAccountQueryDeleteButton.IsEnabled = !_accountQueryRunning;
            LegacyAccountQueryRefreshButton.IsEnabled = !_accountQueryRunning;
            LegacyAccountQueryResetButton.IsEnabled = !_accountQueryRunning;
            LegacyAccountQueryFailedOnlyCheck.IsEnabled = !_accountQueryRunning;
            LegacyAccountQuerySelectOptionsButton.IsEnabled = !_accountQueryRunning;
            LegacyAccountQueryOptionsScrollViewer.IsEnabled = !_accountQueryRunning;
        }
    }

    private void UpdateAccountQueryStartButton() => UpdateAccountQueryButtonState();

    private void UpdateAccountQueryFooter()
    {
        if (LegacyAccountQueryTotalCountText is null)
        {
            return;
        }

        var completed = AccountQueryRows.Count(row =>
            string.Equals(row.StatusCode, "completed", StringComparison.Ordinal));
        var selected = AccountQueryRows.Count(row => row.IsSelected);
        LegacyAccountQueryTotalCountText.Text = Loc.Tf("Legacy_Query_Total_Format", AccountQueryRows.Count);
        LegacyAccountQuerySuccessCountText.Text = Loc.Tf("Legacy_Query_Success_Format", completed);
        LegacyAccountQuerySelectedCountText.Text = Loc.Tf("Legacy_Query_Selected_Format", selected);

        var lastRun = AppState.SettingsService.Load().AccountQueryLastRunAt;
        LegacyAccountQueryLastRunText.Text = lastRun.HasValue
            ? Loc.Tf(
                "Legacy_Query_LastRun_Format",
                lastRun.Value.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture))
            : Loc.T("Legacy_Query_LastRun_Never");
    }

}

/// <summary>账号查询标签的一行；结果字段直接映射到共享账号模型。</summary>
public sealed partial class LegacyAccountQueryRow : INotifyPropertyChanged
{
    private readonly Brush _successBrush;
    private readonly Brush _cooldownBrush;
    private readonly Brush _errorBrush;
    private readonly Brush _mutedBrush;
    private string? _progressText;
    private bool _isSelected;
    private string? _statusCode;
    private string? _message;

    internal LegacyAccountQueryRow(
        SteamAccountHistoryItem item,
        Brush successBrush,
        Brush cooldownBrush,
        Brush errorBrush,
        Brush mutedBrush)
    {
        Item = item;
        _successBrush = successBrush;
        _cooldownBrush = cooldownBrush;
        _errorBrush = errorBrush;
        _mutedBrush = mutedBrush;
        _statusCode = string.IsNullOrWhiteSpace(item.AccountQueryStatus) ? "pending" : item.AccountQueryStatus;
        _message = item.AccountQueryMessage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SteamAccountHistoryItem Item { get; }

    public string AccountName => Item.AccountName;

    private Brush? _highlightBrush;

    /// <summary>当前行是否使用账号管理页同款的半透明主题色高亮。</summary>
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
            Raise();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
        }
    }

    public string? StatusCode => _statusCode;

    public string StatusText => !string.IsNullOrWhiteSpace(_progressText)
        ? _progressText!
        : FormatStatus(_statusCode);

    public string StatusDetail => string.IsNullOrWhiteSpace(_message)
        ? FormatStatus(_statusCode)
        : _message!;

    public Brush StatusBrush => _progressText is not null
        ? _cooldownBrush
        : _statusCode switch
        {
            "completed" => _successBrush,
            "partial" or "rateLimited" => _cooldownBrush,
            "failed" or "invalidCredentials" or "locked" => _errorBrush,
            _ => _mutedBrush
        };

    public string PersonaName => string.IsNullOrWhiteSpace(Item.PersonaName) ? "-" : Item.PersonaName!;

    public string RankText => Item.CsProfileRank?.ToString(
        System.Globalization.CultureInfo.InvariantCulture)
        ?? Item.CsPlayerLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? "-";

    public string LastLoginText => !string.IsNullOrWhiteSpace(Item.LastGameLoginText)
        ? Item.LastGameLoginText!
        : Item.LastSteamLoginAt?.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm",
            System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    public string RegionText => string.IsNullOrWhiteSpace(Item.Region) ? "-" : Item.Region!;

    public string ChinaSteamText => Item.Cs2IsChina switch
    {
        true => Loc.T("Legacy_Query_China_Yes"),
        false => Loc.T("Legacy_Query_China_No"),
        _ => "-"
    };

    public string SteamIdText => string.IsNullOrWhiteSpace(Item.SteamId) ? "-" : Item.SteamId;

    public bool IsBanned =>
        Item.GcVacBanned == true ||
        Item.BanExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.Now;

    public bool IsLevel2 => Item.CsProfileRank == 2;

    public string BanTimeText
    {
        get
        {
            if (Item.GcVacBanned == true)
            {
                return Loc.T("Legacy_Query_PermanentVac");
            }

            if (Item.BanExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.Now)
            {
                return expiresAt.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return Item.AccountQueryBanChecked
                ? Loc.T("Legacy_Query_NotBanned")
                : "-";
        }
    }

    public Brush BanTimeBrush => Item.GcVacBanned == true
        ? _errorBrush
        : Item.BanExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.Now
            ? _cooldownBrush
            : _mutedBrush;

    internal void SetPending()
    {
        _progressText = Loc.T("Legacy_Query_StatusPending");
        _statusCode = "pending";
        _message = null;
        RaiseStatus();
    }

    internal void SetProgress(string message)
    {
        _progressText = message;
        RaiseStatus();
    }

    /// <summary>账号管理手动修改冷却后，只刷新本行“封禁时间”相关绑定，不重建查询列表。</summary>
    internal void RefreshBanTimeFromItem()
    {
        Raise(nameof(BanTimeText));
        Raise(nameof(BanTimeBrush));
        Raise(nameof(IsBanned));
    }

    internal void ApplyResult(SteamAccountQueryResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SteamId))
        {
            Item.SteamId = result.SteamId;
        }

        if (!string.IsNullOrWhiteSpace(result.RefreshToken))
        {
            Item.EyaToken = result.RefreshToken;
            Item.TokenExpiresAt = result.TokenExpiresAt;
        }

        if (result.RankChecked)
        {
            Item.CsProfileRank = result.CsProfileRank;
            Item.LastGameLoginText = result.LastGameLoginText;
        }

        if (result.UserInfoChecked && !string.IsNullOrWhiteSpace(result.PersonaName))
        {
            Item.PersonaName = result.PersonaName;
        }

        if (result.UserInfoChecked)
        {
            Item.Region = result.Region;
        }

        if (result.ChinaSteamChecked)
        {
            Item.Cs2IsChina = result.HasChinaSteam;
        }

        if (result.BanChecked)
        {
            Item.GcVacBanned = result.VacBanned;
            Item.BanExpiresAt = result.BanExpiresAt;
            Item.AccountQueryBanChecked = true;
            Item.CooldownSeconds = result.CooldownSeconds ?? 0;
        }

        if (result.NameApplied && !string.IsNullOrWhiteSpace(result.AppliedPersonaName))
        {
            Item.PersonaName = result.AppliedPersonaName;
        }

        Item.AccountQueryStatus = result.StatusCode;
        Item.AccountQueryMessage = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message;
        Item.AccountQueryUpdatedAt = DateTimeOffset.Now;

        _progressText = null;
        _statusCode = result.StatusCode;
        _message = result.Message;
        RaiseStatus();
        Raise(nameof(PersonaName));
        Raise(nameof(RankText));
        Raise(nameof(LastLoginText));
        Raise(nameof(RegionText));
        Raise(nameof(ChinaSteamText));
        Raise(nameof(SteamIdText));
        Raise(nameof(BanTimeText));
        Raise(nameof(BanTimeBrush));
        Raise(nameof(IsBanned));
        Raise(nameof(IsLevel2));
    }

    internal static bool IsTerminal(string? statusCode) =>
        statusCode is "completed" or "invalidCredentials";

    private static string FormatStatus(string? statusCode) => statusCode switch
    {
        "completed" => Loc.T("Legacy_Query_StatusCompleted"),
        "partial" => Loc.T("Legacy_Query_StatusPartial"),
        "failed" => Loc.T("Legacy_Query_StatusFailed"),
        "cancelled" => Loc.T("Legacy_Query_StatusCancelled"),
        "invalidCredentials" => Loc.T("Legacy_Query_StatusInvalidCredentials"),
        "rateLimited" => Loc.T("Legacy_Query_StatusRateLimited"),
        "locked" => Loc.T("Legacy_Query_StatusLocked"),
        _ => Loc.T("Legacy_Query_StatusPending")
    };

    private void RaiseStatus()
    {
        Raise(nameof(StatusCode));
        Raise(nameof(StatusText));
        Raise(nameof(StatusDetail));
        Raise(nameof(StatusBrush));
        Raise(nameof(BanTimeText));
        Raise(nameof(BanTimeBrush));
        Raise(nameof(IsBanned));
        Raise(nameof(IsLevel2));
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
