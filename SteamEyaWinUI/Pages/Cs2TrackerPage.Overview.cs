using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.Foundation;
using Windows.Media.Core;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage
{
    private const string TrackerAssetRoot = "https://cs2tracker.gg";
    private const string TrackerAccentGreen = "#6FE7BF";
    private const string TrackerAccentYellow = "#F5C84C";
    private const string TrackerAccentBlue = "#2AA6FF";
    private const string TrackerAccentPink = "#FF4D9D";
    private const string TrackerAccentRed = "#FF5A65";
    private const string TrackerMuted = "#8491AE";

    private void RenderOverview(
        JsonObject root,
        JsonObject? aimStats,
        JsonObject? highlights,
        JsonObject? cheatRisk,
        string fallbackSteamId)
    {
        TrackerModulesPanel.Children.Clear();

        var steamId = GetString(root, "steamid64") ?? fallbackSteamId;
        var name = FirstNonEmpty(
            GetString(root, "nickname"),
            GetString(root, "csstatsgg", "name"),
            steamId);
        var liveCheatResult = GetNode(cheatRisk ?? new JsonObject(), "result") as JsonObject;
        var useLiveCheatRisk =
            GetBool(cheatRisk ?? new JsonObject(), "live") == true &&
            liveCheatResult is not null &&
            GetBool(liveCheatResult, "available") != false;
        var riskScore = useLiveCheatRisk
            ? GetMetricNumber(liveCheatResult?["percent"])
            : null;
        riskScore ??= GetMetricNumber(GetNode(root, "gauges", "cheating")) ??
            GetMetricNumber(GetNode(root, "gauges", "cheating_details", "percent"));
        double? riskGaugeScore = riskScore is null
            ? null
            : Math.Clamp(Math.Round(riskScore.Value, MidpointRounding.AwayFromZero), 1, 99);
        var headshot = GetMetricNumber(GetNode(root, "gauges", "headshot")) ??
            GetMetricNumber(GetNode(root, "gauges", "headshot%")) ??
            GetMetricNumber(GetNode(root, "csstatsgg", "stats", "hs"));
        var winRate = GetMetricNumber(GetNode(root, "gauges", "winrate"));

        TrackerModulesPanel.Children.Add(BuildProfileTabs(root, steamId, name));

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });
        top.Children.Add(BuildProfileCard(root, steamId, name, riskScore));

        var gaugeGrid = CreateEqualGrid(3, 8);
        gaugeGrid.Children.Add(BuildGaugeCard(
            "作弊风险",
            riskGaugeScore,
            "%",
            riskGaugeScore,
            riskScore switch
            {
                null => TrackerMuted,
                >= 60 => TrackerAccentRed,
                >= 30 => TrackerAccentYellow,
                _ => TrackerAccentGreen
            },
            "ms-appx:///Assets/Cs2Tracker/gauge_agent.svg",
            riskScore switch
            {
                null => "未知",
                >= 60 => "高风险",
                >= 30 => "可疑",
                _ => "低风险"
            },
            "疑似信号",
            riskScore switch
            {
                null => "暂无风险信号。",
                >= 60 => "检测到较强异常信号。",
                >= 30 => "存在少量待复核信号。",
                _ => "近期对局表现与普通玩家一致。"
            },
            4));
        gaugeGrid.Children.Add(BuildGaugeCard(
            "爆头率",
            headshot,
            "%",
            headshot,
            TrackerAccentGreen,
            "ms-appx:///Assets/Cs2Tracker/gauge_headshot.svg",
            headshot switch
            {
                null => "未知",
                >= 55 => "神枪手",
                >= 35 => "稳定",
                _ => "待提升"
            },
            "瞄准能力",
            headshot switch
            {
                null => "暂无爆头率数据。",
                >= 55 => "首发射击与爆头转化表现突出。",
                _ => "爆头转化处于普通区间。"
            },
            35));
        gaugeGrid.Children.Add(BuildGaugeCard(
            "胜率",
            winRate,
            "%",
            winRate,
            TrackerAccentYellow,
            "ms-appx:///Assets/Cs2Tracker/gauge_trophy.svg",
            winRate switch
            {
                null => "未知",
                >= 55 => "上升中",
                >= 45 => "稳定",
                _ => "需调整"
            },
            "近期状态",
            winRate switch
            {
                null => "暂无胜率数据。",
                >= 55 => "近期对局胜率高于平均水平。",
                _ => "近期对局胜率接近平均水平。"
            },
            50));
        ArrangeColumnsInOrder(gaugeGrid);
        Grid.SetColumn(gaugeGrid, 1);
        top.Children.Add(gaugeGrid);
        TrackerModulesPanel.Children.Add(top);

        var middle = new Grid { ColumnSpacing = 12 };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });

        var leftColumn = new StackPanel { Spacing = 12 };
        var mini = CreateEqualGrid(2, 8);
        mini.Children.Add(BuildMiniStatCard("账号年龄", ReadAccountAge(root) ?? "—"));
        mini.Children.Add(BuildMiniStatCard(
            "游戏时长",
            ReadPlaytimeHours(root) is { } hours ? $"{hours} 小时" : "—"));
        ArrangeColumnsInOrder(mini);
        leftColumn.Children.Add(mini);
        leftColumn.Children.Add(BuildLeetifyCard(root, steamId));
        middle.Children.Add(leftColumn);

        var rankGrid = CreateEqualGrid(2, 8);
        rankGrid.Children.Add(BuildPremierCard(root, name));
        rankGrid.Children.Add(BuildFaceitCard(root, steamId));
        ArrangeColumnsInOrder(rankGrid);
        Grid.SetColumn(rankGrid, 1);
        middle.Children.Add(rankGrid);
        TrackerModulesPanel.Children.Add(middle);

        var duo = new Grid { ColumnSpacing = 12 };
        duo.ColumnDefinitions.Add(new ColumnDefinition());
        duo.ColumnDefinitions.Add(new ColumnDefinition());
        duo.Children.Add(BuildHighlightsCard(highlights, steamId));
        var aim = BuildAimByRankCard(aimStats);
        Grid.SetColumn(aim, 1);
        duo.Children.Add(aim);
        TrackerModulesPanel.Children.Add(duo);

        TrackerModulesPanel.Children.Add(BuildTrackerStatsCard(aimStats));
        TrackerModulesPanel.Children.Add(BuildScopeCard(root, steamId));
        TrackerModulesPanel.Children.Add(BuildCollectiblesCard(root));
        TrackerModulesPanel.Children.Add(BuildRecentMatchesCard(root));

        var weapons = BuildWeaponsCard(root);
        if (weapons is not null)
        {
            TrackerModulesPanel.Children.Add(weapons);
        }
    }

    private Border BuildProfileCard(
        JsonObject root,
        string steamId,
        string name,
        double? riskScore)
    {
        var content = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatarUrl = FirstNonEmpty(
            GetString(root, "avatar_url"),
            GetString(root, "csstatsgg", "avatar"));
        var avatar = new Border
        {
            Width = 70,
            Height = 70,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            Background = ThemeBrush("AuroraPillBrush"),
            Child = CreateAvatarFallback(name)
        };
        var normalizedAvatarUrl = NormalizeSteamImageUrl(avatarUrl);
        if (Uri.TryCreate(normalizedAvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            var avatarImage = new Image
            {
                Stretch = Stretch.UniformToFill
            };
            avatarImage.ImageFailed += (_, _) => avatar.Child = CreateAvatarFallback(name);
            avatarImage.Source = new BitmapImage(avatarUri)
            {
                DecodePixelWidth = 140
            };
            avatar.Child = avatarImage;
        }
        header.Children.Add(avatar);

        var identity = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        identity.Children.Add(new TextBlock
        {
            Text = steamId,
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            IsTextSelectionEnabled = true
        });
        var level = GetInt(root, "level");
        var friends = GetInt(root, "friends_count") ??
            GetInt(root, "steam_raw", "friends_count");
        identity.Children.Add(new TextBlock
        {
            Text = $"好友 {FormatWhole(friends)}  ·  等级 {FormatWhole(level)}",
            FontSize = 11,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);

        var riskLabel = riskScore switch
        {
            null => "未知",
            >= 60 => "高危险",
            >= 30 => "可疑",
            _ => "LEGIT"
        };
        var riskColor = riskScore switch
        {
            >= 60 => ColorFrom(TrackerAccentRed),
            >= 30 => ColorFrom(TrackerAccentYellow),
            _ => ColorFrom(TrackerAccentGreen)
        };
        var riskBadge = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(WithAlpha(riskColor, 0x24)),
            BorderBrush = new SolidColorBrush(WithAlpha(riskColor, 0x80)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = riskLabel,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(riskColor)
            }
        };
        var topActions = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        topActions.Children.Add(riskBadge);
        var history = CreateSmallLinkButton(
            "Steam History",
            $"https://steamback.gg/archive?profile={Uri.EscapeDataString($"https://steamcommunity.com/profiles/{steamId}/")}",
            "\uE81C");
        topActions.Children.Add(history);
        Grid.SetColumn(topActions, 2);
        header.Children.Add(topActions);
        content.Children.Add(header);

        var sourceBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sourceBar.Children.Add(CreateSourceButton(
            $"{TrackerAssetRoot}/images/stats/steam.png",
            "Steam",
            $"https://steamcommunity.com/profiles/{steamId}"));
        sourceBar.Children.Add(CreateSourceButton(
            $"{TrackerAssetRoot}/images/stats/leetify.png",
            "Leetify",
            $"https://leetify.com/public/profile/{steamId}"));
        sourceBar.Children.Add(CreateSourceButton(
            $"{TrackerAssetRoot}/images/stats/scope-icon.webp",
            "Scope.gg",
            $"https://app.scope.gg/profile/{steamId}"));
        if (GetString(root, "faceit", "nickname") is { Length: > 0 } faceitNickname)
        {
            sourceBar.Children.Add(CreateSourceButton(
                $"{TrackerAssetRoot}/images/stats/faceit.png",
                "FACEIT",
                $"https://www.faceit.com/en/players/{Uri.EscapeDataString(faceitNickname)}"));
        }

        sourceBar.Children.Add(CreateSourceButton(
            $"{TrackerAssetRoot}/images/stats/cybershoke.png",
            "Cybershoke",
            $"https://cybershoke.net/{steamId}"));
        content.Children.Add(sourceBar);

        if (steamId == "76561198386265483")
        {
            content.Children.Add(BuildProPageBanner());
        }

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = $"正在查看：{name}",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        var publicProfile = new TextBlock
        {
            Text = "CS2Tracker 公开资料",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };
        Grid.SetColumn(publicProfile, 1);
        footer.Children.Add(publicProfile);
        content.Children.Add(footer);

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildProfileTabs(JsonObject root, string steamId, string name)
    {
        var content = new StackPanel { Spacing = 8 };
        var profile = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var avatarUrl = FirstNonEmpty(
            GetString(root, "avatar_url"),
            GetString(root, "csstatsgg", "avatar"));
        if (avatarUrl.Length > 0)
        {
            profile.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = ThemeBrush("AuroraPillBrush"),
                Child = new Image
                {
                    Source = new BitmapImage(new Uri(NormalizeSteamImageUrl(avatarUrl))),
                    Stretch = Stretch.UniformToFill
                }
            });
        }
        profile.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(profile);

        var tabs = new Grid { ColumnSpacing = 4 };
        for (var column = 0; column < 4; column++)
        {
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var entries = new (string Label, string Url, bool Active)[]
        {
            ("概览", $"{TrackerAssetRoot}/stats/{steamId}", true),
            ("瞄准", $"{TrackerAssetRoot}/stats/{steamId}?view=aim", false),
            ("比赛记录", $"{TrackerAssetRoot}/stats/{steamId}?view=matches", false),
            ("对比", $"{TrackerAssetRoot}/stats/{steamId}?view=pros", false),
            ("习惯", $"{TrackerAssetRoot}/stats/{steamId}?view=habits", false),
            ("高光", $"{TrackerAssetRoot}/stats/{steamId}?view=highlights", false),
            ("提升", $"{TrackerAssetRoot}/stats/{steamId}?view=skill", false)
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var part = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var text = new HyperlinkButton
            {
                Content = entry.Label,
                FontSize = 10,
                Foreground = entry.Active
                    ? new SolidColorBrush(ColorFrom(TrackerAccentBlue))
                    : ThemeBrush("AuroraTextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            text.Click += async (_, _) => await AppState.OpenUrlAsync(entry.Url);
            part.Children.Add(text);
            part.Children.Add(new Border
            {
                Height = entry.Active ? 2 : 0,
                Background = new SolidColorBrush(ColorFrom(TrackerAccentBlue)),
                CornerRadius = new CornerRadius(1)
            });
            Grid.SetColumn(part, index % 4);
            Grid.SetRow(part, index / 4);
            if (index / 4 >= tabs.RowDefinitions.Count)
            {
                tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            tabs.Children.Add(part);
        }
        content.Children.Add(tabs);

        var demo = new Grid { ColumnSpacing = 8 };
        demo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        demo.ColumnDefinitions.Add(new ColumnDefinition());
        demo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        demo.Children.Add(new Border
        {
            Width = 38,
            Height = 30,
            CornerRadius = new CornerRadius(5),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "暂无\n记录",
                FontSize = 8,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var demoText = new TextBlock
        {
            Text = "暂无比赛演示记录。",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(demoText, 1);
        demo.Children.Add(demoText);
        var demoLink = CreateTextLink(
            "关联比赛代码",
            $"{TrackerAssetRoot}/link");
        Grid.SetColumn(demoLink, 2);
        demo.Children.Add(demoLink);
        content.Children.Add(demo);

        return OverviewCard(content, new Thickness(10, 8, 10, 8));
    }

    private Border BuildProPageBanner()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Child = new Image
            {
                Source = new BitmapImage(new Uri($"{TrackerAssetRoot}/images/team-manager/players/donk-128.png")),
                Stretch = Stretch.UniformToFill
            }
        });
        var copy = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = "donk 的职业选手页",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        copy.Children.Add(new TextBlock
        {
            Text = "准星、库存与 Team Manager 卡片",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var arrow = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);

        var link = new Button
        {
            Padding = new Thickness(9, 6, 9, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = ThemeBrush("AuroraGlassButtonBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Content = grid
        };
        link.Click += async (_, _) => await AppState.OpenUrlAsync($"{TrackerAssetRoot}/pro/donk");
        return new Border
        {
            BorderBrush = new SolidColorBrush(ColorFrom("#205EA6")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = link
        };
    }

    private Border BuildMiniStatCard(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(OverviewLabel(label));
        text.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        grid.Children.Add(text);
        var feedback = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top
        };
        feedback.Children.Add(SmallFeedbackIcon("\uE8E1"));
        feedback.Children.Add(SmallFeedbackIcon("\uE8E0"));
        Grid.SetColumn(feedback, 1);
        grid.Children.Add(feedback);
        return OverviewCard(grid, new Thickness(10, 8, 10, 8));
    }

    private Border BuildGaugeCard(
        string title,
        double? rawValue,
        string suffix,
        double? percent,
        string colorHex,
        string iconUri,
        string status,
        string subStatus,
        string description,
        double averageMarker)
    {
        var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 120,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(CreateRingGauge(
            percent ?? 0,
            rawValue is null ? "—" : Math.Round(rawValue.Value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture),
            suffix,
            colorHex,
            iconUri,
            108,
            27,
            averageMarker));
        content.Children.Add(new TextBlock
        {
            Text = status,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 90,
            Foreground = new SolidColorBrush(ColorFrom(colorHex)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = subStatus,
            FontSize = 9,
            CharacterSpacing = 50,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return OverviewCard(content, new Thickness(12, 10, 12, 10));
    }

    private Border BuildLeetifyCard(JsonObject root, string steamId)
    {
        var ratings = GetNode(root, "leetify_extra", "recentGameRatings") as JsonObject;
        var content = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(ColorFrom("#35182B")),
            Child = new TextBlock
            {
                Text = "L",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColorFrom(TrackerAccentPink)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        title.Children.Add(new TextBlock
        {
            Text = "Leetify 数据",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        var gamesPlayed = GetMetricNumber(ratings?["gamesPlayed"]);
        var gameCount = new TextBlock
        {
            Text = gamesPlayed is null ? "—" : $"{gamesPlayed.Value:N0} 场近期对局",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 110,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(gameCount, 1);
        header.Children.Add(gameCount);
        content.Children.Add(header);

        var rings = CreateEqualGrid(3, 6);
        rings.Children.Add(BuildLeetifyRating(
            "瞄准",
            GetMetricNumber(ratings?["aim"]),
            LeetifyPercentile(root, "aim"),
            TrackerAccentGreen));
        rings.Children.Add(BuildLeetifyRating(
            "站位",
            GetMetricNumber(ratings?["positioning"]),
            LeetifyPercentile(root, "positioning"),
            TrackerAccentGreen));
        rings.Children.Add(BuildLeetifyRating(
            "道具",
            GetMetricNumber(ratings?["utility"]),
            LeetifyPercentile(root, "utility"),
            TrackerAccentGreen));
        ArrangeColumnsInOrder(rings);
        content.Children.Add(rings);

        var breakdown = CreateEqualGrid(3, 6);
        breakdown.Children.Add(BuildSimpleValue(
            "首杀",
            ScaleSignedRating(GetMetricNumber(ratings?["opening"])),
            TrackerAccentRed));
        breakdown.Children.Add(BuildSimpleValue(
            "残局",
            ScaleSignedRating(GetMetricNumber(ratings?["clutch"])),
            TrackerAccentRed));
        breakdown.Children.Add(BuildSimpleValue(
            "Leetify 评分",
            ScaleSignedRating(GetMetricNumber(ratings?["leetify"])),
            TrackerAccentGreen));
        ArrangeColumnsInOrder(breakdown);
        content.Children.Add(breakdown);

        var link = CreateTextLink("查看完整资料", $"https://leetify.com/public/profile/{steamId}");
        link.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(link);

        return OverviewCard(content, new Thickness(14));
    }

    private StackPanel BuildLeetifyRating(
        string label,
        double? value,
        double? percentile,
        string colorHex)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        var labelText = OverviewLabel(label);
        labelText.HorizontalAlignment = HorizontalAlignment.Center;
        labelText.TextAlignment = TextAlignment.Center;
        labelText.TextWrapping = TextWrapping.WrapWholeWords;
        labelText.MaxWidth = 92;
        content.Children.Add(labelText);
        content.Children.Add(CreateRingGauge(
            value ?? 0,
            value is null ? "—" : value.Value.ToString("F1", CultureInfo.InvariantCulture),
            "",
            colorHex,
            null,
            74,
            18,
            50));
        content.Children.Add(new TextBlock
        {
            Text = percentile is null ? "—" : $"前 {FormatTopPercent(percentile.Value)}",
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(CreateTierStrip(value));
        return content;
    }

    private Border BuildPremierCard(JsonObject root, string name)
    {
        var seasons = ReadPremierSeasons(root);
        var current = seasons.FirstOrDefault();
        var rating = current?.Rank is > 0 ? current.Rank : null;
        var season = current?.Season ?? 4;
        var wins = Math.Max(0, current?.Wins ?? 0);
        var results = ReadPremierRecentResults(root);
        var content = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Image
        {
            Source = new SvgImageSource(new Uri("ms-appx:///Assets/Cs2Tracker/gauge_agent.svg")),
            Width = 14,
            Height = 18
        });
        title.Children.Add(new TextBlock
        {
            Text = "优先模式数据",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        var rankBadge = CreatePremierRankBadge(rating);
        rankBadge.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(rankBadge, 1);
        header.Children.Add(rankBadge);
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });

        if (rating is null && wins < 10)
        {
            content.Children.Add(CreatePremierRankProgress(season, wins, 10));
        }

        content.Children.Add(CreatePremierRecentPerformance(results));
        var totalMatches =
            GetMetricNumber(root, "steam_userstats_730", "stats", "total_matches_played") ??
            GetMetricNumber(root, "stats", "total_matches_played") ??
            GetMetricNumber(root, "csstatsgg", "stats", "matches");
        content.Children.Add(CreatePremierStatsGrid(
            [
                ("ELO", rating is null ? "—" : rating.Value.ToString("N0", CultureInfo.InvariantCulture)),
                ("比赛场次", FormatWhole(totalMatches)),
                ("K/D", Number(ReadPremierKd(root), "F2")),
                ("爆头率", PremierPercent(ReadPremierHeadshot(root)))
            ]));

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildFaceitCard(JsonObject root, string steamId)
    {
        var faceit = GetNode(root, "faceit") as JsonObject;
        var nickname = GetString(faceit ?? new JsonObject(), "nickname");
        if (faceit is null || string.IsNullOrWhiteSpace(nickname))
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(BuildFaceitHeader(null, null));
            empty.Children.Add(new TextBlock
            {
                Text = "该 Steam 资料未绑定 FACEIT 账号。",
                FontSize = 11,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return OverviewCard(empty, new Thickness(14));
        }

        var cs2 = GetNode(faceit, "cs2") as JsonObject;
        var stats = GetNode(faceit, "cs2_stats") as JsonObject ??
            GetNode(faceit, "csgo_stats") as JsonObject;
        var basic = GetNode(faceit, "basic") as JsonObject;
        var elo = GetMetricNumber(cs2?["elo"]);
        var level = GetMetricNumber(cs2?["skill_level"]);
        var country = GetString(faceit, "country")?.ToUpperInvariant();

        var content = new StackPanel { Spacing = 9 };
        content.Children.Add(BuildFaceitHeader(nickname, steamId));

        var hero = new Grid { ColumnSpacing = 10 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        if (level is >= 1 and <= 10)
        {
            hero.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(
                    $"{TrackerAssetRoot}/images/faceit-levels/level{5000 + (int)level.Value}.png")),
                Width = 52,
                Height = 52
            });
        }
        var heroText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock
        {
            Text = elo is null ? "—" : elo.Value.ToString("N0", CultureInfo.InvariantCulture),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        heroText.Children.Add(new TextBlock
        {
            Text = $"ELO · 等级 {(level is null ? "—" : level.Value.ToString("F0", CultureInfo.InvariantCulture))}",
            FontSize = 10,
            Foreground = new SolidColorBrush(ColorFrom("#FF6B3D"))
        });
        var player = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (!string.IsNullOrWhiteSpace(country))
        {
            player.Children.Add(new Image
            {
                Source = new SvgImageSource(new Uri(
                    $"{TrackerAssetRoot}/images/faceit-flags/{country.ToLowerInvariant()}.svg?v=2")),
                Width = 15,
                Height = 15
            });
        }
        player.Children.Add(new TextBlock
        {
            Text = nickname,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        if (!string.IsNullOrWhiteSpace(country))
        {
            player.Children.Add(new TextBlock
            {
                Text = country,
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        heroText.Children.Add(player);
        var joined = FormatFaceitDate(GetString(basic ?? new JsonObject(), "activated_at"));
        if (!string.IsNullOrWhiteSpace(joined))
        {
            heroText.Children.Add(new TextBlock
            {
                Text = $"加入于 {joined}",
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
        }
        Grid.SetColumn(heroText, 1);
        hero.Children.Add(heroText);
        content.Children.Add(hero);

        var recent = ReadFaceitRecentResults(stats);
        var recentPanel = new StackPanel { Spacing = 4 };
        var recentHead = new Grid();
        recentHead.ColumnDefinitions.Add(new ColumnDefinition());
        recentHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recentHead.Children.Add(OverviewLabel("最近表现"));
        var lastMatch = stats?["recent_matches"] is JsonArray matches && matches.Count > 0
            ? FormatTrackerDate(GetString(matches[0] as JsonObject ?? new JsonObject(), "finished_at"))
            : null;
        var lastText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(lastMatch) ? "近期战绩" : $"最近比赛 {lastMatch}",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(lastText, 1);
        recentHead.Children.Add(lastText);
        recentPanel.Children.Add(recentHead);
        var recentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var index = 0; index < 5; index++)
        {
            recentRow.Children.Add(CreateResultIcon(index < recent.Count ? recent[index] : null, 18));
        }
        recentPanel.Children.Add(recentRow);
        content.Children.Add(recentPanel);

        var firstStats = CreateEqualGrid(4, 8);
        firstStats.Children.Add(BuildSimpleValue("ELO", FormatWhole(elo), null, 14));
        firstStats.Children.Add(BuildSimpleValue(
            "比赛",
            FormatWhole(GetMetricNumber(stats?["matches"])),
            null,
            14));
        firstStats.Children.Add(BuildSimpleValue(
            "K/D",
            Number(GetMetricNumber(stats?["average_kd_ratio"]), "F2"),
            null,
            14));
        firstStats.Children.Add(BuildSimpleValue(
            "爆头率",
            PremierPercent(GetMetricNumber(stats?["average_headshots_percent"])),
            null,
            14));
        ArrangeColumnsInOrder(firstStats);
        content.Children.Add(firstStats);

        var secondStats = CreateEqualGrid(4, 8);
        secondStats.Children.Add(BuildSimpleValue(
            "胜率",
            PremierPercent(GetMetricNumber(stats?["win_rate_percent"])),
            null,
            14));
        secondStats.Children.Add(BuildSimpleValue(
            "ADR",
            Number(GetMetricNumber(stats?["adr"]), "F1"),
            null,
            14));
        secondStats.Children.Add(BuildSimpleValue(
            "1V1/1V2",
            FormatFaceitClutches(stats).Replace(" / ", "/", StringComparison.Ordinal),
            null,
            14));
        secondStats.Children.Add(BuildSimpleValue(
            "UDR",
            Number(GetMetricNumber(stats?["utility_damage_per_round"]), "F1"),
            null,
            14));
        ArrangeColumnsInOrder(secondStats);
        content.Children.Add(secondStats);

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildHighlightsCard(JsonObject? highlights, string steamId)
    {
        var clips = GetNode(highlights ?? new JsonObject(), "clips") as JsonArray;
        var clip = clips?.OfType<JsonObject>().FirstOrDefault();
        var content = new StackPanel { Spacing = 8 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ColorFrom(TrackerAccentGreen)),
            VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new TextBlock
        {
            Text = "高光集锦",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        header.Children.Add(title);
        var seeAll = CreateTextLink(
            "查看全部 ›",
            $"{TrackerAssetRoot}/stats/{steamId}?view=highlights");
        Grid.SetColumn(seeAll, 1);
        header.Children.Add(seeAll);
        content.Children.Add(header);

        if (clip is null)
        {
            content.Children.Add(new Border
            {
                Height = 210,
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("AuroraPaneBrush"),
                Child = new TextBlock
                {
                    Text = "暂无公开高光视频。",
                    Foreground = ThemeBrush("AuroraTextMutedBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        else
        {
            var videoUrl = GetString(clip, "url");
            var map = GetString(clip, "map");
            var from = GetString(clip, "periodFrom");
            var to = GetString(clip, "periodTo");
            var mediaGrid = new Grid { Height = 270 };
            if (!string.IsNullOrWhiteSpace(map))
            {
                mediaGrid.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(GetMapThumbnailUri(map))),
                    Stretch = Stretch.UniformToFill,
                    Opacity = 0.72
                });
            }
            mediaGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(ColorFrom("#4005080F"))
            });
            if (videoUrl is not null && Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri))
            {
                var playButton = new Button
                {
                    Width = 46,
                    Height = 46,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(23),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(ColorFrom("#B305080F")),
                    BorderBrush = new SolidColorBrush(ColorFrom("#88FFFFFF")),
                    BorderThickness = new Thickness(1),
                    Content = new FontIcon
                    {
                        Glyph = "\uE768",
                        FontSize = 18,
                        Foreground = new SolidColorBrush(ColorFrom("#FFFFFF"))
                    }
                };
                ToolTipService.SetToolTip(playButton, "在浏览器中播放高光");
                playButton.Click += async (_, _) => await AppState.OpenUrlAsync(videoUri.AbsoluteUri);
                mediaGrid.Children.Add(playButton);
            }
            var labelText = string.Join(
                " · ",
                new[]
                {
                    "高光集锦",
                    FormatPeriod(from, to),
                    MapDisplayName(map)
                }.Where(text => !string.IsNullOrWhiteSpace(text)));
            var label = new Border
            {
                Margin = new Thickness(8),
                Padding = new Thickness(7, 3, 7, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ColorFrom("#D905080F")),
                Child = new TextBlock
                {
                    Text = labelText,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(ColorFrom("#FFFFFF"))
                }
            };
            mediaGrid.Children.Add(label);
            content.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(ColorFrom("#05080F")),
                Child = mediaGrid
            });

            var metadata = new Grid();
            metadata.ColumnDefinitions.Add(new ColumnDefinition());
            metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            metadata.Children.Add(new TextBlock
            {
                Text = MapDisplayName(map) ?? "CS2",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextHeadingBrush")
            });
            var date = new TextBlock
            {
                Text = FormatTrackerDate(GetString(clip, "createdAt")),
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            };
            Grid.SetColumn(date, 1);
            metadata.Children.Add(date);
            content.Children.Add(metadata);
        }

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildAimByRankCard(JsonObject? aimStats)
    {
        var aim = GetNode(aimStats ?? new JsonObject(), "aim") as JsonObject;
        var benchmarks = GetNode(aimStats ?? new JsonObject(), "benchmarks") as JsonObject;
        var matches = GetMetricNumber(aimStats?["matches"]);
        var content = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "瞄准能力排行",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        };
        header.Children.Add(title);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        right.Children.Add(new TextBlock
        {
            Text = matches is null ? "暂无样本" : $"最近 {matches.Value:F0} 场",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        right.Children.Add(CreatePill("v/s"));
        Grid.SetColumn(right, 1);
        header.Children.Add(right);
        content.Children.Add(header);

        var gauges = CreateEqualGrid(4, 8);
        gauges.Children.Add(BuildAimGauge(
            "伤害速度",
            GetMetricNumber(aim?["ttdSeenMs"]),
            "ms",
            BenchmarkTop(benchmarks, "ttdSeenMs"),
            TrackerAccentGreen));
        gauges.Children.Add(BuildAimGauge(
            "反应时间",
            GetMetricNumber(aim?["reactionTimeMs"]),
            "ms",
            BenchmarkTop(benchmarks, "reactionTimeMs"),
            TrackerAccentGreen));
        gauges.Children.Add(BuildAimGauge(
            "命中率",
            GetMetricNumber(aim?["accuracyPct"]),
            "%",
            BenchmarkTop(benchmarks, "accuracyPct"),
            TrackerAccentYellow));
        gauges.Children.Add(BuildAimGauge(
            "爆头命中率",
            GetMetricNumber(aim?["headAccuracyPct"]),
            "%",
            BenchmarkTop(benchmarks, "headAccuracyPct"),
            TrackerAccentGreen));
        ArrangeColumnsInOrder(gauges);
        content.Children.Add(gauges);

        var lower = CreateEqualGrid(3, 8);
        lower.Children.Add(BuildAimSmallStat(
            "预瞄",
            Number(GetMetricNumber(aim?["preaimDeg"]), "F1", "°"),
            BenchmarkTop(benchmarks, "preaimDeg"),
            TrackerAccentGreen));
        lower.Children.Add(BuildAimSmallStat(
            "急停",
            Percent(GetMetricNumber(aim?["counterStrafePct"])),
            BenchmarkTop(benchmarks, "counterStrafePct"),
            TrackerAccentGreen));
        lower.Children.Add(BuildAimSmallStat(
            "扫射",
            Percent(GetMetricNumber(aim?["sprayAccuracyPct"])),
            BenchmarkTop(benchmarks, "sprayAccuracyPct"),
            TrackerAccentGreen));
        ArrangeColumnsInOrder(lower);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeBrush("AuroraGlassBorderBrush")
        });
        content.Children.Add(lower);

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildTrackerStatsCard(JsonObject? aimStats)
    {
        var aim = GetNode(aimStats ?? new JsonObject(), "aim") as JsonObject;
        var benchmarks = GetNode(aimStats ?? new JsonObject(), "benchmarks") as JsonObject;
        var matches = GetMetricNumber(aimStats?["matches"]);
        var rounds = GetMetricNumber(aimStats?["rounds"]);
        var content = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri($"{TrackerAssetRoot}/images/nav/tracker-fill-big.png")),
            Width = 20,
            Height = 20
        });
        title.Children.Add(new TextBlock
        {
            Text = "CS2TRACKER 数据",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 90,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        header.Children.Add(title);
        var sample = new TextBlock
        {
            Text = string.Join(
                " · ",
                new[]
                {
                    matches is null ? null : $"最近 {matches.Value:F0} 场",
                    rounds is null ? null : $"{rounds.Value:F0} 回合"
                }.Where(text => text is not null)),
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };
        Grid.SetColumn(sample, 1);
        header.Children.Add(sample);
        content.Children.Add(header);

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var column = 0; column < 4; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var row = 0; row < 2; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var details = new (string Label, double? Value, string Suffix, string Benchmark, string Color, int Column, int Row)[]
        {
            ("预瞄", GetMetricNumber(aim?["preaimDeg"]), "°", "preaimDeg", TrackerAccentPink, 0, 0),
            ("伤害速度", GetMetricNumber(aim?["ttdSeenMs"]), "ms", "ttdSeenMs", TrackerAccentYellow, 1, 0),
            ("反应时间", GetMetricNumber(aim?["reactionTimeMs"]), "ms", "reactionTimeMs", TrackerAccentYellow, 2, 0),
            ("命中率", GetMetricNumber(aim?["accuracyPct"]), "%", "accuracyPct", TrackerAccentBlue, 3, 0),
            ("爆头命中率", GetMetricNumber(aim?["headAccuracyPct"]), "%", "headAccuracyPct", TrackerAccentYellow, 0, 1),
            ("扫射命中率", GetMetricNumber(aim?["sprayAccuracyPct"]), "%", "sprayAccuracyPct", TrackerAccentYellow, 1, 1),
            ("急停", GetMetricNumber(aim?["counterStrafePct"]), "%", "counterStrafePct", TrackerAccentYellow, 2, 1)
        };
        foreach (var detail in details)
        {
            var card = BuildDetailedAimMetric(
                detail.Label,
                detail.Value,
                detail.Suffix,
                BenchmarkTop(benchmarks, detail.Benchmark),
                detail.Color);
            Grid.SetColumn(card, detail.Column);
            Grid.SetRow(card, detail.Row);
            grid.Children.Add(card);
        }
        content.Children.Add(grid);

        var flick = GetMetricNumber(aimStats?["flickAccuracyPct"]) ??
            GetMetricNumber(aimStats?["flickAccuracy"]) ??
            ReadFlickAccuracy(aimStats);
        var flickHeader = new Grid();
        flickHeader.ColumnDefinitions.Add(new ColumnDefinition());
        flickHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        flickHeader.Children.Add(new TextBlock
        {
            Text = "甩枪方向准确率",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 70,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        var flickValue = new TextBlock
        {
            Text = flick is null ? "—" : $"{flick.Value:F0}%",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        };
        Grid.SetColumn(flickValue, 1);
        flickHeader.Children.Add(flickValue);
        var expander = new Expander
        {
            Header = flickHeader,
            Content = BuildFlickDirections(aimStats)
        };
        content.Children.Add(expander);

        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildScopeCard(JsonObject root, string steamId)
    {
        var display = GetNode(root, "scopegg_display") as JsonObject ??
            GetNode(root, "scopegg_partner") as JsonObject;
        var timeToDamage = GetNode(display ?? new JsonObject(), "time_to_damage") as JsonObject;
        var percentiles = GetNode(root, "scopegg_percentiles") as JsonObject;
        var average = GetMetricNumber(timeToDamage?["avg_sec"]);
        var current = GetMetricNumber(timeToDamage?["current_sec"]);
        var fastest = GetMetricNumber(timeToDamage?["fastest_sec"]) ??
            GetMetricNumber(timeToDamage?["min_sec"]);
        var fastPercent = GetMetricNumber(timeToDamage?["fast_percent"]);
        var suspicious = GetMetricNumber(timeToDamage?["suspicious_count"]);
        var inhuman = GetMetricNumber(timeToDamage?["inhuman_count"]);
        var content = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri($"{TrackerAssetRoot}/images/stats/scope-icon.webp")),
            Width = 16,
            Height = 16
        });
        title.Children.Add(new TextBlock
        {
            Text = "Scope.gg 数据",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        header.Children.Add(title);
        var link = CreateSmallLinkButton(
            "查看资料",
            $"https://app.scope.gg/profile/{steamId}",
            "\uE8A7");
        Grid.SetColumn(link, 1);
        header.Children.Add(link);
        content.Children.Add(header);

        var metrics = CreateEqualGrid(6, 10);
        metrics.Children.Add(BuildScopeMetric(
            "平均 TTD",
            ScopeDuration(average),
            ScopeTopPercent(percentiles, "avg_ttd_sec"),
            55));
        metrics.Children.Add(BuildScopeMetric(
            "当前 TTD",
            ScopeDuration(current),
            ScopeTopPercent(percentiles, "current_ttd_sec", "current_sec"),
            55));
        metrics.Children.Add(BuildScopeMetric(
            "最快",
            ScopeDuration(fastest),
            ScopeTopPercent(percentiles, "fastest_ttd_sec"),
            60));
        metrics.Children.Add(BuildScopeMetric(
            "快速回合",
            fastPercent is null ? "—" : $"{fastPercent.Value:F0}%",
            ScopeTopPercent(percentiles, "fast_ttd_percent"),
            20));
        metrics.Children.Add(BuildScopeMetric(
            "可疑",
            suspicious is null ? "—" : suspicious.Value.ToString("F0", CultureInfo.InvariantCulture),
            null,
            null));
        metrics.Children.Add(BuildScopeMetric(
            "非人类",
            inhuman is null ? "—" : inhuman.Value.ToString("F0", CultureInfo.InvariantCulture),
            null,
            null));
        ArrangeColumnsInOrder(metrics);
        content.Children.Add(metrics);
        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildCollectiblesCard(JsonObject root)
    {
        var coordinator = GetNode(root, "game_coordinator") as JsonObject;
        var items = GetNode(coordinator ?? new JsonObject(), "collectibles") as JsonArray;
        var images = new List<string>();
        if (items is not null)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                var image = FirstNonEmpty(
                    GetString(item, "img_url"),
                    GetString(item, "image_url"),
                    GetString(item, "url"));
                if (image.Length > 0 && Uri.TryCreate(image, UriKind.Absolute, out _))
                {
                    images.Add(image);
                }
            }
        }
        var featured = GetNode(coordinator ?? new JsonObject(), "featured_collectible") as JsonObject;
        var featuredImage = featured is null
            ? null
            : FirstNonEmpty(GetString(featured, "img_url"), GetString(featured, "image_url"));
        if (!string.IsNullOrWhiteSpace(featuredImage) &&
            Uri.TryCreate(featuredImage, UriKind.Absolute, out _) &&
            !images.Contains(featuredImage))
        {
            images.Insert(0, featuredImage);
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "收藏品",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        if (images.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "暂无收藏品。",
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var imageUrl in images.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
            {
                row.Children.Add(new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(15),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                    Background = ThemeBrush("AuroraPaneBrush"),
                    Child = new Image
                    {
                        Source = new BitmapImage(new Uri(imageUrl)),
                        Stretch = Stretch.UniformToFill
                    }
                });
            }
            content.Children.Add(row);
        }
        return OverviewCard(content, new Thickness(14));
    }

    private Border BuildRecentMatchesCard(JsonObject root)
    {
        var matches = GetNode(root, "csstatsgg", "recent_matches") as JsonArray;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "最近比赛",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        var games = GetNode(root, "leetify", "games") as JsonArray;
        if ((matches is null || matches.Count == 0) &&
            (games is null || games.Count == 0))
        {
            content.Children.Add(new TextBlock
            {
                Text = "暂无最近比赛。",
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
            return OverviewCard(content, new Thickness(14));
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        IEnumerable<JsonObject> recentSources = matches?.OfType<JsonObject>() ?? [];
        if (!recentSources.Any() && games is not null)
        {
            recentSources = games
                .OfType<JsonObject>()
                .OrderByDescending(game => GetString(game, "gameFinishedAt") ?? string.Empty)
                .Select(game =>
                {
                    var scores = game["scores"] as JsonArray;
                    return new JsonObject
                    {
                        ["map"] = GetString(game, "mapName"),
                        ["score_for"] = scores is { Count: >= 2 } ? GetMetricNumber(scores[0]) : null,
                        ["score_against"] = scores is { Count: >= 2 } ? GetMetricNumber(scores[1]) : null,
                        ["date"] = GetString(game, "gameFinishedAt")
                    };
                });
        }
        foreach (var match in recentSources.Take(60))
        {
            row.Children.Add(BuildRecentMatchTile(match));
        }
        var scroll = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
        var container = new Grid();
        container.Children.Add(scroll);
        var previous = CreateScrollButton("\uE76B", false, scroll);
        previous.HorizontalAlignment = HorizontalAlignment.Left;
        previous.VerticalAlignment = VerticalAlignment.Center;
        container.Children.Add(previous);
        var next = CreateScrollButton("\uE76C", true, scroll);
        next.HorizontalAlignment = HorizontalAlignment.Right;
        next.VerticalAlignment = VerticalAlignment.Center;
        container.Children.Add(next);
        content.Children.Add(container);
        return OverviewCard(content, new Thickness(14));
    }

    private Border? BuildWeaponsCard(JsonObject root)
    {
        var stats = GetNode(root, "steam_userstats_730", "stats") as JsonObject;
        var definitions = GetNode(root, "csstatsgg", "weapons", "weapons") as JsonArray;
        var usages = new Dictionary<string, WeaponUsage>(StringComparer.OrdinalIgnoreCase);
        if (definitions is not null)
        {
            foreach (var definition in definitions.OfType<JsonObject>())
            {
                var id = NormalizeWeaponId(
                    GetString(definition, "icon_name") ??
                    GetString(definition, "name"));
                if (id is null)
                {
                    continue;
                }
                var kills = GetMetricNumber(definition["kills"]) ??
                    GetMetricNumber(stats?[$"total_kills_{id}"]) ?? 0;
                var shots = GetMetricNumber(stats?[$"total_shots_{id}"]);
                var hits = GetMetricNumber(stats?[$"total_hits_{id}"]);
                double? accuracy = hits is not null && shots is > 0
                    ? hits.Value / shots.Value * 100
                    : null;
                usages[id] = new WeaponUsage(
                    id,
                    WeaponDisplayName(id, GetString(definition, "name")),
                    kills,
                    shots,
                    hits,
                    accuracy);
            }
        }
        if (stats is not null)
        {
            foreach (var pair in stats)
            {
                if (!pair.Key.StartsWith("total_kills_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var id = NormalizeWeaponId(pair.Key["total_kills_".Length..]);
                if (id is null or "headshot" or "enemy_weapon" or "against_zoomed_sniper")
                {
                    continue;
                }
                var kills = GetMetricNumber(pair.Value) ?? 0;
                if (!usages.TryGetValue(id, out var existing) || kills > existing.Kills)
                {
                    var shots = GetMetricNumber(stats[$"total_shots_{id}"]);
                    var hits = GetMetricNumber(stats[$"total_hits_{id}"]);
                    double? accuracy = hits is not null && shots is > 0
                        ? hits.Value / shots.Value * 100
                        : null;
                    usages[id] = new WeaponUsage(
                        id,
                        WeaponDisplayName(id, null),
                        kills,
                        shots,
                        hits,
                        accuracy);
                }
            }
        }
        var weapons = usages.Values
            .Where(weapon => weapon.Kills > 0)
            .OrderByDescending(weapon => weapon.Kills)
            .ToArray();
        if (weapons.Length == 0)
        {
            return null;
        }

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = "武器表现",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        var maxKills = Math.Max(1, weapons.Max(weapon => weapon.Kills));
        var maxShots = Math.Max(1, weapons.Max(weapon => weapon.Shots ?? 0));
        var maxHits = Math.Max(1, weapons.Max(weapon => weapon.Hits ?? 0));
        var header = BuildWeaponRow(null, "武器", "击杀", "射击", "命中", "命中率", isHeader: true);
        content.Children.Add(header);
        var rows = new StackPanel { Spacing = 4 };
        foreach (var weapon in weapons)
        {
            var row = BuildWeaponRow(
                weapon,
                weapon.Name,
                FormatWhole(weapon.Kills),
                FormatWhole(weapon.Shots),
                FormatWhole(weapon.Hits),
                PremierPercent(weapon.Accuracy),
                maxKills,
                maxShots,
                maxHits);
            row.Height = 40;
            rows.Children.Add(row);
        }
        content.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 348,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled
        });
        return OverviewCard(content, new Thickness(14));
    }

    private Border OverviewCard(UIElement child, Thickness padding) =>
        new()
        {
            Padding = padding,
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("CustomUiDialogBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = child
        };

    private TextBlock OverviewLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };

    private static Grid CreateEqualGrid(int columns, double spacing)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        for (var index = 0; index < columns; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        return grid;
    }

    private static void ArrangeColumnsInOrder(Grid grid)
    {
        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, index);
            }
        }
    }

    private FontIcon SmallFeedbackIcon(string glyph) =>
        new()
        {
            Glyph = glyph,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };

    private Button CreateSourceButton(string imageUrl, string tooltip, string linkUrl)
    {
        var image = new Image
        {
            Source = imageUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                ? new BitmapImage(new Uri(imageUrl))
                : new BitmapImage(new Uri(imageUrl)),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        };
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Background = ThemeBrush("AuroraGlassButtonBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Content = image
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += async (_, _) => await AppState.OpenUrlAsync(linkUrl);
        return button;
    }

    private Button CreateSmallLinkButton(string text, string url, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 10,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        var button = new Button
        {
            Padding = new Thickness(7, 3, 7, 3),
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("AuroraGlassButtonBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Content = content
        };
        button.Click += async (_, _) => await AppState.OpenUrlAsync(url);
        return button;
    }

    private HyperlinkButton CreateTextLink(string text, string url)
    {
        var link = new HyperlinkButton
        {
            Content = text,
            Padding = new Thickness(0),
            FontSize = 10
        };
        link.Click += async (_, _) => await AppState.OpenUrlAsync(url);
        return link;
    }

    private Grid CreateRingGauge(
        double percent,
        string value,
        string suffix,
        string colorHex,
        string? iconUri,
        double size,
        double valueFontSize,
        double averageMarker)
    {
        var container = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var canvas = new Canvas { Width = size, Height = size };
        var center = size / 2;
        var radius = size / 2 - 5;
        const double startAngle = 128;
        const double arcSweep = 281;
        var thickness = size >= 100 ? 5 : 4;
        canvas.Children.Add(CreateArc(
            center,
            center,
            radius,
            startAngle,
            arcSweep,
            new SolidColorBrush(ColorFrom("#303744")),
            thickness));
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped > 0)
        {
            canvas.Children.Add(CreateArc(
                center,
                center,
                radius,
                startAngle,
                arcSweep * clamped / 100,
                new SolidColorBrush(ColorFrom(colorHex)),
                thickness));
        }
        if (averageMarker is >= 0 and <= 100)
        {
            canvas.Children.Add(CreateGaugeThresholdTick(
                center,
                radius,
                startAngle,
                arcSweep,
                averageMarker,
                size >= 100 ? 2.2 : 1.8));
        }
        container.Children.Add(canvas);
        container.Children.Add(new Border
        {
            Width = 2,
            Height = 3,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(ColorFrom(colorHex)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var centerContent = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        valuePanel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = valueFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush")
        });
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            valuePanel.Children.Add(new TextBlock
            {
                Text = suffix,
                FontSize = Math.Max(10, valueFontSize * 0.42),
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 0, 3)
            });
        }
        centerContent.Children.Add(valuePanel);
        if (!string.IsNullOrWhiteSpace(iconUri))
        {
            centerContent.Children.Add(new Image
            {
                Source = new SvgImageSource(new Uri(iconUri, UriKind.Absolute)),
                Width = size >= 100 ? 19 : 14,
                Height = size >= 100 ? 19 : 14,
                Stretch = Stretch.Uniform,
                Opacity = 0.9,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        container.Children.Add(centerContent);
        return container;
    }

    private static Microsoft.UI.Xaml.Shapes.Line CreateGaugeThresholdTick(
        double center,
        double radius,
        double startAngle,
        double arcSweep,
        double markerPercent,
        double thickness)
    {
        var angle = startAngle + arcSweep * Math.Clamp(markerPercent, 0, 100) / 100;
        var radians = angle * Math.PI / 180;
        var innerRadius = radius - 4;
        var outerRadius = radius + 3;
        return new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = center + innerRadius * Math.Cos(radians),
            Y1 = center + innerRadius * Math.Sin(radians),
            X2 = center + outerRadius * Math.Cos(radians),
            Y2 = center + outerRadius * Math.Sin(radians),
            Stroke = new SolidColorBrush(ColorFrom("#F2FFFFFF")),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    private static Microsoft.UI.Xaml.Shapes.Path CreateArc(
        double centerX,
        double centerY,
        double radius,
        double startAngle,
        double sweepAngle,
        Brush stroke,
        double thickness)
    {
        static Point PointAt(double cx, double cy, double r, double angle)
        {
            var radians = angle * Math.PI / 180;
            return new Point(cx + r * Math.Cos(radians), cy + r * Math.Sin(radians));
        }

        var start = PointAt(centerX, centerY, radius, startAngle);
        var end = PointAt(centerX, centerY, radius, startAngle + sweepAngle);
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepAngle > 180
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    private Border BuildSimpleValue(
        string label,
        string value,
        string? colorHex = null,
        double valueSize = 15)
    {
        var content = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var labelText = OverviewLabel(label);
        labelText.HorizontalAlignment = HorizontalAlignment.Stretch;
        labelText.TextAlignment = TextAlignment.Center;
        labelText.TextWrapping = TextWrapping.WrapWholeWords;
        labelText.MaxLines = 2;
        content.Children.Add(labelText);
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = valueSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = colorHex is null
                ? ThemeBrush("AuroraTextHeadingBrush")
                : new SolidColorBrush(ColorFrom(colorHex)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2
        });
        return new Border
        {
            Padding = new Thickness(1, 2, 1, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
    }

    private Border BuildAimGauge(
        string label,
        double? value,
        string suffix,
        double? topPercent,
        string colorHex)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        content.Children.Add(CreateRingGauge(
            topPercent is null ? 0 : 100 - topPercent.Value,
            value is null ? "—" : Math.Round(value.Value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture),
            suffix,
            colorHex,
            null,
            88,
            20,
            50));
        content.Children.Add(new TextBlock
        {
            Text = topPercent is null ? "—" : $"前 {FormatTopPercent(topPercent.Value)}",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        content.Children.Add(CreateAimTierStrip(topPercent));
        return new Border { Padding = new Thickness(3), Child = content };
    }

    private Border BuildAimSmallStat(
        string label,
        string value,
        double? topPercent,
        string colorHex)
    {
        var stack = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom(colorHex))
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = topPercent is null ? "—" : $"前 {FormatTopPercent(topPercent.Value)}",
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        return new Border { Padding = new Thickness(4, 2, 4, 2), Child = stack };
    }

    private Border BuildDetailedAimMetric(
        string label,
        double? value,
        string suffix,
        double? topPercent,
        string colorHex)
    {
        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(OverviewLabel(label));
        var valueLine = new StackPanel { Orientation = Orientation.Horizontal };
        valueLine.Children.Add(new TextBlock
        {
            Text = value is null ? "—" : value.Value.ToString("0.#", CultureInfo.InvariantCulture),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom(colorHex))
        });
        if (!string.IsNullOrWhiteSpace(suffix) && value is not null)
        {
            valueLine.Children.Add(new TextBlock
            {
                Text = suffix,
                FontSize = 9,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 0, 2)
            });
        }
        content.Children.Add(valueLine);
        content.Children.Add(CreateSegmentBar(
            topPercent is null ? 0 : 100 - topPercent.Value,
            colorHex));
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(CreatePill(
            topPercent is null ? "TOP —" : $"TOP {FormatTopPercent(topPercent.Value)}",
            8));
        var tier = CreateAimTierStrip(topPercent);
        tier.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(tier, 1);
        footer.Children.Add(tier);
        content.Children.Add(footer);
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            Background = ThemeBrush("AuroraPaneBrush"),
            Child = content
        };
    }

    private Grid CreateSegmentBar(double percent, string colorHex)
    {
        const int segments = 10;
        var grid = new Grid { ColumnSpacing = 5, Height = 4 };
        var filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * segments);
        for (var index = 0; index < segments; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var segment = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = index < filled
                    ? new SolidColorBrush(ColorFrom(colorHex))
                    : ThemeBrush("AuroraGlassBorderBrush")
            };
            Grid.SetColumn(segment, index);
            grid.Children.Add(segment);
        }
        return grid;
    }

    private static Border CreateTierStrip(double? rating)
    {
        var (color, text) = rating switch
        {
            >= 95 => ("yellow", "35,000"),
            >= 75 => ("red", "29,750"),
            >= 50 => ("dark_blue", "11,750"),
            _ => ("gray", "—")
        };
        var grid = new Grid { Width = 62, Height = 22 };
        grid.Children.Add(new Image
        {
            Source = new SvgImageSource(new Uri(
                $"ms-appx:///Assets/Cs2Tracker/premier_rank_{color}.svg",
                UriKind.Absolute)),
            Stretch = Stretch.Fill
        });
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom("#E6ECFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = grid
        };
    }

    private static Border CreateAimTierStrip(double? topPercent)
    {
        var (color, text) = topPercent switch
        {
            <= 10 => ("yellow", "30K+"),
            <= 30 => ("pink", "20K–25K"),
            _ => (null, (string?)null)
        };
        if (color is null || text is null)
        {
            return new Border { Height = 18 };
        }
        var grid = new Grid { Width = 60, Height = 18 };
        grid.Children.Add(new Image
        {
            Source = new SvgImageSource(new Uri(
                $"ms-appx:///Assets/Cs2Tracker/premier_rank_{color}.svg",
                UriKind.Absolute)),
            Stretch = Stretch.Fill
        });
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 7.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom("#E6ECFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border { Child = grid };
    }

    private Border BuildScopeMetric(
        string label,
        string value,
        double? topPercent,
        double? thresholdPercent)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = topPercent is null ? " " : $"Top {FormatTopPercent(topPercent.Value)}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(CreateScopeRatingBar(topPercent, thresholdPercent));
        return new Border
        {
            Padding = new Thickness(2, 0, 2, 0),
            Child = content
        };
    }

    private Grid CreateScopeRatingBar(double? topPercent, double? thresholdPercent)
    {
        var track = new Grid
        {
            Height = 10,
            Margin = new Thickness(0, 4, 0, 0)
        };
        track.Children.Add(new Border
        {
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ColorFrom("#1AFFFFFF"))
        });
        var fill = new Border
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false
        };
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop
        {
            Color = ColorFrom("#3B82F6"),
            Offset = 0
        });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = ColorFrom("#1D4ED8"),
            Offset = 0.5
        });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = ColorFrom("#3B82F6"),
            Offset = 1
        });
        fill.Background = gradient;
        track.Children.Add(fill);

        var threshold = new Border
        {
            Width = 2,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(ColorFrom("#99FFFFFF")),
            IsHitTestVisible = false,
            Visibility = thresholdPercent is null ? Visibility.Collapsed : Visibility.Visible
        };
        track.Children.Add(threshold);
        track.SizeChanged += (_, args) =>
        {
            var width = args.NewSize.Width;
            var fillPercent = topPercent is null
                ? 0
                : Math.Clamp(100 - topPercent.Value, 0, 100);
            fill.Width = width * fillPercent / 100;
            if (thresholdPercent is not null)
            {
                threshold.Margin = new Thickness(
                    Math.Clamp(width * thresholdPercent.Value / 100 - 1, 0, Math.Max(0, width - 2)),
                    0,
                    0,
                    0);
            }
        };
        return track;
    }

    private static double? ScopeTopPercent(JsonObject? percentiles, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetMetricNumber(GetNode(percentiles ?? new JsonObject(), key, "topPercent"));
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string ScopeDuration(double? seconds) =>
        seconds is null
            ? "—"
            : $"{Math.Round(seconds.Value * 1000, MidpointRounding.AwayFromZero):F0}ms";

    private UIElement BuildFlickDirections(JsonObject? aimStats)
    {
        var directions = GetNode(aimStats ?? new JsonObject(), "flickDirections") as JsonObject;
        if (directions is null || directions.Count == 0)
        {
            return new TextBlock
            {
                Text = "暂无甩枪方向数据。",
                FontSize = 10,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            };
        }

        var labels = new Dictionary<string, string>
        {
            ["E"] = "右",
            ["NE"] = "右上",
            ["N"] = "上",
            ["NW"] = "左上",
            ["W"] = "左",
            ["SW"] = "左下",
            ["S"] = "下",
            ["SE"] = "右下"
        };
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var column = 0; column < 4; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var index = 0;
        foreach (var pair in directions)
        {
            if (pair.Value is not JsonObject values)
            {
                continue;
            }
            var accuracy = GetMetricNumber(values["accPct"]);
            var item = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                Background = ThemeBrush("AuroraPaneBrush"),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = labels.GetValueOrDefault(pair.Key, pair.Key),
                            FontSize = 9,
                            Foreground = ThemeBrush("AuroraTextMutedBrush")
                        },
                        new TextBlock
                        {
                            Text = accuracy is null ? "—" : $"{accuracy.Value:F0}%",
                            FontSize = 14,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = ThemeBrush("AuroraTextHeadingBrush")
                        }
                    }
                }
            };
            Grid.SetColumn(item, index % 4);
            Grid.SetRow(item, index / 4);
            grid.Children.Add(item);
            index++;
        }
        return grid;
    }

    private static Border CreateResultIcon(string? result, double size)
    {
        var image = result switch
        {
            "W" => "ms-appx:///Assets/Cs2Tracker/win.svg",
            "L" => "ms-appx:///Assets/Cs2Tracker/lose.svg",
            _ => "ms-appx:///Assets/Cs2Tracker/win.svg"
        };
        return new Border
        {
            Width = size,
            Height = size,
            Opacity = result is null ? 0.18 : 1,
            Child = new Image
            {
                Source = new SvgImageSource(new Uri(image, UriKind.Absolute)),
                Stretch = Stretch.Uniform
            }
        };
    }

    private Button CreateScrollButton(string glyph, bool forward, ScrollViewer scroll)
    {
        var button = new Button
        {
            Width = 29,
            Height = 48,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(ColorFrom("#B305080F")),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 11,
                Foreground = ThemeBrush("AuroraTextHeadingBrush")
            }
        };
        button.Click += (_, _) =>
        {
            var target = scroll.HorizontalOffset + (forward ? 520 : -520);
            scroll.ChangeView(Math.Max(0, target), null, null, disableAnimation: true);
        };
        return button;
    }

    private Border BuildRecentMatchTile(JsonObject match)
    {
        var scoreFor = GetMetricNumber(match["score_for"]) ?? 0;
        var scoreAgainst = GetMetricNumber(match["score_against"]) ?? 0;
        var won = scoreFor > scoreAgainst;
        var draw = Math.Abs(scoreFor - scoreAgainst) < 0.01;
        var resultColor = won
            ? ColorFrom(TrackerAccentGreen)
            : draw ? ColorFrom(TrackerMuted) : ColorFrom(TrackerAccentRed);
        var mapName = GetString(match, "map");
        var grid = new Grid();
        grid.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(GetMapThumbnailUri(mapName))),
            Stretch = Stretch.UniformToFill,
            Opacity = 0.45
        });
        var shade = new Border
        {
            Background = new SolidColorBrush(ColorFrom("#85070B13"))
        };
        grid.Children.Add(shade);
        var text = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = $"{scoreFor:F0}-{scoreAgainst:F0}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(resultColor),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        text.Children.Add(new TextBlock
        {
            Text = MapDisplayName(mapName) ?? "未知地图",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 50,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        text.Children.Add(new TextBlock
        {
            Text = FormatTrackerDate(GetString(match, "date")),
            FontSize = 8,
            Foreground = ThemeBrush("AuroraTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        grid.Children.Add(text);
        return new Border
        {
            Width = 178,
            Height = 70,
            CornerRadius = new CornerRadius(8),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("AuroraPaneBrush"),
            Child = grid
        };
    }

    private Grid BuildWeaponRow(
        WeaponUsage? weapon,
        string name,
        string kills,
        string shots,
        string hits,
        string accuracy,
        double maxKills = 1,
        double maxShots = 1,
        double maxHits = 1,
        bool isHeader = false)
    {
        var grid = new Grid { ColumnSpacing = 56, Padding = new Thickness(6, 5, 6, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(2, GridUnitType.Star)
        });
        for (var index = 0; index < 3; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (!isHeader && weapon is not null)
        {
            var imageSource = CreateWeaponImageSource(weapon.Id);
            if (imageSource is not null)
            {
                namePanel.Children.Add(new Image
                {
                    Source = imageSource,
                    Width = 44,
                    Height = 20,
                    Stretch = Stretch.Uniform
                });
            }
        }
        namePanel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = isHeader ? 9 : 11,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isHeader
                ? ThemeBrush("AuroraTextMutedBrush")
                : ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        grid.Children.Add(namePanel);
        AddWeaponValue(
            grid,
            kills,
            1,
            isHeader,
            isHeader ? null : WeaponBarPercent(weapon?.Kills, maxKills));
        AddWeaponValue(
            grid,
            shots,
            2,
            isHeader,
            isHeader ? null : WeaponBarPercent(weapon?.Shots, maxShots));
        AddWeaponValue(
            grid,
            hits,
            3,
            isHeader,
            isHeader ? null : WeaponBarPercent(weapon?.Hits, maxHits));
        AddWeaponAccuracyCell(grid, accuracy, weapon?.Accuracy, 4, isHeader);
        return grid;
    }

    private void AddWeaponValue(
        Grid grid,
        string value,
        int column,
        bool isHeader,
        double? barPercent = null)
    {
        var content = new StackPanel
        {
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = value,
            FontSize = isHeader ? 9 : 11,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isHeader
                ? ThemeBrush("AuroraTextMutedBrush")
                : ThemeBrush("AuroraTextBodyBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left
        };
        content.Children.Add(text);
        if (!isHeader && barPercent is not null)
        {
            content.Children.Add(CreateWeaponProgressBar(barPercent.Value));
        }

        Grid.SetColumn(content, column);
        grid.Children.Add(content);
    }

    private static Grid CreateWeaponProgressBar(double percent)
    {
        var track = new Grid { Height = 3 };
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(ColorFrom("#14FFFFFF"))
        });
        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(ColorFrom(TrackerAccentBlue))
        };
        track.Children.Add(fill);
        track.SizeChanged += (_, args) =>
        {
            fill.Width = args.NewSize.Width * Math.Clamp(percent, 0, 100) / 100;
        };
        return track;
    }

    private void AddWeaponAccuracyCell(
        Grid grid,
        string value,
        double? accuracy,
        int column,
        bool isHeader)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = isHeader ? 9 : 11,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isHeader
                ? ThemeBrush("AuroraTextMutedBrush")
                : ThemeBrush("AuroraTextBodyBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!isHeader && accuracy is not null)
        {
            content.Children.Add(CreateWeaponAccuracyGauge(accuracy.Value));
        }

        Grid.SetColumn(content, column);
        grid.Children.Add(content);
    }

    private Canvas CreateWeaponAccuracyGauge(double accuracy)
    {
        var canvas = new Canvas
        {
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var background = new Ellipse
        {
            Width = 24,
            Height = 24,
            Stroke = new SolidColorBrush(ColorFrom("#14FFFFFF")),
            StrokeThickness = 3
        };
        Canvas.SetLeft(background, 3);
        Canvas.SetTop(background, 3);
        canvas.Children.Add(background);

        var sweep = Math.Clamp(accuracy, 0, 100) * 3.6;
        if (sweep > 0)
        {
            canvas.Children.Add(CreateArc(
                15,
                15,
                12,
                -90,
                Math.Min(359.9, sweep),
                new SolidColorBrush(ColorFrom(TrackerAccentBlue)),
                3));
        }

        return canvas;
    }

    private static double? WeaponBarPercent(double? value, double maximum) =>
        value is null || maximum <= 0
            ? null
            : Math.Clamp(value.Value / maximum * 100, 0, 100);

    private static ImageSource? CreateWeaponImageSource(string id)
    {
        var normalized = NormalizeWeaponId(id);
        if (normalized is null)
        {
            return null;
        }
        if (normalized == "m4a4")
        {
            normalized = "m4a1";
        }
        var catalog = FindCatalogWeapon(normalized);
        if (catalog is not null &&
            File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "weapons", $"{catalog.IconFile}.svg")))
        {
            return new SvgImageSource(new Uri(catalog.IconUri, UriKind.Absolute));
        }
        var supported = normalized is
            "ak47" or "aug" or "awp" or "bizon" or "cz75a" or "deagle" or "decoy" or
            "elite" or "famas" or "fiveseven" or "flashbang" or "g3sg1" or "galilar" or
            "glock" or "hegrenade" or "hkp2000" or "incgrenade" or "knife" or "m249" or
            "m4a1" or "m4a1_silencer" or "mac10" or "mag7" or "molotov" or "mp5sd" or
            "mp7" or "mp9" or "negev" or "nova" or "p250" or "p90" or "revolver" or
            "sawedoff" or "scar20" or "sg556" or "smokegrenade" or "ssg08" or "taser" or
            "tec9" or "ump45" or "usp_silencer" or "xm1014";
        return supported
            ? new SvgImageSource(new Uri($"{TrackerAssetRoot}/demo-viewer/icons/{normalized}.svg"))
            : null;
    }

    private static double? LeetifyPercentile(JsonObject root, string key) =>
        GetMetricNumber(GetNode(root, "leetify_skill_percentiles", key, "topPercent"));

    private static double? BenchmarkTop(JsonObject? benchmarks, string key)
    {
        var node = benchmarks?[key];
        return GetMetricNumber(node) ??
            (benchmarks is null ? null : GetMetricNumber(benchmarks, key, "topPct")) ??
            (benchmarks is null ? null : GetMetricNumber(benchmarks, key, "topPercent")) ??
            (benchmarks is null ? null : GetMetricNumber(benchmarks, key, "rank"));
    }

    private static double? ReadFlickAccuracy(JsonObject? aimStats)
    {
        var directions = GetNode(aimStats ?? new JsonObject(), "flickDirections") as JsonObject;
        if (directions is null)
        {
            return null;
        }
        var shots = 0d;
        var hits = 0d;
        foreach (var direction in directions)
        {
            if (direction.Value is not JsonObject values)
            {
                continue;
            }
            shots += GetMetricNumber(values["shots"]) ?? 0;
            hits += GetMetricNumber(values["hits"]) ?? 0;
        }
        return shots <= 0 ? null : hits / shots * 100;
    }

    private static string ScaleSignedRating(double? value) =>
        value is null
            ? "—"
            : $"{(value.Value * 100 > 0 ? "+" : string.Empty)}{value.Value * 100:F2}";

    private static string FormatTopPercent(double value) =>
        value < 0.01
            ? $"{value:F3}%"
            : value < 0.1
                ? $"{value:F2}%"
                : value < 10
                    ? $"{value:F1}%"
                    : $"{value:F0}%";

    private static string FormatWhole(double? value) =>
        value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatWhole(int? value) =>
        value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private TextBlock CreateAvatarFallback(string name)
    {
        var initial = string.IsNullOrWhiteSpace(name)
            ? "?"
            : StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant();
        return new TextBlock
        {
            Text = initial,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static string NormalizeSteamImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        if (!string.Equals(uri.Host, "avatars.steamstatic.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "avatars.akamai.steamstatic.com"
        };
        return builder.Uri.AbsoluteUri;
    }

    private Border CreatePill(string text, double fontSize = 8)
    {
        return new Border
        {
            Padding = new Thickness(5, 2, 5, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            Background = ThemeBrush("AuroraGlassButtonBrush"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            }
        };
    }

    private Border BuildFaceitHeader(string? nickname, string? steamId)
    {
        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri($"{TrackerAssetRoot}/images/stats/faceit.png")),
            Width = 24,
            Height = 18
        });
        title.Children.Add(new TextBlock
        {
            Text = "FACEIT 数据",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextHeadingBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(title);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(nickname) && !string.IsNullOrWhiteSpace(steamId))
        {
            right.Children.Add(CreateSmallLinkButton(
                "查看资料",
                $"https://www.faceit.com/en/players/{Uri.EscapeDataString(nickname)}",
                "\uE8A7"));
        }
        right.Children.Add(new Border
        {
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(ColorFrom("#FD5A00")),
            Child = new TextBlock
            {
                Text = "CS2",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom("#FFFFFF"))
            }
        });
        right.Children.Add(new Border
        {
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ColorFrom("#FD5A0066")),
            Background = new SolidColorBrush(ColorFrom("#FD5A0012")),
            Child = new TextBlock
            {
                Text = "CSGO",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFrom("#FD6A21"))
            }
        });
        Grid.SetColumn(right, 1);
        header.Children.Add(right);
        return new Border { Child = header };
    }

    private static IReadOnlyList<string> ReadFaceitRecentResults(JsonObject? stats)
    {
        var array = stats?["recent_results"] as JsonArray;
        if (array is null)
        {
            return [];
        }
        var results = new List<string>();
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var normalized = text.Trim().ToUpperInvariant();
                if (normalized is "W" or "WIN" or "1")
                {
                    results.Add("W");
                }
                else if (normalized is "L" or "LOSS" or "0")
                {
                    results.Add("L");
                }
            }
            else if (node is JsonObject result)
            {
                var resultText = GetString(result, "result") ?? GetString(result, "winner");
                if (resultText?.Contains("win", StringComparison.OrdinalIgnoreCase) == true)
                {
                    results.Add("W");
                }
                else if (resultText?.Contains("loss", StringComparison.OrdinalIgnoreCase) == true)
                {
                    results.Add("L");
                }
            }
        }
        return results.Take(5).ToArray();
    }

    private static string FormatFaceitClutches(JsonObject? stats)
    {
        var one = GetMetricNumber(stats?["one_v_one_win_rate"]);
        var two = GetMetricNumber(stats?["one_v_two_win_rate"]);
        if (one is null && two is null)
        {
            return "—";
        }
        static string Convert(double? value) =>
            value is null ? "—" : $"{Math.Round(value.Value * 100):F0}%";
        return $"{Convert(one)} / {Convert(two)}";
    }

    private static string? FormatFaceitDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return null;
        }
        return date.ToLocalTime().ToString("yyyy年M月", CultureInfo.InvariantCulture);
    }

    private static string FormatTrackerDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date)
            ? date.ToLocalTime().ToString("yyyy年M月d日", CultureInfo.InvariantCulture)
            : raw;
    }

    private static string? FormatPeriod(string? from, string? to)
    {
        var start = DateTimeOffset.TryParse(
            from,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var startDate)
            ? startDate.ToLocalTime().ToString("M月d日", CultureInfo.InvariantCulture)
            : null;
        var end = DateTimeOffset.TryParse(
            to,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var endDate)
            ? endDate.ToLocalTime().ToString("M月d日", CultureInfo.InvariantCulture)
            : null;
        return start is null ? null : start == end ? start : $"{start} – {end}";
    }

    private static string GetMapThumbnailUri(string? map)
    {
        var key = (map ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("de_", string.Empty, StringComparison.Ordinal)
            .Replace("cs_", string.Empty, StringComparison.Ordinal);
        var file = key switch
        {
            "ancient" => "de_ancient_1_png.png",
            "anubis" => "de_anubis_1_png.png",
            "cache" => "de_cache_1_png.png",
            "dust2" => "de_dust2_1_png.png",
            "inferno" => "de_inferno_1_png.png",
            "italy" => "cs_italy_1_png.png",
            "mirage" => "de_mirage_1_png.png",
            "nuke" => "de_nuke_1_png.png",
            "office" => "cs_office_1_png.png",
            "overpass" => "de_overpass_1_png.png",
            "poseidon" => "de_poseidon_png.png",
            "train" => "de_train_1_png.png",
            "vertigo" => "de_vertigo_1_png.png",
            "warden" => "de_warden_png.png",
            _ => null
        };
        return file is null
            ? $"{TrackerAssetRoot}/images/stats/map_thumbnails/de_mirage_1_png.png"
            : $"{TrackerAssetRoot}/images/stats/map_thumbnails/{file}";
    }

    private static string? MapDisplayName(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return null;
        }
        return map.Trim()
            .Replace("de_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("cs_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
    }
}
