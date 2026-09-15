using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 界面当前实际主题。主题设在窗口根元素上（见 MainWindow.ApplyTheme），
    /// Application.RequestedTheme 始终是启动值，故需从根元素 ActualTheme 读取。
    /// </summary>
    internal static ElementTheme ActualTheme =>
        Current is App app && app._window?.Content is FrameworkElement root
            ? root.ActualTheme
            : ElementTheme.Default;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 退出时收掉本程序自己拉起的 VPN 内核（订阅直连），避免残留进程占端口。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => VpnProxyService.SafeStopCore();

        // 上次若是异常退出，系统代理可能还指着已经结束的内核（表现：整台电脑上不了网）→ 启动时先兜底还原。
        SystemProxyService.RestoreIfApplied();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 在任何窗口/页面构造前确定界面语言，保证首帧即用所选语言渲染。
        Loc.Initialize(AppState.SettingsService);
        var settings = AppState.SettingsService.Load();
        AppState.UpdateService.SetProxySite(settings.UpdateProxySite);
        ApplyTableSeparatorColor(settings.TableSeparatorColor, UiColorService.ResolveEffectiveTheme(settings.Theme, settings.UiColor), settings.ShowTableSeparators);
        AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);

        // 上次自动更新下载的安装包：装完之后首次启动时删掉（仍在被安装器占用则跳过）。
        AppState.UpdateInstallerService.CleanupDownloadedInstallers();

        _window = new MainWindow();
        _window.Activate();
    }

    internal static Color GetTableSeparatorColor()
    {
        return Current.Resources["TableRowSeparatorBrush"] is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(255, 128, 128, 128);
    }

    internal static void ApplyTableSeparatorColor(string? customColor, string? theme = null, bool showSeparators = true)
    {
        var color = TryParseColor(customColor) ?? GetDefaultTableSeparatorColor(theme);
        if (Current.Resources["TableRowSeparatorBrush"] is SolidColorBrush brush)
        {
            brush.Color = color;
        Current.Resources["TableRowSeparatorThickness"] = showSeparators
            ? new Thickness(0, 0, 0, 1)
            : new Thickness(0);
        }
        else
        {
            Current.Resources["TableRowSeparatorBrush"] = new SolidColorBrush(color);
        }
    }

    private static Color GetDefaultTableSeparatorColor(string? theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase) &&
                Current.RequestedTheme == ApplicationTheme.Dark);
        return dark
            ? Color.FromArgb(255, 62, 62, 62)
            : Color.FromArgb(255, 216, 216, 216);
    }

    private static Color? TryParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8 ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return null;
        }

        return Color.FromArgb(
            (byte)(raw >> 24),
            (byte)(raw >> 16),
            (byte)(raw >> 8),
            (byte)raw);
    }
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 崩溃前尽力落盘，便于排查 AOT 产物上的现场问题。
        try
        {
            var logFolder = AppPaths.DataRoot;
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响异常继续传播。
        }
    }
}
