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
    private static readonly string[] ThemeCodes = ["Default", "Light", "Dark", "Custom"];
    // 自定义背景可选的文件类型：图片（含 GIF）+ 视频（静音循环播放）。
    private static readonly string[] BackgroundImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];
    private static readonly string[] IntroVideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"];

    // 代码设置 ComboBox.SelectedItem 会触发 SelectionChanged，置位以区分“用户选择”与“初始同步”，避免回写/重复应用。
    private bool _syncing;
    private bool _languageMenuBuilt;
    private string _selectedLanguageCode = "zh-Hans";
    private string _selectedThemeCode = "Default";
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

        // 来源账号候选实时跟随账号管理页 / 历史账号页的账号变化（两个事件都在账号集合重载后触发）。
        AppState.HistoryChanged += OnCs2AccountsChanged;
        AppState.WhiteAccountsChanged += OnCs2AccountsChanged;
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

            // VPN 卡片文案（状态/占位）也是代码设置的，语言切换后重算。

            // 来源账号候选/选中文本用 Settings_Cs2Sync_SourceItem_Format 拼装，跟随语言重建。
            RefreshCs2SyncSources();

            // 口令卡片的状态文案与按钮文字都是代码设置的，语言切换后需要重算。
            UpdateVaultPassphraseControls();
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

    private void UpdateChoiceButtonTexts()
    {
        LanguageButtonText.Text = Loc.AvailablePacks
            .FirstOrDefault(pack => string.Equals(pack.Code, _selectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? _selectedLanguageCode;
        ThemeButtonText.Text = ThemeDisplayName(_selectedThemeCode);
        ThemeColorButton.Visibility = string.Equals(_selectedThemeCode, "Custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        UpdateVaultPassphraseControls();
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
    // ---------- 凭据加密口令（第 4 层，可选，默认关闭） ----------

    private const int VaultPassphraseMinLength = 8;

    /// <summary>刷新口令卡片的状态文案与按钮可见性/可用性。</summary>
    private void UpdateVaultPassphraseControls()
    {
        var enabled = CredentialVault.IsPassphraseEnabled;
        var locked = CredentialVault.IsLocked;

        VaultPassphraseStatusText.Text = Loc.T(locked
            ? "Settings_VaultPw_Status_Locked"
            : enabled
                ? "Settings_VaultPw_Status_On"
                : "Settings_VaultPw_Status_Off");

        VaultPassphraseUnlockButton.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        VaultPassphraseClearButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VaultPassphrasePrimaryButtonText.Text = Loc.T(enabled ? "Settings_VaultPw_Change" : "Settings_VaultPw_Set");

        // 锁定状态下拿不到数据密钥：必须先解锁，才能更换或取消口令。
        VaultPassphrasePrimaryButton.IsEnabled = !locked;
        VaultPassphraseClearButton.IsEnabled = !locked;
    }

    private async void VaultPassphraseUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        var passphrase = await VaultPassphraseDialog.AskAsync(
            XamlRoot, Loc.T("VaultPw_Unlock_Title"), Loc.T("VaultPw_Unlock_Desc"), Loc.T("Common_Confirm"));
        if (passphrase is null)
        {
            return;
        }

        if (!CredentialVault.TryUnlock(passphrase))
        {
            AppState.ShowStatus(Loc.T("VaultPw_Error_Wrong"), InfoBarSeverity.Error);
            return;
        }

        AppState.ReloadHistory();
        AppState.ReloadWhiteAccounts();
        AppState.ShowStatus(Loc.T("VaultPw_Status_UnlockedOk"), InfoBarSeverity.Success);
        UpdateVaultPassphraseControls();
    }

    private async void VaultPassphrasePrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = CredentialVault.IsPassphraseEnabled;
        var passphrase = await VaultPassphraseDialog.AskAsync(
            XamlRoot,
            Loc.T(enabled ? "VaultPw_Change_Title" : "VaultPw_Set_Title"),
            Loc.T(enabled ? "VaultPw_Change_Desc" : "VaultPw_Set_Desc"),
            Loc.T("Common_Confirm"));
        if (passphrase is null)
        {
            return;
        }

        if (passphrase.Length < VaultPassphraseMinLength)
        {
            AppState.ShowStatus(Loc.T("VaultPw_Error_TooShort"), InfoBarSeverity.Error);
            return;
        }

        var confirm = await VaultPassphraseDialog.AskAsync(
            XamlRoot, Loc.T("VaultPw_Set_Title"), Loc.T("VaultPw_Confirm_Desc"), Loc.T("Common_Confirm"));
        if (confirm is null)
        {
            return;
        }

        if (!string.Equals(passphrase, confirm, StringComparison.Ordinal))
        {
            AppState.ShowStatus(Loc.T("VaultPw_Error_Mismatch"), InfoBarSeverity.Error);
            return;
        }

        try
        {
            CredentialVault.SetPassphrase(passphrase);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("VaultPw_Error_SetFailed_Format", ex.Message), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("VaultPw_Status_SetOk"), InfoBarSeverity.Success);
        UpdateVaultPassphraseControls();
    }

    private async void VaultPassphraseClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (CredentialVault.IsLocked)
        {
            AppState.ShowStatus(Loc.T("VaultPw_Status_LockedHint"), InfoBarSeverity.Warning);
            return;
        }

        var passphrase = await VaultPassphraseDialog.AskAsync(
            XamlRoot, Loc.T("VaultPw_Clear_Title"), Loc.T("VaultPw_Clear_Desc"), Loc.T("Common_Confirm"));
        if (passphrase is null)
        {
            return;
        }

        if (!CredentialVault.ClearPassphrase(passphrase))
        {
            AppState.ShowStatus(Loc.T("VaultPw_Error_Wrong"), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("VaultPw_Status_ClearedOk"), InfoBarSeverity.Success);
        UpdateVaultPassphraseControls();
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
    /// <summary>来源账号候选项：账号管理页 + 历史账号页里的账号（Steam64 + 展示文本）。</summary>
    private sealed record Cs2SourceOption(string SteamId64, string Display);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // 已保存的来源账号 SteamID64 的本地镜像，避免各处反复 Load 设置来判断“选中了谁”。
    private string? _cs2SourceSteamId;

    /// <summary>
    /// 重建来源账号候选并刷新下拉菜单：候选 = 账号管理页的账号（页面上到下）在前、
    /// 历史账号页的账号（页面上到下）在后，同一 Steam64 只留第一条。
    /// 两个列表都是内存快照，所以这里是同步重建；账号增删/改名由 AppState 的事件实时触发（见构造函数订阅）。
    /// </summary>
    private void RefreshCs2SyncSources()
    {
        _cs2SourceOptions = BuildCs2SourceOptions();

        var settings = AppState.SettingsService.Load();
        Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
        _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

        RebuildCs2SourceFlyout();
    }

    /// <summary>按「账号管理页 → 历史账号页」顺序拼候选，逐条保序去重；没有 Steam64 的账号无法作为来源，跳过。</summary>
    private static List<Cs2SourceOption> BuildCs2SourceOptions()
    {
        var options = new List<Cs2SourceOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in AppState.WhiteAccounts.Concat(AppState.HistoryAccounts))
        {
            var steamId = account.SteamId?.Trim();
            if (string.IsNullOrWhiteSpace(steamId) || !seen.Add(steamId))
            {
                continue;
            }

            // 显示名优先级：昵称 > 登录账号名 > Steam64（名字就是 Steam64 的不重复显示，避免「X（X）」噪音）。
            var name = FirstNonEmpty(account.PersonaName, account.AccountName);
            if (name is not null && string.Equals(name, steamId, StringComparison.OrdinalIgnoreCase))
            {
                name = null;
            }

            var display = name is null
                ? steamId
                : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, steamId);
            options.Add(new Cs2SourceOption(steamId, display));
        }

        return options;
    }

    /// <summary>把候选项灌进下拉菜单（控件样式与上方语言按钮一致）。</summary>
    private void RebuildCs2SourceFlyout()
    {
        Cs2SyncSourceFlyout.Items.Clear();
        foreach (var option in _cs2SourceOptions)
        {
            var item = new MenuFlyoutItem
            {
                Text = option.Display,
                Tag = option
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

    /// <summary>账号集合变化（新增/删除/改名/登录落库）时实时刷新候选；事件可能来自后台线程，统一回 UI 线程。</summary>
    private void OnCs2AccountsChanged(string? selectSteamId)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => RefreshCs2SyncSources());
            return;
        }

        RefreshCs2SyncSources();
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
