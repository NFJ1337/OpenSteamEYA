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
    private const string PerfectSafeColor = "#22C55E";
    private const string PerfectDangerColor = "#EF4444";
    private const string PerfectWarningColor = "#F59E0B";

    private UIElement BuildPerfectReferencePage(
        JsonObject data,
        CrossPlatformProfileResult result)
    {
        var accountsGroup = BuildPerfectAccountsGroup(data);
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(BuildPerfectHeroCard(data, result));
        content.Children.Add(accountsGroup);
        return content;
    }

    private Border BuildPerfectHeroCard(
        JsonObject data,
        CrossPlatformProfileResult result)
    {
        var lookup = data["lookup"] as JsonObject;
        var perfectName = FirstNonEmpty(
            GetString(data, "perfectNickname"),
            GetString(lookup ?? new JsonObject(), "pvpNickName"),
            GetString(data, "nickname"),
            "完美玩家");
        var appName = FirstNonEmpty(
            GetString(data, "appNickname"),
            GetString(lookup ?? new JsonObject(), "appNickName"),
            perfectName);
        var steamId = FirstNonEmpty(
            GetString(data, "steamId"),
            GetString(lookup ?? new JsonObject(), "steamId"),
            "未知 SteamID");
        var avatar = FirstNonEmpty(
            GetString(data, "avatar"),
            GetString(lookup ?? new JsonObject(), "pvpAvatar"));
        var score = GetMetricNumber(data["latestScore"]) ??
            GetMetricNumber(lookup?["pvpScore"]);
        var bestScore = GetMetricNumber(data["bestPvpScore"]) ?? score;

        var root = new Grid { ColumnSpacing = 18 };
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(96)
        });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildPerfectAvatar(avatar, perfectName));

        var identity = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(new TextBlock
        {
            Text = perfectName,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(BuildPerfectCaption($"完美昵称：{perfectName}"));
        identity.Children.Add(BuildPerfectCaption($"App昵称：{appName}"));
        identity.Children.Add(BuildPerfectCaption($"SteamID：{steamId}"));
        var visibleMessage = result.Message;
        if (visibleMessage?.Contains("完美公开资料", StringComparison.Ordinal) == true &&
            (visibleMessage.Contains("当前仅显示", StringComparison.Ordinal) ||
             visibleMessage.Contains("已读取", StringComparison.Ordinal)))
        {
            visibleMessage = null;
        }

        if (!string.IsNullOrWhiteSpace(visibleMessage))
        {
            identity.Children.Add(new TextBlock
            {
                Text = visibleMessage,
                FontSize = 12,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 680
            });
        }

        Grid.SetColumn(identity, 1);
        root.Children.Add(identity);

        var actions = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(BuildCompetitiveRankScoreBadge(
            PerfectRankVisual(score),
            score,
            "完美天梯"));
        actions.Children.Add(new TextBlock
        {
            Text = bestScore is null
                ? "历史最高 —"
                : $"历史最高 {bestScore.Value.ToString("N0", CultureInfo.InvariantCulture)}",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        });
        Grid.SetColumn(actions, 2);
        root.Children.Add(actions);

        var card = OverviewCard(root, new Thickness(16));
        card.Height = PlatformHeroCardHeight;
        return card;
    }

    private Border BuildPerfectAvatar(string? avatarUrl, string name)
    {
        var imageGrid = new Grid();
        imageGrid.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(name)
                ? "?"
                : name.Trim()[0].ToString().ToUpperInvariant(),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(avatarUrl) &&
            Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
        {
            imageGrid.Children.Add(new Image
            {
                Source = new BitmapImage(uri),
                Stretch = Stretch.UniformToFill
            });
        }

        return new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(16),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("AuroraPillBrush"),
            Child = imageGrid
        };
    }

    private Border BuildPerfectScorePill(
        string label,
        string value,
        string backgroundColor,
        string foregroundColor)
    {
        return new Border
        {
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(999),
            Background = new SolidColorBrush(ColorFrom(backgroundColor)),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom(foregroundColor))
            }
        };
    }

    private TextBlock BuildPerfectCaption(string text) =>
        new()
        {
            Text = text,
            FontSize = 14,
            Foreground = ThemeBrush("AuroraTextBodyBrush"),
            TextWrapping = TextWrapping.Wrap
        };

    private Border BuildPerfectAccountsGroup(JsonObject data)
    {
        var accounts = data["boundAccounts"] as JsonArray;
        var content = new StackPanel { Spacing = 10 };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "绑定账号列表（含封禁与黑产检测）",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        var count = accounts?.Count ?? 0;
        var countText = new TextBlock
        {
            Text = $"{count} 个账号",
            FontSize = 12,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 1);
        heading.Children.Add(countText);
        content.Children.Add(heading);

        var cards = new StackPanel { Spacing = 10 };
        if (accounts is { Count: > 0 })
        {
            foreach (var account in accounts.OfType<JsonObject>())
            {
                cards.Children.Add(BuildPerfectAccountCard(account));
            }
        }
        else
        {
            cards.Children.Add(BuildPlatformNotice("暂未读取到绑定账号资料。"));
        }

        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 620,
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        });

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("CustomUiDialogBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    private Border BuildPerfectAccountCard(JsonObject account)
    {
        var steamId = FirstNonEmpty(GetString(account, "steamId"), "N/A");
        var steamName = FirstNonEmpty(GetString(account, "steamName"), steamId);
        var perfectName = GetString(account, "perfectName");
        var isMain = GetBool(account, "mainAccount") == true;
        var steamProfile = account["steamProfile"] as JsonObject;
        var steamFields = steamProfile?["fields"] as JsonObject ?? new JsonObject();
        var officialStats = account["officialStats"] as JsonObject ?? new JsonObject();
        var csInfo = account["csInfo"] as JsonObject ?? new JsonObject();
        var inventory = account["inventory"] as JsonObject ?? new JsonObject();
        var pvpStats = account["pvpStats"] as JsonObject ?? new JsonObject();

        var (inventoryCount, inventoryPrice) = FormatPerfectInventory(inventory);
        var gameHours = GetMetricNumber(officialStats["hours"]) ??
            GetMetricNumber(officialStats["playTimeHours"]);
        var tradeStatus = GetPerfectSteamField(steamFields, "Steam 交易状态");
        var vacStatus = GetPerfectSteamField(steamFields, "VAC 作弊系统检测");
        var communityStatus = GetPerfectSteamField(steamFields, "Steam 社区状态");

        var content = new StackPanel { Spacing = 10 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel { Spacing = 3 };
        names.Children.Add(new TextBlock
        {
            Text = steamName,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        if (!string.IsNullOrWhiteSpace(perfectName) &&
            !string.Equals(perfectName, steamName, StringComparison.Ordinal))
        {
            names.Children.Add(BuildPerfectCaption($"完美昵称：{perfectName}"));
        }

        names.Children.Add(BuildPerfectCaption($"SteamID：{steamId}"));
        header.Children.Add(names);
        if (isMain)
        {
            var mainTag = BuildPerfectScorePill(
                "主账号",
                "是",
                "#1857E6B5",
                PerfectSafeColor);
            mainTag.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(mainTag, 1);
            header.Children.Add(mainTag);
        }

        content.Children.Add(header);

        var infoGrid = new Grid
        {
            ColumnSpacing = 24,
            RowSpacing = 6
        };
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < 6; row++)
        {
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var infoItems = new[]
        {
            ("SteamID", steamId, (Brush?)null),
            ("Steam昵称", steamName, null),
            ("Steam徽章数", GetPerfectSteamField(steamFields, "Steam 徽章数"), null),
            ("Steam社区等级", GetPerfectSteamField(steamFields, "Steam 社区等级"), null),
            ("Steam注册时间", GetPerfectSteamField(steamFields, "Steam 注册时间"), null),
            ("Steam交易状态", tradeStatus, PerfectStatusBrush(tradeStatus)),
            ("CS2饰品数量", inventoryCount, null),
            ("CS2饰品总值", inventoryPrice, null),
            ("VAC作弊系统检测", vacStatus, PerfectStatusBrush(vacStatus)),
            ("Steam社区状态", communityStatus, PerfectStatusBrush(communityStatus)),
            ("主账号", isMain ? "是" : "否", new SolidColorBrush(ColorFrom(
                isMain ? PerfectSafeColor : "#94A3B8"))),
            ("游戏时长", gameHours is null
                ? "—"
                : $"{gameHours.Value.ToString("F1", CultureInfo.InvariantCulture)} 小时", null)
        };
        for (var index = 0; index < infoItems.Length; index++)
        {
            var item = infoItems[index];
            var cell = BuildPerfectInfoLine(item.Item1, item.Item2, item.Item3);
            Grid.SetColumn(cell, index % 2);
            Grid.SetRow(cell, index / 2);
            infoGrid.Children.Add(cell);
        }

        content.Children.Add(infoGrid);
        content.Children.Add(BuildPerfectMetricSection(
            "国服数据",
            new[]
            {
                BuildPerfectMetric(
                    "优先状态",
                    FormatPerfectPriority(csInfo),
                    null),
                BuildPerfectMetric(
                    "场数",
                    FormatWhole(GetMetricNumber(officialStats["cnt"])),
                    null),
                BuildPerfectMetric(
                    "游戏时长",
                    gameHours is null
                        ? "—"
                        : gameHours.Value.ToString("F0", CultureInfo.InvariantCulture),
                    null),
                BuildPerfectMetric(
                    "Rating",
                    Number(GetMetricNumber(officialStats["rating"]), "F2"),
                    PerfectRatingBrush(GetMetricNumber(officialStats["rating"]))),
                BuildPerfectMetric(
                    "Kd",
                    Number(GetMetricNumber(officialStats["kd"]), "F2"),
                    PerfectKdBrush(GetMetricNumber(officialStats["kd"]))),
                BuildPerfectMetric(
                    "胜率",
                    FormatPercent(GetMetricNumber(officialStats["winRate"])),
                    PerfectWinRateBrush(GetMetricNumber(officialStats["winRate"])))
            }));

        var maxPvpScore = GetMetricNumber(account["maxPvpScore"]) ??
            GetMetricNumber(pvpStats["pvpScore"]);
        var pvpRank = PerfectRankVisual(maxPvpScore);
        content.Children.Add(BuildPerfectMetricSection(
            "完美平台数据",
            new[]
            {
                BuildPerfectMetric(
                    "当前赛季分段",
                    FormatPerfectGradeWithScore(maxPvpScore),
                    new SolidColorBrush(ColorFrom(pvpRank.Accent))),
                BuildPerfectMetric(
                    "当前赛季场数",
                    FormatWhole(GetMetricNumber(pvpStats["cnt"])),
                    null),
                BuildPerfectMetric(
                    "Rating",
                    Number(
                        GetMetricNumber(pvpStats["pwRating"]) ??
                        GetMetricNumber(pvpStats["rating"]),
                        "F2"),
                    PerfectRatingBrush(
                        GetMetricNumber(pvpStats["pwRating"]) ??
                        GetMetricNumber(pvpStats["rating"]))),
                BuildPerfectMetric(
                    "Kd",
                    Number(GetMetricNumber(pvpStats["kd"]), "F2"),
                    PerfectKdBrush(GetMetricNumber(pvpStats["kd"]))),
                BuildPerfectMetric(
                    "WE",
                    Number(GetMetricNumber(pvpStats["avgWe"]), "F2"),
                    PerfectKdBrush(GetMetricNumber(pvpStats["avgWe"]))),
                BuildPerfectMetric(
                    "胜率",
                    FormatPercent(GetMetricNumber(pvpStats["winRate"])),
                    PerfectWinRateBrush(GetMetricNumber(pvpStats["winRate"])))
            }));

        var forbidStatus = FirstNonEmpty(
            GetString(account, "forbidStatus"),
            "账号状态正常");
        content.Children.Add(BuildPerfectStatusLine(
            $"封禁状态: {forbidStatus}",
            !forbidStatus.Contains("封禁", StringComparison.Ordinal) &&
            !forbidStatus.Contains("异常", StringComparison.Ordinal)));
        var isBlack = GetBool(account, "isBlack") == true;
        var blackDetails = FirstNonEmpty(
            GetString(account, "blackDetails"),
            isBlack ? "检测到黑产账号特征" : "未检测到黑产账号特征");
        content.Children.Add(BuildPerfectStatusLine(
            $"黑产检测: {blackDetails}",
            !isBlack));

        return new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrush("CustomUiDialogBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    private StackPanel BuildPerfectInfoLine(
        string label,
        string value,
        Brush? valueBrush)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        line.Children.Add(new TextBlock
        {
            Text = $"{label}:",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        line.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = valueBrush ?? ThemeBrush("AuroraTextBodyBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360
        });
        return line;
    }

    private Border BuildPerfectMetricSection(
        string title,
        IReadOnlyList<UIElement> metrics)
    {
        var grid = CreateEqualGrid(metrics.Count, 8);
        foreach (var metric in metrics)
        {
            grid.Children.Add(metric);
        }

        ArrangeColumnsInOrder(grid);
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("CustomUiDialogBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeBrush("AuroraTextMutedBrush")
                    },
                    grid
                }
            }
        };
    }

    private UIElement BuildPerfectMetric(
        string label,
        string value,
        Brush? valueBrush)
    {
        return new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = ThemeBrush("AuroraTextMutedBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = value,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = valueBrush ?? ThemeBrush("AuroraTextHeadingBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
    }

    private TextBlock BuildPerfectStatusLine(string text, bool healthy) =>
        new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = healthy ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom(
                healthy ? PerfectSafeColor : PerfectDangerColor)),
            TextWrapping = TextWrapping.Wrap
        };

    private static string FormatPerfectPriority(JsonObject csInfo)
    {
        var levelTitle = GetString(csInfo, "levelTitle");
        var level = GetMetricNumber(csInfo["curLevel"]);
        if (string.IsNullOrWhiteSpace(levelTitle) || level is null or <= 0)
        {
            return "非优先用户";
        }

        return $"优先用户 {levelTitle}-{level.Value.ToString("F0", CultureInfo.InvariantCulture)}级";
    }

    private static string FormatPerfectGradeWithScore(double? score)
    {
        var grade = PerfectRankVisual(score).Label;
        return score is > 0
            ? $"{grade} · {score.Value.ToString("N0", CultureInfo.InvariantCulture)}"
            : grade;
    }

    private static (string Count, string Price) FormatPerfectInventory(JsonObject inventory)
    {
        if (inventory.Count == 0 || inventory["totalCount"] is null)
        {
            return ("无库存", "无库存 0 RMB");
        }

        var count = GetMetricNumber(inventory["totalCount"]) ?? 0;
        if (count <= 0)
        {
            return ("N/A", "N/A");
        }

        var price = (GetMetricNumber(inventory["totalPrice"]) ?? 0) * 0.01;
        return (
            count.ToString("N0", CultureInfo.InvariantCulture),
            $"{price.ToString("F2", CultureInfo.InvariantCulture)} RMB");
    }

    private static string GetPerfectSteamField(JsonObject fields, string key)
    {
        var node = fields[key];
        return string.IsNullOrWhiteSpace(node?.ToString()) ? "N/A" : node.ToString();
    }

    private static Brush? PerfectStatusBrush(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SolidColorBrush(ColorFrom(
            value.Contains("正常", StringComparison.Ordinal)
                ? PerfectSafeColor
                : PerfectDangerColor));
    }

    private static Brush? PerfectRatingBrush(double? value) =>
        value is null
            ? null
            : new SolidColorBrush(ColorFrom(
                value >= 1.1 ? PerfectSafeColor :
                value >= 0.9 ? PerfectWarningColor :
                PerfectDangerColor));

    private static Brush? PerfectKdBrush(double? value) =>
        value is null
            ? null
            : new SolidColorBrush(ColorFrom(
                value >= 1.0 ? PerfectSafeColor :
                PerfectDangerColor));

    private static Brush? PerfectWinRateBrush(double? value)
    {
        if (value is null)
        {
            return null;
        }

        var percent = value <= 1 ? value.Value * 100 : value.Value;
        return new SolidColorBrush(ColorFrom(
            percent >= 60 ? PerfectSafeColor :
            percent >= 50 ? PerfectWarningColor :
            PerfectDangerColor));
    }
}
