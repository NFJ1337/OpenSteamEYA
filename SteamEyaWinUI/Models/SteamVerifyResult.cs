using Microsoft.UI.Xaml.Media;

namespace SteamEyaWinUI.Models;

/// <summary>核验结果的强调色档位：None 用正文色，其余由调用方映射到主题画刷。</summary>
internal enum SteamVerifyTone
{
    None,
    Ok,
    Warn,
    Bad
}

/// <summary>单个统计卡片（对应核验站 stat-grid 的一格）。</summary>
internal sealed class SteamVerifyStat
{
    public string Label { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    /// <summary>次要说明（封禁游戏清单 / 段位名等），无则留空。</summary>
    public string? Note { get; init; }

    /// <summary>
    /// 值文本的画刷。刻意做成 required 非空：模板里 Foreground 直接绑定它，
    /// 一旦漏赋值（绑到 null）文字就会整格不可见，故交给编译器强制每一格都给出画刷。
    /// </summary>
    public required Brush ValueBrush { get; init; }
}

/// <summary>竞技记录表的一行（优先模式与各地图共用）。</summary>
internal sealed class SteamVerifyRecordRow
{
    public string Map { get; init; } = string.Empty;

    public string Record { get; init; } = string.Empty;

    public string Rank { get; init; } = string.Empty;

    public string LastMatch { get; init; } = string.Empty;
}

/// <summary>
/// 游戏库条目：保留数值字段供搜索/排序/条形图使用，文案在渲染时按当前语言生成
/// （语言切换后重新渲染即可，不必重查上游）。
/// </summary>
internal sealed class SteamVerifyGameEntry
{
    public long AppId { get; init; }

    public string Name { get; init; } = string.Empty;

    public long TotalMinutes { get; init; }

    public long RecentMinutes { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>分平台时长摘要（Windows/macOS/Linux），上游未给则为 null。</summary>
    public string? PlatformNote { get; init; }
}

/// <summary>游戏库列表的一行（已格式化的展示态）。</summary>
internal sealed class SteamVerifyGameRow
{
    public string Name { get; init; } = string.Empty;

    public string PlaytimeText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public string? PlatformNote { get; init; }

    /// <summary>相对本视图最长时长的进度（0-100），对应站点的 meter 条。</summary>
    public double BarPercent { get; init; }
}

/// <summary>游戏库区块表头统计（游戏总数 / 累计时长 / 近两周 / 最常游玩）。</summary>
internal sealed class SteamVerifyLibrarySummary
{
    public string TotalLabel { get; init; } = string.Empty;

    public string TotalValue { get; init; } = string.Empty;

    public string TotalNote { get; init; } = string.Empty;

    public string TimeLabel { get; init; } = string.Empty;

    public string TimeValue { get; init; } = string.Empty;

    public string TimeNote { get; init; } = string.Empty;

    public string RecentLabel { get; init; } = string.Empty;

    public string RecentValue { get; init; } = string.Empty;

    public string RecentNote { get; init; } = string.Empty;

    public string LongestLabel { get; init; } = string.Empty;

    public string LongestValue { get; init; } = string.Empty;

    public string LongestNote { get; init; } = string.Empty;
}

/// <summary>核验结果的整体展示模型（已本地化）。</summary>
internal sealed class SteamVerifyReport
{
    public bool IsOk { get; init; }

    public string Headline { get; init; } = string.Empty;

    public string Subline { get; init; } = string.Empty;

    public bool IsCached { get; init; }

    /// <summary>游戏会话状态行；null 表示上游未返回 presence，不显示该行。</summary>
    public string? PresenceText { get; init; }

    public string PresenceGlyph { get; init; } = "\uE7F4";

    public bool PresenceInGame { get; init; }

    public bool PresenceUnknown { get; init; }

    public IReadOnlyList<SteamVerifyStat> Stats { get; init; } = [];

    /// <summary>竞技冷却提示（冷却中才有值）。</summary>
    public string? CooldownMessage { get; init; }

    public IReadOnlyList<SteamVerifyRecordRow> Records { get; init; } = [];

    public SteamVerifyLibrarySummary? LibrarySummary { get; init; }

    public string LibraryFetchedText { get; init; } = string.Empty;

    public IReadOnlyList<SteamVerifyGameEntry> LibraryGames { get; init; } = [];

    /// <summary>游戏库读取失败原因（上游 library.error）；有值时只显示错误条。</summary>
    public string? LibraryError { get; init; }
}