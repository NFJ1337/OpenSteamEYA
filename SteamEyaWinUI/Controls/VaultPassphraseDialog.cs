using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Controls;

/// <summary>
/// 凭据库口令的输入框（ContentDialog + PasswordBox）。
/// 启动解锁提示与设置页共用同一套外观，避免两处各写一份。
/// </summary>
internal static class VaultPassphraseDialog
{
    /// <returns>确认时返回输入的口令；取消/关闭返回 null。</returns>
    public static async Task<string?> AskAsync(XamlRoot? xamlRoot, string title, string description, string primaryText)
    {
        var box = new PasswordBox
        {
            PlaceholderText = Loc.T("VaultPw_Placeholder"),
            PasswordRevealMode = PasswordRevealMode.Peek
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = Loc.T("Common_Cancel"),
            PrimaryButtonStyle = (Style)Application.Current.Resources["AuroraGlassButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["AuroraGlassButtonStyle"],
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Password : null;
    }
}
