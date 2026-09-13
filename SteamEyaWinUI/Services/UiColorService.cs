using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SteamEyaWinUI.Services;

internal sealed class UiColorService
{
    private static readonly Color AuroraPrimaryColor = Color.FromArgb(255, 236, 72, 153);
    private static readonly string[] AccentBrushKeys =
    [
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
    ];

    private DispatcherQueueTimer? _timer;
    private Color _baseColor = AuroraPrimaryColor;
    private double _baseHue;
    private double _baseSaturation;
    private double _baseLightness;
    private bool _animated;
    private bool _darkTheme;

    public Color BaseColor => _baseColor;

    public bool IsAnimated => _animated;

    public void Apply(string? customColor, bool animated, string? theme)
    {
        _baseColor = TryParseColor(customColor) ?? AuroraPrimaryColor;
        _animated = animated;
        _darkTheme = IsDarkTheme(theme, _baseColor);
        (_baseHue, _baseSaturation, _baseLightness) = ToHsl(_baseColor);

        EnsureTimer();
        if (_animated)
        {
            if (!_timer!.IsRunning)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer?.Stop();
        }

        ApplyColor(_baseColor);
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            return;
        }

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            if (!_animated)
            {
                return;
            }

            _baseHue = (_baseHue + 12) % 360;
            ApplyColor(FromHsl(_baseHue, _baseSaturation, _baseLightness));
        };
    }

    private void ApplyColor(Color color)
    {
        var resources = Application.Current.Resources;
        var accentEnd = ShiftHue(color, 48);

        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight1"] = Lighten(color, 0.16);
        resources["SystemAccentColorLight2"] = Lighten(color, 0.28);
        resources["SystemAccentColorLight3"] = Lighten(color, 0.40);
        resources["SystemAccentColorDark1"] = Darken(color, 0.16);
        resources["SystemAccentColorDark2"] = Darken(color, 0.28);
        resources["SystemAccentColorDark3"] = Darken(color, 0.40);

        SetGradientColors(resources, "AuroraPageBackgroundBrush",
            PageBackgroundColor(color, 0.07),
            PageBackgroundColor(accentEnd, 0.15),
            PageBackgroundColor(ShiftHue(color, 24), 0.11));
        SetGradientColors(resources, "AuroraAccentGradientBrush", color, accentEnd);

        var glass = GlassColor(142);
        var glassStrong = GlassColor(196);
        var glassInput = GlassColor(190);
        var border = GlassColor(154);
        var pane = GlassColor(112);
        var tableHeader = GlassColor(48);
        var navHover = GlassColor(76);
        var navSelected = WithAlpha(42, color);

        SetBrushColor(resources, "AuroraGlassBrush", glass);
        SetBrushColor(resources, "AuroraGlassStrongBrush", glassStrong);
        SetBrushColor(resources, "AuroraGlassButtonBrush", GlassColor(132));
        SetBrushColor(resources, "AuroraGlassInputBrush", glassInput);
        SetBrushColor(resources, "AuroraGlassBorderBrush", border);
        SetBrushColor(resources, "AuroraPaneBrush", pane);
        SetBrushColor(resources, "AuroraTableHeaderBrush", tableHeader);
        SetBrushColor(resources, "AuroraNavHoverBrush", navHover);
        SetBrushColor(resources, "AuroraNavSelectedBrush", navSelected);
        SetBrushColor(resources, "AuroraPillBrush", WithAlpha(56, color));

        SetBrushColor(resources, "CustomUiBrush", color);
        SetBrushColor(resources, "CustomUiMutedBrush", WithAlpha(48, color));
        SetBrushColor(resources, "CustomUiPageBrush", Color.FromArgb(0, 0, 0, 0));
        SetBrushColor(resources, "CustomUiCardBrush", glass);
        var dialogBackground = _darkTheme
            ? Blend(Color.FromArgb(255, 27, 24, 45), color, 0.08)
            : Blend(Color.FromArgb(255, 250, 248, 255), color, 0.04);
        SetBrushColor(resources, "CustomUiDialogBrush", dialogBackground);
        SetBrushColor(resources, "CustomUiButtonBrush", GlassColor(132));
        SetBrushColor(resources, "CustomUiBorderBrush", border);

        var heading = _darkTheme
            ? Color.FromArgb(255, 245, 243, 255)
            : Color.FromArgb(255, 30, 27, 75);
        var body = _darkTheme
            ? Color.FromArgb(255, 233, 231, 255)
            : Color.FromArgb(255, 49, 46, 129);
        var muted = _darkTheme
            ? Color.FromArgb(255, 184, 180, 240)
            : Color.FromArgb(255, 99, 102, 241);
        SetBrushColor(resources, "AuroraTextHeadingBrush", heading);
        SetBrushColor(resources, "AuroraTextBodyBrush", body);
        SetBrushColor(resources, "AuroraTextMutedBrush", muted);

        foreach (var key in AccentBrushKeys)
        {
            var brushColor = key == "AccentFillColorSecondaryBrush"
                ? Lighten(color, 0.14)
                : key == "AccentFillColorTertiaryBrush"
                    ? Darken(color, 0.10)
                    : color;
            SetBrushColor(resources, key, brushColor);
        }
    }

    private Color GlassColor(byte lightAlpha)
    {
        if (!_darkTheme)
        {
            return Color.FromArgb(lightAlpha, 255, 255, 255);
        }

        var alpha = (byte)Math.Clamp(lightAlpha * 0.28, 18, 72);
        return Color.FromArgb(alpha, 255, 255, 255);
    }

    private Color PageBackgroundColor(Color tint, double amount)
    {
        if (!_darkTheme)
        {
            return Blend(Color.FromArgb(255, 255, 255, 255), tint, amount);
        }

        var baseColor = Blend(Color.FromArgb(255, 18, 16, 34), tint, amount);
        return Color.FromArgb(255, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>把自定义主题解析为实际使用的浅色/深色基础模式。</summary>
    internal static string ResolveEffectiveTheme(string? theme, string? customColor)
    {
        if (!string.Equals(theme, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(theme) ? "Default" : theme;
        }

        var color = TryParseColor(customColor) ?? AuroraPrimaryColor;
        return IsDarkTheme(theme, color) ? "Dark" : "Light";
    }

    /// <summary>返回与当前主题解析结果一致的 WinUI ElementTheme。</summary>
    internal static ElementTheme ResolveElementTheme(string? theme, string? customColor) =>
        ResolveEffectiveTheme(theme, customColor) switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    private static bool IsDarkTheme(string? theme, Color customColor)
    {
        if (string.Equals(theme, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            // 自定义主题按颜色亮度自动选择基础模式，避免深色配色配浅色文字。
            return RelativeLuminance(customColor) < 0.18;
        }

        return string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase) &&
                Application.Current.RequestedTheme == ApplicationTheme.Dark);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
            0.7152 * Linearize(color.G) +
            0.0722 * Linearize(color.B);
    }
    private static void SetBrushColor(
        Microsoft.UI.Xaml.ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private static void SetGradientColors(
        Microsoft.UI.Xaml.ResourceDictionary resources,
        string key,
        params Color[] colors)
    {
        if (colors.Length == 0)
        {
            return;
        }

        // Native AOT 下 ResourceDictionary 索引器可能把派生画刷投影成 Brush，
        // 导致 is LinearGradientBrush 失败；此时重建画刷并写回资源字典。
        LinearGradientBrush brush;
        if (resources[key] is LinearGradientBrush existing)
        {
            brush = existing;
        }
        else
        {
            brush = new LinearGradientBrush();
            resources[key] = brush;
        }

        var stops = brush.GradientStops;
        var offsets = colors.Length == 3 ? new[] { 0d, 0.42d, 1d } : new[] { 0d, 1d };
        for (var index = 0; index < colors.Length; index++)
        {
            var offset = index < offsets.Length ? offsets[index] : (double)index / (colors.Length - 1);
            if (index < stops.Count)
            {
                stops[index].Color = colors[index];
                stops[index].Offset = offset;
            }
            else
            {
                stops.Add(new GradientStop { Color = colors[index], Offset = offset });
            }
        }

        while (stops.Count > colors.Length)
        {
            stops.RemoveAt(stops.Count - 1);
        }

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

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2.0;
        var delta = max - min;
        if (delta == 0)
        {
            return (0, 0, lightness);
        }

        var saturation = lightness > 0.5
            ? delta / (2.0 - max - min)
            : delta / (max + min);
        double hue;
        if (max == r)
        {
            hue = ((g - b) / delta + (g < b ? 6 : 0)) * 60;
        }
        else if (max == g)
        {
            hue = ((b - r) / delta + 2) * 60;
        }
        else
        {
            hue = ((r - g) / delta + 4) * 60;
        }

        return (hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromArgb(
            255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Color ShiftHue(Color color, double delta)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        return FromHsl(hue + delta, saturation, lightness);
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private static Color WithAlpha(byte alpha, Color color) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Lighten(Color color, double amount)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Min(255, color.R + (255 - color.R) * amount),
            (byte)Math.Min(255, color.G + (255 - color.G) * amount),
            (byte)Math.Min(255, color.B + (255 - color.B) * amount));
    }

    private static Color Darken(Color color, double amount)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Max(0, color.R * (1 - amount)),
            (byte)Math.Max(0, color.G * (1 - amount)),
            (byte)Math.Max(0, color.B * (1 - amount)));
    }
}
