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
/// 登录态单独存在数据目录的 webview2-steam 下（不写进别的网页 profile）。
/// </summary>
public sealed partial class SteamBrowserWindow : Window
{
    // 带 l=schinese：steamcommunity 认这个参数（实测匿名页能翻成中文，302 跳转也带着它走）。
    private const string TargetUrl = "https://steamcommunity.com/my/?l=schinese";
    private const string CookieDomain = ".steamcommunity.com";
    private const string LanguageCookie = "steamLanguage";
    private const string ChineseLanguage = "schinese";
    private const string CookieUrl = "https://steamcommunity.com/";

    private readonly SteamBrowserSession _session;

    /// <summary>注入中文之前 steamLanguage 的原值（null 表示原本没有这个 cookie）。</summary>
    private string? _previousLanguage;
    private bool _languageInjected;
    private bool _languageRestored;

    internal SteamBrowserWindow(SteamBrowserSession session)
    {
        _session = session;
        InitializeComponent();

        Title = Loc.T("History_Btn_SteamBrowser");
        CenterOnScreen(sizePercent: 0.72);

        // 关窗时还原显示语言：挂在 Closing（界面树还在、WebView2 还能用），不用 Closed。
        AppWindow.Closing += (_, _) => RestoreLanguageCookie();
        // 迁移数据目录时这扇窗会被直接关掉：它同样占着数据目录里的 webview2-steam。
        WebViewDataHost.TrackWindow(Close);

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
                // Language 只作用于这扇窗口的 WebView：让页面语言协商（Accept-Language）也偏中文。
                new CoreWebView2EnvironmentOptions { Language = "zh-CN" });
            await Browser.EnsureCoreWebView2Async(environment);
            WebViewDataHost.Track(Browser.CoreWebView2);

            var cookieManager = Browser.CoreWebView2.CookieManager;
            foreach (var (name, value) in _session.Cookies)
            {
                var cookie = cookieManager.CreateCookie(name, value, CookieDomain, "/");
                cookie.IsSecure = true;
                cookieManager.AddOrUpdateCookie(cookie);
            }

            await InjectChineseLanguageAsync(cookieManager);

            Browser.CoreWebView2.Navigate(TargetUrl);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开 Steam 网页失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("History_SteamBrowser_Error_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 让这扇窗口显示简体中文：先记住 steamLanguage 原值，再写入 schinese。
    /// 只动这个 WebView 自己的 cookie 存储，不改 Steam 账号的语言设置，也不影响系统里其它浏览器。
    /// </summary>
    private async Task InjectChineseLanguageAsync(CoreWebView2CookieManager cookieManager)
    {
        try
        {
            var cookies = await cookieManager.GetCookiesAsync(CookieUrl);
            _previousLanguage = cookies
                .FirstOrDefault(cookie => string.Equals(cookie.Name, LanguageCookie, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            var language = cookieManager.CreateCookie(LanguageCookie, ChineseLanguage, CookieDomain, "/");
            language.IsSecure = true;
            cookieManager.AddOrUpdateCookie(language);
            _languageInjected = true;
        }
        catch (Exception ex)
        {
            // 拿不到 cookie 也不影响打开：URL 上的 l=schinese 仍然生效。
            AppLog.Warn($"设置 Steam 网页中文失败（不影响 l=schinese）：{ex.Message}");
        }
    }

    /// <summary>关窗把语言 cookie 还原回原值（原本没有就删掉），不把改动留给下次打开。</summary>
    private void RestoreLanguageCookie()
    {
        if (!_languageInjected || _languageRestored)
        {
            return;
        }

        _languageRestored = true;
        try
        {
            var core = Browser.CoreWebView2;
            if (core is null)
            {
                return;
            }

            var cookieManager = core.CookieManager;
            if (string.IsNullOrEmpty(_previousLanguage))
            {
                cookieManager.DeleteCookie(cookieManager.CreateCookie(LanguageCookie, string.Empty, CookieDomain, "/"));
                return;
            }

            var language = cookieManager.CreateCookie(LanguageCookie, _previousLanguage, CookieDomain, "/");
            language.IsSecure = true;
            cookieManager.AddOrUpdateCookie(language);
        }
        catch (Exception ex)
        {
            // 还原失败只影响这扇窗口的默认语言，下次打开会重新注入中文，不影响 Steam 账号。
            AppLog.Warn($"还原 Steam 网页语言 cookie 失败：{ex.Message}");
        }
    }
}