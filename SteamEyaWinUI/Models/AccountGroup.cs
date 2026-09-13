namespace SteamEyaWinUI.Models;

/// <summary>
/// 用户自定义账号分组的定义（名称 + 排序）。分组成员关系存在各账号的 <c>GroupIds</c> 上，以稳定 <see cref="Id"/> 关联，
/// 故分组改名不需要改写任何账号。定义列表存于 settings.json 的 AppSettings.Groups。
/// 纯数据类：UI 里分组都被包进 ComboBoxItem/ToggleMenuFlyoutItem（内容是字符串），本类型实例不跨 WinRT ABI。
/// </summary>
public sealed class AccountGroup
{
    /// <summary>稳定标识（账号 GroupIds 引用它，改名不影响成员关系）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>分组显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>显示排序，越小越靠前。</summary>
    public int Order { get; set; }
}
