using System.Diagnostics;
using System.Globalization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 自动更新辅助进程：先等待旧主程序完全退出，再启动安装包。
/// 这样安装器不会与托盘中的旧进程互相等待。
/// </summary>
internal static class UpdateLaunchWorker
{
    public const string CommandLineSwitch = "--launch-update";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);

    public static int Run(string[] args)
    {
        if (args.Length < 3 ||
            !int.TryParse(
                args[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var processId) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            return 2;
        }

        WaitForParentExit(processId);
        Thread.Sleep(400);
        try
        {
            return UpdateInstallerService.StartInstallerProcess(args[2]) ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Error($"更新辅助进程启动安装器失败：{ex.Message}", ex);
            return 1;
        }
    }

    private static void WaitForParentExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.WaitForExit((int)ParentExitTimeout.TotalMilliseconds);
            }
        }
        catch (ArgumentException)
        {
            // 主进程已经先退出。
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新辅助进程等待主程序退出失败：{ex.Message}");
        }
    }
}
