using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage
{
    private const double PlatformHeroCardHeight = 142;
    private CancellationTokenSource? _crossPlatformLoadCts;

    private sealed record FiveEMapPerformance(
        string Key,
        string DisplayName,
        int Matches,
        double? WinRate,
        double? Rating,
        double? Adr);

    private void ResetCrossPlatformPanels()
    {
        _crossPlatformLoadCts?.Cancel();
        _crossPlatformLoadCts?.Dispose();
        _crossPlatformLoadCts = null;
        TrackerPerfectPlatformPanel.Children.Clear();
        TrackerFiveEPlatformPanel.Children.Clear();
    }

    private void BeginCrossPlatformLoad(string steamId, int viewGeneration)
    {
        _crossPlatformLoadCts?.Cancel();
        _crossPlatformLoadCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _crossPlatformLoadCts = cancellation;
        TrackerPerfectPlatformPanel.Children.Clear();
        TrackerFiveEPlatformPanel.Children.Clear();
        TrackerPerfectPlatformPanel.Children.Add(
            BuildPlatformLoadingCard("完美", "正在读取完美平台数据…"));
        TrackerFiveEPlatformPanel.Children.Add(
            BuildPlatformLoadingCard("5E", "正在读取 5E 对战平台数据…"));
        _ = LoadCrossPlatformProfilesAsync(
            steamId,
            viewGeneration,
            cancellation.Token);
    }

    private void SetCrossPlatformUnavailable(string message)
    {
        _crossPlatformLoadCts?.Cancel();
        _crossPlatformLoadCts?.Dispose();
        _crossPlatformLoadCts = null;
        TrackerPerfectPlatformPanel.Children.Clear();
        TrackerFiveEPlatformPanel.Children.Clear();
        TrackerPerfectPlatformPanel.Children.Add(
            BuildPlatformUnavailableCard("完美", message));
        TrackerFiveEPlatformPanel.Children.Add(
            BuildPlatformUnavailableCard("5E", message));
    }

    private async Task LoadCrossPlatformProfilesAsync(
        string steamId,
        int viewGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var fiveTask = CrossPlatformProfileService.QueryFiveEAsync(
                steamId,
                cancellationToken);
            var perfectTask = CrossPlatformProfileService.QueryPerfectAsync(
                steamId,
                cancellationToken);
            await Task.WhenAll(fiveTask, perfectTask);
            if (cancellationToken.IsCancellationRequested ||
                viewGeneration != _trackerViewGeneration)
            {
                return;
            }

            RenderFiveEProfile(await fiveTask);
            RenderPerfectProfile(await perfectTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载完美/5E平台数据失败：{ex.Message}");
            if (viewGeneration == _trackerViewGeneration)
            {
                TrackerPerfectPlatformPanel.Children.Clear();
                TrackerFiveEPlatformPanel.Children.Clear();
                TrackerPerfectPlatformPanel.Children.Add(
                    BuildPlatformUnavailableCard("完美", $"读取失败：{ex.Message}"));
                TrackerFiveEPlatformPanel.Children.Add(
                    BuildPlatformUnavailableCard("5E", $"读取失败：{ex.Message}"));
            }
        }
    }

    private void RenderPerfectProfile(CrossPlatformProfileResult result)
    {
        TrackerPerfectPlatformPanel.Children.Clear();
        if (!result.IsReady || result.Data is null)
        {
            TrackerPerfectPlatformPanel.Children.Add(
                BuildPlatformUnavailableCard(
                    "完美",
                    result.Message ?? "未读取到完美平台数据。"));
            return;
        }

        TrackerPerfectPlatformPanel.Children.Add(
            BuildPerfectReferencePage(result.Data, result));
    }

    private void RenderFiveEProfile(CrossPlatformProfileResult result)
    {
        TrackerFiveEPlatformPanel.Children.Clear();
        if (!result.IsReady || result.Data is null)
        {
            TrackerFiveEPlatformPanel.Children.Add(
                BuildPlatformUnavailableCard(
                    "5E",
                    result.Message ?? "未读取到 5E 平台数据。"));
            return;
        }

        var data = result.Data;
        var stats = data["stats"] as JsonObject;
        var elo = GetMetricNumber(data["elo"]);
        var rankVisual = FiveERankVisual(elo);
        var tier = elo is > 0 ? rankVisual.Label :
            GetString(data, "tier", "label") ??
            GetString(data, "analysis", "tier") ??
            "未定级";
        var rank = GetMetricNumber(data["rank"]);
        string? rankText = rank is > 0 and <= 10_000
            ? $"排名 #{rank.Value:F0}"
            : null;
        var metrics = new JsonArray();
        AddMetric(metrics, "场次", FormatWhole(GetMetricNumber(stats?["matches"])));
        AddMetric(metrics, "胜率", FormatPercent(GetMetricNumber(stats?["winRate"])));
        AddMetric(metrics, "Rating", Number(GetMetricNumber(stats?["rating"]), "F2"));
        AddMetric(metrics, "ADR", Number(GetMetricNumber(stats?["adr"]), "F1"));
        AddMetric(metrics, "RWS", Number(GetMetricNumber(stats?["rws"]), "F2"));
        AddMetric(metrics, "K/D", Number(GetMetricNumber(stats?["kd"]), "F2"));

        TrackerFiveEPlatformPanel.Children.Add(BuildPlatformProfileCard(
            "5E 对战平台",
            FirstNonEmpty(GetString(data, "username"), "5E 玩家"),
            GetString(data, "avatarUrl"),
            elo,
            rankText,
            metrics,
            result.Source));
        if (data["maps"] is JsonArray { Count: > 0 } maps)
        {
            var mapCard = BuildFiveEMapPerformanceCard(maps);
            if (mapCard is not null)
            {
                TrackerFiveEPlatformPanel.Children.Add(mapCard);
            }
        }

        if (data["matches"] is JsonArray { Count: > 0 } matches)
        {
            TrackerFiveEPlatformPanel.Children.Add(
                BuildFiveERecentMatchesTableCard("5E 近期对局", matches));
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            TrackerFiveEPlatformPanel.Children.Add(
                BuildPlatformNotice(result.Message));
        }
    }

    private UIElement BuildPlatformProfileCard(
        string platform,
        string nickname,
        string? avatar,
        double? score,
        string? rank,
        JsonArray metrics,
        string source)
    {
        var content = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(BuildPlatformAvatar(avatar, nickname));
        var identity = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(new TextBlock
        {
            Text = nickname,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        identity.Children.Add(new TextBlock
        {
            Text = $"{platform} · {source}",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        var rankPanel = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rankVisual = FiveERankVisual(score);
        rankPanel.Children.Add(BuildCompetitiveRankScoreBadge(
            rankVisual,
            score,
            "5E 天梯"));

        if (!string.IsNullOrWhiteSpace(rank))
        {
            rankPanel.Children.Add(new TextBlock
            {
                Text = rank,
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right
            });
        }
        Grid.SetColumn(rankPanel, 2);
        header.Children.Add(rankPanel);
        content.Children.Add(header);

        var metricGrid = CreateEqualGrid(6, 8);
        foreach (var node in metrics.OfType<JsonObject>())
        {
            metricGrid.Children.Add(BuildPlatformMetric(
                GetString(node, "label") ?? string.Empty,
                GetString(node, "value") ?? "—"));
        }

        ArrangeColumnsInOrder(metricGrid);
        content.Children.Add(metricGrid);
        return OverviewCard(content, new Thickness(16));
    }

    private Border? BuildFiveEMapPerformanceCard(JsonArray maps)
    {
        var entries = maps
            .OfType<JsonObject>()
            .Select(map =>
            {
                var rawCode = GetString(map, "map")?.Trim();
                var rawName = GetString(map, "mapName")?.Trim();
                var key = FirstNonEmpty(rawCode, rawName);
                if (string.IsNullOrWhiteSpace(key))
                {
                    return null;
                }

                var displayName = !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : MapDisplayName(rawCode) ?? "未知地图";
                var matchCount = (int)Math.Max(
                    0,
                    Math.Round(GetMetricNumber(map["matches"]) ?? 0));
                return new FiveEMapPerformance(
                    key,
                    displayName,
                    matchCount,
                    GetMetricNumber(map["winRate"]),
                    GetMetricNumber(map["rating"]),
                    GetMetricNumber(map["adr"]));
            })
            .OfType<FiveEMapPerformance>()
            .Where(map => map.Matches > 0)
            .OrderByDescending(map => map.Matches)
            .ThenByDescending(map => map.WinRate)
            .ThenByDescending(map => map.Rating)
            .ToArray();
        if (entries.Length == 0)
        {
            return null;
        }

        var content = new StackPanel { Spacing = 10 };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "地图表现",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        var meta = new TextBlock
        {
            Text = $"共 {entries.Length} 张",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(meta, 1);
        heading.Children.Add(meta);
        content.Children.Add(heading);

        var top = entries.Take(3).ToArray();
        var topGrid = CreateEqualGrid(top.Length, 10);
        var maxMatches = entries.Max(map => map.Matches);
        var maxWinRate = entries.Max(map => map.WinRate);
        foreach (var map in top)
        {
            topGrid.Children.Add(BuildFiveEMapSummaryCard(
                map,
                map.Matches == maxMatches,
                map.WinRate is not null &&
                Math.Abs(map.WinRate.Value - maxWinRate.GetValueOrDefault()) < 0.0001));
        }

        ArrangeColumnsInOrder(topGrid);
        content.Children.Add(topGrid);

        var remaining = entries.Skip(3).ToArray();
        if (remaining.Length > 0)
        {
            content.Children.Add(BuildFiveEMapRemainingTable(remaining));
        }

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildFiveEMapSummaryCard(
        FiveEMapPerformance map,
        bool isMostPlayed,
        bool isBestMap)
    {
        var tags = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };
        if (isMostPlayed)
        {
            tags.Children.Add(BuildFiveEMapTag("主玩", TrackerAccentBlue));
        }

        if (isBestMap)
        {
            tags.Children.Add(BuildFiveEMapTag("表现较好", TrackerAccentGreen));
        }

        var header = new Grid { ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(BuildFiveEMapBadge(map.DisplayName));
        var identity = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(new TextBlock
        {
            Text = map.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (tags.Children.Count > 0)
        {
            identity.Children.Add(tags);
        }

        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);

        var metrics = CreateEqualGrid(4, 6);
        metrics.Children.Add(BuildFiveEMapMetric("场次", FormatWhole(map.Matches)));
        metrics.Children.Add(BuildFiveEMapMetric("胜率", FormatPercent(map.WinRate)));
        metrics.Children.Add(BuildFiveEMapMetric("Rating", Number(map.Rating, "F2")));
        metrics.Children.Add(BuildFiveEMapMetric("ADR", Number(map.Adr, "F1")));
        ArrangeColumnsInOrder(metrics);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 12,
                Children = { header, metrics }
            }
        };
    }

    private Border BuildFiveEMapBadge(string mapName)
    {
        var label = string.IsNullOrWhiteSpace(mapName)
            ? "?"
            : mapName.Trim()[0].ToString().ToUpperInvariant();
        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(ColorFrom("#202AA6FF")),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(TrackerAccentBlue)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private Border BuildFiveEMapTag(string text, string color)
    {
        return new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(ColorFrom(
                string.Equals(color, TrackerAccentGreen, StringComparison.OrdinalIgnoreCase)
                    ? "#2057E6B5"
                    : "#202AA6FF")),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(color))
            }
        };
    }

    private Border BuildFiveEMapMetric(string label, string value)
    {
        return new Border
        {
            Padding = new Thickness(7, 6, 7, 6),
            CornerRadius = new CornerRadius(7),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 8,
                        Foreground = ThemeBrush("AuroraTextMutedBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = value,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeBrush("AuroraTextHeadingBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }

    private Border BuildFiveEMapRemainingTable(
        IReadOnlyList<FiveEMapPerformance> maps)
    {
        var rows = new StackPanel { Spacing = 0 };
        var header = CreateFiveEMapGrid();
        AddFiveEMapTableCell(header, "地图", 0, HorizontalAlignment.Left, true);
        AddFiveEMapTableCell(header, "场次", 1, HorizontalAlignment.Center, true);
        AddFiveEMapTableCell(header, "胜率", 2, HorizontalAlignment.Center, true);
        AddFiveEMapTableCell(header, "Rating", 3, HorizontalAlignment.Center, true);
        AddFiveEMapTableCell(header, "ADR", 4, HorizontalAlignment.Center, true);
        rows.Children.Add(new Border
        {
            Height = 30,
            Padding = new Thickness(9, 0, 9, 0),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        });

        foreach (var map in maps)
        {
            var row = CreateFiveEMapGrid();
            AddFiveEMapTableCell(row, map.DisplayName, 0, HorizontalAlignment.Left, false);
            AddFiveEMapTableCell(row, FormatWhole(map.Matches), 1, HorizontalAlignment.Center, false);
            AddFiveEMapTableCell(row, FormatPercent(map.WinRate), 2, HorizontalAlignment.Center, false);
            AddFiveEMapTableCell(row, Number(map.Rating, "F2"), 3, HorizontalAlignment.Center, false);
            AddFiveEMapTableCell(row, Number(map.Adr, "F1"), 4, HorizontalAlignment.Center, false);
            rows.Children.Add(new Border
            {
                Height = 32,
                Padding = new Thickness(9, 0, 9, 0),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = row
            });
        }

        return new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = rows
        };
    }

    private static Grid CreateFiveEMapGrid()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var width in new[] { 64d, 72d, 76d, 68d })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(width)
            });
        }

        return grid;
    }

    private void AddFiveEMapTableCell(
        Grid grid,
        string text,
        int column,
        HorizontalAlignment alignment,
        bool header)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = header ? 9 : 10,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ThemeBrush(
                header ? "AuroraTextMutedBrush" : "AuroraTextBodyBrush"),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private Border BuildPlatformMetric(string label, string value)
    {
        var content = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return new Border
        {
            Padding = new Thickness(8, 7, 8, 7),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("AuroraGlassBrush"),
            Child = content
        };
    }

    private Border BuildPlatformAvatar(string? avatarUrl, string nickname)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = nickname[..Math.Min(1, nickname.Length)].ToUpperInvariant(),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(avatarUrl) &&
            Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
        {
            content.Children.Add(new Image
            {
                Source = new BitmapImage(uri),
                Stretch = Stretch.UniformToFill
            });
        }

        return new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = ThemeBrush("AuroraPillBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(2),
            Child = content
        };
    }

    private UIElement BuildPlatformRecentMatchesCard(
        string title,
        JsonArray matches,
        bool fiveE)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var match in matches.OfType<JsonObject>().Take(8))
        {
            var score = GetString(match, "score") ?? "—";
            var isWin = GetBool(match, "isWin");
            var isTie = GetBool(match, "isTie") == true;
            var result = isTie ? "T" : isWin == true ? "W" : isWin == false ? "L" : "·";
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = result,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColorFrom(
                    result == "W" ? TrackerAccentGreen :
                    result == "L" ? TrackerAccentRed :
                    TrackerMuted))
            });
            var map = FirstNonEmpty(
                GetString(match, "mapName"),
                GetString(match, "map"),
                "未知地图");
            var mapText = new TextBlock
            {
                Text = map,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextHeadingBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(mapText, 1);
            row.Children.Add(mapText);
            var fiveStats = fiveE
                ? $"{(FormatWhole(GetMetricNumber(match["kills"])))}/" +
                  $"{FormatWhole(GetMetricNumber(match["deaths"]))}/" +
                  $"{FormatWhole(GetMetricNumber(match["assists"]))} · " +
                  $"{Number(GetMetricNumber(match["rating"]), "F2")}"
                : FormatPerfectMatchStats(match);
            var statsText = new TextBlock
            {
                Text = fiveStats,
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            };
            Grid.SetColumn(statsText, 2);
            row.Children.Add(statsText);
            var scoreText = new TextBlock
            {
                Text = score,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextHeadingBrush")
            };
            Grid.SetColumn(scoreText, 3);
            row.Children.Add(scoreText);
            list.Children.Add(new Border
            {
                Padding = new Thickness(9, 6, 9, 6),
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("AuroraGlassBrush"),
                Child = row
            });
        }

        return OverviewCard(
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeBrush("AuroraTextHeadingBrush")
                    },
                    list
                }
            },
            new Thickness(14));
    }

    private Border BuildFiveERecentMatchesTableCard(string title, JsonArray matches)
    {
        const double rowHeight = 48;
        var matchRows = matches.OfType<JsonObject>().Take(10).ToArray();
        var content = new StackPanel { Spacing = 10 };

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        var countText = new TextBlock
        {
            Text = $"{matchRows.Length} 场",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 1);
        heading.Children.Add(countText);
        content.Children.Add(heading);

        var tableContent = new StackPanel { Spacing = 0 };
        var headerGrid = CreateFiveERecentMatchesGrid();
        AddFiveERecentMatchHeader(headerGrid, "结果", 0, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "时间", 1, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "地图", 2, HorizontalAlignment.Left);
        AddFiveERecentMatchHeader(headerGrid, "比分", 3, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "变化", 4, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "K / D / A", 5, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "Rating", 6, HorizontalAlignment.Center);
        AddFiveERecentMatchHeader(headerGrid, "ADR", 7, HorizontalAlignment.Center);
        tableContent.Children.Add(new Border
        {
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid
        });

        var rowsPanel = new StackPanel { Spacing = 0 };
        for (var index = 0; index < matchRows.Length; index++)
        {
            rowsPanel.Children.Add(BuildFiveERecentMatchRow(matchRows[index], rowHeight));
        }

        tableContent.Children.Add(new ScrollViewer
        {
            MaxHeight = rowHeight * 6,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = rowsPanel
        });
        content.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = tableContent
        });

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildFiveERecentMatchRow(
        JsonObject match,
        double rowHeight)
    {
        var isWin = GetBool(match, "isWin");
        var isTie = GetBool(match, "isTie") == true;
        var result = isTie ? "平" : isWin == true ? "胜" : isWin == false ? "负" : "—";
        var (resultBackground, resultForeground) =
            isTie || result == "—"
                ? ("#18F5C84C", TrackerAccentYellow)
                : isWin == true
                    ? ("#2057E6B5", TrackerAccentGreen)
                    : ("#20FF5A65", TrackerAccentRed);
        var resultPill = new Border
        {
            Width = 34,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(ColorFrom(resultBackground)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = result,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(resultForeground)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var grid = CreateFiveERecentMatchesGrid();
        AddFiveERecentMatchCell(grid, resultPill, 0, HorizontalAlignment.Center);
        AddFiveERecentMatchCell(
            grid,
            BuildFiveERecentMatchText(
                FormatFiveEMatchTime(GetString(match, "startTime")),
                ThemeBrush("AuroraTextBodyBrush")),
            1,
            HorizontalAlignment.Center);

        var map = MapDisplayName(FirstNonEmpty(
            GetString(match, "mapName"),
            GetString(match, "map"))) ?? "未知地图";
        var mapText = BuildFiveERecentMatchText(
            map,
            ThemeBrush("AuroraTextHeadingBrush"),
            semiBold: true);
        mapText.TextTrimming = TextTrimming.CharacterEllipsis;
        mapText.MaxLines = 1;
        AddFiveERecentMatchCell(grid, mapText, 2, HorizontalAlignment.Left);

        AddFiveERecentMatchCell(
            grid,
            BuildFiveERecentMatchText(
                FormatFiveEScore(GetString(match, "score")),
                ThemeBrush("AuroraTextHeadingBrush"),
                semiBold: true),
            3,
            HorizontalAlignment.Center);

        var eloChange = GetMetricNumber(match["eloChange"]);
        var changeText = BuildFiveERecentMatchText(
            FormatFiveERecentChange(eloChange),
            new SolidColorBrush(ColorFrom(
                eloChange is > 0 ? TrackerAccentGreen :
                eloChange is < 0 ? TrackerAccentRed :
                TrackerMuted)),
            semiBold: true);
        AddFiveERecentMatchCell(grid, changeText, 4, HorizontalAlignment.Center);

        AddFiveERecentMatchCell(
            grid,
            BuildFiveERecentMatchText(
                $"{FormatWhole(GetMetricNumber(match["kills"]))} / " +
                $"{FormatWhole(GetMetricNumber(match["deaths"]))} / " +
                $"{FormatWhole(GetMetricNumber(match["assists"]))}",
                ThemeBrush("AuroraTextBodyBrush")),
            5,
            HorizontalAlignment.Center);
        AddFiveERecentMatchCell(
            grid,
            BuildFiveERecentMatchText(
                Number(GetMetricNumber(match["rating"]), "F2"),
                ThemeBrush("AuroraTextBodyBrush")),
            6,
            HorizontalAlignment.Center);
        AddFiveERecentMatchCell(
            grid,
            BuildFiveERecentMatchText(
                Number(GetMetricNumber(match["adr"]), "F1"),
                ThemeBrush("AuroraTextBodyBrush")),
            7,
            HorizontalAlignment.Center);

        return new Border
        {
            Height = rowHeight,
            Padding = new Thickness(12, 0, 12, 0),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private static Grid CreateFiveERecentMatchesGrid()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        foreach (var width in new[]
        {
            54d, 96d, double.NaN, 82d, 74d, 104d, 68d, 64d
        })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = double.IsNaN(width)
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(width)
            });
        }

        return grid;
    }

    private void AddFiveERecentMatchHeader(
        Grid grid,
        string text,
        int column,
        HorizontalAlignment alignment)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static void AddFiveERecentMatchCell(
        Grid grid,
        FrameworkElement element,
        int column,
        HorizontalAlignment alignment)
    {
        element.HorizontalAlignment = alignment;
        element.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private TextBlock BuildFiveERecentMatchText(
        string text,
        Brush foreground,
        bool semiBold = false) =>
        new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static string FormatFiveEMatchTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }

        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numeric) &&
            double.IsFinite(numeric) &&
            numeric > 0)
        {
            try
            {
                var timestamp = numeric >= 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)numeric)
                    : DateTimeOffset.FromUnixTimeSeconds((long)numeric);
                return timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : raw;
    }

    private static string FormatFiveEScore(string? score)
    {
        if (string.IsNullOrWhiteSpace(score))
        {
            return "—";
        }

        return score.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", " : ", StringComparison.Ordinal);
    }

    private static string FormatFiveERecentChange(double? value)
    {
        if (value is null)
        {
            return "—";
        }

        var rounded = Math.Abs(value.Value) < 0.005 ? 0 : value.Value;
        var text = Math.Abs(rounded - Math.Truncate(rounded)) < 0.005
            ? rounded.ToString("F0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.#", CultureInfo.InvariantCulture);
        return rounded > 0 ? $"+{text}" : text;
    }

    private string FormatPerfectMatchStats(JsonObject match)
    {
        var combat = match["combat"] as JsonObject;
        var kills = GetMetricNumber(combat?["kills"]);
        var deaths = GetMetricNumber(combat?["deaths"]);
        var adr = GetMetricNumber(combat?["adr"]);
        var hs = GetMetricNumber(combat?["hsRate"]);
        return $"{FormatWhole(kills)}/{FormatWhole(deaths)} · " +
            $"{Number(adr, "F0")} ADR · {FormatPercent(hs)} HS";
    }

    private IReadOnlyList<(string Text, string Tone)> BuildPerfectTags(
        JsonObject data,
        string? message)
    {
        var tags = new List<(string, string)>();
        if (GetNode(data, "analysis", "tags") is JsonArray analysisTags)
        {
            foreach (var tag in analysisTags.OfType<JsonObject>())
            {
                var label = GetString(tag, "label");
                if (!string.IsNullOrWhiteSpace(label))
                {
                    tags.Add((label, GetString(tag, "level") ?? "info"));
                }
            }
        }

        if (GetString(data, "sourceMode") == "public")
        {
            tags.Add(("公开资料", "info"));
        }

        return tags.Count == 0 && !string.IsNullOrWhiteSpace(message)
            ? [("登录态不可用", "warn")]
            : tags;
    }

    private Border BuildPlatformNotice(string message) =>
        new()
        {
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(9),
            Background = ThemeBrush("AuroraGlassBrush"),
            Child = new TextBlock
            {
                Text = message,
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        };

    private Border BuildPlatformLoadingCard(string platform, string message) =>
        BuildPlatformStateCard(platform, message, showProgress: true);

    private Border BuildPlatformUnavailableCard(string platform, string message) =>
        BuildPlatformStateCard(platform, message, showProgress: false);

    private Border BuildPlatformStateCard(
        string platform,
        string message,
        bool showProgress)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (showProgress)
        {
            content.Children.Add(new ProgressRing
            {
                Width = 26,
                Height = 26,
                IsActive = true,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = platform,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        content.MinHeight = 150;
        return OverviewCard(content, new Thickness(18));
    }

    private static string? FormatPerfectRank(
        JsonObject? card,
        JsonObject data,
        double? score)
    {
        var stars = GetMetricNumber(card?["sStars"]) ??
            GetMetricNumber(data["latestSStars"]);
        var level = GetMetricNumber(card?["sLevel"]) ??
            GetMetricNumber(data["latestSLevel"]);
        if (level is >= 1)
        {
            return stars is > 0 ? $"S {stars.Value:F0} 星" : "S 0 星";
        }

        return score switch
        {
            >= 2400 => "S（参考）",
            >= 2200 => "A+（参考）",
            >= 2000 => "A（参考）",
            >= 1800 => "B+（参考）",
            >= 1600 => "B（参考）",
            >= 1400 => "C+（参考）",
            >= 1200 => "C（参考）",
            >= 1000 => "D+（参考）",
            > 0 => "D（参考）",
            _ => "暂无段位"
        };
    }

    private static void AddMetric(JsonArray metrics, string label, string value)
    {
        metrics.Add((JsonNode)new JsonObject
        {
            ["label"] = label,
            ["value"] = value
        });
    }

    private static string FormatPercent(double? value) =>
        value is null
            ? "—"
            : $"{Math.Clamp(value.Value <= 1 ? value.Value * 100 : value.Value, 0, 100):F0}%";

    private static string? FiveEEloLabel(double? elo) =>
        elo is > 0 ? FiveERankVisual(elo).Label : null;
}
