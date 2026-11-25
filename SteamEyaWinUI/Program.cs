using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

/// <summary>
/// 自定义入口（csproj 已定义 DISABLE_XAML_GENERATED_MAIN）：
/// 以 <see cref="Cs2CloudPushWorker.CommandLineSwitch"/> 或
/// <see cref="UpdateLaunchWorker.CommandLineSwitch"/> 启动时进入短命辅助进程模式，
/// 完全不初始化 XAML/窗口；否则按 WinUI 生成入口的原样流程启动主界面。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Cs2CloudPushWorker.CommandLineSwitch)
        {
            return Cs2CloudPushWorker.Run(args);
        }

        if (args.Length > 0 && args[0] == UpdateLaunchWorker.CommandLineSwitch)
        {
            return UpdateLaunchWorker.Run(args);
        }

        if (!SingleInstanceService.TryAcquire(
                MainWindow.RequestExternalActivation,
                out var singleInstance))
        {
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        finally
        {
            singleInstance?.Dispose();
        }

        return 0;
    }
}
