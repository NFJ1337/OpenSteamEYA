using Microsoft.UI.Xaml;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class LegacyAccountsPage
{
    /// <summary>「推送CS2配置」：等同设置页「CS2 设置同步 → 立即推送」（把来源账号的 CS2 配置推到当前账号云）。</summary>
    private async void LegacyPushCs2ConfigButton_Click(object sender, RoutedEventArgs e) =>
        await Cs2ConfigPushAction.PushAsync();
}