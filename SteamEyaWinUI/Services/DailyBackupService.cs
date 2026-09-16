using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 每天第一次打开软件时，把整个数据目录压成一个 ZIP 放到数据根目录（口径：所有文件）。
///
/// 设计要点：
///   · 同一天只做一次：日期记在 settings.json 的 LastDailyBackupDate，与本地日期比对；
///   · 后台异步跑，启动后先等一会儿（避开预热/更新检查），失败只写日志、不影响使用；
///   · 先写 .partial 再改名成正式包：半途退出不会留下看起来像正常备份的残缺 ZIP；
///   · 跳过备份包自身与更早的备份包（否则每天的包里套着所有历史包，体积指数增长），
///     以及读不了 / 被占用（WebView2 正在用）的文件 —— 跳过数会写进日志；
///   · 只保留最近 RetentionCount 份，更早的自动删除。
/// </summary>
internal static class DailyBackupService
{
    /// <summary>保留份数：多出来的按文件名（含 yyyy-MM-dd）倒序删最早的。</summary>
    private const int RetentionCount = 7;

    private const string FileNamePrefix = "SteamEYA-数据备份-";
    private const string FileNameSuffix = ".zip";
    private const string PartialSuffix = ".partial";

    /// <summary>多实例认领文件后缀：同一天两个窗口同时启动时，只有一个真的去压缩。</summary>
    private const string ClaimSuffix = ".claim";

    /// <summary>启动后延后多久开始备份：让窗口初始化、Steam 预热、更新检查先跑完。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private static int _running;

    /// <summary>启动时调用：今天还没备份过就后台压一份。</summary>
    public static void RunForTodayIfNeeded()
    {
        // 防重入：同一次进程内被重复调用（或两个实例同时启动）时只跑一次。
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"每日数据备份失败：{ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
    }

    private static async Task RunAsync()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (IsBackedUpToday(today))
        {
            return;
        }

        await Task.Delay(StartupDelay);

        // 等启动那阵忙完再确认一次：这期间用户可能已经手动备份过，或另一个实例先做完了。
        if (IsBackedUpToday(today))
        {
            return;
        }

        var root = AppPaths.DataRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        var target = Path.Combine(root, FileNamePrefix + today + FileNameSuffix);

        // 临时名带上进程号：两个实例万一同时开跑也不会互相抢同一个文件。
        var partial = target + "." + Environment.ProcessId + PartialSuffix;

        // 认领：同一天两个实例同时启动时，只让先抢到的那个真的去压缩。
        var claimPath = target + ClaimSuffix;
        if (!TryClaim(claimPath))
        {
            AppLog.Info("另一个 SteamEYA 实例正在做每日数据备份，本次跳过。");
            return;
        }

        var watch = Stopwatch.StartNew();
        AppLog.Info($"开始每日数据备份：{target}");

        try
        {
            (int Included, int Skipped, int SkippedCache, long Bytes) summary;
            try
            {
                summary = await Task.Run(() => CreateArchive(root, partial, target));
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            // 全部写完再改名：中途任何失败都不会留下能误当成备份的文件。
            File.Move(partial, target, overwrite: true);

            var sizeMb = new FileInfo(target).Length / 1024d / 1024d;
            AppLog.Info(
                $"每日数据备份完成：{summary.Included} 个文件 / {summary.Bytes / 1024d / 1024d:F1} MB" +
                $" → 压缩包 {sizeMb:F1} MB，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。");
            if (summary.Skipped > 0)
            {
                AppLog.Warn($"每日数据备份：{summary.Skipped} 个文件被占用或读不了，已跳过（不影响其它文件）。");
            }

            if (summary.SkippedCache > 0)
            {
                AppLog.Info($"每日数据备份：跳过旧「轻松音乐」缓存 {summary.SkippedCache} 个文件（启动时会清理，不进备份包）。");
            }

            MarkBackedUp(today);
            CleanupOldBackups(root);
        }
        finally
        {
            TryDelete(claimPath);
            TryDelete(partial);
        }
    }

    private static bool IsBackedUpToday(string today)
    {
        try
        {
            return string.Equals(AppState.SettingsService.Load().LastDailyBackupDate, today, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取每日备份日期失败：{ex.Message}");
            return false;
        }
    }

    private static void MarkBackedUp(string today)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            settings.LastDailyBackupDate = today;
            AppState.SettingsService.Save(settings);
        }
        catch (Exception ex)
        {
            // 写不进去只会导致下次启动再备一份（内容相同），不影响数据安全。
            AppLog.Warn($"记录每日备份日期失败：{ex.Message}");
        }
    }

    private static (int Included, int Skipped, int SkippedCache, long Bytes) CreateArchive(string root, string partial, string target)
    {
        var included = 0;
        var skipped = 0;
        var skippedCache = 0;
        long bytes = 0;

        using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // 备份包自身 + 更早的备份包 + 上次没写完的残包，都不进包。
                if (string.Equals(file, partial, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetDirectoryName(file), root, StringComparison.OrdinalIgnoreCase) && IsBackupFileName(Path.GetFileName(file)))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file);
                if (IsRemovedMusicCache(relativePath))
                {
                    // 已移除页面的缓存：没有备份价值，每天还平白多几百 MB。
                    skippedCache++;
                    continue;
                }

                var entryName = relativePath.Replace('\\', '/');
                try
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    try
                    {
                        // 保留原修改时间，回头从包里恢复时顺序还看得懂。
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                    }
                    catch
                    {
                        // 时间戳超出 ZIP 可表示范围（早于 1980 年）时忽略，不影响内容。
                    }

                    using var source = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var destination = entry.Open();
                    source.CopyTo(destination);
                    included++;
                    bytes += source.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // WebView2 的 Cookies 之类被独占占用：跳过这个文件，别让整次备份失败。
                    skipped++;
                }
            }
        }

        return (included, skipped, skippedCache, bytes);
    }

    /// <summary>
    /// 相对路径是否属于「轻松音乐」页留下的 webview2 目录。只跳这一个目录名，
    /// webview2-steam（「Steam 中查看」）还在用，要照常进包。
    /// </summary>
    private static bool IsRemovedMusicCache(string relativePath) =>
        relativePath.StartsWith("webview2" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, "webview2", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 抢占「今天由我来备份」：用 FileMode.CreateNew 保证只有一个实例抢得到；
    /// 上一个实例崩溃留下的认领文件超过 1 小时视为失效，直接接管。
    /// </summary>
    private static bool TryClaim(string claimPath)
    {
        try
        {
            if (File.Exists(claimPath) && DateTime.Now - File.GetLastWriteTime(claimPath) > TimeSpan.FromHours(1))
            {
                File.Delete(claimPath);
            }

            using var stream = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // 已经被别的实例抢走：正常跳过，不是错误。
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"每日数据备份认领失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 是否是本服务自己的产物（正式包 / 唯一临时包 / 认领文件）。
    /// 它们都长成「前缀 + … + .zip + 后缀」，用「含 .zip」一条规则就能全覆盖 ——
    /// 漏掉任何一个都会被当成普通数据打进包里（临时包还会越滚越大）。
    /// </summary>
    private static bool IsBackupFileName(string name) =>
        name.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase) &&
        name.Contains(FileNameSuffix, StringComparison.OrdinalIgnoreCase);

    private static void CleanupOldBackups(string root)
    {
        try
        {
            // 文件名里带 yyyy-MM-dd，字符串倒序就是时间倒序。
            var backups = Directory.EnumerateFiles(root, FileNamePrefix + "*" + FileNameSuffix)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 崩溃/断电留下的残包与失效认领文件：超过 1 天就清掉。
            foreach (var stale in Directory.EnumerateFiles(root, FileNamePrefix + "*" + PartialSuffix)
                         .Concat(Directory.EnumerateFiles(root, FileNamePrefix + "*" + ClaimSuffix)))
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(stale) > TimeSpan.FromDays(1))
                    {
                        File.Delete(stale);
                        AppLog.Info($"每日数据备份：已清理残留文件 {Path.GetFileName(stale)}。");
                    }
                }
                catch
                {
                    // 残包清理失败不影响保留策略。
                }
            }

            foreach (var old in backups.Skip(RetentionCount))
            {
                try
                {
                    File.Delete(old);
                    AppLog.Info($"每日数据备份：已删除超出保留份数的旧备份 {Path.GetFileName(old)}。");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"删除旧备份失败（{Path.GetFileName(old)}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理旧备份失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 退出路径上的清理失败不影响主流程。
        }
    }
}
