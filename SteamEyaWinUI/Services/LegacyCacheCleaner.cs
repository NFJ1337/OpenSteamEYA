using System.IO;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「轻松音乐」页（内嵌音乐平台网页）已经移除，但旧版本在数据目录里留下的 webview2 缓存目录还在占盘，
/// 而且会被每日备份打进包里（每天平白多几百 MB）。这里在启动时清一次：
///   · 只处理 &lt;数据根&gt;\webview2 这一个目录（音乐页的 WebView2 profile）；
///   · 绝不碰 webview2-steam —— 「Steam 中查看」小窗还在用它；
///   · 目录不存在就直接返回；被别的实例占用导致删除失败时只记日志，下次启动再试。
/// </summary>
internal static class LegacyCacheCleaner
{
    /// <summary>启动时调用：后台清掉已移除页面的网页缓存，失败不影响使用。</summary>
    public static void CleanRemovedMusicCacheOnce()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var root = AppPaths.NormalizePath(AppPaths.DataRoot);
                var target = Path.Combine(root, "webview2");

                if (!Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                // 删除前再确认一次：必须正好是「数据根目录下名为 webview2 的那一层」，避免任何路径拼装失误。
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(target), "webview2", StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Warn($"跳过清理旧音乐缓存：路径不符合预期（{target}）");
                    return;
                }

                var bytes = 0L;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                    {
                        bytes += new FileInfo(file).Length;
                    }
                }
                catch
                {
                    // 统计失败不影响清理。
                }

                Directory.Delete(target, recursive: true);
                AppLog.Info($"已清理「轻松音乐」页遗留的网页缓存：{target}（约 {bytes / 1024d / 1024d:F1} MB）");
            }
            catch (DirectoryNotFoundException)
            {
                // 另一个实例已经清掉了：不算错误。
            }
            catch (Exception ex)
            {
                AppLog.Warn($"清理旧音乐缓存未完成（下次启动会再试）：{ex.Message}");
            }
        });
    }
}
