using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

/// <summary>
/// 自定义入口（csproj 已定义 DISABLE_XAML_GENERATED_MAIN）：
/// 以 <see cref="Cs2CloudPushWorker.CommandLineSwitch"/> 启动时进入「CS2 云推送辅助进程」模式，
/// 完全不初始化 XAML/窗口，推完即退；否则按 WinUI 生成入口的原样流程启动主界面。
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

        // VPN 看门狗模式：独立进程，主进程被强杀时负责把系统代理还原回去（不初始化 XAML/窗口）。
        if (args.Length > 0 && args[0] == VpnCoreService.WatchdogCommandLineSwitch)
        {
            return VpnCoreService.RunWatchdog(args);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
