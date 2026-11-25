using Microsoft.Web.WebView2.Core;

namespace SteamEyaWinUI.Services;

/// <summary>
/// CS2Tracker 专用 WebView2 会话。主窗口预热实例与实际查询页共用同一个环境，
/// 从而复用 Cloudflare Cookie 与浏览器进程，避免进入页面后重复冷启动。
/// </summary>
internal static class Cs2TrackerWebSession
{
    public const string HomeUrl = "https://cs2tracker.gg/stats/";

    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);
    private static CoreWebView2Environment? _environment;

    public static async Task<CoreWebView2Environment> GetEnvironmentAsync(string language)
    {
        var existing = Volatile.Read(ref _environment);
        if (existing is not null)
        {
            return existing;
        }

        await EnvironmentGate.WaitAsync();
        try
        {
            existing = Volatile.Read(ref _environment);
            if (existing is not null)
            {
                return existing;
            }

            var userDataFolder = Path.Combine(AppPaths.DataRoot, "webview2-cs2tracker");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                userDataFolder,
                new CoreWebView2EnvironmentOptions { Language = language });
            Volatile.Write(ref _environment, environment);
            return environment;
        }
        finally
        {
            EnvironmentGate.Release();
        }
    }

    /// <summary>数据目录迁移会结束 WebView2 浏览器进程，之后必须重新创建共享环境。</summary>
    public static void Reset() => Interlocked.Exchange(ref _environment, null);
}
