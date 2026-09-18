using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Models;

/// <summary>
/// 「黑号存储」里的一张卡密。
///
/// 关键约定（用户明确要求）：<see cref="Key"/> 原样保存、原样使用，不做任何格式化 ——
/// 不转大小写、不去字符、不拆「账号----卡密」，只去掉首尾空白（那是按行导入必须的）。
/// </summary>
// partial：实例会作为按钮的 Tag 跨 WinRT ABI（列表行上的 验号/去Token登录/删除），需要 CsWinRT 源生成 vtable（AOT）。
public sealed partial class CardKeyEntry : INotifyPropertyChanged
{
    /// <summary>卡密来源：奶味。</summary>
    public const string SourceNaiwei = "naiwei";

    /// <summary>卡密来源：伊万（上游 70.39.201.195:9099）。</summary>
    public const string SourceIvan = "ivan";

    /// <summary>卡密来源：路飞（上游 38.76.193.80:9099）。</summary>
    public const string SourceLuffy = "luffy";

    /// <summary>历史值：早期版本把「伊万/路飞」当成一个来源存过，按伊万处理。</summary>
    public const string SourceIvanLuffyLegacy = "ivanluffy";

    /// <summary>历史值：早期版本误存成「tokenlogin」的，按奶味处理。</summary>
    public const string SourceTokenLoginLegacy = "tokenlogin";

    private string _statusText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>卡密原文（原样保存）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>来源：<see cref="SourceNaiwei"/> / <see cref="SourceIvan"/> / <see cref="SourceLuffy"/>。</summary>
    public string Source { get; set; } = SourceNaiwei;

    /// <summary>入库时间。</summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>最近一次验号时间；null = 没验过。</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>验号结果：true = 令牌可用，false = 不可用，null = 没验过。</summary>
    public bool? CheckOk { get; set; }

    /// <summary>验号解析到的账号名（上游给的，拿不到就是 SteamID）。</summary>
    public string? AccountName { get; set; }

    /// <summary>验号解析到的 SteamID64。</summary>
    public string? SteamId { get; set; }

    /// <summary>验号失败的原文原因（上游/Steam 给的，不翻译，便于核对）。</summary>
    public string? CheckMessage { get; set; }

    /// <summary>是否已经用这张卡密登录过（用户要求：标成「已用」而不是删除）。</summary>
    public bool IsUsed { get; set; }

    /// <summary>首次「已用」的时间。</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>奶味来源验号时顺带拉回来的核验信息：VAC 封禁次数（0 = 正常）。</summary>
    public int? VacBans { get; set; }

    /// <summary>核验信息：游戏封禁次数（0 = 正常）。</summary>
    public int? GameBans { get; set; }

    /// <summary>核验信息：是否处于竞技冷却。</summary>
    public bool? CooldownActive { get; set; }

    /// <summary>核验信息：是否优先账户。</summary>
    public bool? Prime { get; set; }

    /// <summary>核验信息：资料等级。</summary>
    public int? ProfileRank { get; set; }

    /// <summary>界面上的来源文案（页面按当前语言填，随列表重建刷新）。</summary>
    [JsonIgnore]
    public string SourceText { get; set; } = string.Empty;

    /// <summary>界面上的状态文案（同上）。x:Bind OneWay，必须通知才会重绘。</summary>
    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }
}