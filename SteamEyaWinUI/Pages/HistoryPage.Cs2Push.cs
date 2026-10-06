using Microsoft.UI.Xaml;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class HistoryPage
{
    /// <summary>「推送CS2配置」（账号查询页）：等同设置页「CS2 设置同步 → 立即推送」。</summary>
    private async void WhitePushCs2ConfigButton_Click(object sender, RoutedEventArgs e) =>
        await Cs2ConfigPushAction.PushAsync();
}