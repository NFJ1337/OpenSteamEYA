using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class ClashPage : Page, INotifyPropertyChanged
{
    private const string DefaultApiUrl = "http://127.0.0.1:9097";

    private readonly ClashProxyService _clashService = new();
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<ClashProxyGroup> _groups = [];
    private readonly ObservableCollection<ClashNodeViewModel> _nodes = [];
    private readonly Dictionary<string, int> _knownDelays = new(StringComparer.Ordinal);

    private CancellationTokenSource? _operationCancellation;
    private ClashProxySnapshot? _snapshot;
    private ClashProxyGroup? _currentGroup;
    private string? _preferredGroupName;
    private bool _suppressNodeSelection;
    private bool _busy;
    private bool _hasConnected;

    public ClashPage()
    {
        InitializeComponent();
        ProxyListView.ItemsSource = _nodes;
        Loc.LanguageChanged += OnLanguageChanged;
        SetStatus(Loc.T("Clash_Status_Ready"), ClashStatusKind.Info);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var settings = AppState.SettingsService.Load();
        _preferredGroupName = settings.ClashProxyGroup;
        ApiUrlTextBox.Text = string.IsNullOrWhiteSpace(settings.ClashApiUrl)
            ? DefaultApiUrl
            : settings.ClashApiUrl;
        ApiSecretPasswordBox.Password = settings.ClashApiSecret ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(settings.ClashApiUrl))
        {
            // 参考程序的页面行为：保存过配置时，进入页面自动尝试连接；
            // 自动连接失败只写状态，不弹窗打断用户。
            _ = LoadSnapshotAsync(
                saveOnSuccess: false,
                showSuccessDialog: false,
                autoConnect: true);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _operationCancellation?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await LoadSnapshotAsync(
            saveOnSuccess: true,
            showSuccessDialog: true,
            autoConnect: false);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await LoadSnapshotAsync(
            saveOnSuccess: false,
            showSuccessDialog: false,
            autoConnect: false);

    private async void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs())
        {
            return;
        }

        try
        {
            SaveConfiguration();
            SetStatus(Loc.T("Clash_Status_Saved"), ClashStatusKind.Success);
            await ShowMessageDialogAsync(
                Loc.T("Clash_Dialog_SaveSuccessTitle"),
                Loc.T("Clash_Dialog_SaveSuccessContent"));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存 Clash API 配置失败：{ex.Message}");
            SetStatus(
                Loc.Tf("Clash_Status_SaveFailed_Format", ex.Message),
                ClashStatusKind.Error);
        }
    }

    private async Task LoadSnapshotAsync(
        bool saveOnSuccess,
        bool showSuccessDialog,
        bool autoConnect)
    {
        if (_busy || !ValidateInputs())
        {
            return;
        }

        var cancellation = ResetOperationCancellation();
        SetBusy(true);
        SetStatus(Loc.T("Clash_Status_Connecting"), ClashStatusKind.Info);
        try
        {
            var snapshot = await _clashService.GetSnapshotAsync(
                ApiUrlTextBox.Text,
                ApiSecretPasswordBox.Password,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            ApplySnapshot(snapshot);
            _hasConnected = true;
            RefreshActionButtons();
            if (saveOnSuccess)
            {
                SaveConfiguration();
            }

            if (_groups.Count == 0)
            {
                SetStatus(Loc.T("Clash_Status_NoGroups"), ClashStatusKind.Warning);
            }
            else
            {
                SetStatus(
                    Loc.Tf("Clash_Status_Connected_Format", _groups.Count),
                    ClashStatusKind.Success);
            }

            if (showSuccessDialog)
            {
                await ShowMessageDialogAsync(
                    Loc.T("Clash_Dialog_ConnectionSuccessTitle"),
                    Loc.T("Clash_Dialog_ConnectionSuccessContent"));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!autoConnect)
            {
                SetStatus(Loc.T("Clash_Status_Cancelled"), ClashStatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clash API 连接失败：{ex.Message}");
            SetStatus(
                Loc.Tf("Clash_Status_ConnectionFailed_Format", ex.Message),
                ClashStatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplySnapshot(ClashProxySnapshot snapshot)
    {
        _snapshot = snapshot;
        _groups.Clear();
        foreach (var group in snapshot.Groups)
        {
            _groups.Add(group);
        }

        var selectedGroup =
            _groups.FirstOrDefault(group => string.Equals(
                group.Name,
                _preferredGroupName,
                StringComparison.OrdinalIgnoreCase))
            ?? _groups.FirstOrDefault();
        UpdateGroupView(selectedGroup);
        RebuildProxyGroupMenu();
    }

    private void RebuildProxyGroupMenu()
    {
        ProxyGroupFlyout.Items.Clear();
        foreach (var group in _groups)
        {
            var item = new MenuFlyoutItem
            {
                Text = group.DisplayName,
                Tag = group
            };
            item.Click += ProxyGroupMenuItem_Click;
            ProxyGroupFlyout.Items.Add(item);
        }
    }

    private void ProxyGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ClashProxyGroup group })
        {
            return;
        }

        UpdateGroupView(group);
        if (_hasConnected)
        {
            _preferredGroupName = group.Name;
            SaveConfiguration();
        }
    }

    private void UpdateGroupView(ClashProxyGroup? group)
    {
        _currentGroup = group;
        ProxyGroupButtonText.Text = group?.DisplayName ?? Loc.T("Clash_CurrentProxy_None");
        _suppressNodeSelection = true;
        try
        {
            _nodes.Clear();
            ProxyListView.SelectedItem = null;
            if (group is null)
            {
                CurrentProxyText.Text = Loc.T("Clash_CurrentProxy_None");
                return;
            }

            CurrentProxyText.Text = Loc.Tf("Clash_CurrentProxy_Format", group.Current);
            foreach (var nodeName in group.Nodes)
            {
                var nodeType = _snapshot?.NodeTypes.TryGetValue(nodeName, out var type) == true
                    ? type
                    : "Unknown";
                var node = new ClashNodeViewModel(nodeName, nodeType, group.CanSelectNode)
                {
                    IsCurrent = string.Equals(nodeName, group.Current, StringComparison.Ordinal)
                };
                if (_knownDelays.TryGetValue(nodeName, out var delay))
                {
                    ApplyDelayVisual(node, delay);
                }

                _nodes.Add(node);
            }

            var currentNode = _nodes.FirstOrDefault(node =>
                string.Equals(node.Name, group.Current, StringComparison.Ordinal));
            ProxyListView.SelectedItem = currentNode;
        }
        finally
        {
            _suppressNodeSelection = false;
        }

        RefreshActionButtons();
    }

    private async void ProxyListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNodeSelection ||
            _busy ||
            _currentGroup is not { } group ||
            ProxyListView.SelectedItem is not ClashNodeViewModel node ||
            string.Equals(node.Name, group.Current, StringComparison.Ordinal))
        {
            return;
        }

        if (!group.CanSelectNode)
        {
            SetStatus(Loc.T("Clash_Status_GroupAuto"), ClashStatusKind.Warning);
            SelectNodeWithoutNotification(group.Current);
            return;
        }

        var cancellation = ResetOperationCancellation();
        SetBusy(true);
        try
        {
            await _clashService.SelectProxyAsync(
                ApiUrlTextBox.Text,
                ApiSecretPasswordBox.Password,
                group.Name,
                node.Name,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var updatedGroup = group with { Current = node.Name };
            var groupIndex = _groups.IndexOf(group);
            if (groupIndex >= 0)
            {
                _groups[groupIndex] = updatedGroup;
                RebuildProxyGroupMenu();
            }

            _currentGroup = updatedGroup;
            foreach (var item in _nodes)
            {
                item.IsCurrent = string.Equals(item.Name, node.Name, StringComparison.Ordinal);
            }

            CurrentProxyText.Text = Loc.Tf("Clash_CurrentProxy_Format", node.Name);
            SetStatus(
                Loc.Tf("Clash_Status_Switched_Format", node.Name),
                ClashStatusKind.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SelectNodeWithoutNotification(group.Current);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"切换 Clash 节点失败：{ex.Message}");
            SetStatus(
                Loc.Tf("Clash_Status_SwitchFailed_Format", ex.Message),
                ClashStatusKind.Error);
            SelectNodeWithoutNotification(group.Current);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            sender is not FrameworkElement { DataContext: ClashNodeViewModel node })
        {
            return;
        }

        var cancellation = ResetOperationCancellation();
        SetBusy(true);
        try
        {
            SetStatus(
                Loc.Tf("Clash_Status_TestingNode_Format", node.Name),
                ClashStatusKind.Info);
            var delay = await TestNodeCoreAsync(node, cancellation.Token);
            SetStatus(
                delay.HasValue
                    ? Loc.Tf("Clash_Status_NodeDelay_Format", node.Name, delay.Value)
                    : Loc.Tf("Clash_Status_TestFailed_Format", node.Name),
                delay.HasValue ? ClashStatusKind.Success : ClashStatusKind.Warning);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"测试 Clash 节点延迟失败：{ex.Message}");
            ApplyDelayFailure(node);
            SetStatus(
                Loc.Tf("Clash_Status_TestFailed_Format", ex.Message),
                ClashStatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _nodes.Count == 0)
        {
            return;
        }

        var cancellation = ResetOperationCancellation();
        SetBusy(true);
        SetStatus(Loc.T("Clash_Status_TestingAll"), ClashStatusKind.Info);
        try
        {
            var nodes = _nodes.ToList();
            using var concurrency = new SemaphoreSlim(8);
            await Task.WhenAll(nodes.Select(async node =>
            {
                await concurrency.WaitAsync(cancellation.Token);
                try
                {
                    await TestNodeCoreAsync(node, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"测试 Clash 节点 {node.Name} 延迟失败：{ex.Message}");
                    ApplyDelayFailure(node);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
            cancellation.Token.ThrowIfCancellationRequested();

            SortNodesByDelay();
            SetStatus(Loc.T("Clash_Status_TestDone"), ClashStatusKind.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus(Loc.T("Clash_Status_Cancelled"), ClashStatusKind.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"批量测试 Clash 节点延迟失败：{ex.Message}");
            SetStatus(
                Loc.Tf("Clash_Status_TestAllFailed_Format", ex.Message),
                ClashStatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<int?> TestNodeCoreAsync(
        ClashNodeViewModel node,
        CancellationToken cancellationToken)
    {
        node.IsTesting = true;
        try
        {
            var delay = await _clashService.TestDelayAsync(
                ApiUrlTextBox.Text,
                ApiSecretPasswordBox.Password,
                node.Name,
                cancellationToken);
            if (delay.HasValue)
            {
                _knownDelays[node.Name] = delay.Value;
                ApplyDelayVisual(node, delay.Value);
            }
            else
            {
                _knownDelays.Remove(node.Name);
                ApplyDelayFailure(node);
            }

            return delay;
        }
        finally
        {
            node.IsTesting = false;
        }
    }

    private void SortNodesByDelay()
    {
        var currentName = _currentGroup?.Current;
        var ordered = _nodes
            .OrderBy(node => node.DelayMilliseconds ?? int.MaxValue)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _suppressNodeSelection = true;
        try
        {
            for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
            {
                var currentIndex = _nodes.IndexOf(ordered[targetIndex]);
                if (currentIndex != targetIndex)
                {
                    _nodes.Move(currentIndex, targetIndex);
                }
            }

            ProxyListView.SelectedItem = _nodes.FirstOrDefault(node =>
                string.Equals(node.Name, currentName, StringComparison.Ordinal));
        }
        finally
        {
            _suppressNodeSelection = false;
        }
    }

    private void ApplyDelayVisual(ClashNodeViewModel node, int milliseconds)
    {
        var (text, color) = milliseconds switch
        {
            <= 100 => (Loc.Tf("Clash_Delay_Excellent_Format", milliseconds), Colors.LimeGreen),
            <= 200 => (Loc.Tf("Clash_Delay_Good_Format", milliseconds), Colors.DodgerBlue),
            <= 350 => (Loc.Tf("Clash_Delay_Average_Format", milliseconds), Colors.Goldenrod),
            _ => (Loc.Tf("Clash_Delay_Poor_Format", milliseconds), Colors.OrangeRed)
        };
        node.SetDelay(milliseconds, text, color);
    }

    private static void ApplyDelayFailure(ClashNodeViewModel node) =>
        node.SetDelay(null, Loc.T("Clash_Delay_Failed"), Colors.OrangeRed);

    private void SelectNodeWithoutNotification(string nodeName)
    {
        _suppressNodeSelection = true;
        try
        {
            ProxyListView.SelectedItem = _nodes.FirstOrDefault(node =>
                string.Equals(node.Name, nodeName, StringComparison.Ordinal));
        }
        finally
        {
            _suppressNodeSelection = false;
        }
    }

    private bool ValidateInputs()
    {
        if (!string.IsNullOrWhiteSpace(ApiUrlTextBox.Text))
        {
            return true;
        }

        SetStatus(Loc.T("Clash_Status_UrlRequired"), ClashStatusKind.Warning);
        ApiUrlTextBox.Focus(FocusState.Programmatic);
        return false;
    }

    private void SaveConfiguration()
    {
        var settings = AppState.SettingsService.Load();
        settings.ClashApiUrl = ApiUrlTextBox.Text.Trim();
        settings.ClashApiSecret = ApiSecretPasswordBox.Password;
        settings.ClashProxyGroup =
            _currentGroup?.Name
            ?? _preferredGroupName;
        AppState.SettingsService.Save(settings);
        _preferredGroupName = settings.ClashProxyGroup;
    }

    private CancellationTokenSource ResetOperationCancellation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        return _operationCancellation;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ApiUrlTextBox.IsEnabled = !busy;
        ApiSecretPasswordBox.IsEnabled = !busy;
        TestConnectionButton.IsEnabled = !busy;
        SaveConfigButton.IsEnabled = !busy;
        ProxyGroupButton.IsEnabled = !busy && _groups.Count > 0;
        ProxyListView.IsEnabled = !busy;
        RefreshActionButtons();
    }

    private void RefreshActionButtons()
    {
        var canOperate = !_busy && _hasConnected;
        RefreshButton.IsEnabled = canOperate;
        TestAllButton.IsEnabled = canOperate && _nodes.Count > 0;
    }

    private void SetStatus(string text, ClashStatusKind kind)
    {
        StatusText.Text = text;
        var resourceKey = kind switch
        {
            ClashStatusKind.Success => "CustomUiBrush",
            ClashStatusKind.Warning => "SystemFillColorCautionBrush",
            ClashStatusKind.Error => "SystemFillColorCriticalBrush",
            _ => "AuroraTextMutedBrush"
        };
        StatusText.Foreground = Application.Current.Resources[resourceKey] as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    private async Task ShowMessageDialogAsync(string title, string content)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = Loc.T("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            foreach (var node in _nodes)
            {
                node.RefreshLocalization();
                if (node.DelayMilliseconds is { } delay)
                {
                    ApplyDelayVisual(node, delay);
                }
            }
        });
    }

    private enum ClashStatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }
}
