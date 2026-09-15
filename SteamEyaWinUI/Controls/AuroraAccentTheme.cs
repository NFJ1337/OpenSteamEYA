using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Controls;

/// <summary>
/// 「实色主题色」上色：底色用当前主题色（不是半透明玻璃），文字与图标按底色明度自动取深色或白色，
/// 悬停/按下各亮/暗一档。百宝箱那几个按钮和「轻松音乐」页的下拉/按钮共用这一套，保证观感一致；
/// 页面每次进入（以及切主题色后重新进入）都会重算一遍。
/// </summary>
internal static class AuroraAccentTheme
{
    public static void ApplySolidAccent(Button button, TextBlock label, FontIcon icon)
    {
        var accent = AppState.UiColorService.BaseColor;
        var background = new SolidColorBrush(accent);
        var hover = new SolidColorBrush(Blend(accent, Colors.White, 0.16));
        var pressed = new SolidColorBrush(Blend(accent, Colors.Black, 0.14));
        var foreground = new SolidColorBrush(ReadableTextColor(accent));

        button.Background = background;
        button.BorderBrush = background;
        button.Foreground = foreground;
        label.Foreground = foreground;
        icon.Foreground = foreground;

        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        button.Resources["ButtonBorderBrush"] = background;
        button.Resources["ButtonBorderBrushPointerOver"] = hover;
        button.Resources["ButtonBorderBrushPressed"] = pressed;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    /// <summary>
    /// 下拉框：背景、边框、文字都换成主题色（含悬停/按下/失焦态）。
    /// 只覆盖颜色资源，不改模板，所以下拉展开的列表仍沿用系统样式。
    /// </summary>
    public static void ApplySolidAccent(ComboBox comboBox)
    {
        var accent = AppState.UiColorService.BaseColor;
        var background = new SolidColorBrush(accent);
        var hover = new SolidColorBrush(Blend(accent, Colors.White, 0.16));
        var pressed = new SolidColorBrush(Blend(accent, Colors.Black, 0.14));
        var foreground = new SolidColorBrush(ReadableTextColor(accent));

        comboBox.Background = background;
        comboBox.BorderBrush = background;
        comboBox.Foreground = foreground;

        comboBox.Resources["ComboBoxBackground"] = background;
        comboBox.Resources["ComboBoxBackgroundUnfocused"] = background;
        comboBox.Resources["ComboBoxBackgroundPointerOver"] = hover;
        comboBox.Resources["ComboBoxBackgroundPressed"] = pressed;
        comboBox.Resources["ComboBoxBorderBrush"] = background;
        comboBox.Resources["ComboBoxBorderBrushPointerOver"] = hover;
        comboBox.Resources["ComboBoxBorderBrushPressed"] = pressed;
        comboBox.Resources["ComboBoxForeground"] = foreground;
        comboBox.Resources["ComboBoxForegroundPointerOver"] = foreground;
        comboBox.Resources["ComboBoxForegroundPressed"] = foreground;
        comboBox.Resources["ComboBoxForegroundFocused"] = foreground;
        comboBox.Resources["ComboBoxForegroundFocusedPressed"] = foreground;
        comboBox.Resources["ComboBoxDropDownForeground"] = foreground;

        // 下拉展开的那层弹窗（Popup）也要跟着主题色，否则收起时是主题色、一点开又变回系统浅色底，很割裂。
        comboBox.Resources["ComboBoxDropDownBackground"] = background;
        comboBox.Resources["ComboBoxDropDownBorderBrush"] = background;
        comboBox.Resources["ComboBoxItemBackground"] = background;
        comboBox.Resources["ComboBoxItemBackgroundPointerOver"] = hover;
        comboBox.Resources["ComboBoxItemBackgroundSelected"] = pressed;
        comboBox.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = hover;
        comboBox.Resources["ComboBoxItemBackgroundSelectedPressed"] = pressed;
        comboBox.Resources["ComboBoxItemForeground"] = foreground;
        comboBox.Resources["ComboBoxItemForegroundPointerOver"] = foreground;
        comboBox.Resources["ComboBoxItemForegroundSelected"] = foreground;
        comboBox.Resources["ComboBoxItemForegroundSelectedPointerOver"] = foreground;
        comboBox.Resources["ComboBoxItemForegroundSelectedPressed"] = foreground;
        // 选中项左侧那枚「胶囊」的高亮色
        comboBox.Resources["ComboBoxItemPillFillBrush"] = pressed;
    }

    /// <summary>底色亮就配深字、底色暗就配白字，保证文字始终看得清。</summary>
    private static Windows.UI.Color ReadableTextColor(Windows.UI.Color background)
    {
        var luminance = (0.2126 * Channel(background.R)) + (0.7152 * Channel(background.G)) + (0.0722 * Channel(background.B));
        return luminance > 0.62
            ? Windows.UI.Color.FromArgb(255, 32, 28, 60)
            : Colors.White;

        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
    }

    private static Windows.UI.Color Blend(Windows.UI.Color from, Windows.UI.Color to, double amount) =>
        Windows.UI.Color.FromArgb(
            255,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
}