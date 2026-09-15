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
    private bool _pickingVpnPath;
    private bool _vpnConnecting;
    private bool _vpnNodeMenuBuilt;
    private IReadOnlyList<string> _vpnNodeMenuNodes = [];
    private IReadOnlyDictionary<string, int> _vpnNodeMenuDelays = new Dictionary<string, int>(StringComparer.Ordinal);

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
            UpdateUpdateProxyMenuTexts();
            UpdateChoiceButtonTexts();
            UpdateCustomBackgroundControls();
            UpdateIntroVideoControls();

            // 未设置时 SteamPathText 显示的是本地化占位文案，需随语言刷新（已设置时是中性路径，刷新无副作用）。
            UpdateSteamPathText();

            // VPN 卡片文案（状态/占位）也是代码设置的，语言切换后重算。
            UpdateVpnControls();

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
        UpdateVpnControls();
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

    // ---------- VPN（Clash Verge）：仅本程序访问 GitHub 时走它的本地代理端口 ----------

    /// <summary>刷新 VPN 卡片：路径文本、开关状态、状态说明。</summary>
    private void UpdateVpnControls()
    {
        if (VpnPathText is null)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        VpnPathText.Text = VpnProxyService.IsValidExecutable(settings.VpnExecutablePath)
            ? settings.VpnExecutablePath!
            : Loc.T("Settings_Vpn_Path_None");

        // 正在输入时不要打断用户；否则用设置里的链接回填。
        if (VpnSubscriptionBox.FocusState == FocusState.Unfocused &&
            VpnSubscriptionBox.Text != (settings.VpnSubscriptionUrl ?? string.Empty))
        {
            VpnSubscriptionBox.Text = settings.VpnSubscriptionUrl ?? string.Empty;
        }

        SetVpnProxyToggle(settings.VpnProxyEnabled);

        UpdateVpnChoiceControls(settings);
        UpdateVpnNodeControl(settings);

        VpnProxyStatusText.Text = DescribeVpnProxy(settings);

        // 端口探测放到后台：界面立即刷新，探完再补一次状态（绝不阻塞 UI 线程）。
        if (!_vpnConnecting)
        {
            _ = RefreshVpnPortAsync();
        }
    }

    /// <summary>
    /// 同步下方「启用 VPN 代理」开关。<c>_syncing</c> 屏蔽掉 Toggled：
    /// 否则这次赋值会被当成用户操作，反过来又触发连接/断开。
    /// </summary>
    private void SetVpnProxyToggle(bool isOn)
    {
        if (VpnProxyToggle is null || VpnProxyToggle.IsOn == isOn)
        {
            return;
        }

        _syncing = true;
        try
        {
            VpnProxyToggle.IsOn = isOn;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task RefreshVpnPortAsync()
    {
        await VpnProxyService.RefreshProxyPortAsync();
        VpnProxyStatusText.Text = DescribeVpnProxy(AppState.SettingsService.Load());
    }

    /// <summary>状态说明：未启用 / 内核在跑（显示端口 + 当前接管方式与模式）/ 已启用但内核没起来。</summary>
    private static string DescribeVpnProxy(AppSettings settings)
    {
        if (!settings.VpnProxyEnabled)
        {
            return Loc.T("Settings_Vpn_Proxy_Off");
        }

        if (VpnCoreService.IsRunning)
        {
            // 内核在跑就是已连接：端口探测偶尔会慢/失败，用配置端口兜底，避免显示成「未连接」误导用户。
            var port = VpnProxyService.CachedPort > 0 ? VpnProxyService.CachedPort : VpnCoreService.ConfiguredPort;
            return Loc.Tf(
                "Settings_Vpn_Proxy_On_Format",
                port,
                TakeoverName(settings.VpnTakeover),
                ModeName(settings.VpnMode));
        }

        return VpnCoreService.HasSubscription
            ? Loc.T("Settings_Vpn_Status_NotConnected")
            : Loc.T("Settings_Vpn_Error_SubscriptionRequired");
    }

    /// <summary>接管方式显示名（与两个单选按钮共用同一套文案，避免界面与状态行说法不一致）。</summary>
    private static string TakeoverName(string? takeover) =>
        VpnCoreService.NormalizeTakeover(takeover) == VpnCoreService.TakeoverTun
            ? Loc.T("Settings_Vpn_Takeover_Tun")
            : Loc.T("Settings_Vpn_Takeover_System");

    /// <summary>代理模式显示名。</summary>
    private static string ModeName(string? mode) =>
        VpnCoreService.NormalizeMode(mode) == VpnCoreService.ModeGlobal
            ? Loc.T("Settings_Vpn_Mode_Global")
            : Loc.T("Settings_Vpn_Mode_Rule");

    /// <summary>
    /// 代理方式 / 代理模式两个单选控件：按设置回填选中项，并随语言刷新文案与说明。
    /// 选择本身上面两个事件里立即写回设置（用户要求：选完就保存，下次启动按上次的选择来）。
    /// </summary>
    private void UpdateVpnChoiceControls(AppSettings settings)
    {
        if (VpnTakeoverRadios is null || VpnModeRadios is null || VpnTakeoverHintText is null)
        {
            return;
        }

        VpnTakeoverSystemRadio.Content = Loc.T("Settings_Vpn_Takeover_System");
        VpnTakeoverTunRadio.Content = Loc.T("Settings_Vpn_Takeover_Tun");
        VpnModeRuleRadio.Content = Loc.T("Settings_Vpn_Mode_Rule");
        VpnModeGlobalRadio.Content = Loc.T("Settings_Vpn_Mode_Global");

        var takeover = VpnCoreService.NormalizeTakeover(settings.VpnTakeover);
        VpnTakeoverHintText.Text = takeover == VpnCoreService.TakeoverTun
            ? Loc.T("Settings_Vpn_Takeover_Hint_Tun")
            : Loc.T("Settings_Vpn_Takeover_Hint_System");

        _syncing = true;
        try
        {
            VpnTakeoverRadios.SelectedItem = takeover == VpnCoreService.TakeoverTun
                ? VpnTakeoverTunRadio
                : VpnTakeoverSystemRadio;
            VpnModeRadios.SelectedItem = VpnCoreService.NormalizeMode(settings.VpnMode) == VpnCoreService.ModeGlobal
                ? VpnModeGlobalRadio
                : VpnModeRuleRadio;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 节点下拉：按钮文字 = 当前节点（或「自动」），菜单项 = 自动 + 订阅里的节点。
    /// 节点列表没变就不重建菜单，免得用户正拉开菜单时被清掉。
    /// </summary>
    private void UpdateVpnNodeControl(AppSettings settings)
    {
        if (VpnNodeButton is null || VpnNodeFlyout is null || VpnNodeButtonText is null)
        {
            return;
        }

        var nodes = VpnCoreService.ListNodes();
        var selected = string.IsNullOrWhiteSpace(settings.VpnNode) ? null : settings.VpnNode.Trim();
        if (selected is not null && !nodes.Contains(selected, StringComparer.Ordinal))
        {
            // 换过订阅：原来选的节点已经不在了，回到自动。
            selected = null;
        }

        VpnNodeButtonText.Text = selected ?? Loc.T("Settings_Vpn_Node_Auto");

        // 延迟也是这样：自动连回来的那次测量发生在设置页创建之前，进页面时必须把数字补上。
        var delays = VpnCoreService.NodeDelays;
        if (!_vpnNodeMenuBuilt ||
            !_vpnNodeMenuNodes.SequenceEqual(nodes, StringComparer.Ordinal) ||
            !DelaysEqual(_vpnNodeMenuDelays, delays))
        {
            _vpnNodeMenuNodes = nodes;
            _vpnNodeMenuDelays = delays;
            VpnNodeFlyout.Items.Clear();
            VpnNodeFlyout.Items.Add(BuildNodeMenuItem(null, Loc.T("Settings_Vpn_Node_Auto")));

            // 节点按地区归拢：同一地区的节点永远连在一起（组间用分隔线隔开），组内保持订阅原顺序。
            var firstGroup = true;
            foreach (var group in GroupNodesByRegion(nodes))
            {
                if (!firstGroup)
                {
                    VpnNodeFlyout.Items.Add(new MenuFlyoutSeparator());
                }

                firstGroup = false;
                foreach (var node in group)
                {
                    VpnNodeFlyout.Items.Add(BuildNodeMenuItem(node, BuildNodeLabel(node)));
                }
            }

            _vpnNodeMenuBuilt = true;
        }
        else if (VpnNodeFlyout.Items.FirstOrDefault() is MenuFlyoutItem autoItem)
        {
            autoItem.Text = Loc.T("Settings_Vpn_Node_Auto");   // 语言切换后「自动」项文案要跟着变
        }
    }

    /// <summary>两份延迟快照是否一致（数量 + 每个节点的数值）。</summary>
    private static bool DelaysEqual(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>菜单项文案：节点名 + 最近一次测到的延迟（没测到或测不通显示 —）。</summary>
    private static string BuildNodeLabel(string node)
    {
        var delays = VpnCoreService.NodeDelays;
        return delays.TryGetValue(node, out var delay) ? $"{node}  ·  {delay} ms" : $"{node}  ·  —";
    }

    /// <summary>延迟变了：强制重建节点菜单，让数字刷新出来。</summary>
    private void RebuildVpnNodeMenu()
    {
        _vpnNodeMenuBuilt = false;
        UpdateVpnNodeControl(AppState.SettingsService.Load());
    }

    /// <summary>刷新节点延迟：内核把它那个分流组里的节点都测一遍（失败的按 — 显示）。</summary>
    private async void RefreshVpnNodeDelayButton_Click(object sender, RoutedEventArgs e)
    {
        var nodes = VpnCoreService.ListNodes();
        if (nodes.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Error_DelayNoNodes"), InfoBarSeverity.Warning);
            return;
        }

        RefreshVpnNodeDelayButton.IsEnabled = false;
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_TestingDelay"), InfoBarSeverity.Informational);
        try
        {
            await VpnCoreService.MeasureNodeDelaysAsync(nodes);
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_DelayUpdated"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新节点延迟失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_DelayFailed_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            RefreshVpnNodeDelayButton.IsEnabled = true;
            RebuildVpnNodeMenu();
        }
    }

    /// <summary>
    /// 连上之后自动测一轮延迟——「保存并连接」和打开下方开关都会走到这里。
    /// 刚起来的内核控制口可能还没完全就绪（上一次日志里就是这里失败的），所以先等一拍，
    /// 失败隔一会儿再试一次；两次都不行就只写日志，不弹提示打扰用户。
    /// </summary>
    private async Task RefreshNodeDelaysQuietlyAsync()
    {
        await VpnCoreService.RefreshNodeDelaysAsync();
        RebuildVpnNodeMenu();
    }

    private MenuFlyoutItem BuildNodeMenuItem(string? node, string text)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = node };
        item.Click += VpnNodeMenuItem_Click;
        return item;
    }

    /// <summary>
    /// 地区关键字表（顺序 = 菜单里的排序）：中文名、常用英文写法、两位国家代码、国旗 emoji 都认。
    /// 纯 ASCII 字母的关键字要求词边界，否则 "US" 会命中 "russia"、"IN" 会命中 "singapore"。
    /// </summary>
    private static readonly string[][] RegionKeywords =
    [
        ["香港", "港", "HK", "HKG", "Hong Kong", "HongKong", "🇭🇰"],
        ["台湾", "台灣", "台", "TW", "Taiwan", "台北", "🇹🇼"],
        ["日本", "日", "JP", "Japan", "东京", "東京", "大阪", "🇯🇵"],
        ["韩国", "韓國", "韩", "KR", "Korea", "首尔", "首爾", "🇰🇷"],
        ["新加坡", "狮城", "SG", "Singapore", "🇸🇬"],
        ["美国", "美", "US", "USA", "United States", "America", "洛杉矶", "洛杉磯", "圣何塞", "西雅图", "达拉斯", "🇺🇸"],
        ["英国", "英", "UK", "GB", "United Kingdom", "Britain", "伦敦", "倫敦", "🇬🇧"],
        ["德国", "德", "DE", "Germany", "法兰克福", "法蘭克福", "🇩🇪"],
        ["法国", "法", "FR", "France", "🇫🇷"],
        ["荷兰", "荷", "NL", "Netherlands", "🇳🇱"],
        ["俄罗斯", "俄", "RU", "Russia", "莫斯科", "🇷🇺"],
        ["加拿大", "加", "CA", "Canada", "🇨🇦"],
        ["澳大利亚", "澳洲", "澳", "AU", "Australia", "悉尼", "雪梨", "🇦🇺"],
        ["马来西亚", "馬來西亞", "马来", "MY", "Malaysia", "🇲🇾"],
        ["泰国", "泰國", "泰", "TH", "Thailand", "🇹🇭"],
        ["越南", "越", "VN", "Vietnam", "🇻🇳"],
        ["菲律宾", "菲律賓", "菲", "PH", "Philippines", "🇵🇭"],
        ["印尼", "印度尼西亚", "ID", "Indonesia", "🇮🇩"],
        ["印度", "印", "IN", "India", "🇮🇳"],
        ["土耳其", "土", "TR", "Turkey", "🇹🇷"],
        ["巴西", "巴", "BR", "Brazil", "🇧🇷"],
        ["阿根廷", "AR", "Argentina", "🇦🇷"],
    ];

    /// <summary>把节点按地区分组：组内保持订阅里的原始顺序，组间按 <see cref="RegionKeywords"/> 的顺序。</summary>
    private static List<List<string>> GroupNodesByRegion(IReadOnlyList<string> nodes)
    {
        var groups = new Dictionary<int, List<string>>();
        var ranks = new List<int>();
        foreach (var node in nodes)
        {
            var rank = RegionRank(node);
            if (!groups.TryGetValue(rank, out var list))
            {
                list = [];
                groups[rank] = list;
                ranks.Add(rank);
            }

            list.Add(node);
        }

        return ranks.OrderBy(rank => rank).Select(rank => groups[rank]).ToList();
    }

    /// <summary>节点名 → 地区序号；识别不出的一律排最后。</summary>
    private static int RegionRank(string nodeName)
    {
        for (var index = 0; index < RegionKeywords.Length; index++)
        {
            foreach (var keyword in RegionKeywords[index])
            {
                if (MatchesRegionKeyword(nodeName, keyword))
                {
                    return index;
                }
            }
        }

        return RegionKeywords.Length;
    }

    /// <summary>关键字匹配：纯字母关键字要词边界（"US" 不该命中 "russia"），中文/emoji 直接包含即可。</summary>
    private static bool MatchesRegionKeyword(string name, string keyword)
    {
        var needsBoundary = keyword.Length > 0 && keyword.All(ch => ch < 128 && char.IsLetter(ch));
        var start = 0;
        while (true)
        {
            var index = name.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (!needsBoundary)
            {
                return true;
            }

            var beforeOk = index == 0 || !char.IsLetter(name[index - 1]);
            var afterIndex = index + keyword.Length;
            var afterOk = afterIndex >= name.Length || !char.IsLetter(name[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = index + 1;
        }
    }

    /// <summary>选节点：立即保存，已连接时按新节点重写配置并重启内核。</summary>
    private async void VpnNodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
        {
            return;
        }

        var node = item.Tag as string;   // null = 自动
        var settings = AppState.SettingsService.Load();
        var current = string.IsNullOrWhiteSpace(settings.VpnNode) ? null : settings.VpnNode.Trim();
        if (string.Equals(current, node, StringComparison.Ordinal))
        {
            return;
        }

        settings.VpnNode = node;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 节点已选择：{node ?? "自动"}");
        UpdateVpnControls();
        AppState.ShowStatus(
            Loc.Tf("Settings_Vpn_Status_NodeSaved_Format", node ?? Loc.T("Settings_Vpn_Node_Auto")),
            InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    /// <summary>
    /// 切换代理方式：立即保存；已连接时按新方式重连——接管方式写在生成的内核配置里，
    /// 不重启内核的话切换不会生效（系统代理还要额外改写/还原 WinINET 代理）。
    /// </summary>
    private async void VpnTakeoverRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VpnTakeoverRadios.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var takeover = VpnCoreService.NormalizeTakeover(tag);
        if (VpnCoreService.NormalizeTakeover(settings.VpnTakeover) == takeover)
        {
            return;
        }

        settings.VpnTakeover = takeover;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 代理方式已切换为：{takeover}");
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_ChoiceSaved"), InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    /// <summary>切换代理模式（规则 / 全局）：同样立即保存，已连接时重启内核让新配置生效。</summary>
    private async void VpnModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VpnModeRadios.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var mode = VpnCoreService.NormalizeMode(tag);
        if (VpnCoreService.NormalizeMode(settings.VpnMode) == mode)
        {
            return;
        }

        settings.VpnMode = mode;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"VPN 代理模式已切换为：{mode}");
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_ChoiceSaved"), InfoBarSeverity.Success);

        if (settings.VpnProxyEnabled)
        {
            await ConnectVpnAsync(restart: true);
        }
    }

    private void DetectVpnButton_Click(object sender, RoutedEventArgs e)
    {
        var detected = VpnProxyService.AutoDetectExecutablePath();
        if (detected is null)
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_DetectFail"), InfoBarSeverity.Warning);
            UpdateVpnControls();
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnExecutablePath = detected;
        AppState.SettingsService.Save(settings);
        AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_Detected_Format", detected), InfoBarSeverity.Success);
        UpdateVpnControls();
    }

    private async void ChangeVpnPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickingVpnPath)
        {
            return;
        }

        _pickingVpnPath = true;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);

            StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("打开 VPN 选择器失败。", ex);
                AppState.ShowStatus(Loc.T("Settings_Vpn_Status_PathInvalid"), InfoBarSeverity.Error);
                return;
            }

            if (file is null)
            {
                return;
            }

            if (!VpnProxyService.IsValidExecutable(file.Path))
            {
                AppState.ShowStatus(Loc.T("Settings_Vpn_Status_PathInvalid"), InfoBarSeverity.Error);
                return;
            }

            var settings = AppState.SettingsService.Load();
            settings.VpnExecutablePath = file.Path;
            AppState.SettingsService.Save(settings);
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_PathSaved_Format", file.Path), InfoBarSeverity.Success);
            UpdateVpnControls();
        }
        finally
        {
            _pickingVpnPath = false;
        }
    }

    private void LaunchVpnButton_Click(object sender, RoutedEventArgs e)
    {
        if (VpnProxyService.IsRunning())
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_AlreadyRunning"), InfoBarSeverity.Informational);
            UpdateVpnControls();
            return;
        }

        if (VpnProxyService.TryLaunch(out var error))
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Launched"), InfoBarSeverity.Success);
        }
        else
        {
            AppState.ShowStatus(
                error is null ? Loc.T("Settings_Vpn_Error_NotFound") : Loc.Tf("Settings_Vpn_Status_LaunchFail_Format", error),
                InfoBarSeverity.Error);
        }

        UpdateVpnControls();
    }

    private async void VpnProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = VpnProxyToggle.IsOn;
        AppState.SettingsService.Save(settings);

        if (!settings.VpnProxyEnabled)
        {
            VpnCoreService.Stop();
            await VpnProxyService.RefreshProxyPortAsync();
            UpdateVpnControls();
            return;
        }

        // 打开开关 = 拉起内核（不需要用户启动 Clash 软件）：整个过程异步，不阻塞界面。
        await ConnectVpnAsync();
    }

    /// <summary>保存订阅并连接内核（按钮与开关共用）。</summary>
    private async void SaveVpnSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        var url = VpnSubscriptionBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AppState.ShowStatus(Loc.T("Settings_Vpn_Error_SubscriptionRequired"), InfoBarSeverity.Warning);
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.VpnSubscriptionUrl = url;
        settings.VpnProxyEnabled = true;
        AppState.SettingsService.Save(settings);
        await ConnectVpnAsync(forceRefreshSubscription: true);
    }

    /// <summary>
    /// 「清空」：①清空文本框（并保存空值）→ ②断开连接 → ③关掉下方 VPN 开关，最后抹掉本机留存的订阅数据。
    /// 断开走 <see cref="VpnCoreService.Stop"/>，它内部会还原被接管的系统代理。
    /// </summary>
    private async void ClearVpnSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        // ① 先清文本（同时把已保存的订阅链接置空）
        VpnSubscriptionBox.Text = string.Empty;
        var settings = AppState.SettingsService.Load();
        settings.VpnSubscriptionUrl = null;
        AppState.SettingsService.Save(settings);

        // ② 断开连接
        VpnCoreService.Stop();

        // ③ 关掉下方 VPN 开关（设置 + 界面都显式置为关闭）
        settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = false;
        AppState.SettingsService.Save(settings);
        SetVpnProxyToggle(false);
        AppLog.Info("已清空 VPN 订阅链接、断开连接并关闭 VPN 代理。");

        VpnCoreService.ClearLocalSubscription();
        await VpnProxyService.RefreshProxyPortAsync();
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Cleared"), InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 「断开」：结束内核并把下方 VPN 开关一并关掉；**不动订阅链接**（下次还能直接连回来）。
    /// </summary>
    private async void StopVpnCoreButton_Click(object sender, RoutedEventArgs e)
    {
        // ① 断开连接
        VpnCoreService.Stop();

        // ② 关掉下方 VPN 开关：设置 + 界面都显式置为关闭（不等 UpdateVpnControls 的间接路径）
        var settings = AppState.SettingsService.Load();
        settings.VpnProxyEnabled = false;
        AppState.SettingsService.Save(settings);
        SetVpnProxyToggle(false);
        AppLog.Info("已点击「断开」：内核已结束，VPN 开关已关闭（订阅链接保留）。");

        await VpnProxyService.RefreshProxyPortAsync();
        UpdateVpnControls();
        AppState.ShowStatus(Loc.T("Settings_Vpn_Status_Disconnected"), InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 连接流程：拉订阅（可选）→ 启动内核 → 等端口就绪 → 按所选方式接管（系统代理 / TUN）→ 刷新界面。
    /// <paramref name="restart"/> = true 时先停掉现有内核：接管方式与模式都写在内核配置里，不重启不会生效。
    /// </summary>
    private async Task ConnectVpnAsync(bool forceRefreshSubscription = false, bool restart = false)
    {
        if (_vpnConnecting)
        {
            return;
        }

        _vpnConnecting = true;
        VpnProxyToggle.IsEnabled = false;
        SaveVpnSubscriptionButton.IsEnabled = false;
        VpnProxyStatusText.Text = Loc.T("Settings_Vpn_Status_Connecting");
        try
        {
            if (restart)
            {
                VpnCoreService.Stop();
                // 换节点 / 换模式只是改配置：用本地订阅副本立刻重写，不必再下一次订阅。
                await VpnCoreService.RewriteFromLocalSubscriptionAsync();
            }

            IProgress<string> progress = new Progress<string>(message => VpnProxyStatusText.Text = message);
            if (forceRefreshSubscription)
            {
                progress.Report(Loc.T("Settings_Vpn_Status_FetchingSubscription"));
                await VpnCoreService.RefreshSubscriptionAsync(VpnSubscriptionBox.Text.Trim());
            }

            await VpnCoreService.EnsureRunningAsync(progress);
            await VpnProxyService.RefreshProxyPortAsync();

            // 系统代理 = 把 WinINET 代理写到内核端口；TUN = 内核自己接管全部流量，系统代理必须还原，
            // 否则流量会「系统代理 → 内核 → TUN」绕一圈，还会在断开后留给用户一个死代理。
            var port = VpnProxyService.CachedPort > 0 ? VpnProxyService.CachedPort : VpnCoreService.ConfiguredPort;
            var current = AppState.SettingsService.Load();
            if (VpnCoreService.CurrentTakeover == VpnCoreService.TakeoverTun)
            {
                SystemProxyService.RestoreIfApplied();
            }
            else
            {
                SystemProxyService.Apply(port);
            }

            UpdateVpnControls();
            AppState.ShowStatus(
                Loc.Tf("Settings_Vpn_Proxy_On_Format", port, TakeoverName(current.VpnTakeover), ModeName(current.VpnMode)),
                InfoBarSeverity.Success);

            // 连上后顺手测一轮节点延迟：菜单里的节点会带上 ms 数（后台进行，不阻塞界面）。
            _ = RefreshNodeDelaysQuietlyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"连接 VPN 失败：{ex.Message}");
            UpdateVpnControls();
            AppState.ShowStatus(Loc.Tf("Settings_Vpn_Status_ConnectFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _vpnConnecting = false;
            VpnProxyToggle.IsEnabled = true;
            SaveVpnSubscriptionButton.IsEnabled = true;
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
