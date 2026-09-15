using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Controls;
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

    // 「好看的精品壁纸网站」：点击直接用系统默认浏览器打开。
    private const string WallpaperSiteUrl = "https://haowallpaper.com/";

    // 「枫喵 CS2 LUA 注入器」：点击直接用系统默认浏览器打开。
    private const string LuaInjectorUrl = "https://fneko.icu";
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
            ("Xone", "xone.fun"),
            ("褪黑素", "melatonin.win")
        ])
    ];

    /// <summary>
    /// 推荐卡网：标签 + 链接（点击用系统默认浏览器打开）。弹窗样式与「查看Cheat官网」完全一致，
    /// 只是每行一组（标签偏长）。链接文本按商家给的原样显示；含中文的域名在打开时才做 IDN 规范化。
    /// </summary>
    private static readonly (string SectionKey, string HeaderColor, (string Label, string Url)[] Entries)[] CardSites =
    [
        ("", "", [
            ("小泽代理(CS2白号，各别Cheat)", "accountcheat.xzhvh.cc"),
            ("奶味卡网(优先黑号)", "奶味.cc"),
            ("爱代购(SK、NL、Pri、午夜)", "爱代购.com"),
            ("1t FA续费", "shop.fumo.cat")
        ])
    ];

    /// <summary>HVH 服务器：标签 + 链接，弹窗与上面几个清单同一套外观。</summary>
    private static readonly (string SectionKey, string HeaderColor, (string Label, string Url)[] Entries)[] HvhServers =
    [
        ("", "", [
            ("Flux", "https://cshvh.cn/servers"),
            ("5x5平台", "https://mmhvh.cn"),
            ("HVH名人堂", "https://cs.hvh.one"),
            ("457 HVH", "https://457hvh.com"),
            ("RW0TER(Meme插件)", "https://rw0ter.tech"),
            ("低调余-武装直升机", "dev.武装直升机.vip"),
            ("茶社CSGO", "https://www.teahvh.cc/servers")
        ])
    ];

    private async void CardSitesButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSitesDialogAsync(BuildSitesGrid(CardSites, 1));
    }

    private async void HvhServersButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSitesDialogAsync(BuildSitesGrid(HvhServers, 1));
    }

    private async void CheatSitesButton_Click(object sender, RoutedEventArgs e)
    {
        // 与「推荐卡网」「HVH论坛」共用同一套弹窗：关闭按钮居中、宽度放宽到 920 以便放下三列链接。
        // （以前这里是单独一份 ContentDialog，关闭按钮留在右下角，和另外两个不一致。）
        await ShowSitesDialogAsync(BuildSitesGrid(CheatSites, 3));
    }

    /// <summary>「梯子推荐」：点击直接用系统默认浏览器打开（不弹窗）。</summary>
    private async void ProxySiteButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(ProxySiteUrl);
    }

    /// <summary>「好看的精品壁纸网站」：点击直接用系统默认浏览器打开（不弹窗）。</summary>
    private async void WallpaperSiteButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(WallpaperSiteUrl);
    }

    /// <summary>「枫喵 CS2 LUA 注入器」：点击直接用系统默认浏览器打开（不弹窗）。</summary>
    private async void LuaInjectorButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(LuaInjectorUrl);
    }

    /// <summary>
    /// 弹窗正文：每行 3 组「英文标签：域名」，分区标题跨整行。
    /// 每条链接占两列（标签列 + 链接列）并三条共用：标签列 Auto + 标签右对齐 →
    /// 同一列的「：」全部落在同一条竖线上；域名紧接着「：」从左边排（也就是按「：」左对齐）。
    /// </summary>
    private static Grid BuildSitesGrid(
        (string SectionKey, string HeaderColor, (string Label, string Url)[] Entries)[] sections,
        int columnsPerRow)
    {
        const double LabelGap = 4;          // 「：」到域名之间的距离
        const double GroupGap = 18;         // 相邻两组之间的距离

        var grid = new Grid
        {
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        for (var column = 0; column < columnsPerRow; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 标签列
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 链接列
        }

        var nextRow = 0;
        foreach (var (sectionKey, headerColor, entries) in sections)
        {
            var entryStart = nextRow;
            if (!string.IsNullOrEmpty(sectionKey))
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
                Grid.SetColumnSpan(header, columnsPerRow * 2);
                grid.Children.Add(header);

                entryStart = nextRow + 1;
            }

            for (var index = 0; index < entries.Length; index++)
            {
                if (index % columnsPerRow == 0)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                var (label, url) = entries[index];
                var group = index % columnsPerRow;
                var row = entryStart + (index / columnsPerRow);

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
                    // 显示只留域名：去掉 https:// 前缀与 /路径（https://cshvh.cn/servers → cshvh.cn）；
                    // 点开时仍用清单里的完整地址，保证能正常访问。
                    Content = DisplayUrl(url),
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,   // 域名从「：」右边开始左对齐
                    Margin = new Thickness(0, 0, group == columnsPerRow - 1 ? 0 : GroupGap, 0)
                };
                link.Click += async (_, _) => await AppState.OpenUrlAsync(ToAbsoluteUrl(url));
                Grid.SetRow(link, row);
                Grid.SetColumn(link, (group * 2) + 1);
                grid.Children.Add(link);
            }

            nextRow = entryStart + ((entries.Length + columnsPerRow - 1) / columnsPerRow);
        }

        return grid;
    }

    /// <summary>把链接简化成只显示域名：去掉协议前缀与路径部分；清单里没写协议的（如 奶味.cc）原样返回。</summary>
    private static string DisplayUrl(string url)
    {
        var text = url.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        var slash = text.IndexOf('/');
        return slash >= 0 ? text[..slash] : text;
    }

    /// <summary>
    /// 把清单里的链接文本变成能直接打开的 URL：没写协议就补 https://；
    /// 域名含中文的（如 奶味.cc）交给 Uri 做 IDN 规范化，否则系统浏览器可能打不开。
    /// </summary>
    private static string ToAbsoluteUrl(string url)
    {
        var text = url.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        try
        {
            // 域名含中文（奶味.cc / 爱代购.com）时要转成 punycode 再交给浏览器：
            // AbsoluteUri 会保留中文，部分环境下浏览器打不开。
            var uri = new Uri("https://" + text);
            var builder = new UriBuilder(uri) { Host = uri.IdnHost };
            return builder.Uri.AbsoluteUri;
        }
        catch
        {
            return "https://" + text;
        }
    }

    /// <summary>
    /// 弹出「标签：链接」清单（Cheat 官网 / 推荐卡网 / HVH论坛共用同一套外观）。
    /// 关闭按钮做成内容区里的居中按钮：ContentDialog 自带的 CloseButton 被模板固定在右下角，
    /// 改不成居中，所以不用它。
    /// </summary>
    private async Task ShowSitesDialogAsync(Grid content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot
        };

        var closeButton = new Button
        {
            Content = Loc.T("Common_Close"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 120,
            Style = (Style)Application.Current.Resources["AuroraGlassButtonStyle"]
        };
        closeButton.Click += (_, _) => dialog.Hide();

        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 420,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                closeButton
            }
        };

        dialog.Resources["ContentDialogMinWidth"] = 560d;
        dialog.Resources["ContentDialogMaxWidth"] = 920d;
        await dialog.ShowAsync();
    }
    // ---------- 主题色实色按钮（不用透明玻璃底） ----------

    /// <summary>把本页下方按钮刷成软件当前的实色主题色（跟随设置里的主题色）。</summary>
    private void ApplyThemeColorToButtons()
    {
        ApplySolidThemeColor(CheatSitesButton, CheatSitesLabel, CheatSitesIcon);
        ApplySolidThemeColor(ProxySiteButton, ProxySiteLabel, ProxySiteIcon);
        // 壁纸网站按钮与「梯子推荐」同款：同样的实色主题底 + 自动深浅文字。
        ApplySolidThemeColor(WallpaperSiteButton, WallpaperSiteLabel, WallpaperSiteIcon);
        ApplySolidThemeColor(CardSitesButton, CardSitesLabel, CardSitesIcon);
        ApplySolidThemeColor(HvhServersButton, HvhServersLabel, HvhServersIcon);
        ApplySolidThemeColor(LuaInjectorButton, LuaInjectorLabel, LuaInjectorIcon);
    }

    /// <summary>
    /// 底色用不透明的主题色（不是 AuroraGlassButtonBrush / CustomUiButtonBrush 那种半透明玻璃），
    /// 文字与图标颜色按底色明度自动取深色或白色；悬停/按下各亮/暗一档。
    /// </summary>
    private static void ApplySolidThemeColor(Button button, TextBlock label, FontIcon icon) =>
        AuroraAccentTheme.ApplySolidAccent(button, label, icon);

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