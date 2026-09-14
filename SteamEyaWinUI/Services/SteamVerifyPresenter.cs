using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 把 /api/verify 的原始快照翻译成界面模型：文案与判定规则对齐核验站前端
/// （status=ok 且无 vac/gameban/tradeban/community/cooldown 标记才算「状态正常」）。
/// 所有文案在此按当前语言生成，语言切换后重新 Build 即可。
/// </summary>
internal static class SteamVerifyPresenter
{
    /// <summary>命中任一标记就不算「正常」，与站点前端的 flags 判定一致。</summary>
    private static readonly string[] AttentionFlags = ["vac", "gameban", "tradeban", "community", "cooldown"];

    public static SteamVerifyReport Build(SteamVerifyPayload payload, Func<SteamVerifyTone, Brush> toneBrush)
    {
        var bans = payload.Bans;
        var cs2 = payload.Cs2;
        var cooldown = cs2?.Cooldown;
        var flags = payload.Flags ?? [];
        var isOk = string.Equals(payload.Status, "ok", StringComparison.OrdinalIgnoreCase)
            && !flags.Any(flag => AttentionFlags.Contains(flag, StringComparer.OrdinalIgnoreCase));

        var stats = new List<SteamVerifyStat>(8)
        {
            BuildCountStat("Login_Verify_Stat_Vac", bans?.VacBans, BanNote(bans, "vac"), toneBrush),
            BuildCountStat("Login_Verify_Stat_Game", bans?.GameBans, BanNote(bans, "game"), toneBrush),
            new()
            {
                Label = Loc.T("Login_Verify_Stat_Cooldown"),
                Value = string.IsNullOrWhiteSpace(cooldown?.Text) ? Loc.T("Login_Verify_Value_Unknown") : cooldown!.Text!,
                ValueBrush = toneBrush(cooldown?.Active == true ? SteamVerifyTone.Warn : SteamVerifyTone.None)
            },
            new()
            {
                Label = Loc.T("Login_Verify_Stat_Prime"),
                Value = cs2?.Prime?.Status is { } prime
                    ? Loc.T(prime ? "Login_Verify_Value_Prime_Yes" : "Login_Verify_Value_Prime_No")
                    : Loc.T("Login_Verify_Value_Unknown"),
                ValueBrush = toneBrush(SteamVerifyTone.None)
            },
            new()
            {
                Label = Loc.T("Login_Verify_Stat_ProfileRank"),
                Value = cs2?.ProfileRank is int rank and > 0
                    ? Loc.Tf("Login_Verify_Value_Level_Format", rank)
                    : Loc.T("Login_Verify_Value_Unknown"),
                Note = cs2?.ProfileRankName,
                ValueBrush = toneBrush(SteamVerifyTone.None)
            },
            new()
            {
                Label = Loc.T("Login_Verify_Stat_Medal"),
                Value = cs2?.ServiceMedal is { } medal
                    ? Loc.T(medal ? "Login_Verify_Value_Yes" : "Login_Verify_Value_No")
                    : Loc.T("Login_Verify_Value_Unknown"),
                ValueBrush = toneBrush(SteamVerifyTone.None)
            },
            BuildBoolStat("Login_Verify_Stat_Trade", bans?.TradeBanned, toneBrush),
            BuildBoolStat("Login_Verify_Stat_Community", bans?.CommunityBanned, toneBrush)
        };

        var records = new List<SteamVerifyRecordRow>();
        if (cs2?.Premier is { } premier)
        {
            records.Add(new SteamVerifyRecordRow
            {
                Map = Loc.T("Login_Verify_Records_Premier"),
                Record = FormatRecord(premier),
                Rank = FormatRank(premier),
                LastMatch = FormatLastMatch(premier.LastMatchAt)
            });
        }

        foreach (var item in cs2?.PerMap ?? [])
        {
            records.Add(new SteamVerifyRecordRow
            {
                Map = string.IsNullOrWhiteSpace(item.Map) ? Loc.T("Login_Verify_Value_Unknown") : item.Map!,
                Record = FormatRecord(item),
                Rank = FormatRank(item),
                LastMatch = FormatLastMatch(item.LastMatchAt)
            });
        }

        var library = payload.Library;
        var games = (library?.Games ?? []).Select(BuildGameEntry).ToList();

        var station = payload.Presence;
        var presenceText = station is null
            ? null
            : BuildPresenceText(station);

        return new SteamVerifyReport
        {
            IsOk = isOk,
            Headline = Loc.T(isOk ? "Login_Verify_Headline_Ok" : "Login_Verify_Headline_Attention"),
            Subline = BuildSubline(payload),
            IsCached = payload.Cached == true,
            PresenceText = presenceText,
            PresenceGlyph = station switch
            {
                null => "\uE7F4",
                { Error: not null } => "\uE7F4",
                { InGame: null } => "\uE7F4",
                { InGame: true } => "\uE7FC",
                _ => "\uE9F5"
            },
            PresenceInGame = station?.InGame == true,
            PresenceUnknown = station is null || station.Error is not null || station.InGame is null,
            Stats = stats,
            CooldownMessage = BuildCooldownMessage(cooldown),
            Records = records,
            LibrarySummary = library is null || !string.IsNullOrWhiteSpace(library.Error)
                ? null
                : BuildLibrarySummary(games),
            LibraryFetchedText = library is null
                ? string.Empty
                : Loc.Tf("Login_Verify_Library_Fetched_Format", FormatDateTime(ParseTime(library.FetchedAt))),
            LibraryGames = games,
            LibraryError = string.IsNullOrWhiteSpace(library?.Error) ? null : library!.Error
        };
    }

    /// <summary>相对视图内最长时长的进度百分比（站点中的 meter 条），供列表行使用。</summary>
    public static IReadOnlyList<SteamVerifyGameRow> BuildGameRows(
        IReadOnlyList<SteamVerifyGameEntry> games,
        bool recentTab,
        string search,
        string sort)
    {
        var keyword = search.Trim();
        var filtered = games
            .Where(game => !recentTab || game.RecentMinutes > 0)
            .Where(game => keyword.Length == 0
                || game.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || game.AppId.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.Ordinal))
            .ToList();

        var ordered = sort switch
        {
            "name" => filtered.OrderBy(game => game.Name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-Hans-CN"), true)).ToList(),
            "lastPlayed" => filtered.OrderByDescending(game => game.LastPlayed ?? DateTimeOffset.MinValue).ThenByDescending(game => game.TotalMinutes).ToList(),
            _ => filtered
                .OrderByDescending(game => recentTab ? game.RecentMinutes : game.TotalMinutes)
                .ThenByDescending(game => game.TotalMinutes)
                .ThenBy(game => game.Name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-Hans-CN"), true))
                .ToList()
        };

        var longest = ordered.Count == 0
            ? 1
            : Math.Max(1, ordered.Max(game => recentTab ? game.RecentMinutes : game.TotalMinutes));

        return ordered.Select(game =>
        {
            var minutes = recentTab ? game.RecentMinutes : game.TotalMinutes;
            var secondary = recentTab
                ? Loc.Tf("Login_Verify_Library_Career_Format", FormatPlaytime(game.TotalMinutes))
                : game.RecentMinutes > 0
                    ? Loc.Tf("Login_Verify_Library_Recent_Format", FormatPlaytime(game.RecentMinutes))
                    : game.LastPlayed is { } lastPlayed
                        ? Loc.Tf("Login_Verify_Library_LastPlayed_Format", lastPlayed.LocalDateTime.ToString("yyyy-MM-dd"))
                        : Loc.Tf("Login_Verify_Library_App_Format", game.AppId);

            return new SteamVerifyGameRow
            {
                Name = game.Name,
                PlaytimeText = minutes > 0 ? FormatPlaytime(minutes) : Loc.T("Login_Verify_Library_Unplayed"),
                SecondaryText = secondary,
                PlatformNote = game.PlatformNote,
                BarPercent = Math.Round(minutes / (double)longest * 100, 1)
            };
        }).ToList();
    }

    /// <summary>站点把时长渲染成「N 分钟」或「X 小时」（≥100 小时取整，否则一位小数）。</summary>
    public static string FormatPlaytime(long minutes)
    {
        var value = Math.Max(0, minutes);
        if (value < 60)
        {
            return Loc.Tf("Login_Verify_Library_Minutes_Format", value);
        }

        var hours = value / 60d;
        var text = hours >= 100
            ? Math.Round(hours).ToString("0", CultureInfo.CurrentCulture)
            : hours.ToString("0.0", CultureInfo.CurrentCulture);
        return Loc.Tf("Login_Verify_Library_Hours_Format", text);
    }

    private static SteamVerifyStat BuildCountStat(
        string labelKey,
        int? count,
        string? note,
        Func<SteamVerifyTone, Brush> toneBrush) => new()
        {
            Label = Loc.T(labelKey),
            Value = count switch
            {
                null => Loc.T("Login_Verify_Value_Unknown"),
                0 => Loc.T("Login_Verify_Value_None"),
                _ => Loc.Tf("Login_Verify_Value_Times_Format", count)
            },
            Note = note,
            ValueBrush = count switch
            {
                null => toneBrush(SteamVerifyTone.None),
                0 => toneBrush(SteamVerifyTone.Ok),
                _ => toneBrush(SteamVerifyTone.Bad)
            }
        };

    private static SteamVerifyStat BuildBoolStat(
        string labelKey,
        bool? banned,
        Func<SteamVerifyTone, Brush> toneBrush) => new()
        {
            Label = Loc.T(labelKey),
            Value = banned switch
            {
                null => Loc.T("Login_Verify_Value_Unknown"),
                true => Loc.T("Login_Verify_Value_Banned"),
                false => Loc.T("Login_Verify_Value_Normal")
            },
            ValueBrush = banned switch
            {
                null => toneBrush(SteamVerifyTone.None),
                true => toneBrush(SteamVerifyTone.Bad),
                false => toneBrush(SteamVerifyTone.Ok)
            }
        };

    /// <summary>封禁卡片下方的明细：只列该类型的封禁记录；全是 unknown 类型时按「未标注类型」归入。</summary>
    private static string? BanNote(SteamVerifyBansDto? bans, string type)
    {
        var games = bans?.Games ?? [];
        var count = type == "vac" ? bans?.VacBans : bans?.GameBans;
        if (games.Count == 0 || count is null or 0)
        {
            return null;
        }

        var hasTyped = games.Any(game => string.Equals(game.Type, type, StringComparison.OrdinalIgnoreCase));
        var matched = games.Where(game =>
            string.Equals(game.Type, type, StringComparison.OrdinalIgnoreCase)
            || (!hasTyped && string.Equals(game.Type, "unknown", StringComparison.OrdinalIgnoreCase)));

        var details = matched
            .Select(game => string.IsNullOrWhiteSpace(game.Detail) ? game.Name : $"{game.Name}（{game.Detail}）")
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return details.Length > 0 ? string.Join("、", details) : null;
    }

    private static string BuildSubline(SteamVerifyPayload payload)
    {
        var steamId = string.IsNullOrWhiteSpace(payload.SteamId)
            ? Loc.T("Login_Verify_Value_Unknown")
            : payload.SteamId!;
        var checkedAt = ParseTime(payload.CheckedAt) is { } time
            ? FormatDateTime(time)
            : Loc.T("Login_Verify_Checked_ThisRequest");

        return Loc.Tf("Login_Verify_Subline_Format", steamId, checkedAt);
    }

    private static string BuildPresenceText(SteamVerifyPresenceDto presence)
    {
        if (presence.Error is not null || presence.InGame is null)
        {
            return Loc.T("Login_Verify_Presence_Unknown");
        }

        if (presence.InGame != true)
        {
            return Loc.T("Login_Verify_Presence_Idle");
        }

        var game = string.IsNullOrWhiteSpace(presence.GameName)
            ? Loc.Tf("Login_Verify_Library_App_Format", presence.GameAppId ?? 0)
            : presence.GameName!;

        return Loc.Tf("Login_Verify_Presence_Ingame_Format", game);
    }

    private static string? BuildCooldownMessage(SteamVerifyCooldownDto? cooldown)
    {
        if (cooldown?.Active != true)
        {
            return null;
        }

        var reason = string.IsNullOrWhiteSpace(cooldown.Reason)
            ? Loc.T("Login_Verify_Cooldown_Default")
            : cooldown.Reason!;

        return ParseTime(cooldown.Until) is { } until
            ? reason + Loc.Tf("Login_Verify_Cooldown_Until_Format", FormatDateTime(until))
            : reason;
    }

    private static SteamVerifyLibrarySummary BuildLibrarySummary(IReadOnlyList<SteamVerifyGameEntry> games)
    {
        var played = games.Count(game => game.TotalMinutes > 0);
        var totalMinutes = games.Sum(game => game.TotalMinutes);
        var recentMinutes = games.Sum(game => game.RecentMinutes);
        var recentCount = games.Count(game => game.RecentMinutes > 0);
        var longest = games.Count == 0 ? 0 : games.Max(game => game.TotalMinutes);
        var longestGame = games.FirstOrDefault(game => game.TotalMinutes == longest);

        return new SteamVerifyLibrarySummary
        {
            TotalLabel = Loc.T("Login_Verify_Library_Stat_Total"),
            TotalValue = games.Count.ToString(CultureInfo.CurrentCulture),
            TotalNote = Loc.Tf("Login_Verify_Library_Stat_Played_Format", played),
            TimeLabel = Loc.T("Login_Verify_Library_Stat_Time"),
            TimeValue = FormatPlaytime(totalMinutes),
            TimeNote = played > 0
                ? Loc.Tf("Login_Verify_Library_Stat_Avg_Format", FormatPlaytime((long)Math.Round(totalMinutes / (double)played)))
                : Loc.T("Login_Verify_Library_Stat_NoPlay"),
            RecentLabel = Loc.T("Login_Verify_Library_Stat_Recent"),
            RecentValue = FormatPlaytime(recentMinutes),
            RecentNote = recentCount > 0
                ? Loc.Tf("Login_Verify_Library_Stat_Recent_Format", recentCount)
                : Loc.T("Login_Verify_Library_Stat_Idle"),
            LongestLabel = Loc.T("Login_Verify_Library_Stat_Longest"),
            LongestValue = longest > 0 ? FormatPlaytime(longest) : "—",
            LongestNote = longestGame?.Name ?? string.Empty
        };
    }

    private static SteamVerifyGameEntry BuildGameEntry(SteamVerifyGameDto game)
    {
        var platforms = new List<string>();
        AddPlatform(platforms, game.PlaytimeWindowsMinutes, "Windows");
        AddPlatform(platforms, game.PlaytimeMacMinutes, "macOS");
        AddPlatform(platforms, game.PlaytimeLinuxMinutes, "Linux");

        return new SteamVerifyGameEntry
        {
            AppId = game.AppId ?? 0,
            Name = string.IsNullOrWhiteSpace(game.Name)
                ? Loc.Tf("Login_Verify_Library_App_Format", game.AppId ?? 0)
                : game.Name!,
            TotalMinutes = Math.Max(0, game.PlaytimeMinutes ?? 0),
            RecentMinutes = Math.Max(0, game.PlaytimeTwoWeeksMinutes ?? 0),
            LastPlayed = ParseTime(game.LastPlayed),
            PlatformNote = platforms.Count > 0 ? string.Join(" · ", platforms) : null
        };
    }

    private static void AddPlatform(List<string> parts, long? minutes, string platform)
    {
        if (minutes is > 0)
        {
            parts.Add($"{platform} {FormatPlaytime(minutes.Value)}");
        }
    }

    private static string FormatRecord(SteamVerifyRecordDto record) =>
        Loc.Tf("Login_Verify_Record_Values_Format", record.Wins ?? 0, record.Ties ?? 0, record.Losses ?? 0);

    private static string FormatRank(SteamVerifyRecordDto record) =>
        string.IsNullOrWhiteSpace(record.RatingLabel)
            ? string.IsNullOrWhiteSpace(record.SkillGroupLabel)
                ? Loc.T("Login_Verify_Records_Unrated")
                : record.SkillGroupLabel!
            : record.RatingLabel!;

    private static string FormatLastMatch(JsonElement value) =>
        ParseTime(value) is { } time
            ? FormatDateTime(time)
            : Loc.T("Login_Verify_Records_None");

    /// <summary>
    /// 上游时间字段混用两种形态：checkedAt / cooldown.until / lastMatchAt 是 epoch 毫秒数字，
    /// library.fetchedAt / games.lastPlayed 是 ISO 串；空值、空串、越界值一律容错成 null。
    /// </summary>
    private static DateTimeOffset? ParseTime(JsonElement value)
    {
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0:
                    // 兼容秒级时间戳：毫秒级现在已是 1.7e12 量级，低于 1e11 的按秒解释。
                    return number < 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeSeconds(number)
                        : DateTimeOffset.FromUnixTimeMilliseconds(number);

                case JsonValueKind.String:
                    var text = value.GetString();
                    return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        && parsed != default
                            ? parsed
                            : null;

                default:
                    return null;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // 超出 DateTimeOffset 表示范围的时间戳按未知处理，不影响其余字段。
            return null;
        }
    }

    private static string FormatDateTime(DateTimeOffset? value) =>
        value is { } time ? FormatHelper.FormatDateTime(time) : Loc.T("Login_Verify_Value_Unknown");
}