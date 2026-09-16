using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Graphics;

namespace SteamEyaWinUI.Pages;

/// <summary>
/// 「Steam中查看」弹出的小窗口：内嵌 WebView2，先把会话 cookie 注入进去，
/// 再打开 <c>https://steamcommunity.com/my/</c>，所以一打开就是已登录状态。
/// 登录态单独存在数据目录的 webview2-steam 下（与「轻松音乐」页互不干扰）。
/// </summary>
public sealed partial class SteamBrowserWindow : Window
{
    private const string TargetUrl = "https://steamcommunity.com/my/";
    private const string CookieDomain = ".steamcommunity.com";

    private readonly SteamBrowserSession _session;

    internal SteamBrowserWindow(SteamBrowserSession session)
    {
        _session = session;
        InitializeComponent();

        Title = Loc.T("History_Btn_SteamBrowser");
        CenterOnScreen(sizePercent: 0.72);

        _ = LoadSessionAsync();
    }

    /// <summary>
    /// 把窗口按「屏幕工作区的百分比」放在正中间：比固定尺寸更贴合不同分辨率，
    /// 大屏上不会显得很小，小屏上也不会超出可用区域。
    /// </summary>
    private void CenterOnScreen(double sizePercent)
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var width = (int)Math.Clamp(work.Width * sizePercent, 900, 1800);
            var height = (int)Math.Clamp(work.Height * sizePercent, 680, 1300);
            AppWindow.MoveAndResize(new RectInt32(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2),
                width,
                height));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置 Steam 网页窗口位置失败：{ex.Message}");
            AppWindow.Resize(new SizeInt32(1100, 820));
        }
    }

    private async Task LoadSessionAsync()
    {
        try
        {
            var userDataFolder = System.IO.Path.Combine(AppPaths.DataRoot, "webview2-steam");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                userDataFolder,
                new CoreWebView2EnvironmentOptions());
            await Browser.EnsureCoreWebView2Async(environment);

            var cookieManager = Browser.CoreWebView2.CookieManager;
            foreach (var (name, value) in _session.Cookies)
            {
                var cookie = cookieManager.CreateCookie(name, value, CookieDomain, "/");
                cookie.IsSecure = true;
                cookieManager.AddOrUpdateCookie(cookie);
            }

            Browser.CoreWebView2.Navigate(TargetUrl);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开 Steam 网页失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("History_SteamBrowser_Error_Format", ex.Message), InfoBarSeverity.Error);
        }
    }
}