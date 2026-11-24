using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Pages;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private static readonly string[] ThemeCodes = ["Default", "Light", "Dark"];
    // 自定义背景可选的文件类型：图片（含 GIF）+ 视频（静音循环播放）。
    private static readonly string[] BackgroundImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];
    private static readonly string[] IntroVideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    // 代码设置 ComboBox.SelectedItem 会触发 SelectionChanged，置位以区分“用户选择”与“初始同步”，避免回写/重复应用。
    private bool _syncing;
    private bool _languageMenuBuilt;
    private string _selectedLanguageCode = "zh-Hans";
    private string _selectedThemeCode = "Dark";
    private bool _movingDataFolder;
    private bool _pickingBackground;
    private bool _pickingIntroVideo;

    public SettingsPage()
    {
        // XAML 初始化 Slider/Toggle 时会触发事件；暂时屏蔽，避免首帧用默认值覆盖已保存设置。
        _syncing = true;
        InitializeComponent();

        BuildLanguageMenu();
        BuildThemeMenu();
        // 语言切换后，让本页所有 {x:Bind Strings.Get(...), Mode=OneWay} 重新求值（主题项文本等）。
        Loc.LanguageChanged += OnLanguageChanged;
        _syncing = false;

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BuildLanguageMenu();
        SyncFromSettings();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));

            UpdateThemeMenuTexts();
            UpdateChoiceButtonTexts();
            UpdateCustomBackgroundControls();
            UpdateIntroVideoControls();

            // 未设置时 SteamPathText 显示的是本地化占位文案，需随语言刷新（已设置时是中性路径，刷新无副作用）。
            UpdateSteamPathText();

            // 来源账号候选/选中文本用 Settings_Cs2Sync_SourceItem_Format 拼装，跟随语言重建。
            RefreshCs2SyncSources();
        });
    }

    /// <summary>按已加载的语言包动态生成语言菜单（只建一次）。语言自称名不随界面语言变化。</summary>
    private void BuildLanguageMenu()
    {
        if (_languageMenuBuilt)
        {
            return;
        }

        foreach (var pack in Loc.AvailablePacks)
        {
            var item = new MenuFlyoutItem
            {
                Text = pack.Name,
                Tag = pack.Code
            };
            item.Click += LanguageMenuItem_Click;
            LanguageFlyout.Items.Add(item);
        }

        _languageMenuBuilt = true;
    }

    private void BuildThemeMenu()
    {
        foreach (var code in ThemeCodes)
        {
            var item = new MenuFlyoutItem
            {
                Text = ThemeDisplayName(code),
                Tag = code
            };
            item.Click += ThemeMenuItem_Click;
            ThemeFlyout.Items.Add(item);
        }
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string code })
        {
            return;
        }

        _selectedLanguageCode = code;
        LanguageButtonText.Text = sender is MenuFlyoutItem item ? item.Text : code;
        Loc.SetLanguage(code);
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string theme })
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        if (string.Equals(settings.Theme, theme, StringComparison.OrdinalIgnoreCase))
        {
            _selectedThemeCode = theme;
            ThemeButtonText.Text = ThemeDisplayName(theme);
            return;
        }

        _selectedThemeCode = theme;
        ThemeButtonText.Text = ThemeDisplayName(theme);
        settings.Theme = theme;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);

        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            RestartApplication();
        }
    }

    private void UpdateChoiceButtonTexts()
    {
        LanguageButtonText.Text = Loc.AvailablePacks
            .FirstOrDefault(pack => string.Equals(pack.Code, _selectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? _selectedLanguageCode;
        ThemeButtonText.Text = ThemeDisplayName(_selectedThemeCode);
    }

    private void UpdateThemeMenuTexts()
    {
        foreach (var item in ThemeFlyout.Items.OfType<MenuFlyoutItem>())
        {
            if (item.Tag is string code)
            {
                item.Text = ThemeDisplayName(code);
            }
        }
    }

    private static string ThemeDisplayName(string code) => code switch
    {
        "Light" => Loc.T("Settings_Theme_Light"),
        "Dark" => Loc.T("Settings_Theme_Dark"),
        _ => Loc.T("Settings_Theme_System")
    };

    private void ApplyThemeSettings(AppSettings settings)
    {
        MainWindow.Instance?.ApplyTheme(UiColorService.ResolveElementTheme(settings.Theme, settings.UiColor));
        App.ApplyTableSeparatorColor(
            settings.TableSeparatorColor,
            UiColorService.ResolveEffectiveTheme(settings.Theme, settings.UiColor),
            settings.ShowTableSeparators);
        AppState.UiColorService.Apply(settings.UiColor, settings.UiColorAnimated, settings.Theme);
        TableSeparatorColorSwatch.Background = new SolidColorBrush(App.GetTableSeparatorColor());
    }

    /// <summary>浅色/深色切换后启动新进程，并结束当前进程以完整重建系统标题栏与窗口主题。</summary>
    private static void RestartApplication()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("当前进程路径不可用。");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("系统未能创建新的应用进程。");
        }
        catch (Exception ex)
        {
            AppLog.Error("切换主题后自动重启失败。", ex);
            AppState.ShowStatus(
                Loc.Tf("Settings_Theme_RestartFailed_Format", ex.Message),
                InfoBarSeverity.Error);
            return;
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// 打开「选择显示的页面」浮出层时按当前导航项现搭勾选列表：
    /// 标题直接取导航项文案（跟随语言），「设置」页固定勾选且不可取消（否则改不回来）。
    /// </summary>
    private void NavPagesFlyout_Opened(object sender, object e)
    {
        NavPagesList.Children.Clear();
        var hidden = AppState.SettingsService.Load().HiddenNavPages ?? [];

        foreach (var entry in MainWindow.Instance?.GetNavPageEntries() ?? [])
        {
            var box = new CheckBox
            {
                Content = entry.Title,
                Tag = entry.Tag,
                IsChecked = entry.AlwaysVisible || !hidden.Contains(entry.Tag),
                IsEnabled = !entry.AlwaysVisible,
            };
            box.Checked += NavPageCheckBox_Changed;
            box.Unchecked += NavPageCheckBox_Changed;
            NavPagesList.Children.Add(box);
        }
    }

    /// <summary>勾选变化：写盘并让主窗口立刻按新选择显示 / 隐藏导航项（启动页也按它挑）。</summary>
    private void NavPageCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var hidden = NavPagesList.Children
            .OfType<CheckBox>()
            .Where(box => box.Tag is string && box.IsChecked == false)
            .Select(box => (string)box.Tag!)
            .ToList();

        var settings = AppState.SettingsService.Load();
        settings.HiddenNavPages = hidden;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyNavPageVisibility();
    }

    /// <summary>按当前语言与已保存设置同步各选择控件。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            var settings = AppState.SettingsService.Load();
            _selectedLanguageCode = Loc.AvailablePacks.Any(pack => string.Equals(pack.Code, Loc.CurrentCode, StringComparison.OrdinalIgnoreCase))
                ? Loc.CurrentCode
                : Loc.AvailablePacks.FirstOrDefault()?.Code ?? "zh-Hans";
            _selectedThemeCode = ThemeCodes.Contains(settings.Theme, StringComparer.OrdinalIgnoreCase) ? settings.Theme : "Default";
            if (!string.Equals(settings.Theme, _selectedThemeCode, StringComparison.OrdinalIgnoreCase))
            {
                // 「自定义」主题已从主题卡片移除；旧配置统一回落到跟随系统。
                settings.Theme = _selectedThemeCode;
                AppState.SettingsService.Save(settings);
                ApplyThemeSettings(settings);
            }

            UpdateChoiceButtonTexts();

            WindowWidthBox.Text = Math.Clamp(settings.WindowWidth ?? MainWindow.DefaultWindowWidth, MainWindow.MinimumWindowWidth, MainWindow.MaximumWindowWidth).ToString(CultureInfo.InvariantCulture);
            WindowHeightBox.Text = Math.Clamp(settings.WindowHeight ?? MainWindow.DefaultWindowHeight, MainWindow.MinimumWindowHeight, MainWindow.MaximumWindowHeight).ToString(CultureInfo.InvariantCulture);
            var separatorColor = App.GetTableSeparatorColor();
            TableSeparatorColorPicker.Color = separatorColor;
            TableSeparatorColorSwatch.Background = new SolidColorBrush(separatorColor);
            var uiColor = AppState.UiColorService.BaseColor;
            UiColorPicker.Color = uiColor;
            UiColorSwatch.Background = new SolidColorBrush(uiColor);
            AnimateUiColorToggle.IsOn = settings.UiColorAnimated;
            IntroVideoToggle.IsOn = settings.IntroVideoEnabled;
            UpdateIntroVideoControls();
            RememberWindowPositionToggle.IsOn = settings.RememberWindowPosition;
            TableSeparatorToggle.IsOn = settings.ShowTableSeparators;
            UpdateTableSeparatorControls();
            CustomBackgroundToggle.IsOn = settings.CustomBackgroundEnabled && AppState.SettingsService.GetCustomBackgroundImagePath(settings) is not null;
            CustomBackgroundOpacitySlider.Value = Math.Clamp(settings.CustomBackgroundOpacity, 0.1, 0.9);
            UpdateCustomBackgroundControls();
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
        RefreshCs2SyncSources();
    }

    private async void CustomBackgroundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        if (CustomBackgroundToggle.IsOn && AppState.SettingsService.GetCustomBackgroundImagePath(settings) is null)
        {
            if (!await PickAndApplyCustomBackgroundAsync())
            {
                _syncing = true;
                try
                {
                    CustomBackgroundToggle.IsOn = false;
                }
                finally
                {
                    _syncing = false;
                }

                settings.CustomBackgroundEnabled = false;
                AppState.SettingsService.Save(settings);
                MainWindow.Instance?.ApplyCustomBackground(settings);
                UpdateCustomBackgroundControls();
            }

            return;
        }

        settings.CustomBackgroundEnabled = CustomBackgroundToggle.IsOn;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyCustomBackground(settings);
             UpdateCustomBackgroundControls();
    }

    private async void ChooseCustomBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndApplyCustomBackgroundAsync();
    }

    private async Task<bool> PickAndApplyCustomBackgroundAsync()
    {
        if (_pickingBackground)
        {
            return false;
        }

        _pickingBackground = true;
             UpdateCustomBackgroundControls();
        try
        {
            var picker = new FileOpenPicker
            {
                // 现在背景也支持视频，起始位置用「此电脑」而不是图片库，免得选视频时还要手动翻目录。
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.Thumbnail
            };
            foreach (var extension in BackgroundImageExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开背景图片选择器失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Background_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            if (file is null || string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            string fileName;
            try
            {
                var sourcePath = file.Path;
                fileName = await Task.Run(() => AppState.SettingsService.ImportCustomBackgroundImage(sourcePath));
            }
            catch (Exception ex)
            {
                AppLog.Error("导入自定义背景图片失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Background_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            var settings = AppState.SettingsService.Load();
            settings.CustomBackgroundImageFileName = fileName;
            settings.CustomBackgroundEnabled = true;
            settings.CustomBackgroundOpacity = Math.Clamp(CustomBackgroundOpacitySlider.Value, 0.1, 0.9);
            AppState.SettingsService.Save(settings);

            _syncing = true;
            try
            {
                CustomBackgroundToggle.IsOn = true;
                CustomBackgroundOpacitySlider.Value = settings.CustomBackgroundOpacity;
            }
            finally
            {
                _syncing = false;
            }

            MainWindow.Instance?.ApplyCustomBackground(settings);
            AppState.SettingsService.DeleteOtherCustomBackgroundImages(fileName);
            return true;
        }
        finally
        {
            _pickingBackground = false;
            UpdateCustomBackgroundControls();
        }
    }

    private void ClearCustomBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundEnabled = false;
        settings.CustomBackgroundImageFileName = null;
        AppState.SettingsService.Save(settings);
        MainWindow.Instance?.ApplyCustomBackground(settings);
        AppState.SettingsService.DeleteCustomBackgroundImages();

        _syncing = true;
        try
        {
            CustomBackgroundToggle.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }

            UpdateCustomBackgroundControls();
    }

    private void IntroVideoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.IntroVideoEnabled = IntroVideoToggle.IsOn;
        AppState.SettingsService.Save(settings);
        UpdateIntroVideoControls();
    }

    private async void ChooseIntroVideoButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndApplyIntroVideoAsync();
        ChooseIntroVideoButton.Focus(FocusState.Programmatic);
    }

    private async Task<bool> PickAndApplyIntroVideoAsync()
    {
        if (_pickingIntroVideo)
        {
            return false;
        }

        _pickingIntroVideo = true;
        UpdateIntroVideoControls();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                ViewMode = PickerViewMode.Thumbnail
            };
            foreach (var extension in IntroVideoExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开入场动画选择器失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Intro_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            if (file is null || string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            string fileName;
            try
            {
                var sourcePath = file.Path;
                fileName = await Task.Run(() => AppState.SettingsService.ImportCustomIntroVideo(sourcePath));
            }
            catch (Exception ex)
            {
                AppLog.Error("导入自定义入场动画失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Intro_ChooseFail"), InfoBarSeverity.Error);
                return false;
            }

            var settings = AppState.SettingsService.Load();
            settings.CustomIntroVideoFileName = fileName;
            settings.CustomIntroVideoDisplayName = file.Name;
            settings.IntroVideoEnabled = true;
            AppState.SettingsService.Save(settings);

            _syncing = true;
            try
            {
                IntroVideoToggle.IsOn = true;
            }
            finally
            {
                _syncing = false;
            }

            AppState.SettingsService.DeleteOtherCustomIntroVideos(fileName);
            UpdateIntroVideoControls();
            return true;
        }
        finally
        {
            _pickingIntroVideo = false;
            UpdateIntroVideoControls();
        }
    }

    private void ResetIntroVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.CustomIntroVideoFileName = null;
        settings.CustomIntroVideoDisplayName = null;
        settings.IntroVideoEnabled = AppSettings.DefaultIntroVideoEnabled;
        AppState.SettingsService.Save(settings);
        AppState.SettingsService.DeleteCustomIntroVideos();

        _syncing = true;
        try
        {
            IntroVideoToggle.IsOn = settings.IntroVideoEnabled;
        }
        finally
        {
            _syncing = false;
        }

        UpdateIntroVideoControls();
        ChooseIntroVideoButton.Focus(FocusState.Programmatic);
    }

    private void UpdateIntroVideoControls()
    {
        var settings = AppState.SettingsService.Load();
        var hasCustomVideo = AppState.SettingsService.GetCustomIntroVideoPath(settings) is not null;
        IntroVideoSourceText.Text = hasCustomVideo
            ? Loc.Tf(
                "Settings_Intro_CurrentCustom_Format",
                settings.CustomIntroVideoDisplayName ?? Loc.T("Settings_Intro_CustomFallback"))
            : Loc.T("Settings_Intro_CurrentDefault");
        ChooseIntroVideoButton.IsEnabled = !_pickingIntroVideo;
        ResetIntroVideoButton.IsEnabled = !_pickingIntroVideo &&
            (!string.IsNullOrWhiteSpace(settings.CustomIntroVideoFileName) || !IntroVideoToggle.IsOn);
    }
    private void ResetCustomBackgroundOpacityButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundOpacity = AppSettings.DefaultCustomBackgroundOpacity;
        AppState.SettingsService.Save(settings);

        _syncing = true;
        try
        {
            CustomBackgroundOpacitySlider.Value = AppSettings.DefaultCustomBackgroundOpacity;
        }
        finally
        {
            _syncing = false;
        }

        if (settings.CustomBackgroundEnabled)
        {
            MainWindow.Instance?.ApplyCustomBackground(settings);
        }
    }

    private void CustomBackgroundOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.CustomBackgroundOpacity = Math.Clamp(e.NewValue, 0.1, 0.9);
        AppState.SettingsService.Save(settings);
        if (settings.CustomBackgroundEnabled)
        {
            MainWindow.Instance?.ApplyCustomBackground(settings);
        }
    }

    private void UpdateCustomBackgroundControls()
    {
        var settings = AppState.SettingsService.Load();
        var hasImage = AppState.SettingsService.GetCustomBackgroundImagePath(settings) is not null;
        CustomBackgroundFileNameText.Text = Loc.T(hasImage
            ? "Settings_Background_Selected"
            : "Settings_Background_None");
        ChooseCustomBackgroundButton.IsEnabled = !_pickingBackground;
        ClearCustomBackgroundButton.IsEnabled = hasImage && !_pickingBackground;
        CustomBackgroundOpacitySlider.IsEnabled = hasImage && CustomBackgroundToggle.IsOn;
        ResetCustomBackgroundOpacityButton.IsEnabled = hasImage && CustomBackgroundToggle.IsOn;
    }

    /// <summary>显示当前持久化的 Steam 安装目录；未设置时显示占位文案（启动会自动检测）。</summary>
    private void UpdateSteamPathText()
    {
        SteamPathText.Text = SteamPathCoordinator.GetPersistedInstallPath() ?? Loc.T("Settings_SteamPath_NotSet");
    }
    private void ApplyWindowSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseWindowDimension(WindowWidthBox.Text, MainWindow.MinimumWindowWidth, MainWindow.MaximumWindowWidth, out var width) ||
            !TryParseWindowDimension(WindowHeightBox.Text, MainWindow.MinimumWindowHeight, MainWindow.MaximumWindowHeight, out var height))
        {
            AppState.ShowStatus(Loc.T("Settings_WindowSize_Invalid"), InfoBarSeverity.Error);
            return;
        }

        ApplyAndPersistWindowSize(width, height);
    }

    private void RememberWindowPositionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.RememberWindowPosition = RememberWindowPositionToggle.IsOn;
        if (settings.RememberWindowPosition && MainWindow.Instance is { } window)
        {
            var position = window.GetCurrentWindowPosition();
            settings.WindowX = position.X;
            settings.WindowY = position.Y;
        }
        else if (!settings.RememberWindowPosition)
        {
            settings.WindowX = null;
            settings.WindowY = null;
        }

        AppState.SettingsService.Save(settings);
    }
    private void IncreaseWindowWidthButton_Click(object sender, RoutedEventArgs e) => AdjustWindowWidth(20);

    private void DecreaseWindowWidthButton_Click(object sender, RoutedEventArgs e) => AdjustWindowWidth(-20);

    private void IncreaseWindowHeightButton_Click(object sender, RoutedEventArgs e) => AdjustWindowHeight(20);

    private void DecreaseWindowHeightButton_Click(object sender, RoutedEventArgs e) => AdjustWindowHeight(-20);

    private void AdjustWindowWidth(int delta)
    {
        var value = TryParseWindowDimension(
            WindowWidthBox.Text,
            MainWindow.MinimumWindowWidth,
            MainWindow.MaximumWindowWidth,
            out var current)
            ? current
            : MainWindow.DefaultWindowWidth;
        WindowWidthBox.Text = Math.Clamp(value + delta, MainWindow.MinimumWindowWidth, MainWindow.MaximumWindowWidth)
            .ToString(CultureInfo.InvariantCulture);
    }

    private void AdjustWindowHeight(int delta)
    {
        var value = TryParseWindowDimension(
            WindowHeightBox.Text,
            MainWindow.MinimumWindowHeight,
            MainWindow.MaximumWindowHeight,
            out var current)
            ? current
            : MainWindow.DefaultWindowHeight;
        WindowHeightBox.Text = Math.Clamp(value + delta, MainWindow.MinimumWindowHeight, MainWindow.MaximumWindowHeight)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParseWindowDimension(string? text, int minimum, int maximum, out int value)
    {
        value = 0;
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            !int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        value = Math.Clamp(parsed, minimum, maximum);
        return true;
    }
    private void ResetWindowSizeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyAndPersistWindowSize(MainWindow.DefaultWindowWidth, MainWindow.DefaultWindowHeight);
    }

    private void ApplyAndPersistWindowSize(int width, int height)
    {
        if (MainWindow.Instance is not { } window)
        {
            return;
        }

        var actual = window.ApplyWindowSize(width, height);
        var settings = AppState.SettingsService.Load();
        settings.WindowWidth = actual.Width;
        settings.WindowHeight = actual.Height;
        AppState.SettingsService.Save(settings);

        _syncing = true;
        try
        {
            WindowWidthBox.Text = actual.Width.ToString(CultureInfo.InvariantCulture);
            WindowHeightBox.Text = actual.Height.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }

        AppState.ShowStatus(
            Loc.Tf("Settings_WindowSize_Applied_Format", actual.Width, actual.Height),
            InfoBarSeverity.Success);
    }
    private void TableSeparatorColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        var color = args.NewColor;
        var hex = $"# {color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}".Replace(" ", "");
        var settings = AppState.SettingsService.Load();
        settings.TableSeparatorColor = hex;
        AppState.SettingsService.Save(settings);
        App.ApplyTableSeparatorColor(hex, UiColorService.ResolveEffectiveTheme(settings.Theme, settings.UiColor), settings.ShowTableSeparators);
        TableSeparatorColorSwatch.Background = new SolidColorBrush(color);
    }

    private void ResetTableSeparatorColorButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.TableSeparatorColor = null;
        AppState.SettingsService.Save(settings);
        App.ApplyTableSeparatorColor(null, UiColorService.ResolveEffectiveTheme(settings.Theme, settings.UiColor), settings.ShowTableSeparators);
        var color = App.GetTableSeparatorColor();
        _syncing = true;
        try
        {
            TableSeparatorColorPicker.Color = color;
        }
        finally
        {
            _syncing = false;
        }
        TableSeparatorColorSwatch.Background = new SolidColorBrush(color);
    }
    private void TableSeparatorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.ShowTableSeparators = TableSeparatorToggle.IsOn;
        AppState.SettingsService.Save(settings);
        App.ApplyTableSeparatorColor(settings.TableSeparatorColor, UiColorService.ResolveEffectiveTheme(settings.Theme, settings.UiColor), settings.ShowTableSeparators);
        UpdateTableSeparatorControls();
    }

    private void UpdateTableSeparatorControls()
    {
        TableSeparatorColorButton.IsEnabled = TableSeparatorToggle.IsOn;
    }
    private void UiColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        ApplyCustomUiColor(args.NewColor);

    private void ApplyCustomUiColor(Color color)
    {
        if (_syncing)
        {
            return;
        }

        var hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        var settings = AppState.SettingsService.Load();
        settings.UiColor = hex;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);

        _syncing = true;
        try
        {
            UiColorPicker.Color = color;
        }
        finally
        {
            _syncing = false;
        }

        UiColorSwatch.Background = new SolidColorBrush(color);
    }

    private void AnimateUiColorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.UiColorAnimated = AnimateUiColorToggle.IsOn;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);
    }

    private void ResetUiColorButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        settings.UiColor = null;
        settings.UiColorAnimated = false;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);

        var color = AppState.UiColorService.BaseColor;
        _syncing = true;
        try
        {
            UiColorPicker.Color = color;
            AnimateUiColorToggle.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }

        UiColorSwatch.Background = new SolidColorBrush(color);
    }

    /// <summary>手动更改上号使用的 Steam 安装目录（多 Steam 时指定要用哪一个）。选择器+校验+持久化都在协调器里。</summary>
    private async void ChangeSteamPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SteamPathCoordinator.PickAndPersistManuallyAsync())
        {
            UpdateSteamPathText();
            AppState.ShowStatus(Loc.T("Settings_SteamPath_Changed"), InfoBarSeverity.Success);
        }
    }

    // ---------- CS2 设置同步（issue #10）：来源账号 + 登录时强推 + 立即推送 ----------

    /// <summary>来源账号候选项：本机 Steam 缓存中具备 CS2 云配置的账号。</summary>
    private sealed record Cs2SourceOption(string SteamId64, string? Name, string Display);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // 已保存的来源账号 SteamID64 的本地镜像，避免各处反复 Load 设置来判断“选中了谁”。
    private string? _cs2SourceSteamId;

    /// <summary>
    /// 重建来源账号候选并刷新下拉菜单：只读取本机 Steam userdata 中确实存在 CS2 配置的账号。
    /// </summary>
    private void RefreshCs2SyncSources()
    {
        _cs2SourceOptions = BuildCs2SourceOptions();
        ApplyCs2SyncSourceState(clearInvalidSelection: false);
    }

    /// <summary>把重查结果写回下拉；显式刷新时同步清掉已不在本机缓存中的旧来源。</summary>
    private void ApplyCs2SyncSourceState(bool clearInvalidSelection)
    {
        var settings = AppState.SettingsService.Load();
        Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
        _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

        if (clearInvalidSelection &&
            !string.IsNullOrWhiteSpace(_cs2SourceSteamId) &&
            !_cs2SourceOptions.Any(option =>
                string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase)))
        {
            _cs2SourceSteamId = null;
            settings.Cs2SyncSourceSteamId = null;
            AppState.SettingsService.Save(settings);
        }

        RebuildCs2SourceFlyout();
    }

    /// <summary>只列出本机 Steam userdata 中具备 CS2 云配置的账号，并用本机缓存离线补全名称。</summary>
    private List<Cs2SourceOption> BuildCs2SourceOptions()
    {
        try
        {
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();
            var sources = AppState.Cs2CloudService.EnumerateSources(paths.UserdataPath);
            var offlineNames = SteamAccountNameService.BuildOfflineNames(paths, sources);

            return sources
                .Select(source =>
                {
                    offlineNames.TryGetValue(source.SteamId64, out var offline);
                    var name = FirstNonEmpty(offline?.PersonaName, offline?.AccountName);
                    if (name is not null &&
                        string.Equals(name, source.SteamId64, StringComparison.OrdinalIgnoreCase))
                    {
                        name = null;
                    }

                    var display = name is null
                        ? source.SteamId64
                        : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, source.SteamId64);
                    return new Cs2SourceOption(source.SteamId64, name, display);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取本机 Steam CS2 缓存账号失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>把候选项灌进下拉菜单（控件样式与上方语言按钮一致）。</summary>
    private void RebuildCs2SourceFlyout()
    {
        Cs2SyncSourceFlyout.Items.Clear();
        foreach (var option in _cs2SourceOptions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = option.Display,
                Tag = option,
                GroupName = "Cs2SyncSource",
                IsChecked = string.Equals(
                    option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += Cs2SyncSourceMenuItem_Click;
            Cs2SyncSourceFlyout.Items.Add(item);
        }

        Cs2SyncSourceButton.IsEnabled = _cs2SourceOptions.Count > 0;
        UpdateCs2SourceButtonText();
    }

    /// <summary>按钮文本：未选择显示占位；已选账号已不在候选里（被删了）时回退显示 Steam64。</summary>
    private void UpdateCs2SourceButtonText()
    {
        if (string.IsNullOrWhiteSpace(_cs2SourceSteamId))
        {
            Cs2SyncSourceText.Text = Loc.T("Settings_Cs2Sync_Source_None");
            return;
        }

        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase));
        Cs2SyncSourceText.Text = option?.Display ?? _cs2SourceSteamId;
    }

    /// <summary>点选来源账号：写入设置（重复选择同一账号不重复写盘）。</summary>
    private void Cs2SyncSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Cs2SourceOption option })
        {
            return;
        }

        if (string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cs2SourceSteamId = option.SteamId64;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = option.SteamId64;
        AppState.SettingsService.Save(settings);
        UpdateCs2SourceButtonText();
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;
    private void Cs2SyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncOnLogin = Cs2SyncToggle.IsOn;
        AppState.SettingsService.Save(settings);
    }

    private async void Cs2SyncRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Cs2SyncRefreshButton.IsEnabled = false;
        try
        {
            // 主动刷新时重新解析路径：持久化路径失效会重新自动探测，完全找不到时再让用户选择。
            if (!await SteamPathCoordinator.EnsureResolvedAsync())
            {
                AppState.ShowStatus(Loc.T("SteamPath_Error_NotResolved"), InfoBarSeverity.Error);
                return;
            }

            _cs2SourceOptions = await Task.Run(BuildCs2SourceOptions);
            ApplyCs2SyncSourceState(clearInvalidSelection: true);

            AppState.ShowStatus(
                Loc.Tf("Legacy_Status_Refreshed_Format", _cs2SourceOptions.Count),
                _cs2SourceOptions.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            Cs2SyncRefreshButton.IsEnabled = true;
        }
    }

    private async void Cs2SyncPushNowButton_Click(object sender, RoutedEventArgs e)
    {
        var source = AppState.SettingsService.Load().Cs2SyncSourceSteamId;
        if (string.IsNullOrWhiteSpace(source))
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Error_NoSourceSelected"), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
        // 推送期间禁用按钮，避免重复点击排队多次串行推送。
        Cs2SyncPushNowButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                    var activeAccount = new SteamConfigService().GetActiveLoginAccount(paths);
                    if (activeAccount is not null)
                    {
                        AppState.LoginService.TrySyncCs2UserdataFolders(paths, activeAccount.SteamId, null);
                    }

                    return AppState.Cs2CloudService.PushSourceNow(paths, source);
                }
                catch (Exception ex)
                {
                    return new Cs2CloudPushResult(false, 0, ex.Message);
                }
            });

            var severity = !result.Ok
                ? InfoBarSeverity.Error
                : result.AccountCloudDisabled ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            AppState.ShowStatus(Cs2CloudService.DescribeResult(result), severity);
        }
        finally
        {
            Cs2SyncPushNowButton.IsEnabled = true;
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppState.SettingsService.AppFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            // explorer.exe 接受目录路径作参数直接打开资源管理器；比 ShellExecute 文件夹更稳。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_OpenFail"), InfoBarSeverity.Error);
        }
    }
    /// <summary>
    /// 「切换数据目录（不迁移）」：只把程序的数据目录指向选中的文件夹，不复制、不删除任何文件。
    /// 与「移动」的区别：移动是复制全部数据再删掉旧目录；这里新旧目录都原地不动，
    /// 新位置已有的数据文件会被直接使用，缺的文件由程序自己新建。
    /// </summary>
    /// <summary>
    /// 取一个数据目录：优先用系统文件夹选择器；个别机器上选择器会直接抛异常（例如组件没注册），
    /// 这时退化成「手动输入路径」对话框 —— 否则用户连「切到旧数据目录」这条自救路都走不了。
    /// 返回 null 表示用户取消。
    /// </summary>
    private async Task<string?> PickDataFolderAsync()
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            var picked = (await picker.PickSingleFolderAsync())?.Path;
            if (!string.IsNullOrWhiteSpace(picked))
            {
                return picked;
            }

            // 用户在选择器里点了取消：不要再弹手输框追问。
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录选择器失败，改为手动输入路径。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_PickFallback_Hint"), InfoBarSeverity.Warning);
        }

        return await PromptForDataFolderPathAsync();
    }

    /// <summary>选择器不可用时的兜底：直接让用户粘贴数据目录路径（校验存在且可用）。</summary>
    private async Task<string?> PromptForDataFolderPathAsync()
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        var box = new TextBox
        {
            Text = AppPaths.DataRoot,
            PlaceholderText = @"D:\SteamEYA",
            IsSpellCheckEnabled = false
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Settings_Data_PickFallback_Title"),
            Content = box,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var raw = box.Text?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string path;
        try
        {
            path = AppPaths.NormalizePath(raw);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"手动输入的数据目录路径无效：{ex.Message}");
            AppState.ShowStatus(Loc.T("Settings_Data_PickFallback_Invalid"), InfoBarSeverity.Error);
            return null;
        }

        if (!Directory.Exists(path))
        {
            AppState.ShowStatus(Loc.Tf("Settings_Data_PickFallback_Missing_Format", path), InfoBarSeverity.Error);
            return null;
        }

        return path;
    }
    private async void SwitchDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_movingDataFolder)
        {
            return;
        }

        var destination = await PickDataFolderAsync();
        if (string.IsNullOrWhiteSpace(destination))
        {
            return; // 用户取消
        }

        if (string.Equals(
                AppPaths.NormalizePath(destination),
                AppPaths.NormalizePath(AppPaths.DataRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            AppState.ShowStatus(Loc.T("Settings_Data_MoveSame"), InfoBarSeverity.Informational);
            return;
        }

        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Settings_Data_SwitchDialogTitle"),
            Content = Loc.Tf("Settings_Data_SwitchConfirm_Format", destination),
            PrimaryButtonText = Loc.T("Settings_Btn_SwitchDataFolder"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await SwitchDataFolderToAsync(destination);
    }

    /// <summary>
    /// 真正执行「切换数据目录」：只写注册表里的指向并刷新界面，不复制、不删除任何文件。
    /// 与 MoveDataFolderToAsync 的区别就在这里：那边会先复制全部数据、再删旧目录。
    /// </summary>
    private async Task SwitchDataFolderToAsync(string destination)
    {
        // 复用「正在操作数据目录」开关：与移动/恢复默认互斥，避免并发切目录。
        _movingDataFolder = true;
        SwitchDataFolderButton.IsEnabled = false;
        MoveDataFolderButton.IsEnabled = false;
        MoveToDefaultDataFolderButton.IsEnabled = false;
        OpenDataFolderButton.IsEnabled = false;

        try
        {
            Directory.CreateDirectory(destination);
            AppPaths.SetDataRoot(destination);   // 只改指向：不复制、不删除任何文件

            DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
            MainWindow.Instance?.ApplyCustomBackground(AppState.SettingsService.Load());
            AppState.ReloadHistory();
            AppState.ReloadWhiteAccounts();

            AppLog.Info($"数据目录已切换（未迁移、未删除任何文件）：\"{AppState.SettingsService.AppFolderPath}\"");
            AppState.ShowStatus(
                Loc.Tf("Settings_Data_SwitchSuccess_Format", AppState.SettingsService.AppFolderPath),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("切换数据目录失败。", ex);
            AppState.ShowStatus(Loc.Tf("Settings_Data_MoveFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _movingDataFolder = false;
            SwitchDataFolderButton.IsEnabled = true;
            MoveDataFolderButton.IsEnabled = true;
            MoveToDefaultDataFolderButton.IsEnabled = true;
            OpenDataFolderButton.IsEnabled = true;
        }
    }


    private async void MoveDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_movingDataFolder)
        {
            return;
        }

        var destination = await PickDataFolderAsync();
        if (string.IsNullOrWhiteSpace(destination))
        {
            return; // 用户取消
        }

        await MoveDataFolderToAsync(destination);
    }

    private async void MoveToDefaultDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_movingDataFolder)
        {
            return;
        }

        await MoveDataFolderToAsync(AppPaths.DefaultDataRoot, moveToDefault: true);
    }

    private async Task MoveDataFolderToAsync(string destination, bool moveToDefault = false)
    {
        DataDirectoryMoveFailure validation;
        try
        {
            validation = moveToDefault
                ? DataDirectoryService.ValidateDefaultDestination()
                : DataDirectoryService.ValidateDestination(destination);
        }
        catch (Exception ex)
        {
            AppLog.Error("校验新数据目录失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_MoveFailed"), InfoBarSeverity.Error);
            return;
        }

        if (validation != DataDirectoryMoveFailure.None)
        {
            ShowDataMoveValidationError(validation);
            return;
        }

        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Settings_Data_MoveDialogTitle"),
            Content = Loc.Tf("Settings_Data_MoveConfirm_Format", destination),
            PrimaryButtonText = Loc.T("Settings_Btn_MoveDataFolder"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 迁移前先把 WebView2（「Steam 中查看」小窗）放掉：Chromium 独占打开 Cookies，
        // 不停掉它，复制一定报「being used by another process」，迁移就会失败。
        var webViewReleased = await WebViewDataHost.ReleaseForDataMoveAsync();

        _movingDataFolder = true;
        MoveDataFolderButton.IsEnabled = false;
        MoveToDefaultDataFolderButton.IsEnabled = false;
        OpenDataFolderButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_Data_Moving"), InfoBarSeverity.Informational);

        try
        {
            var result = moveToDefault
                ? await DataDirectoryService.MoveToDefaultAsync()
                : await DataDirectoryService.MoveAsync(destination);
            if (!result.Success)
            {
                var reason = string.IsNullOrWhiteSpace(result.Error)
                    ? Loc.T("Settings_Data_MoveFailed")
                    : result.Error;
                // 还占着（多见于另一个客户端窗口也开着网页窗口）：给一条能照着做的提示，不要糊一屏英文报错。
                if (!webViewReleased && reason.Contains("webview2", StringComparison.OrdinalIgnoreCase))
                {
                    reason = Loc.T("Settings_Data_MoveFailed_WebView2");
                }
                AppLog.Error($"移动数据目录失败：{reason}");
                AppState.ShowStatus(Loc.Tf("Settings_Data_MoveFailed_Format", reason), InfoBarSeverity.Error);
                return;
            }

            DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
            MainWindow.Instance?.ApplyCustomBackground(AppState.SettingsService.Load());
            AppState.ReloadHistory();
            if (result.CleanupFailed)
            {
                AppLog.Warn($"数据目录已移动，但旧目录清理失败：{result.Error}");
                AppState.ShowStatus(
                    Loc.Tf("Settings_Data_MoveCleanupWarning_Format", AppState.SettingsService.AppFolderPath),
                    InfoBarSeverity.Warning);
                return;
            }

            AppLog.Info($"数据目录已移动到：\"{AppState.SettingsService.AppFolderPath}\"");
            AppState.ShowStatus(
                Loc.Tf("Settings_Data_MoveSuccess_Format", AppState.SettingsService.AppFolderPath),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("移动数据目录失败。", ex);
            AppState.ShowStatus(Loc.Tf("Settings_Data_MoveFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _movingDataFolder = false;
            // 不论成功失败都要通知：页面里的 WebView2 已经被放掉，需要重建。
            WebViewDataHost.NotifyDataMoveFinished();
            MoveDataFolderButton.IsEnabled = true;
            MoveToDefaultDataFolderButton.IsEnabled = true;
            OpenDataFolderButton.IsEnabled = true;
        }
    }
    private static void ShowDataMoveValidationError(DataDirectoryMoveFailure failure)
    {
        var messageKey = failure switch
        {
            DataDirectoryMoveFailure.SameDirectory => "Settings_Data_MoveSame",
            DataDirectoryMoveFailure.DestinationInsideSource => "Settings_Data_MoveInside",
            DataDirectoryMoveFailure.DestinationContainsSource => "Settings_Data_MoveContains",
            DataDirectoryMoveFailure.DestinationNotEmpty => "Settings_Data_MoveNotEmpty",
            _ => "Settings_Data_MoveFailed"
        };
        AppState.ShowStatus(Loc.T(messageKey), InfoBarSeverity.Error);
    }
}
