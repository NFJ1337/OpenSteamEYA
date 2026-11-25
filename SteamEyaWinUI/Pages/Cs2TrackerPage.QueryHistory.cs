using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var entries = LoadTrackerQueryHistory();
        ContentDialog? dialog = null;
        string? selectedSteamId = null;
        UIElement content;
        if (entries.Count == 0)
        {
            content = new TextBlock
            {
                Text = "暂无历史查询。",
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                FontSize = 12
            };
        }
        else
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var entry in entries)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
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
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(steamId, 1);
                row.Children.Add(steamId);
                var rowBorder = new Border
                {
                    Height = 34,
                    Padding = new Thickness(10, 7, 10, 7),
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeBrush("AuroraPaneBrush"),
                    BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Child = row
                };
                ToolTipService.SetToolTip(rowBorder, "双击重新查询");
                rowBorder.DoubleTapped += (_, args) =>
                {
                    args.Handled = true;
                    selectedSteamId = entry.SteamId;
                    dialog?.Hide();
                };
                list.Children.Add(rowBorder);
            }
            content = new ScrollViewer
            {
                // 10 × 34px 行高 + 9 × 6px 间距，超出部分使用内部滚动条。
                MaxHeight = 394,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = list
            };
        }

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
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存 CS2Tracker 历史查询失败：{ex.Message}");
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
