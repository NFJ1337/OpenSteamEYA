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
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SteamEyaWinUI.Pages;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private static readonly string[] ThemeCodes = ["Default", "Light", "Dark", "Custom"];
    private static readonly string[] UpdateProxyCodes = ["direct", "gh-proxy.org", "v4.gh-proxy.org", "v6.gh-proxy.org", "cdn.gh-proxy.org"];
    private static readonly string[] BackgroundImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];
    private static readonly string[] IntroVideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    // 代码设置 ComboBox.SelectedItem 会触发 SelectionChanged，置位以区分“用户选择”与“初始同步”，避免回写/重复应用。
    private bool _syncing;
    private bool _languageMenuBuilt;
    private string _selectedLanguageCode = "zh-Hans";
    private string _selectedThemeCode = "Default";
    private string _selectedUpdateProxyCode = "direct";
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
        BuildUpdateProxyMenu();
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
            UpdateUpdateProxyMenuTexts();
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

    private void BuildUpdateProxyMenu()
    {
        foreach (var code in UpdateProxyCodes)
        {
            var item = new MenuFlyoutItem
            {
                Text = UpdateProxyDisplayName(code),
                Tag = code
            };
            item.Click += UpdateProxyMenuItem_Click;
            UpdateProxyFlyout.Items.Add(item);
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

        _selectedThemeCode = theme;
        ThemeButtonText.Text = ThemeDisplayName(theme);

        var settings = AppState.SettingsService.Load();
        if (string.Equals(theme, "Custom", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(settings.UiColor))
        {
            var current = AppState.UiColorService.BaseColor;
            settings.UiColor = $"#{current.A:X2}{current.R:X2}{current.G:X2}{current.B:X2}";
        }

        settings.Theme = theme;
        AppState.SettingsService.Save(settings);
        ApplyThemeSettings(settings);
    }
    private void UpdateProxyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string proxyCode })
        {
            return;
        }

        _selectedUpdateProxyCode = proxyCode;
        UpdateProxyButtonText.Text = UpdateProxyDisplayName(proxyCode);

        var settings = AppState.SettingsService.Load();
        settings.UpdateProxySite = proxyCode;
        AppState.SettingsService.Save(settings);
        AppState.UpdateService.SetProxySite(proxyCode);
    }

    private void UpdateChoiceButtonTexts()
    {
        LanguageButtonText.Text = Loc.AvailablePacks
            .FirstOrDefault(pack => string.Equals(pack.Code, _selectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? _selectedLanguageCode;
        ThemeButtonText.Text = ThemeDisplayName(_selectedThemeCode);
        ThemeColorButton.Visibility = string.Equals(_selectedThemeCode, "Custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateProxyButtonText.Text = UpdateProxyDisplayName(_selectedUpdateProxyCode);
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

    private void UpdateUpdateProxyMenuTexts()
    {
        foreach (var item in UpdateProxyFlyout.Items.OfType<MenuFlyoutItem>())
        {
            if (item.Tag is string code)
            {
                item.Text = UpdateProxyDisplayName(code);
            }
        }
    }

    private static string ThemeDisplayName(string code) => code switch
    {
        "Light" => Loc.T("Settings_Theme_Light"),
        "Dark" => Loc.T("Settings_Theme_Dark"),
        "Custom" => Loc.T("Settings_Theme_Custom"),
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
        UpdateThemeColorControls(settings);
    }

    private void UpdateThemeColorControls(AppSettings settings)
    {
        var color = AppState.UiColorService.BaseColor;
        ThemeColorButton.Visibility = string.Equals(settings.Theme, "Custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ThemeColorSwatch.Background = new SolidColorBrush(color);
    }

    private static string UpdateProxyDisplayName(string code) => code switch
    {
        "gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrg"),
        "v4.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgV4"),
        "v6.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgV6"),
        "cdn.gh-proxy.org" => Loc.T("Settings_UpdateProxy_GhProxyOrgCdn"),
        _ => Loc.T("Settings_UpdateProxy_Direct")
    };
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
            _selectedUpdateProxyCode = UpdateProxyCodes.Contains(settings.UpdateProxySite, StringComparer.OrdinalIgnoreCase)
                ? settings.UpdateProxySite
                : "direct";
            UpdateChoiceButtonTexts();

            WindowWidthBox.Text = Math.Clamp(settings.WindowWidth ?? MainWindow.DefaultWindowWidth, MainWindow.MinimumWindowWidth, MainWindow.MaximumWindowWidth).ToString(CultureInfo.InvariantCulture);
            WindowHeightBox.Text = Math.Clamp(settings.WindowHeight ?? MainWindow.DefaultWindowHeight, MainWindow.MinimumWindowHeight, MainWindow.MaximumWindowHeight).ToString(CultureInfo.InvariantCulture);
            var separatorColor = App.GetTableSeparatorColor();
            TableSeparatorColorPicker.Color = separatorColor;
            TableSeparatorColorSwatch.Background = new SolidColorBrush(separatorColor);
            var uiColor = AppState.UiColorService.BaseColor;
            UiColorPicker.Color = uiColor;
            UiColorSwatch.Background = new SolidColorBrush(uiColor);
            ThemeColorPicker.Color = uiColor;
            UpdateThemeColorControls(settings);
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
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
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

    private void ThemeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
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
            ThemeColorPicker.Color = color;
        }
        finally
        {
            _syncing = false;
        }

        UiColorSwatch.Background = new SolidColorBrush(color);
        ThemeColorSwatch.Background = new SolidColorBrush(color);
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
            ThemeColorPicker.Color = color;
            AnimateUiColorToggle.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }

        UiColorSwatch.Background = new SolidColorBrush(color);
        ThemeColorSwatch.Background = new SolidColorBrush(color);
    }

    private void ResetThemeColorButton_Click(object sender, RoutedEventArgs e) =>
        ResetUiColorButton_Click(sender, e);
    private async void UpdateProxyLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var proxyCode = _selectedUpdateProxyCode;
        if (string.IsNullOrWhiteSpace(proxyCode))
        {
            return;
        }

        UpdateProxyLatencyButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_UpdateProxy_Testing"), InfoBarSeverity.Informational);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var elapsed = await AppState.UpdateService.ProbeLatencyAsync(proxyCode, cts.Token);
            AppState.ShowStatus(
                Loc.Tf("Settings_UpdateProxy_LatencyResult_Format", Math.Round(elapsed.TotalMilliseconds)),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("Settings_UpdateProxy_LatencyTimeout"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Settings_UpdateProxy_LatencyFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            UpdateProxyLatencyButton.IsEnabled = true;
        }
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

    /// <summary>来源账号下拉候选：展示文本（昵称（Steam64））+ 搜索文本（昵称/账号名/Steam64/历史备注）。</summary>
    private sealed record Cs2SourceOption(string SteamId64, string Display, string SearchText);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // 已保存的来源账号 SteamID64 的本地镜像，避免各处反复 Load 设置来判断“选中了谁”。
    private string? _cs2SourceSteamId;

    // 刷新代数：语言切换 / 刷新按钮连点 / 导航竞态下并发多轮扫描时，只让最后发起的一轮落地。
    private int _cs2RefreshGen;

    // 搜索框（内部 TextBox）是否持有焦点：刷新完成时正在输入则不动文本/候选，避免打断用户。
    private bool _cs2SourceBoxFocused;

    private async void RefreshCs2SyncSources()
    {
        // userdata 目录解析 + 扫描来源账号 + 离线名称解析都放后台线程：大 userdata / 慢盘 /
        // 数 MB 的 localconfig.vdf 都不卡设置页导航。
        // userdata 路径与「立即推送」一致走 ResolvePathsOrThrow（带自动探测回退），
        // 修掉“未持久化 Steam 路径时下拉恒空、但立即推送却能工作”的不一致。
        var gen = ++_cs2RefreshGen;
        var (sources, names) = await Task.Run(() =>
        {
            try
            {
                var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                var scanned = AppState.Cs2CloudService.EnumerateSources(paths.UserdataPath);
                return (scanned, SteamAccountNameService.BuildOfflineNames(paths, scanned));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"扫描 CS2 设置来源账号失败：{ex.Message}");
                return ((IReadOnlyList<Cs2SettingsSource>)Array.Empty<Cs2SettingsSource>(),
                    (IReadOnlyDictionary<string, OfflineAccountName>)new Dictionary<string, OfflineAccountName>());
            }
        });

        if (gen != _cs2RefreshGen)
        {
            return;
        }

        _syncing = true;
        try
        {
            _cs2SourceOptions = BuildCs2SourceOptions(sources, names);

            var settings = AppState.SettingsService.Load();
            Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
            _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

            // 正在输入时不动文本/候选：下一次击键会用新数据重新过滤，失焦时恢复规范文本。
            if (!_cs2SourceBoxFocused)
            {
                Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
                Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>拼装候选并排序：有名字的按名字排前面，只剩 Steam64 的按数字排后面。</summary>
    private static List<Cs2SourceOption> BuildCs2SourceOptions(
        IReadOnlyList<Cs2SettingsSource> sources,
        IReadOnlyDictionary<string, OfflineAccountName> names)
    {
        var options = new List<Cs2SourceOption>(sources.Count);
        foreach (var source in sources)
        {
            // 显示名优先级：应用历史（含在线刷新过的昵称）> 本机 Steam 文件的离线解析。
            var history = AppState.HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, source.SteamId64, StringComparison.OrdinalIgnoreCase));
            names.TryGetValue(source.SteamId64, out var offline);

            var persona = FirstNonEmpty(history?.PersonaName, offline?.PersonaName);
            var accountName = FirstNonEmpty(history?.AccountName, offline?.AccountName);
            var name = persona ?? accountName;
            // 有的数据源会把 Steam64 本身存成名字，显示成「X（X）」纯属噪音，按无名处理。
            if (string.Equals(name, source.SteamId64, StringComparison.OrdinalIgnoreCase))
            {
                name = null;
            }

            var display = name is null
                ? source.SteamId64
                : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, source.SteamId64);

            // 搜索面覆盖昵称、登录账号名、Steam64 和历史备注——展示文本只放昵称，避免下拉太长。
            var searchText = string.Join(' ',
                new[] { persona, accountName, source.SteamId64, history?.Note }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            options.Add(new Cs2SourceOption(source.SteamId64, display, searchText));
        }

        return options
            .OrderBy(option => option.Display == option.SteamId64 ? 1 : 0)
            .ThenBy(option => option.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    /// <summary>当前已保存来源的展示文本；来源目录已消失时退回显示原始 Steam64，未选择时为空。</summary>
    private string Cs2SourceSelectedDisplay()
    {
        if (string.IsNullOrWhiteSpace(_cs2SourceSteamId))
        {
            return string.Empty;
        }

        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase));
        return option?.Display ?? _cs2SourceSteamId;
    }

    private List<string> FilterCs2SourceDisplays(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        return _cs2SourceOptions
            .Where(option => trimmed.Length == 0 ||
                option.SearchText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Display)
            .ToList();
    }

    /// <summary>把候选设为当前来源：规范化文本并持久化（重复选择同一账号时不重复写盘）。</summary>
    private void CommitCs2Source(string display)
    {
        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.Display, display, StringComparison.Ordinal));
        if (option is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = option.Display;
        }
        finally
        {
            _syncing = false;
        }

        if (string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cs2SourceSteamId = option.SteamId64;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = option.SteamId64;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncSourceBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只响应用户敲键；程序赋值/选中回填触发的 TextChanged 不重开候选。
        if (_syncing || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = FilterCs2SourceDisplays(sender.Text);
    }

    private void Cs2SyncSourceBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            CommitCs2Source(chosen);
            return;
        }

        // 直接回车：文本恰好等于某候选、或非空搜索词过滤后只剩一个候选，则视为选中它。
        // 空文本回车不自动选中（单账号机器上会“无输入即选择”），只展开全部候选。
        var exact = _cs2SourceOptions.FirstOrDefault(option =>
            string.Equals(option.Display, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            CommitCs2Source(exact.Display);
            return;
        }

        var matches = FilterCs2SourceDisplays(args.QueryText);
        if (matches.Count == 1 && !string.IsNullOrWhiteSpace(args.QueryText))
        {
            CommitCs2Source(matches[0]);
            return;
        }

        sender.ItemsSource = matches;
    }

    private void Cs2SyncSourceBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = true;

        // 获得焦点即展开全部候选，保留旧 ComboBox「点开就能挑」的体验。
        Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
        if (_cs2SourceOptions.Count > 0)
        {
            Cs2SyncSourceBox.IsSuggestionListOpen = true;
        }
    }

    private void Cs2SyncSourceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = false;

        if (_syncing)
        {
            return;
        }

        // 提交只走 QuerySubmitted（回车/点选候选）。失焦一律回退为已保存选择的展示文本，
        // 与旧 ComboBox 的轻取消语义一致：仅方向键预览过的候选或半截搜索词都不算选择，
        // 否则“瞄一眼就点走”会把高亮项静默写进设置，后续推送就推错账号。
        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
        }
        finally
        {
            _syncing = false;
        }
    }

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

    private void Cs2SyncRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCs2SyncSources();
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
    private async void MoveDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_movingDataFolder)
        {
            return;
        }

        string? destination;
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            destination = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录选择器失败。", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_MovePickFail"), InfoBarSeverity.Error);
            return;
        }

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
