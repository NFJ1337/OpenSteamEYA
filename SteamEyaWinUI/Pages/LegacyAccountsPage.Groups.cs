using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class LegacyAccountsPage
{
    private const string LegacyUngroupedSentinel = "__legacy_ungrouped__";
    private List<AccountGroup> _legacyGroups = [];
    private string? _legacyGroupFilter;
    private bool _legacyGroupDialogActive;

    private void LoadLegacyGroups()
    {
        var settings = AppState.SettingsService.Load();
        _legacyGroups = GetLegacyGroupDefinitions(settings)
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RebuildLegacyGroupFilterMenu();
    }

    private static List<AccountGroup> GetLegacyGroupDefinitions(AppSettings settings)
    {
        settings.WhiteAccountGroups ??= [];
        return settings.WhiteAccountGroups;
    }

    private void RebuildLegacyGroupFilterMenu()
    {
        if (LegacyGroupFilterButton is null || LegacyGroupFilterFlyout is null)
        {
            return;
        }

        LegacyGroupFilterFlyout.Items.Clear();
        MenuFlyoutItem? selected = null;

        AddItem(Loc.T("History_Group_Filter_All"), null);
        foreach (var group in _legacyGroups)
        {
            AddItem(group.Name, group.Id);
        }
        AddItem(Loc.T("History_Group_Filter_Ungrouped"), LegacyUngroupedSentinel);

        selected ??= LegacyGroupFilterFlyout.Items.FirstOrDefault() as MenuFlyoutItem;
        _legacyGroupFilter = selected?.Tag as string;
        LegacyGroupFilterText.Text = selected?.Text ?? Loc.T("History_Group_Filter_All");
        return;

        void AddItem(string text, string? tag)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Tag = tag
            };
            item.Click += LegacyGroupFilterMenuItem_Click;
            LegacyGroupFilterFlyout.Items.Add(item);
            if (selected is null &&
                string.Equals(tag, _legacyGroupFilter, StringComparison.Ordinal))
            {
                selected = item;
            }
        }
    }

    private void LegacyGroupFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
        {
            return;
        }

        _legacyGroupFilter = item.Tag as string;
        LegacyGroupFilterText.Text = item.Text;
        RebuildRows();
    }

    private bool MatchesLegacyGroupFilter(SteamAccountHistoryItem account)
    {
        if (_legacyGroupFilter is null)
        {
            return true;
        }

        if (_legacyGroupFilter == LegacyUngroupedSentinel)
        {
            return account.GroupIds is not { Count: > 0 };
        }

        return account.GroupIds is { Count: > 0 } &&
            account.GroupIds.Contains(_legacyGroupFilter);
    }

    private void LegacyBatchGroupFlyout_Opening(object sender, object e)
    {
        LegacyBatchGroupFlyout.Items.Clear();
        var accounts = CheckedAccounts();

        foreach (var group in _legacyGroups)
        {
            var allMembers = accounts.Count > 0 &&
                accounts.All(account =>
                    account.GroupIds is { Count: > 0 } &&
                    account.GroupIds.Contains(group.Id));
            var groupId = group.Id;
            var add = !allMembers;
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = group.Name,
                IsChecked = allMembers
            };
            toggle.Click += (_, _) => ApplyLegacyBatchGroup(groupId, add);
            LegacyBatchGroupFlyout.Items.Add(toggle);
        }

        if (_legacyGroups.Count > 0)
        {
            LegacyBatchGroupFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        var newItem = new MenuFlyoutItem
        {
            Text = Loc.T("History_Group_NewAndAdd")
        };
        newItem.Click += async (_, _) => await CreateLegacyGroupAndAddSelectedAsync();
        LegacyBatchGroupFlyout.Items.Add(newItem);

        var manageItem = new MenuFlyoutItem
        {
            Text = Loc.T("History_Group_Manage")
        };
        manageItem.Click += async (_, _) => await ManageLegacyGroupsAsync();
        LegacyBatchGroupFlyout.Items.Add(manageItem);
    }

    private void ApplyLegacyBatchGroup(string groupId, bool add)
    {
        var accounts = CheckedAccounts();
        if (accounts.Count == 0)
        {
            return;
        }

        try
        {
            var changed = AppState.WhiteAccountService.SetGroupMembership(
                accounts,
                groupId,
                add);
            AppState.ReloadWhiteAccounts();
            var groupName = _legacyGroups
                .FirstOrDefault(group => group.Id == groupId)?
                .Name ?? string.Empty;
            AppState.ShowStatus(
                add
                    ? Loc.Tf("History_Status_GroupAdded_Format", changed, groupName)
                    : Loc.Tf("History_Status_GroupRemoved_Format", changed, groupName),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"账号管理批量分组失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CreateLegacyGroupAndAddSelectedAsync()
    {
        var name = await PromptLegacyGroupNameAsync(
            Loc.T("History_Group_NewDialog_Title"),
            string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var group = CreateLegacyGroup(name);
            var accounts = CheckedAccounts();
            if (accounts.Count > 0)
            {
                AppState.WhiteAccountService.SetGroupMembership(
                    accounts,
                    group.Id,
                    true);
                AppState.ReloadWhiteAccounts();
            }

            LoadLegacyGroups();
            RebuildRows();
            AppState.ShowStatus(
                Loc.Tf("History_Status_GroupCreated_Format", group.Name),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"账号管理新建分组失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private AccountGroup CreateLegacyGroup(string name)
    {
        var settings = AppState.SettingsService.Load();
        var groups = GetLegacyGroupDefinitions(settings);
        var group = new AccountGroup
        {
            Name = name.Trim(),
            Order = groups.Count
        };
        groups.Add(group);
        AppState.SettingsService.Save(settings);
        return group;
    }

    private async void LegacyManageGroupsButton_Click(object sender, RoutedEventArgs e) =>
        await ManageLegacyGroupsAsync();

    private async Task<string?> PromptLegacyGroupNameAsync(
        string title,
        string initial)
    {
        if (_legacyGroupDialogActive)
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
            PrimaryButtonStyle =
                (Style)Application.Current.Resources["ThemedDialogPrimaryButtonStyle"],
            CloseButtonStyle =
                (Style)Application.Current.Resources["ThemedDialogCloseButtonStyle"],
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        _legacyGroupDialogActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(box.Text)
                ? null
                : box.Text.Trim();
        }
        finally
        {
            _legacyGroupDialogActive = false;
        }
    }

    private async Task ManageLegacyGroupsAsync()
    {
        if (_legacyGroupDialogActive)
        {
            return;
        }

        LoadLegacyGroups();
        var newNameBox = new TextBox
        {
            PlaceholderText = Loc.T("History_Group_Name_Placeholder"),
            AcceptsReturn = false
        };
        var addButton = new Button
        {
            Content = Loc.T("History_Group_Add")
        };
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(newNameBox);
        addRow.Children.Add(addButton);

        var listPanel = new StackPanel { Spacing = 6 };
        var root = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380
        };
        root.Children.Add(addRow);
        root.Children.Add(listPanel);

        void RebuildRows()
        {
            listPanel.Children.Clear();
            LoadLegacyGroups();
            if (_legacyGroups.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("History_Group_Manage_Empty"),
                    Foreground = (Brush)Resources["LegacyTextMutedBrush"]
                });
                return;
            }

            foreach (var group in _legacyGroups)
            {
                var count = AppState.WhiteAccounts.Count(account =>
                    account.GroupIds is { Count: > 0 } &&
                    account.GroupIds.Contains(group.Id));
                var groupId = group.Id;
                var nameBox = new TextBox
                {
                    Text = group.Name,
                    VerticalAlignment = VerticalAlignment.Center
                };
                nameBox.LostFocus += (_, _) =>
                    RenameLegacyGroup(groupId, nameBox.Text);

                var countText = new TextBlock
                {
                    Text = Loc.Tf("History_Group_Manage_Count_Format", count),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = FormatHelper.GetStatusBrush(
                        InfoBarSeverity.Informational)
                };
                var deleteButton = new Button
                {
                    Content = new FontIcon
                    {
                        Glyph = "\uE74D",
                        FontSize = 14
                    }
                };
                deleteButton.Click += (_, _) =>
                {
                    DeleteLegacyGroup(groupId);
                    RebuildRows();
                };

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = GridLength.Auto });
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

            CreateLegacyGroup(newNameBox.Text);
            newNameBox.Text = string.Empty;
            RebuildRows();
        };

        RebuildRows();
        var dialog = new ContentDialog
        {
            Title = Loc.T("History_Group_Manage"),
            Content = new ScrollViewer
            {
                Content = root,
                MaxHeight = 420
            },
            CloseButtonText = Loc.T("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _legacyGroupDialogActive = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _legacyGroupDialogActive = false;
        }

        LoadLegacyGroups();
        RebuildRows();
    }

    private void RenameLegacyGroup(string id, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var groups = GetLegacyGroupDefinitions(settings);
        var group = groups.FirstOrDefault(item => item.Id == id);
        if (group is null ||
            string.Equals(group.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        group.Name = newName;
        AppState.SettingsService.Save(settings);
        LoadLegacyGroups();
    }

    private void DeleteLegacyGroup(string id)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            var groups = GetLegacyGroupDefinitions(settings);
            groups.RemoveAll(item => item.Id == id);
            AppState.SettingsService.Save(settings);
            AppState.WhiteAccountService.RemoveGroupFromAllAccounts(id);
            AppState.ReloadWhiteAccounts();
            LoadLegacyGroups();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"账号管理删除分组失败：{ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }
}
