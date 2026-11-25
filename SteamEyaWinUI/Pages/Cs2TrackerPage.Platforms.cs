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
    private CancellationTokenSource? _crossPlatformLoadCts;

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

        var data = result.Data;
        var card = data["card"] as JsonObject;
        var stats = data["stats"] as JsonObject;
        var nickname = FirstNonEmpty(
            GetString(card ?? new JsonObject(), "nickname"),
            GetString(data, "nickname"),
            GetString(data, "pwaNickname"),
            "完美玩家");
        var avatar = FirstNonEmpty(
            GetString(card ?? new JsonObject(), "avatar"),
            GetString(data, "avatar"));
        var score = GetMetricNumber(card?["score"]) ??
            GetMetricNumber(data["latestScore"]);
        var rank = FormatPerfectRank(card, data, score);
        var metrics = new JsonArray();
        AddMetric(metrics, "场次", FormatWhole(GetMetricNumber(stats?["matches"])));
        AddMetric(metrics, "胜率", FormatPercent(GetMetricNumber(stats?["winRate"])));
        AddMetric(metrics, "Rating", Number(
            GetMetricNumber(stats?["rating"]) ??
            GetMetricNumber(stats?["ratingPro"]), "F2"));
        AddMetric(metrics, "ADR", Number(GetMetricNumber(stats?["adr"]), "F1"));
        AddMetric(metrics, "K/D", Number(GetMetricNumber(stats?["kd"]), "F2"));
        AddMetric(metrics, "爆头率", FormatPercent(
            GetMetricNumber(stats?["hsRate"]) ??
            GetMetricNumber(stats?["headshotRate"])));

        TrackerPerfectPlatformPanel.Children.Add(BuildPlatformProfileCard(
            "完美世界竞技平台",
            nickname,
            avatar,
            score,
            rank,
            metrics,
            BuildPerfectTags(data, result.Message),
            result.Source));
        var matches = data["matches"] as JsonArray;
        if (matches is { Count: > 0 })
        {
            TrackerPerfectPlatformPanel.Children.Add(
                BuildPlatformRecentMatchesCard("完美近期对局", matches, false));
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            TrackerPerfectPlatformPanel.Children.Add(
                BuildPlatformNotice(result.Message));
        }
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
        var tier = GetString(data, "tier", "label") ??
            GetString(data, "analysis", "tier") ??
            "未定级";
        var rank = GetMetricNumber(data["rank"]);
        var rankText = elo is null
            ? tier
            : $"{tier} · 天梯 {elo.Value:F0}" +
              (rank is > 0 ? $" · #{rank.Value:F0}" : string.Empty);
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
            BuildFiveETags(data, result.Message),
            result.Source));
        if (data["matches"] is JsonArray { Count: > 0 } matches)
        {
            TrackerFiveEPlatformPanel.Children.Add(
                BuildPlatformRecentMatchesCard("5E 近期对局", matches, true));
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
        IReadOnlyList<(string Text, string Tone)> tags,
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
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (score is not null)
        {
            rankPanel.Children.Add(new TextBlock
            {
                Text = score.Value.ToString("N0", CultureInfo.InvariantCulture),
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(TrackerAccentBlue)),
                HorizontalAlignment = HorizontalAlignment.Right
            });
        }

        rankPanel.Children.Add(new TextBlock
        {
            Text = rank ?? "暂无段位",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right
        });
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
        if (tags.Count > 0)
        {
            var tagPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            foreach (var tag in tags)
            {
                tagPanel.Children.Add(BuildPlatformTag(tag.Text, tag.Tone));
            }

            content.Children.Add(tagPanel);
        }

        return OverviewCard(content, new Thickness(16));
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

    private IReadOnlyList<(string Text, string Tone)> BuildFiveETags(
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

        return tags.Count == 0 && !string.IsNullOrWhiteSpace(message)
            ? [("公开数据", "info")]
            : tags;
    }

    private Border BuildPlatformTag(string text, string tone)
    {
        var (background, foreground) = tone switch
        {
            "good" => ("#2057E6B5", TrackerAccentGreen),
            "warn" => ("#20F5C84C", TrackerAccentYellow),
            "danger" => ("#20FF5A65", TrackerAccentRed),
            _ => ("#202AA6FF", TrackerAccentBlue)
        };
        return new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(999),
            Background = new SolidColorBrush(ColorFrom(background)),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = new SolidColorBrush(ColorFrom(foreground))
            }
        };
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
}
