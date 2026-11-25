using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage
{
    private const int MaxTrackerQueryHistoryEntries = 30;
    private static string TrackerQueryHistoryPath =>
        Path.Combine(AppPaths.DataRoot, "cs2tracker-query-history.json");
    private static readonly JsonSerializerOptions TrackerHistoryJsonOptions = new()
    {
        WriteIndented = true
    };

    private async void HistoryQueriesButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = LoadTrackerQueryHistory()
            .OrderByDescending(entry => entry.QueriedAt)
            .ToList();
        ContentDialog? dialog = null;
        string? selectedSteamId = null;
        var list = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var emptyText = new TextBlock
        {
            Text = "暂无历史查询。",
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            FontSize = 12,
            Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var scroll = new ScrollViewer
        {
            // 10 × 34px 行高 + 9 × 6px 间距，超出部分使用内部滚动条。
            MaxHeight = 394,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
            Content = list
        };
        ApplyTrackerHistoryScrollBarStyle(scroll);
        foreach (var entry in entries)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextHeadingBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var steamId = new TextBlock
            {
                Text = entry.SteamId,
                FontSize = 11,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };
            Grid.SetColumn(steamId, 1);
            row.Children.Add(steamId);

            var deleteButton = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 11,
                    Foreground = ThemeBrush("AuroraTextMutedBrush")
                }
            };
            ToolTipService.SetToolTip(deleteButton, "删除此历史记录");

            var confirmText = new TextBlock
            {
                Text = $"确定删除 {entry.Name}（{entry.SteamId}）吗？",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = ThemeBrush("AuroraTextHeadingBrush"),
                MaxWidth = 260
            };
            var confirmError = new TextBlock
            {
                Text = "删除失败，请稍后重试。",
                FontSize = 10,
                Foreground = new SolidColorBrush(ColorFrom(TrackerAccentRed)),
                Visibility = Visibility.Collapsed
            };
            var promptButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6
            };
            var cancelButton = new Button { Content = "取消" };
            var confirmButton = new Button
            {
                Content = "删除",
                Foreground = new SolidColorBrush(ColorFrom(TrackerAccentRed))
            };
            promptButtons.Children.Add(cancelButton);
            promptButtons.Children.Add(confirmButton);
            var prompt = new StackPanel { Spacing = 8 };
            prompt.Children.Add(confirmText);
            prompt.Children.Add(confirmError);
            prompt.Children.Add(promptButtons);
            var flyout = new Flyout
            {
                Content = prompt,
                Placement = FlyoutPlacementMode.Bottom
            };
            deleteButton.Flyout = flyout;
            cancelButton.Click += (_, _) => flyout.Hide();

            var rowBorder = new Border
            {
                Height = 34,
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("AuroraPaneBrush"),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = row
            };
            confirmButton.Click += (_, _) =>
            {
                if (!DeleteTrackerQueryHistoryEntry(entry.SteamId))
                {
                    confirmError.Visibility = Visibility.Visible;
                    return;
                }

                flyout.Hide();
                list.Children.Remove(rowBorder);
                if (list.Children.Count == 0)
                {
                    scroll.Visibility = Visibility.Collapsed;
                    emptyText.Visibility = Visibility.Visible;
                }
            };

            Grid.SetColumn(deleteButton, 2);
            row.Children.Add(deleteButton);
            ToolTipService.SetToolTip(rowBorder, "双击重新查询");
            rowBorder.DoubleTapped += (_, args) =>
            {
                args.Handled = true;
                selectedSteamId = entry.SteamId;
                dialog?.Hide();
            };
            list.Children.Add(rowBorder);
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(emptyText);
        content.Children.Add(scroll);

        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "历史查询",
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
        if (selectedSteamId is { Length: 17 })
        {
            TrackerIdBox.Text = selectedSteamId;
            await RunQueryAsync();
        }
    }

    private static void ApplyTrackerHistoryScrollBarStyle(ScrollViewer scroll)
    {
        var resources = scroll.Resources;
        resources["ScrollBarSize"] = 8d;
        resources["ScrollBarVerticalThumbMinWidth"] = 6d;
        resources["ScrollBarThumbOffset"] = 1d;
        resources["ScrollBarCornerRadius"] = new CornerRadius(3);
        resources["ScrollBarThumbFill"] = ThemeBrush("AuroraTextMutedBrush");
        resources["ScrollBarThumbFillPointerOver"] = ThemeBrush("AuroraTextBodyBrush");
        resources["ScrollBarThumbFillPressed"] = ThemeBrush("AuroraTextHeadingBrush");
        resources["ScrollBarPanningThumbBackground"] = ThemeBrush("AuroraTextMutedBrush");
        resources["ScrollBarTrackFill"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackFillPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackFillDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackStroke"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static void SaveTrackerQueryHistory(string steamId, string name)
    {
        try
        {
            var entries = LoadTrackerQueryHistory()
                .Where(entry => !string.Equals(entry.SteamId, steamId, StringComparison.Ordinal))
                .Append(new TrackerQueryHistoryEntry(
                    steamId,
                    string.IsNullOrWhiteSpace(name) ? "未知玩家" : name.Trim(),
                    DateTimeOffset.UtcNow))
                .OrderBy(entry => entry.QueriedAt)
                .TakeLast(MaxTrackerQueryHistoryEntries)
                .ToArray();
            WriteTrackerQueryHistory(entries);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存 CS2Tracker 历史查询失败：{ex.Message}");
        }
    }

    private static bool DeleteTrackerQueryHistoryEntry(string steamId)
    {
        var entries = LoadTrackerQueryHistory()
            .Where(entry => !string.Equals(entry.SteamId, steamId, StringComparison.Ordinal))
            .ToArray();
        return WriteTrackerQueryHistory(entries);
    }

    private static bool WriteTrackerQueryHistory(IReadOnlyList<TrackerQueryHistoryEntry> entries)
    {
        try
        {
            var root = new JsonObject
            {
                ["entries"] = new JsonArray(entries
                    .Select(entry => (JsonNode)new JsonObject
                    {
                        ["name"] = entry.Name,
                        ["steamId"] = entry.SteamId,
                        ["queriedAt"] = entry.QueriedAt.ToString("O", CultureInfo.InvariantCulture)
                    })
                    .ToArray())
            };

            Directory.CreateDirectory(AppPaths.DataRoot);
            var tempPath = TrackerQueryHistoryPath + ".tmp";
            File.WriteAllText(
                tempPath,
                root.ToJsonString(TrackerHistoryJsonOptions),
                new UTF8Encoding(false));
            File.Move(tempPath, TrackerQueryHistoryPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入 CS2Tracker 历史查询失败：{ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<TrackerQueryHistoryEntry> LoadTrackerQueryHistory()
    {
        try
        {
            if (!File.Exists(TrackerQueryHistoryPath))
            {
                return [];
            }
            if (JsonNode.Parse(File.ReadAllText(TrackerQueryHistoryPath)) is not JsonObject root ||
                root["entries"] is not JsonArray array)
            {
                return [];
            }

            return array
                .OfType<JsonObject>()
                .Select(entry =>
                {
                    var steamId = GetString(entry, "steamId");
                    var name = GetString(entry, "name");
                    var queriedAt = GetString(entry, "queriedAt");
                    return steamId is { Length: 17 } &&
                           DateTimeOffset.TryParse(
                               queriedAt,
                               CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeUniversal,
                               out var timestamp)
                        ? new TrackerQueryHistoryEntry(
                            steamId,
                            string.IsNullOrWhiteSpace(name) ? "未知玩家" : name,
                            timestamp)
                        : null;
                })
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .OrderBy(entry => entry.QueriedAt)
                .TakeLast(MaxTrackerQueryHistoryEntries)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 CS2Tracker 历史查询失败：{ex.Message}");
            return [];
        }
    }

    private sealed record TrackerQueryHistoryEntry(
        string SteamId,
        string Name,
        DateTimeOffset QueriedAt);
}
