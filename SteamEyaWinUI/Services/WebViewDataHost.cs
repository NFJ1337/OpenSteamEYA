using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 看管本程序里所有 WebView2（目前是「Steam 中查看」的小窗口；此前「轻松音乐」页也用过同一套机制），
/// 唯一职责：迁移数据目录之前，把 WebView2 对被搬走的 webview2 / webview2-steam 目录的占用放掉。
///
/// 为什么必须这么做（实测结论，不是猜的）：
///   · Chromium 是<b>独占方式</b>打开 Cookies 的：File.Copy 之外，任何 FileShare 组合都读不到它
///     （连 FileShare.ReadWrite 也报「being used by another process」），所以没有「换个共享模式再复制」的退路；
///   · 本项目引用的 WebView2 SDK 里 CoreWebView2 没有 Close()，Stop() 也不释放目录锁；
///   · 可用的手段是 CoreWebView2.BrowserProcessId：按 PID 结束本程序自己启动的浏览器进程树
///     （Cookies 由它的 network service 子进程持有，整棵树退出后句柄才会释放）。
/// 只动我们自己启动的 msedgewebview2，不碰系统里其它应用与浏览器；
/// 别的客户端窗口占着的文件本进程放不掉，那种情况只能提示用户先关掉其它窗口。
/// </summary>
internal static class WebViewDataHost
{
    /// <summary>进程退出后句柄关闭的宽限时间：等这么久还占着就认为不是我们占的。</summary>
    private static readonly TimeSpan HandleCloseGrace = TimeSpan.FromSeconds(2);

    private static readonly string[] NetworkFileCandidates =
        [Path.Combine("EBWebView", "Default", "Network", "Cookies"), Path.Combine("EBWebView", "lockfile")];

    private static readonly List<WeakReference<CoreWebView2>> Cores = [];
    private static readonly List<WeakReference<Action>> WindowClosers = [];

    /// <summary>数据目录迁移结束（成功或失败）时触发：各页面需要重建被放掉的 WebView2。</summary>
    public static event Action? DataMoveFinished;

    /// <summary>登记一个活的 WebView2 引擎（页面初始化成功后调用一次）。</summary>
    public static void Track(CoreWebView2 core) => Cores.Add(new WeakReference<CoreWebView2>(core));

    /// <summary>登记一个「迁移前可以直接关掉」的窗口（Steam 网页小窗用的也是数据目录里的 webview2-steam）。</summary>
    public static void TrackWindow(Action closeWindow) => WindowClosers.Add(new WeakReference<Action>(closeWindow));

    /// <summary>
    /// 放掉 WebView2 对数据目录的占用：关弹窗 → 结束浏览器进程树 → 等句柄真正关掉。
    /// 返回是否确认释放：false 表示仍有 WebView2 数据文件被占用（通常是另一个客户端窗口也开着），
    /// 调用方据此在复制失败时给出「关掉其它窗口」的提示。
    /// </summary>
    public static async Task<bool> ReleaseForDataMoveAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(6);

        CloseTrackedWindows();

        // 先记下当前被占用的文件：结束后只等这一批，别人的占用没必要陪着等。
        var locked = LockedWebViewFiles();
        var pids = KillTrackedBrowserProcesses();
        await WaitForProcessExitAsync(pids, budget);

        var released = await WaitUntilReleasedAsync(locked, HandleCloseGrace);
        AppLog.Info(released
            ? $"迁移数据目录前已释放 WebView2 占用（结束浏览器进程 {pids.Count} 个）。"
            : $"迁移数据目录前仍有 WebView2 文件被占用（结束浏览器进程 {pids.Count} 个），可能还有其它客户端窗口开着。");
        return released;
    }

    /// <summary>迁移收尾：清空登记表并通知各页面重建 WebView2。</summary>
    public static void NotifyDataMoveFinished()
    {
        Cores.Clear();
        WindowClosers.Clear();

        var handlers = DataMoveFinished;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().OfType<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"重建 WebView2 时出错：{ex.Message}");
            }
        }
    }

    private static void CloseTrackedWindows()
    {
        foreach (var closer in Snapshot(WindowClosers))
        {
            try
            {
                closer();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"关闭网页窗口失败：{ex.Message}");
            }
        }

        WindowClosers.Clear();
    }

    /// <summary>结束本程序自己的 WebView2 浏览器进程树，返回这些 PID（供后续等待退出）。</summary>
    private static List<int> KillTrackedBrowserProcesses()
    {
        var pids = new List<int>();
        foreach (var core in Snapshot(Cores))
        {
            try
            {
                // 先停导航/渲染，减少落地中的写入；它不释放目录锁，真正释放靠后面结束进程。
                core.Stop();
            }
            catch
            {
                // 引擎可能已经退出：忽略。
            }

            try
            {
                var pid = (int)core.BrowserProcessId;
                if (pid > 0)
                {
                    pids.Add(pid);
                }
            }
            catch
            {
                // 拿不到 PID 就算了：后面还有「等文件释放」的兜底判断。
            }
        }

        Cores.Clear();

        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // 进程已经自己退出了。
            }
            catch (Exception ex)
            {
                AppLog.Warn($"结束 WebView2 浏览器进程 {pid} 失败：{ex.Message}");
            }
        }

        return pids;
    }

    private static async Task WaitForProcessExitAsync(List<int> pids, TimeSpan budget)
    {
        if (pids.Count == 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && pids.Any(IsProcessAlive))
        {
            await Task.Delay(100);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilReleasedAsync(List<string> paths, TimeSpan budget)
    {
        if (paths.Count == 0)
        {
            return true;
        }

        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            var remaining = paths.Where(IsExclusivelyLocked).ToList();
            if (remaining.Count == 0)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                AppLog.Warn($"WebView2 数据文件仍被占用：{remaining[0]}");
                return false;
            }

            await Task.Delay(200);
        }
    }

    /// <summary>列出当前仍被独占占用的 WebView2 数据文件。</summary>
    private static List<string> LockedWebViewFiles()
    {
        var locked = new List<string>();
        try
        {
            foreach (var root in Directory.EnumerateDirectories(AppPaths.DataRoot, "webview2*"))
            {
                // Cookies 是实测里最先撞上的那个（network service 子进程持有），lockfile 顺带一起看。
                foreach (var relative in NetworkFileCandidates)
                {
                    var path = Path.Combine(root, relative);
                    if (IsExclusivelyLocked(path))
                    {
                        locked.Add(path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"检查 WebView2 占用状态失败：{ex.Message}");
        }

        return locked;
    }

    private static bool IsExclusivelyLocked(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static List<T> Snapshot<T>(List<WeakReference<T>> list) where T : class
    {
        var items = new List<T>();
        foreach (var reference in list)
        {
            if (reference.TryGetTarget(out var target) && target is not null)
            {
                items.Add(target);
            }
        }

        return items;
    }
}
