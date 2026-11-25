using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Services;
using Windows.Foundation;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage
{
    private static readonly TimeSpan MatchHistoryCacheLifetime = TimeSpan.FromMinutes(2);

    private bool _recentMatchDialogOpen;
    private string? _matchHistoryCacheSteamId;
    private JsonArray? _matchHistoryCache;
    private DateTimeOffset _matchHistoryCachedAt;

    private async Task ShowRecentMatchDetailsAsync(JsonObject fallbackMatch, string steamId)
    {
        if (_recentMatchDialogOpen || XamlRoot is null)
        {
            return;
        }

        _recentMatchDialogOpen = true;
        _suppressTrackerBringIntoViewUntil = DateTimeOffset.UtcNow.AddMilliseconds(700);
        TrackerScrollViewer.Focus(FocusState.Programmatic);
        var dialogWidth = Math.Clamp(XamlRoot.Size.Width - 140, 650, 920);
        var dialogHeight = Math.Clamp(XamlRoot.Size.Height - 130, 460, 740);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "对局详情",
            Content = BuildMatchDetailsLoading(),
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Background = ThemeBrush("CustomUiDialogBrush")
        };
        dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxHeight"] = dialogHeight;

        try
        {
            var showOperation = dialog.ShowAsync();
            JsonObject? richMatch = null;
            string? warning = null;
            try
            {
                var history = await LoadMatchHistoryAsync(steamId);
                richMatch = history is null
                    ? null
                    : FindMatchingHistoryEntry(history, fallbackMatch);
                if (richMatch is null)
                {
                    warning = "未读取到该局的详细计分板，已显示现有的基础比赛信息。";
                }
            }
            catch (Exception ex)
            {
                warning = $"详细计分板读取失败：{ex.Message}";
                AppLog.Warn($"读取 CS2Tracker 对局详情失败：{ex.Message}");
            }

            var detailMatch = richMatch ?? fallbackMatch;
            var mapName = GetString(detailMatch, "map") ?? GetString(fallbackMatch, "map");
            dialog.Title = string.IsNullOrWhiteSpace(mapName)
                ? "对局详情"
                : $"对局详情 · {MapDisplayName(mapName)}";
            if (showOperation.Status != AsyncStatus.Completed)
            {
                dialog.Content = BuildMatchDetailsContent(
                    detailMatch,
                    fallbackMatch,
                    steamId,
                    richMatch is not null,
                    warning,
                    dialogHeight);
            }

            await showOperation;
        }
        finally
        {
            _recentMatchDialogOpen = false;
        }
    }

    private async Task<JsonArray?> LoadMatchHistoryAsync(string steamId)
    {
        if (string.Equals(_matchHistoryCacheSteamId, steamId, StringComparison.Ordinal) &&
            _matchHistoryCache is not null &&
            DateTimeOffset.UtcNow - _matchHistoryCachedAt < MatchHistoryCacheLifetime)
        {
            return _matchHistoryCache;
        }

        await EnsureRequestBrowserAsync();
        var response = await FetchWithChallengeRetryAsync(
            $"/api/match-history?steamid={Uri.EscapeDataString(steamId)}",
            "application/json");
        if (response.Error is { Length: > 0 } error)
        {
            throw new InvalidOperationException(error);
        }

        if (response.Status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"CS2Tracker 返回 HTTP {response.Status}。");
        }

        var payload = JsonNode.Parse(response.Text) as JsonObject;
        if (payload?["matches"] is not JsonArray matches)
        {
            return null;
        }

        _matchHistoryCacheSteamId = steamId;
        _matchHistoryCache = matches;
        _matchHistoryCachedAt = DateTimeOffset.UtcNow;
        return matches;
    }

    private static JsonObject? FindMatchingHistoryEntry(JsonArray matches, JsonObject fallbackMatch)
    {
        var targetDate = ParseMatchDate(GetString(fallbackMatch, "date"));
        var targetMap = NormalizeMatchMap(GetString(fallbackMatch, "map"));
        var targetFor = GetMetricNumber(fallbackMatch["score_for"]);
        var targetAgainst = GetMetricNumber(fallbackMatch["score_against"]);
        var targetResult = MatchResult(targetFor, targetAgainst);
        JsonObject? bestMatch = null;
        var bestScore = -1;
        var bestDistance = double.MaxValue;

        foreach (var candidate in matches.OfType<JsonObject>())
        {
            var candidateDate = ParseMatchDate(GetString(candidate, "playedAt"));
            var candidateMap = NormalizeMatchMap(GetString(candidate, "map"));
            var candidateFor = GetMetricNumber(candidate["ownScore"]);
            var candidateAgainst = GetMetricNumber(candidate["enemyScore"]);
            var candidateResult = MatchResult(
                candidateFor,
                candidateAgainst,
                GetString(candidate, "result"));
            var score = 0;
            var distance = double.MaxValue;

            if (targetDate is not null && candidateDate is not null)
            {
                distance = Math.Abs((candidateDate.Value - targetDate.Value).TotalMinutes);
                if (candidateDate.Value.LocalDateTime.Date == targetDate.Value.LocalDateTime.Date)
                {
                    score += 8;
                }
                else if (Math.Abs(
                    (candidateDate.Value.LocalDateTime.Date -
                     targetDate.Value.LocalDateTime.Date).TotalDays) <= 1)
                {
                    score += 2;
                }
            }

            if (!string.IsNullOrWhiteSpace(targetMap) &&
                string.Equals(targetMap, candidateMap, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }

            if (targetFor is not null &&
                targetAgainst is not null &&
                candidateFor is not null &&
                candidateAgainst is not null &&
                Math.Abs(targetFor.Value - candidateFor.Value) < 0.01 &&
                Math.Abs(targetAgainst.Value - candidateAgainst.Value) < 0.01)
            {
                score += 4;
            }

            if (targetResult is not null &&
                string.Equals(targetResult, candidateResult, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score > bestScore || score == bestScore && distance < bestDistance)
            {
                bestScore = score;
                bestDistance = distance;
                bestMatch = candidate;
            }
        }

        return bestScore >= 7 ? bestMatch : null;
    }

    private UIElement BuildMatchDetailsLoading()
    {
        var panel = new StackPanel
        {
            MinHeight = 150,
            Padding = new Thickness(24),
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new ProgressRing
        {
            Width = 32,
            Height = 32,
            IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "正在读取对局详情…",
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return panel;
    }

    private UIElement BuildMatchDetailsContent(
        JsonObject match,
        JsonObject fallbackMatch,
        string steamId,
        bool hasRichDetails,
        string? warning,
        double dialogHeight)
    {
        var content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(BuildMatchHero(match, fallbackMatch));

        var ownPlayer = FindMatchPlayer(match, steamId);
        var kills = GetMetricNumber(match["kills"]) ?? GetMetricNumber(ownPlayer?["kills"]);
        var deaths = GetMetricNumber(match["deaths"]) ?? GetMetricNumber(ownPlayer?["deaths"]);
        var assists = GetMetricNumber(match["assists"]) ?? GetMetricNumber(ownPlayer?["assists"]);
        var rating = GetMetricNumber(ownPlayer?["rating"]) ?? GetMetricNumber(match["rating"]);
        var adr = GetMetricNumber(match["adr"]) ?? GetMetricNumber(ownPlayer?["adr"]);
        var headshot = GetMetricNumber(match["hsPct"]) ?? GetMetricNumber(ownPlayer?["hsPct"]);
        var mvps = GetMetricNumber(match["mvps"]) ?? GetMetricNumber(ownPlayer?["mvps"]);
        var kd = kills is not null && deaths is > 0 ? kills / deaths : null;

        var metrics = CreateEqualGrid(6, 8);
        metrics.Children.Add(BuildMatchDetailMetric(
            "K / D / A",
            $"{FormatWhole(kills)} / {FormatWhole(deaths)} / {FormatWhole(assists)}"));
        metrics.Children.Add(BuildMatchDetailMetric("K/D", Number(kd, "F2")));
        metrics.Children.Add(BuildMatchDetailMetric("ADR", Number(adr, "F1")));
        metrics.Children.Add(BuildMatchDetailMetric(
            "爆头率",
            headshot is null ? "—" : $"{headshot.Value:F0}%"));
        metrics.Children.Add(BuildMatchDetailMetric("Rating", Number(rating, "F2")));
        metrics.Children.Add(BuildMatchDetailMetric("MVP", FormatWhole(mvps), TrackerAccentYellow));
        ArrangeColumnsInOrder(metrics);
        content.Children.Add(metrics);

        var aim = GetNode(match, "aim") as JsonObject;
        var preaim = GetMetricNumber(aim?["preaimDeg"]);
        var reaction = GetMetricNumber(aim?["reactionTimeMs"]);
        var accuracy = GetMetricNumber(aim?["accuracyPct"]);
        if (preaim is not null || reaction is not null || accuracy is not null)
        {
            var aimGrid = CreateEqualGrid(3, 8);
            aimGrid.Children.Add(BuildMatchDetailMetric(
                "预瞄",
                preaim is null ? "—" : $"{preaim.Value:F1}°",
                TrackerAccentPink));
            aimGrid.Children.Add(BuildMatchDetailMetric(
                "反应时间",
                reaction is null ? "—" : $"{reaction.Value:F0} ms",
                TrackerAccentYellow));
            aimGrid.Children.Add(BuildMatchDetailMetric(
                "命中率",
                accuracy is null ? "—" : $"{accuracy.Value:F0}%",
                TrackerAccentBlue));
            ArrangeColumnsInOrder(aimGrid);
            content.Children.Add(BuildMatchDetailSection("瞄准表现", aimGrid));
        }

        var extra = BuildMatchExtraInfo(match, fallbackMatch);
        if (extra is not null)
        {
            content.Children.Add(extra);
        }

        var ownTeam = GetNode(match, "ownTeam") as JsonArray;
        var enemyTeam = GetNode(match, "enemyTeam") as JsonArray;
        if (ownTeam is { Count: > 0 } || enemyTeam is { Count: > 0 })
        {
            var teams = new Grid { ColumnSpacing = 10 };
            teams.ColumnDefinitions.Add(new ColumnDefinition());
            teams.ColumnDefinitions.Add(new ColumnDefinition());
            var own = BuildMatchTeam("我方队伍", ownTeam, steamId, TrackerAccentGreen);
            var enemy = BuildMatchTeam("敌方队伍", enemyTeam, steamId, TrackerAccentRed);
            if (own is not null)
            {
                teams.Children.Add(own);
            }

            if (enemy is not null)
            {
                Grid.SetColumn(enemy, 1);
                teams.Children.Add(enemy);
            }

            content.Children.Add(teams);
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            content.Children.Add(new TextBlock
            {
                Text = warning,
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(BuildMatchActions(match, steamId, hasRichDetails));
        return new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(300, dialogHeight - 130),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled
        };
    }

    private UIElement BuildMatchHero(JsonObject match, JsonObject fallbackMatch)
    {
        var mapName = GetString(match, "map") ?? GetString(fallbackMatch, "map");
        var scoreFor = GetMetricNumber(match["ownScore"]) ?? GetMetricNumber(fallbackMatch["score_for"]) ?? 0;
        var scoreAgainst = GetMetricNumber(match["enemyScore"]) ?? GetMetricNumber(fallbackMatch["score_against"]) ?? 0;
        var result = MatchResult(scoreFor, scoreAgainst, GetString(match, "result")) ?? "tie";
        var resultColor = result switch
        {
            "win" => TrackerAccentGreen,
            "loss" => TrackerAccentRed,
            _ => TrackerMuted
        };
        var hero = new Grid { Height = 150 };
        hero.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(GetMapThumbnailUri(mapName))),
            Stretch = Stretch.UniformToFill,
            Opacity = 0.55
        });
        hero.Children.Add(new Border
        {
            Background = new SolidColorBrush(ColorFrom("#B8070B13"))
        });

        var content = new Grid
        {
            Padding = new Thickness(18, 14, 18, 14),
            ColumnSpacing = 18
        };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        left.Children.Add(new Border
        {
            Padding = new Thickness(9, 3, 9, 3),
            CornerRadius = new CornerRadius(999),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(ColorFrom($"{resultColor}26")),
            Child = new TextBlock
            {
                Text = result switch
                {
                    "win" => "胜利",
                    "loss" => "失败",
                    _ => "平局"
                },
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(resultColor))
            }
        });
        left.Children.Add(new TextBlock
        {
            Text = MapDisplayName(mapName) ?? "未知地图",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom("#FFFFFF"))
        });
        var dateText = FormatTrackerDate(
            GetString(match, "playedAt") ?? GetString(fallbackMatch, "date"));
        if (!string.IsNullOrWhiteSpace(dateText))
        {
            left.Children.Add(new TextBlock
            {
                Text = dateText,
                FontSize = 10,
                Foreground = new SolidColorBrush(ColorFrom("#B8FFFFFF"))
            });
        }

        content.Children.Add(left);
        var scorePanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 2
        };
        scorePanel.Children.Add(new TextBlock
        {
            Text = "比分",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(ColorFrom("#A8FFFFFF"))
        });
        scorePanel.Children.Add(new TextBlock
        {
            Text = $"{scoreFor:F0} : {scoreAgainst:F0}",
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(ColorFrom(resultColor))
        });
        Grid.SetColumn(scorePanel, 1);
        content.Children.Add(scorePanel);
        hero.Children.Add(content);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = hero
        };
    }

    private Border BuildMatchDetailMetric(string label, string value, string? colorHex = null)
    {
        var content = new StackPanel
        {
            Spacing = 3,
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
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = colorHex is null
                ? ThemeBrush("AuroraTextHeadingBrush")
                : new SolidColorBrush(ColorFrom(colorHex)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("AuroraPaneBrush"),
            Child = content
        };
    }

    private Border BuildMatchDetailSection(string title, UIElement content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        panel.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("AuroraPaneBrush"),
            Child = panel
        };
    }

    private Border? BuildMatchExtraInfo(JsonObject match, JsonObject fallbackMatch)
    {
        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        if (GetNode(match, "badges") is JsonArray badges)
        {
            foreach (var badge in badges)
            {
                var text = badge?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    chips.Children.Add(BuildMatchInfoChip(text, TrackerAccentYellow));
                }
            }
        }

        var mvps = GetMetricNumber(match["mvps"]);
        if (mvps is > 0)
        {
            chips.Children.Add(BuildMatchInfoChip($"MVP {mvps.Value:F0}", TrackerAccentYellow));
        }

        var placement = GetMetricNumber(match["placement"]);
        if (placement is not null)
        {
            chips.Children.Add(BuildMatchInfoChip($"第 {placement.Value:F0} 名", TrackerAccentBlue));
        }

        var averageRank = GetMetricNumber(match["avgPremierRank"]);
        if (averageRank is not null)
        {
            chips.Children.Add(BuildMatchInfoChip($"平均优先分 {averageRank.Value:N0}", TrackerAccentPink));
        }

        var duration = GetString(match, "duration") ?? GetString(fallbackMatch, "duration");
        if (!string.IsNullOrWhiteSpace(duration))
        {
            chips.Children.Add(BuildMatchInfoChip($"时长 {duration}", TrackerMuted));
        }

        return chips.Children.Count == 0
            ? null
            : new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("AuroraPaneBrush"),
                Child = chips
            };
    }

    private Border BuildMatchInfoChip(string text, string colorHex) =>
        new()
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(999),
            Background = new SolidColorBrush(ColorFrom($"{colorHex}20")),
            BorderBrush = new SolidColorBrush(ColorFrom($"{colorHex}66")),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = new SolidColorBrush(ColorFrom(colorHex))
            }
        };

    private Border? BuildMatchTeam(
        string title,
        JsonArray? team,
        string currentSteamId,
        string accentColor)
    {
        if (team is not { Count: > 0 })
        {
            return null;
        }

        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom(accentColor))
        });
        foreach (var player in team
            .OfType<JsonObject>()
            .OrderByDescending(player => GetMetricNumber(player["kills"]) ?? 0))
        {
            var steamId = GetString(player, "steamid64");
            var isCurrent = string.Equals(steamId, currentSteamId, StringComparison.Ordinal);
            var name = FirstNonEmpty(
                GetString(player, "name"),
                steamId is { Length: > 9 } ? $"…{steamId[^9..]}" : steamId,
                "未知玩家");
            var row = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(7, 6, 7, 6)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.Children.Add(new TextBlock
            {
                Text = isCurrent ? $"{name}（你）" : name,
                FontSize = 10,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isCurrent
                    ? new SolidColorBrush(ColorFrom(accentColor))
                    : ThemeBrush("AuroraTextHeadingBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var kills = GetMetricNumber(player["kills"]);
            var deaths = GetMetricNumber(player["deaths"]);
            var assists = GetMetricNumber(player["assists"]);
            var stats = new TextBlock
            {
                Text = $"{FormatWhole(kills)} / {FormatWhole(deaths)} / {FormatWhole(assists)}",
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(stats, 1);
            row.Children.Add(stats);
            var adr = GetMetricNumber(player["adr"]);
            var rating = GetMetricNumber(player["rating"]);
            var secondary = new TextBlock
            {
                Text = $"{Number(adr, "F0")} · {Number(rating, "F2")}",
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(secondary, 2);
            row.Children.Add(secondary);
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = isCurrent ? ThemeBrush("AuroraPillBrush") : ThemeBrush("AuroraGlassBrush"),
                Child = row
            });
        }

        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("AuroraPaneBrush"),
            Child = panel
        };
    }

    private UIElement BuildMatchActions(JsonObject match, string steamId, bool hasRichDetails)
    {
        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var shareCode = GetString(match, "shareCode");
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(shareCode)
                ? hasRichDetails ? "详细计分板" : "基础比赛信息"
                : $"分享码：{shareCode}",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            IsTextSelectionEnabled = !string.IsNullOrWhiteSpace(shareCode),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        if (!string.IsNullOrWhiteSpace(shareCode))
        {
            var replay = new Button
            {
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE768", FontSize = 12 },
                        new TextBlock { Text = "查看 2D 回放" }
                    }
                }
            };
            replay.Click += async (_, _) =>
                await AppState.OpenUrlAsync(
                    $"{TrackerAssetRoot}/demo-viewer/viewer.html?m={Uri.EscapeDataString(shareCode)}");
            actions.Children.Add(replay);
        }

        var history = new Button
        {
            Style = (Style)Application.Current.Resources["AuroraGlassButtonStyle"],
            Content = "打开比赛记录"
        };
        history.Click += async (_, _) =>
            await AppState.OpenUrlAsync($"{TrackerAssetRoot}/stats/{steamId}?view=matches");
        actions.Children.Add(history);
        Grid.SetColumn(actions, 1);
        panel.Children.Add(actions);
        return panel;
    }

    private static JsonObject? FindMatchPlayer(JsonObject match, string steamId)
    {
        foreach (var teamName in new[] { "ownTeam", "enemyTeam" })
        {
            if (GetNode(match, teamName) is not JsonArray team)
            {
                continue;
            }

            var player = team
                .OfType<JsonObject>()
                .FirstOrDefault(item =>
                    string.Equals(GetString(item, "steamid64"), steamId, StringComparison.Ordinal));
            if (player is not null)
            {
                return player;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseMatchDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeMatchMap(string? map) =>
        string.IsNullOrWhiteSpace(map)
            ? string.Empty
            : Regex.Replace(
                map.Trim().ToLowerInvariant(),
                "^((de|cs|ar)_)",
                string.Empty,
                RegexOptions.IgnoreCase);

    private static string? MatchResult(
        double? scoreFor,
        double? scoreAgainst,
        string? explicitResult = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitResult))
        {
            var normalized = explicitResult.Trim().ToLowerInvariant();
            if (normalized is "win" or "loss" or "tie")
            {
                return normalized;
            }
        }

        if (scoreFor is null || scoreAgainst is null)
        {
            return null;
        }

        return scoreFor > scoreAgainst ? "win" :
            scoreFor < scoreAgainst ? "loss" : "tie";
    }
}
