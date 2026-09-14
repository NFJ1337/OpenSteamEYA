using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class TreasureBoxPage : Page, INotifyPropertyChanged
{
    private const string SecretUrl = "https://www.yuanshen.com/";

    // 「一键获取18位UID」：官方下载引导地址，点击后用系统默认浏览器打开。
    private const string UidDownloadUrl = "https://ys-api.mihoyo.com/event/download_porter/link/ys_cn/official/pc_default";

    // 「梯子推荐」：点击直接用系统默认浏览器打开。
    private const string ProxySiteUrl = "https://ssrsub.com/";
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public TreasureBoxPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
        ApplyThemeColorToButtons();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 页面是缓存实例：在设置里改过主题色后再次进入要重新上色。
        ApplyThemeColorToButtons();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings))));
    }

    private async void SecretCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is not { } window)
        {
            return;
        }

        await window.PlaySecretVideoAsync();
        await AppState.OpenUrlAsync(SecretUrl);
    }

    /// <summary>「一键获取18位UID」：只做浏览器跳转，不播放彩蛋视频（独立方法，不复用机密卡片的流程）。</summary>
    private async void UidCardButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(UidDownloadUrl);
    }

    // ---------- 查看 Cheat 官网（独立功能：弹窗 + 标签/链接，不改动上面两个按钮） ----------

    /// <summary>
    /// 清单：分区标题语言键 + 标题颜色（#AARRGGBB）→ （显示名, 域名）。
    /// 域名只在弹窗里展示，点击时补 https:// 用默认浏览器打开。
    /// </summary>
    private static readonly (string SectionKey, string HeaderColor, (string Label, string Domain)[] Entries)[] CheatSites =
    [
        ("TreasureBox_CheatSites_Rage", "#FFEF4444",
        [
            ("GameSense", "gamesense.pub"),
            ("NeverLose", "neverlose.cc"),
            ("Fatality", "fatality.win"),
            ("Nixware", "nixware.cc"),
            ("Aimware", "aimware.net"),
            ("CK", "compkiller.net"),
            ("plaguecheat", "plaguecheat.cc")
        ]),
        ("TreasureBox_CheatSites_Semirage", "#FFF59E0B",
        [
            ("Pri", "primordial.dev")
        ]),
        ("TreasureBox_CheatSites_Legit", "#FF22C55E",
        [
            ("MemeSense", "memesense.gg"),
            ("midnight", "midnight.im"),
            ("Xone", "xone.fun")
        ])
    ];

    private async void CheatSitesButton_Click(object sender, RoutedEventArgs e)
    {
        // 弹窗沿用程序自带的 ContentDialog 样式（圆角、实色不透明底、随主题走），内容超过高度时可滚动。
        var dialog = new ContentDialog
        {
            Content = new ScrollViewer
            {
                Content = BuildCheatSitesGrid(),
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = Loc.T("Common_Close"),
            CloseButtonStyle = (Style)Application.Current.Resources["AuroraGlassButtonStyle"],
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        // 弹窗默认最大宽度只有 548（WinUI 的 ContentDialogMaxWidth），三列链接放不下会被裁掉；
        // 这里放宽到 920，保证每一行 3 条链接都能完整显示。
        dialog.Resources["ContentDialogMinWidth"] = 560d;
        dialog.Resources["ContentDialogMaxWidth"] = 920d;

        await dialog.ShowAsync();
    }

    /// <summary>「梯子推荐」：点击直接用系统默认浏览器打开（不弹窗）。</summary>
    private async void ProxySiteButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(ProxySiteUrl);
    }

    /// <summary>
    /// 弹窗正文：每行 3 组「英文标签：域名」，分区标题跨整行。
    /// 每条链接占两列（标签列 + 链接列）并三条共用：标签列 Auto + 标签右对齐 →
    /// 同一列的「：」全部落在同一条竖线上；域名紧接着「：」从左边排（也就是按「：」左对齐）。
    /// </summary>
    private static Grid BuildCheatSitesGrid()
    {
        const int ColumnsPerRow = 3;        // 每行 3 组
        const double LabelGap = 4;          // 「：」到域名之间的距离
        const double GroupGap = 18;         // 相邻两组之间的距离

        var grid = new Grid
        {
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        for (var column = 0; column < ColumnsPerRow; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 标签列
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 链接列
        }

        var nextRow = 0;
        foreach (var (sectionKey, headerColor, entries) in CheatSites)
        {
            // 分区标题：独占一行，按清单里的色值着色（Rage 红 / Semirage 琥珀 / Legit 绿）。
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock
            {
                Text = Loc.T(sectionKey),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, nextRow == 0 ? 0 : 6, 0, 0)
            };
            if (ParseColor(headerColor) is { } color)
            {
                header.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }

            Grid.SetRow(header, nextRow);
            Grid.SetColumn(header, 0);
            Grid.SetColumnSpan(header, ColumnsPerRow * 2);
            grid.Children.Add(header);

            var entryStart = nextRow + 1;
            for (var index = 0; index < entries.Length; index++)
            {
                if (index % ColumnsPerRow == 0)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                var (label, domain) = entries[index];
                var group = index % ColumnsPerRow;
                var row = entryStart + (index / ColumnsPerRow);

                var labelText = new TextBlock
                {
                    Text = label + "：",
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,   // 右对齐 → 冒号贴列右缘，成一竖行
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0, 0, LabelGap, 0)
                };
                Grid.SetRow(labelText, row);
                Grid.SetColumn(labelText, group * 2);
                grid.Children.Add(labelText);

                var link = new HyperlinkButton
                {
                    Content = domain,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,   // 域名从「：」右边开始左对齐
                    Margin = new Thickness(0, 0, group == ColumnsPerRow - 1 ? 0 : GroupGap, 0)
                };
                link.Click += async (_, _) => await AppState.OpenUrlAsync("https://" + domain);
                Grid.SetRow(link, row);
                Grid.SetColumn(link, (group * 2) + 1);
                grid.Children.Add(link);
            }

            nextRow = entryStart + ((entries.Length + ColumnsPerRow - 1) / ColumnsPerRow);
        }

        return grid;
    }
    // ---------- 主题色实色按钮（不用透明玻璃底） ----------

    /// <summary>把本页下方按钮刷成软件当前的实色主题色（跟随设置里的主题色）。</summary>
    private void ApplyThemeColorToButtons()
    {
        ApplySolidThemeColor(CheatSitesButton, CheatSitesLabel, CheatSitesIcon);
        ApplySolidThemeColor(ProxySiteButton, ProxySiteLabel, ProxySiteIcon);
    }

    /// <summary>
    /// 底色用不透明的主题色（不是 AuroraGlassButtonBrush / CustomUiButtonBrush 那种半透明玻璃），
    /// 文字与图标颜色按底色明度自动取深色或白色；悬停/按下各亮/暗一档。
    /// </summary>
    private static void ApplySolidThemeColor(Button button, TextBlock label, FontIcon icon)
    {
        var accent = AppState.UiColorService.BaseColor;
        var hover = Blend(accent, Microsoft.UI.Colors.White, 0.16);
        var pressed = Blend(accent, Microsoft.UI.Colors.Black, 0.14);
        var textColor = ReadableTextColor(accent);

        var background = new SolidColorBrush(accent);
        var foreground = new SolidColorBrush(textColor);

        button.Background = background;
        button.BorderBrush = background;
        button.Foreground = foreground;
        label.Foreground = foreground;
        icon.Foreground = foreground;

        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        button.Resources["ButtonBorderBrush"] = background;
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(hover);
        button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(pressed);
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    /// <summary>底色亮就配深字、底色暗就配白字，保证按钮文字始终看得清。</summary>
    private static Windows.UI.Color ReadableTextColor(Windows.UI.Color background)
    {
        var luminance = (0.2126 * Channel(background.R)) + (0.7152 * Channel(background.G)) + (0.0722 * Channel(background.B));
        return luminance > 0.62
            ? Windows.UI.Color.FromArgb(255, 32, 28, 60)
            : Microsoft.UI.Colors.White;

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

    /// <summary>把 "#AARRGGBB" 或 "#RRGGBB" 解析成颜色；解析失败返回 null（标题就保持默认前景色）。</summary>
    private static Windows.UI.Color? ParseColor(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6)
        {
            text = "FF" + text;
        }

        if (text.Length != 8 ||
            !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return Windows.UI.Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
    }
}