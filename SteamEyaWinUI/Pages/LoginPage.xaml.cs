using System.ComponentModel;
using System.Text;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.System;

#pragma warning disable CS8600

namespace SteamEyaWinUI.Pages;

public sealed partial class LoginPage : Page, INotifyPropertyChanged
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private SteamAccountData? _cachedAccountData;
    private string? _cachedLicenseKey;
    private SteamAccountData? _cachedLegacyAccountData;
    private string? _cachedLegacyLicenseKey;

    // ---------- 「伊万/路飞」模块状态（两条上游取卡 → 解析 → 一键登录） ----------
    private SteamUpstreamServer? _ivanLuffyServer;
    private SteamAccountData? _ivanLuffyCachedAccount;
    private string? _ivanLuffyCachedKey;

    // 历史账号保存是否失败：SaveLoginHistoryAsync 置位，登录消息据此决定 Warning/Success 严重度
    // （本地化后历史后缀文案不再含固定中文前缀，故改用此标志替代原先的字符串前缀判断）。
    private bool _lastLoginHistorySaveFailed;
    private bool _pendingTokenLoginSelection;
    private string? _accountInfoPanelSteamId;
    private GridLength _sideColumnWidth = new(340);
    // ---------- 账号核验（卡密）模块状态 ----------
    // 上游快照按卡密命中缓存，语言切换/重渲染时用 _verifyPayload 重建展示文案，不重复请求。
    private readonly List<SteamVerifyGameEntry> _verifyLibraryGames = [];
    private SteamVerifyPayload? _verifyPayload;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _verifyStageTimer;
    private int _verifyStageIndex;
    private int _verifyElapsedSeconds;
    private bool _verifyQueryRunning;
    private bool _verifyLibraryRecentTab = true;
    private string _verifyLibrarySort = "playtime";

    public LoginPage()
    {
        InitializeComponent();

        // 核验模式会临时把右列宽度归零，这里先记住 XAML 里的原始宽度以便还原。
        _sideColumnWidth = LayoutGrid.ColumnDefinitions[1].Width;

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Loaded += LoginPage_Loaded;

        // SelectorBarItem.IsSelected 在 XAML 解析期不可靠，显式设定初始模式。
        ModeSelector.SelectedItem = TokenLoginModeItem;
        ApplyLanguageModeAvailability();
        InitializeVerifyModule();
        InitializeIvanLuffyModule();
        InitializeCardStoreModule();

        UpdateAccountInfoFromCurrentInputs();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private bool IsLegacyEyaMode => ModeSelector.SelectedItem == LegacyEyaModeItem;
    private bool IsTokenLoginMode => ModeSelector.SelectedItem == TokenLoginModeItem;
    private bool IsLegacyTokenFlow => IsLegacyEyaMode || IsTokenLoginMode;
    private bool IsAutoMode => ModeSelector.SelectedItem == AutoModeItem;

    private bool IsCredsMode => ModeSelector.SelectedItem == CredsModeItem;

    private bool IsVerifyMode => ModeSelector.SelectedItem == VerifyModeItem;

    private bool IsIvanLuffyMode => ModeSelector.SelectedItem == IvanLuffyModeItem;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；右侧账号信息面板的命令式文本（用户名/SteamID/过期/可用状态/分数/等级/冷却）
            // 重跑下面两个方法即可让已显示的“未填写/未解析/未验证”等占位换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            ApplyLanguageModeAvailability();
            UpdateAccountInfoFromCurrentInputs();
            UpdateVerifyTexts();
            UpdateCardStoreSourceText();
            RefreshCardStoreList();
            OnBusyChanged(AppState.IsBusy);
        });
    }

    private static void ShowStatus(string message, InfoBarSeverity severity)
    {
        AppState.ShowStatus(message, severity);
    }

    private void OnBusyChanged(bool isBusy)
    {
        var enabled = !isBusy;
        ModeSelector.IsEnabled = enabled;
        ClearAllButton.IsEnabled = enabled;
        CardStoreImportBox.IsEnabled = enabled;
        CardStoreImportButton.IsEnabled = enabled;
        CardStoreSourceButton.IsEnabled = enabled;
        CardStoreList.IsEnabled = enabled;
        AccountNameBox.IsEnabled = enabled;
        EyaTokenBox.IsEnabled = enabled;
        ClearManualButton.IsEnabled = enabled;
        LicenseKeyBox.IsEnabled = enabled;
        ClearLicenseButton.IsEnabled = enabled;
        ResolveLicenseButton.IsEnabled = enabled;
        IvanLuffyServerButton.IsEnabled = enabled;
        IvanLuffyKeyBox.IsEnabled = enabled;
        IvanLuffyResolveButton.IsEnabled = enabled;
        IvanLuffyClearButton.IsEnabled = enabled;
        IvanLuffyLoginButton.IsEnabled = enabled;
        IvanLuffyClearWorkshopButton.IsEnabled = enabled;
        IvanLuffyApplyLoadoutButton.IsEnabled = enabled;
        IvanLuffyPersonalizeButton.IsEnabled = enabled;
        LegacyEyaLicenseKeyBox.IsEnabled = enabled;
        ClearLegacyLicenseButton.IsEnabled = enabled;
        ResolveLegacyLicenseButton.IsEnabled = enabled;
        TokenLicenseKeyBox.IsEnabled = enabled;
        ClearTokenButton.IsEnabled = enabled;
        ResolveTokenLicenseButton.IsEnabled = enabled;
        TokenSteamIdBox.IsEnabled = enabled;
        TokenBox.IsEnabled = enabled;
        CredAccountNameBox.IsEnabled = enabled;
        CredPasswordBox.IsEnabled = enabled;
        ClearCredsButton.IsEnabled = enabled;
        CredFetchButton.IsEnabled = enabled;
        ClearWorkshopButton.IsEnabled = enabled;
        ApplyLoadoutButton.IsEnabled = enabled;
        VerifyKeyBox.IsEnabled = enabled;
        ClearVerifyButton.IsEnabled = enabled;
        VerifyQueryButton.IsEnabled = enabled;
        VerifyLibrarySearchBox.IsEnabled = enabled;
        VerifyLibrarySortButton.IsEnabled = enabled;
        VerifyLibraryTabSelector.IsEnabled = enabled;
        VerifyRedeemLoginButton.IsEnabled = enabled;
        VerifyClearWorkshopButton.IsEnabled = enabled;
        VerifyApplyLoadoutButton.IsEnabled = enabled;
        VerifyPersonalizeButton.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
        OneClickQueryButton.IsEnabled = enabled;

        // 取消按钮反其道而行：仅忙碌时出现且保持可用，让用户中断长任务。
        CancelButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        AccountNameBox.Text = string.Empty;
        EyaTokenBox.Text = string.Empty;
        CredAccountNameBox.Text = string.Empty;
        CredPasswordBox.Password = string.Empty;
        ClearTokenLoginFields();
        LegacyEyaLicenseKeyBox.Text = string.Empty;
        LegacyResolvedAccountBox.Text = string.Empty;
        LicenseKeyBox.Text = string.Empty;
        ResolvedAccountBox.Text = string.Empty;
        VerifyKeyBox.Text = string.Empty;
        ResetVerifyResult();
        IvanLuffyKeyBox.Text = string.Empty;
        IvanLuffyResolvedBox.Text = string.Empty;
        CardStoreImportBox.Text = string.Empty;
        _ivanLuffyCachedAccount = null;
        _ivanLuffyCachedKey = null;

        _cachedAccountData = null;
        _cachedLicenseKey = null;
        _cachedLegacyAccountData = null;
        _cachedLegacyLicenseKey = null;

        UpdateAccountInfoFromCurrentInputs();
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(Loc.T("Login_Status_Cancelling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    /// <summary>从历史页载入账号到登录页：切到 Token 登录并回填 SteamID 与 Token。</summary>
    public void LoadHistoryAccount(SteamAccountHistoryItem account)
    {
        ClearTokenLoginFields();
        _pendingTokenLoginSelection = true;
        ApplyTokenLoginSelection();
        TokenSteamIdBox.Text = account.SteamId;
        TokenBox.Text = account.EyaToken;
        UpdateAccountInfo(account.SteamId, account.EyaToken);
        ApplyAccountInfoProfile(account);

        ShowStatus(
            Loc.Tf("Login_Status_HistoryLoaded_Format", account.AccountTitle, FormatHelper.FormatDateTime(account.LastLoginAt)),
            InfoBarSeverity.Informational);
    }

    private void LoginPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_pendingTokenLoginSelection)
        {
            return;
        }

        ApplyTokenLoginSelection();
        _pendingTokenLoginSelection = false;
    }

    private void ApplyTokenLoginSelection()
    {
        LegacyEyaModeItem.IsSelected = false;
        TokenLoginModeItem.IsSelected = true;
        ModeSelector.SelectedItem = TokenLoginModeItem;
        ApplyModeVisibility();
        if (IsLoaded)
        {
            ModeSelector.Focus(FocusState.Programmatic);
        }
    }
	private async void ApplyLoadoutButton_Click(object sender, RoutedEventArgs e)
	{
		CsLoadoutPreset preset = AppState.SettingsService.Load().Loadout;
		if (preset.T.Count == 0 && preset.Ct.Count == 0)
		{
			ShowStatus(Loc.T("Login_Loadout_EmptyPreset"), InfoBarSeverity.Warning);
			return;
		}
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		try
		{
			var (text, eyaToken) = await GetCredentialsAsync(cancellationToken);
			EnsureTokenValidForAction(eyaToken, "Login_Action_ApplyLoadout");
			UpdateAccountInfo(text, eyaToken);
			await UpdateAccountProfileAsync(text, eyaToken);
			string steamId = AppState.JwtTokenService.Inspect(eyaToken).SteamId ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdLoadout"));
			ShowStatus(Loc.T("Login_Status_ApplyingLoadout"), InfoBarSeverity.Informational);
			CsLoadoutApplyResult csLoadoutApplyResult = await AppState.LoadoutService.ApplyPresetAsync(preset, eyaToken, steamId, cancellationToken);
			if (csLoadoutApplyResult.IsSuccess)
			{
				ShowStatus(Loc.Tf("Login_Status_LoadoutApplied_Format", csLoadoutApplyResult.Confirmed), InfoBarSeverity.Success);
				return;
			}
			string text2 = string.Join("、", csLoadoutApplyResult.Failures.Take(6));
			if (csLoadoutApplyResult.Failures.Count > 6)
			{
				text2 += "…";
			}
			ShowStatus(Loc.Tf("Login_Status_LoadoutPartial_Format", csLoadoutApplyResult.Confirmed, csLoadoutApplyResult.Requested, text2), InfoBarSeverity.Warning);
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_LoadoutCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async void PersonalizeButton_Click(object sender, RoutedEventArgs e)
	{
		AppSettings settings = AppState.SettingsService.Load();
		string personaName = settings.PersonaName;
		string avatarPath = AppState.SettingsService.PersonalizationAvatarPath;
		bool hasName = !string.IsNullOrWhiteSpace(personaName);
		bool hasRealName = !string.IsNullOrWhiteSpace(settings.ProfileRealName);
		bool hasSummary = !string.IsNullOrWhiteSpace(settings.ProfileSummary);
		bool hasAvatar = File.Exists(avatarPath);
		bool clearAliases = settings.ClearAliasHistoryOnPersonalize;
		if (!hasName && !hasRealName && !hasSummary && !hasAvatar && !clearAliases)
		{
			ShowStatus(Loc.T("Login_Personalize_Empty"), InfoBarSeverity.Warning);
			return;
		}
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		Progress<string> progress = new Progress<string>(delegate(string message)
		{
			ShowStatus(message, InfoBarSeverity.Informational);
		});
		try
		{
			var (userName, eyaToken) = await GetCredentialsAsync(cancellationToken);
			EnsureTokenValidForAction(eyaToken, "Login_Action_Personalize");
			UpdateAccountInfo(userName, eyaToken);
			SteamProfileApplyResult result = await AppState.ProfileService.ApplyAsync(eyaToken, new SteamProfileApplyRequest(hasName ? personaName : null, hasRealName ? settings.ProfileRealName : null, hasSummary ? settings.ProfileSummary : null, hasAvatar ? avatarPath : null, clearAliases), progress, cancellationToken);
			if (result.IsFullSuccess)
			{
				ShowStatus(Loc.T("Login_Status_Personalized"), InfoBarSeverity.Success);
			}
			if (result.NameApplied || result.AvatarApplied)
			{
				string steamId = AppState.JwtTokenService.Inspect(eyaToken).SteamId;
				if (!string.IsNullOrWhiteSpace(steamId))
				{
					await Task.Run(delegate
					{
						AppState.AccountHistoryService.UpdateProfileLocally(steamId, result.NameApplied ? personaName : null, result.AvatarApplied ? avatarPath : null);
					});
					AppState.ReloadHistory();
					if (string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
					{
						ApplyStoredAccountInfoProfile(steamId);
					}
				}
			}
			if (!result.IsFullSuccess)
			{
				List<string> list = new List<string>();
				if (result.ProfileRequested && !result.ProfileApplied)
				{
					list.Add(result.ProfileError ?? Loc.T("Profile_Error_Unknown"));
				}
				if (result.AvatarRequested && !result.AvatarApplied)
				{
					list.Add(result.AvatarError ?? Loc.T("Profile_Error_Unknown"));
				}
				if (result.AliasClearRequested && !result.AliasesCleared)
				{
					list.Add(result.AliasClearError ?? Loc.T("Profile_Error_Unknown"));
				}
				ShowStatus(Loc.Tf("Login_Status_PersonalizePartial_Format", string.Join("; ", list)), InfoBarSeverity.Warning);
			}
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_PersonalizeCancelled"), InfoBarSeverity.Informational);
		}
		catch (SteamCmException ex2) when (ex2.IsTokenFailure)
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		catch (Exception ex3)
		{
			ShowStatus(ex3.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private void ClearCredsButton_Click(object sender, RoutedEventArgs e)
	{
		CredAccountNameBox.Text = string.Empty;
		CredPasswordBox.Password = string.Empty;
	}

	private async void CredFetchButton_Click(object sender, RoutedEventArgs e)
	{
		string text = CredAccountNameBox.Text.Trim();
		string password = CredPasswordBox.Password;
		if (string.IsNullOrWhiteSpace(text))
		{
			ShowStatus(Loc.T("Creds_Error_AccountRequired"), InfoBarSeverity.Warning);
			return;
		}
		if (string.IsNullOrEmpty(password))
		{
			ShowStatus(Loc.T("Creds_Error_PasswordRequired"), InfoBarSeverity.Warning);
			return;
		}
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		Progress<string> progress = new Progress<string>(delegate(string message)
		{
			ShowStatus(message, InfoBarSeverity.Informational);
		});
		try
		{
			CredentialsAuthResult result = await AppState.CredentialsAuthService.GetRefreshTokenAsync(text, password, PromptGuardCodeAsync, progress, cancellationToken);
			string token = FormatHelper.NormalizeToken(result.RefreshToken);
			JwtTokenInfo jwtTokenInfo = AppState.JwtTokenService.Inspect(token);
			await AppState.AccountHistoryService.SaveLoginAsync(result.AccountName, result.SteamId, token, jwtTokenInfo.ExpiresAt);
			AppState.ReloadHistory(result.SteamId);
			ModeSelector.SelectedItem = AutoModeItem;
			AccountNameBox.Text = result.AccountName;
			EyaTokenBox.Text = token;
			CredPasswordBox.Password = string.Empty;
			UpdateAccountInfo(result.AccountName, token);
			ShowStatus(Loc.Tf("Creds_Status_Saved_Format", result.AccountName), InfoBarSeverity.Success);
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Creds_Status_Cancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			AppLog.Error("账密获取 EYA 令牌失败。", ex2);
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async Task<string?> PromptGuardCodeAsync(SteamGuardPrompt prompt, CancellationToken cancellationToken)
	{
		bool flag = prompt.Type == SteamGuardType.DeviceCode;
		TextBox codeBox = new TextBox
		{
			PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
			IsSpellCheckEnabled = false,
			MaxLength = 10,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 4.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = (string.IsNullOrWhiteSpace(prompt.AssociatedMessage) ? Loc.T(flag ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage") : prompt.AssociatedMessage),
			TextWrapping = TextWrapping.Wrap
		});
		stackPanel.Children.Add(codeBox);
		return (await new ContentDialog
		{
			Title = Loc.T(flag ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
			Content = stackPanel,
			PrimaryButtonText = Loc.T("Common_Confirm"),
			CloseButtonText = Loc.T("Common_Cancel"),
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary) ? codeBox.Text.Trim() : null;
	}

	private async void ClearWorkshopButton_Click(object sender, RoutedEventArgs e)
	{
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_ClearingWorkshop"), InfoBarSeverity.Informational);
		Progress<string> progress = new Progress<string>(delegate(string message)
		{
			ShowStatus(message, InfoBarSeverity.Informational);
		});
		try
		{
			var (text, eyaToken) = await GetCredentialsAsync(cancellationToken);
			EnsureTokenValidForAction(eyaToken, "Login_Action_ClearWorkshop");
			UpdateAccountInfo(text, eyaToken);
			await UpdateAccountProfileAsync(text, eyaToken);
			int num = await AppState.WorkshopService.ClearSubscriptionsAsync(eyaToken, progress, cancellationToken);
			ShowStatus(Loc.Tf("Login_Status_WorkshopCleared_Format", num), InfoBarSeverity.Success);
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_ClearWorkshopCancelled"), InfoBarSeverity.Informational);
		}
		catch (SteamCmException ex2) when (ex2.IsTokenFailure)
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
			ShowStatus(ex2.Message + Loc.T("Login_Error_CannotClearWorkshopSuffix"), InfoBarSeverity.Error);
		}
		catch (Exception ex3)
		{
			ShowStatus(ex3.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async void LoginButton_Click(object sender, RoutedEventArgs e)
	{
		if (IsLegacyTokenFlow)
		{
			await LoginLegacyEyaAsync();
		}
		else
		{
			await RunStandardLoginAsync();
		}
	}

	private async Task RunStandardLoginAsync(string? whiteAccountName = null)
	{
		if (!(await SteamPathCoordinator.EnsureResolvedAsync()))
		{
			ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
			return;
		}
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_Processing"), InfoBarSeverity.Informational);
		Progress<string> progress = new Progress<string>(delegate(string message)
		{
			ShowStatus(message, InfoBarSeverity.Informational);
		});
		try
		{
			var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
			EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
			UpdateAccountInfo(accountName, eyaToken);
			SteamAccountHistoryItem profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
			await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
			LoginResult result = await Task.Run(() => AppState.LoginService.Login(accountName, eyaToken, progress), cancellationToken);
			if (whiteAccountName != null)
			{
				await AppState.WhiteAccountService.SaveLoginAsync(whiteAccountName, result.SteamId, eyaToken, result.ExpiresAt, NullIfBlank(profile?.PersonaName), NullIfBlank(profile?.AvatarUrl), NullIfBlank(profile?.AvatarPath), isWhiteAccount: true, preserveLastLoginAt: true);
				AppState.ReloadWhiteAccounts(result.SteamId);
				ShowStatus(Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining), string.Empty), InfoBarSeverity.Success);
			}
			else
			{
				string text = await SaveLoginHistoryAsync(result, eyaToken, profile);
				ShowStatus(Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining), text), (!_lastLoginHistorySaveFailed) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
			}
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			AppLog.Error("登录失败。", ex2);
			ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex2.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async Task LoginLegacyEyaAsync()
	{
		if (!(await SteamPathCoordinator.EnsureResolvedAsync()))
		{
			ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
			return;
		}
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_Processing"), InfoBarSeverity.Informational);
		Progress<string> progress = new Progress<string>(delegate(string message)
		{
			ShowStatus(message, InfoBarSeverity.Informational);
		});
		try
		{
			var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
			UpdateAccountInfo(accountName, eyaToken);
			LoginResult result = await Task.Run(() => AppState.LegacyEyaLoginService.Login(accountName, eyaToken, progress), cancellationToken);
			string text = await SaveLoginHistoryAsync(result, eyaToken, null);
			ShowStatus(Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining), text), (!_lastLoginHistorySaveFailed) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			AppLog.Error("旧版 EYA Token 登录失败。", ex2);
			ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex2.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	public async Task<LoginResult> QuickLoginAsync(string accountName, string eyaToken, IProgress<string> progress, CancellationToken cancellationToken, bool whiteStore = false)
	{
		if (!(await SteamPathCoordinator.EnsureResolvedAsync()))
		{
			throw new OperationCanceledException();
		}
		eyaToken = FormatHelper.NormalizeToken(eyaToken);
		EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
		UpdateAccountInfo(accountName, eyaToken);
		SteamAccountHistoryItem profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
		await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
		LoginResult result = await Task.Run(() => AppState.LoginService.Login(accountName, eyaToken, progress), cancellationToken);
		if (!whiteStore)
		{
			await SaveLoginHistoryAsync(result, eyaToken, profile);
		}
		else
		{
			await AppState.WhiteAccountService.SaveLoginAsync(result.AccountName, result.SteamId, eyaToken, result.ExpiresAt, NullIfBlank(profile?.PersonaName), NullIfBlank(profile?.AvatarUrl), NullIfBlank(profile?.AvatarPath), isWhiteAccount: true, preserveLastLoginAt: true);
			AppState.ReloadWhiteAccounts(result.SteamId);
		}
		return result;
	}

	private void ClearLicenseButton_Click(object sender, RoutedEventArgs e)
	{
		LicenseKeyBox.Text = string.Empty;
		ResolvedAccountBox.Text = string.Empty;
		_cachedAccountData = null;
		_cachedLicenseKey = null;
	}

	private async void ResolveLicenseButton_Click(object sender, RoutedEventArgs e)
	{
		await ResolveLicenseInteractiveAsync();
	}

	private async Task ResolveLicenseInteractiveAsync()
	{
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
		try
		{
			SteamAccountData account = await ResolveLicenseAsync(cancellationToken);
			SteamTokenOnlineValidationResult steamTokenOnlineValidationResult = await ValidateTokenOnlineAsync(account.Token, cancellationToken);
			ShowStatus(steamTokenOnlineValidationResult.IsValid ? Loc.Tf("Login_Status_LicenseResolved_Format", account.User, account.SteamId) : steamTokenOnlineValidationResult.Status, steamTokenOnlineValidationResult.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async void OneClickQueryButton_Click(object sender, RoutedEventArgs e)
	{
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_QueryingAccount"), InfoBarSeverity.Informational);
		try
		{
			(string, string) obj = await GetCredentialsAsync(cancellationToken);
			string item = obj.Item1;
			string item2 = obj.Item2;
			CsPremierScoreResult csPremierScoreResult = await QueryAndSaveCsStatusAsync(item, item2, cancellationToken);
			ShowStatus(Loc.Tf("Login_Status_QueryDone_Format", csPremierScoreResult.DisplayText, csPremierScoreResult.PlayerLevelText, csPremierScoreResult.CooldownText, csPremierScoreResult.GcVacText), InfoBarSeverity.Success);
		}
		catch (OperationCanceledException)
		{
			ShowStatus(Loc.T("Login_Status_QueryCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
	{
		if ((object)ManualPanel != null && (object)LegacyEyaPanel != null && (object)TokenLoginPanel != null && (object)AutoPanel != null && (object)CredsPanel != null && (object)VerifyPanel != null && (object)CardStorePanel != null && (object)ActionButtonGrid != null)
		{
			ApplyModeVisibility();
			UpdateAccountInfoFromCurrentInputs();
		}
	}

	private void ApplyModeVisibility()
	{
		bool isAutoMode = IsAutoMode;
		bool isLegacyEyaMode = IsLegacyEyaMode;
		bool isTokenLoginMode = IsTokenLoginMode;
		bool isCredsMode = IsCredsMode;
		bool isVerifyMode = IsVerifyMode;
		bool isIvanLuffyMode = IsIvanLuffyMode;
		bool isCardStoreMode = IsCardStoreMode;
		ManualPanel.Visibility = ((isAutoMode | isLegacyEyaMode | isTokenLoginMode | isCredsMode | isVerifyMode | isIvanLuffyMode | isCardStoreMode) ? Visibility.Collapsed : Visibility.Visible);
		LegacyEyaPanel.Visibility = ((!isLegacyEyaMode) ? Visibility.Collapsed : Visibility.Visible);
		TokenLoginPanel.Visibility = ((!isTokenLoginMode) ? Visibility.Collapsed : Visibility.Visible);
		AutoPanel.Visibility = ((!isAutoMode) ? Visibility.Collapsed : Visibility.Visible);
		CredsPanel.Visibility = ((!isCredsMode) ? Visibility.Collapsed : Visibility.Visible);
		VerifyPanel.Visibility = ((!isVerifyMode) ? Visibility.Collapsed : Visibility.Visible);
		// 「伊万/路飞」与「Token 登录」「账号核验」平级：选了那个页签才显示。
		IvanLuffyPanel.Visibility = ((!isIvanLuffyMode) ? Visibility.Collapsed : Visibility.Visible);
		// 「黑号存储」同样是独立页签：只在选中它时显示，并按当前内容刷一遍列表。
		CardStorePanel.Visibility = ((!isCardStoreMode) ? Visibility.Collapsed : Visibility.Visible);
		if (isCardStoreMode)
		{
			RefreshCardStoreList();
		}
		// 核验模式只查账号状态，不改 Steam 配置也不落盘，故与账密模式一样收起登录/装配操作区。
		ActionButtonGrid.Visibility = ((isCredsMode || isVerifyMode || isIvanLuffyMode || isCardStoreMode) ? Visibility.Collapsed : Visibility.Visible);

		// 右侧账号信息卡描述的是本机 EYA 会话，与核验（远端账号）无关：核验模式下暂时隐藏，
		// 并把右列宽度归零，让核验面板用满宽度；切回其它模式即恢复。
		AccountInfoPanel.Visibility = ((isVerifyMode || isCardStoreMode) ? Visibility.Collapsed : Visibility.Visible);
		LayoutGrid.ColumnDefinitions[1].Width = ((isVerifyMode || isCardStoreMode) ? new GridLength(0) : _sideColumnWidth);
	}

	private void ApplyLanguageModeAvailability()
	{
		AutoModeItem.Visibility = Visibility.Collapsed;
		ManualModeItem.Visibility = Visibility.Collapsed;
		CredsModeItem.Visibility = Visibility.Collapsed;
		LegacyEyaModeItem.Visibility = Visibility.Collapsed;
		VerifyModeItem.Visibility = Visibility.Visible;
		IvanLuffyModeItem.Visibility = Visibility.Visible;
		CardStoreModeItem.Visibility = Visibility.Visible;
		// 语言切换不得把用户从核验/伊万路飞模式踢回 Token 登录：仅当当前选中项不可见时才回退。
		if (ModeSelector.SelectedItem != VerifyModeItem && ModeSelector.SelectedItem != IvanLuffyModeItem && ModeSelector.SelectedItem != CardStoreModeItem)
		{
			ModeSelector.SelectedItem = TokenLoginModeItem;
		}
		ApplyModeVisibility();
	}

	private void ClearManualButton_Click(object sender, RoutedEventArgs e)
	{
		AccountNameBox.Text = string.Empty;
		EyaTokenBox.Text = string.Empty;
	}

	private void ManualCredentialBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!IsAutoMode && !IsLegacyEyaMode)
		{
			UpdateAccountInfoFromCurrentInputs();
		}
	}

	private void LicenseKeyBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_cachedAccountData = null;
		_cachedLicenseKey = null;
		ResolvedAccountBox.Text = "";
		if (IsAutoMode)
		{
			UpdateAccountInfoFromCurrentInputs();
		}
	}

	private void LegacyEyaLicenseKeyBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_cachedLegacyAccountData = null;
		_cachedLegacyLicenseKey = null;
		LegacyResolvedAccountBox.Text = string.Empty;
		if (IsLegacyEyaMode)
		{
			UpdateAccountInfoFromCurrentInputs();
		}
	}

	private void ClearTokenButton_Click(object sender, RoutedEventArgs e)
	{
		ClearTokenLoginFields();
	}

	private void ClearTokenLoginFields()
	{
		TokenLicenseKeyBox.Text = string.Empty;
		TokenSteamIdBox.Text = string.Empty;
		TokenBox.Text = string.Empty;
	}

	private async void TokenLicenseKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
		{
			e.Handled = true;
			await ResolveTokenLicenseInteractiveAsync();
		}
	}

	private async void ResolveTokenLicenseButton_Click(object sender, RoutedEventArgs e)
	{
		await ResolveTokenLicenseInteractiveAsync();
	}


	private async Task ResolveTokenLicenseInteractiveAsync()
	{
		if (string.IsNullOrWhiteSpace(TokenLicenseKeyBox.Text))
		{
			ShowStatus(Loc.T("Login_Error_LicenseKeyRequired"), InfoBarSeverity.Warning);
			return;
		}
		TokenSteamIdBox.Text = string.Empty;
		TokenBox.Text = string.Empty;
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
		try
		{
			EyaLicenseParseResult eyaLicenseParseResult = await AppState.LegacyEyaLicenseClient.ParseLicenseKeyAsync(TokenLicenseKeyBox.Text, cancellationToken);
			TokenSteamIdBox.Text = eyaLicenseParseResult.SteamId;
			TokenBox.Text = eyaLicenseParseResult.Token;
			UpdateAccountInfo(eyaLicenseParseResult.SteamId, eyaLicenseParseResult.Token);
			string text = (string.IsNullOrWhiteSpace(eyaLicenseParseResult.AccountName) ? eyaLicenseParseResult.SteamId : eyaLicenseParseResult.AccountName);
			ShowStatus(Loc.Tf("Login_Status_LicenseResolved_Format", text, eyaLicenseParseResult.SteamId), InfoBarSeverity.Success);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private void TokenLoginBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (IsTokenLoginMode)
		{
			UpdateAccountInfoFromCurrentInputs();
		}
	}

	private void AccountNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter)
		{
			EyaTokenBox.Focus(FocusState.Programmatic);
			e.Handled = true;
		}
	}

	private async void LicenseKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
		{
			e.Handled = true;
			await ResolveLicenseInteractiveAsync();
		}
	}

	private async void LegacyEyaLicenseKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
		{
			e.Handled = true;
			await ResolveLegacyLicenseInteractiveAsync();
		}
	}

	private void ClearLegacyLicenseButton_Click(object sender, RoutedEventArgs e)
	{
		LegacyEyaLicenseKeyBox.Text = string.Empty;
		LegacyResolvedAccountBox.Text = string.Empty;
		_cachedLegacyAccountData = null;
		_cachedLegacyLicenseKey = null;
	}

	private async void ResolveLegacyLicenseButton_Click(object sender, RoutedEventArgs e)
	{
		await ResolveLegacyLicenseInteractiveAsync();
	}

	private async Task ResolveLegacyLicenseInteractiveAsync()
	{
		CancellationToken cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
		try
		{
			SteamAccountData account = await ResolveLegacyLicenseAsync(cancellationToken);
			SteamTokenOnlineValidationResult steamTokenOnlineValidationResult = await ValidateTokenOnlineAsync(account.Token, cancellationToken);
			ShowStatus(steamTokenOnlineValidationResult.IsValid ? Loc.Tf("Login_Status_LicenseResolved_Format", account.User, account.SteamId) : steamTokenOnlineValidationResult.Status, steamTokenOnlineValidationResult.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex2)
		{
			ShowStatus(ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	private async Task<SteamAccountData> ResolveLegacyLicenseAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string licenseKey = LegacyEyaLicenseKeyBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(licenseKey))
		{
			throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
		}
		if ((object)_cachedLegacyAccountData != null && string.Equals(_cachedLegacyLicenseKey, licenseKey, StringComparison.Ordinal))
		{
			return _cachedLegacyAccountData;
		}
		SteamAccountData account = (_cachedLegacyAccountData = await AppState.LegacyEyaLicenseClient.GetAccountDataAsync(licenseKey, cancellationToken));
		_cachedLegacyLicenseKey = licenseKey;
		LegacyResolvedAccountBox.Text = account.User + "  (" + account.SteamId + ")";
		UpdateAccountInfo(account.User, account.Token);
		await UpdateAccountProfileAsync(account.User, account.Token);
		return account;
	}

	private async Task<(string AccountName, string EyaToken)> GetCredentialsAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (IsTokenLoginMode)
		{
			string text = TokenSteamIdBox.Text.Trim();
			string text2 = FormatHelper.NormalizeToken(TokenBox.Text.Trim());
			if (text.Length == 0)
			{
				throw new InvalidOperationException(Loc.T("Login_Error_SteamIdRequired"));
			}
			if (text2.Length == 0)
			{
				throw new InvalidOperationException(Loc.T("Login_Error_TokenRequired"));
			}
			return (AccountName: text, EyaToken: text2);
		}
		if (IsLegacyEyaMode)
		{
			SteamAccountData steamAccountData = await ResolveLegacyLicenseAsync(cancellationToken);
			return (AccountName: steamAccountData.User, EyaToken: FormatHelper.NormalizeToken(steamAccountData.Token));
		}
		if (IsAutoMode)
		{
			SteamAccountData steamAccountData2 = await ResolveLicenseAsync(cancellationToken);
			return (AccountName: steamAccountData2.User, EyaToken: FormatHelper.NormalizeToken(steamAccountData2.Token));
		}
		string text3 = AccountNameBox.Text.Trim();
		string text4 = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());
		if (string.IsNullOrWhiteSpace(text3))
		{
			throw new InvalidOperationException(Loc.T("Login_Error_AccountNameRequired"));
		}
		if (string.IsNullOrWhiteSpace(text4))
		{
			throw new InvalidOperationException(Loc.T("Login_Error_EyaTokenRequired"));
		}
		return (AccountName: text3, EyaToken: text4);
	}

	private async Task<SteamAccountData> ResolveLicenseAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string text = LicenseKeyBox.Text.Trim();
		string licenseKey = NormalizeLicenseKey(text);
		if (!string.Equals(text, licenseKey, StringComparison.Ordinal))
		{
			LicenseKeyBox.Text = licenseKey;
			LicenseKeyBox.SelectionStart = licenseKey.Length;
		}
		if (string.IsNullOrWhiteSpace(licenseKey))
		{
			throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
		}
		// 只剩奶味一家上游，取卡固定用它，不再需要服务器选择。
		SteamUpstreamServer server = SteamLicenseClient.Upstream;
		if ((object)_cachedAccountData != null && string.Equals(_cachedLicenseKey, licenseKey, StringComparison.Ordinal))
		{
			return _cachedAccountData;
		}
		SteamAccountData account = (_cachedAccountData = await AppState.LicenseClient.GetAccountDataAsync(licenseKey, server, cancellationToken));
		_cachedLicenseKey = licenseKey;
		ResolvedAccountBox.Text = account.User + "  (" + account.SteamId + ")";
		UpdateAccountInfo(account.User, account.Token);
		await UpdateAccountProfileAsync(account.User, account.Token);
		return account;
	}

    // ---------- 「伊万/路飞」：与 Token登录 面板一致的三项登录前操作 ----------

    private enum IvanLuffyPreLoginAction
    {
        ClearWorkshop,
        ApplyLoadout,
        Personalize
    }

    private async void IvanLuffyClearWorkshopButton_Click(object sender, RoutedEventArgs e) =>
        await RunIvanLuffyPreLoginActionAsync(IvanLuffyPreLoginAction.ClearWorkshop);

    private async void IvanLuffyApplyLoadoutButton_Click(object sender, RoutedEventArgs e) =>
        await RunIvanLuffyPreLoginActionAsync(IvanLuffyPreLoginAction.ApplyLoadout);

    private async void IvanLuffyPersonalizeButton_Click(object sender, RoutedEventArgs e) =>
        await RunIvanLuffyPreLoginActionAsync(IvanLuffyPreLoginAction.Personalize);

    /// <summary>
    /// 登录前可选操作：先用本模块的卡密解析出账号，回填 Token登录 面板，再调用那三个按钮同一个处理函数
    /// ——与「账号核验」页完全同一套做法，不复制也不改写既有逻辑。
    /// </summary>
    private async Task RunIvanLuffyPreLoginActionAsync(IvanLuffyPreLoginAction action)
    {
        if (AppState.IsBusy)
        {
            return;
        }

        if (!await EnsureIvanLuffyCredentialsAsync())
        {
            return;
        }

        ApplyTokenLoginSelection();
        switch (action)
        {
            case IvanLuffyPreLoginAction.ClearWorkshop:
                ClearWorkshopButton_Click(ClearWorkshopButton, new RoutedEventArgs());
                break;
            case IvanLuffyPreLoginAction.ApplyLoadout:
                ApplyLoadoutButton_Click(ApplyLoadoutButton, new RoutedEventArgs());
                break;
            case IvanLuffyPreLoginAction.Personalize:
                PersonalizeButton_Click(PersonalizeButton, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>用本模块的卡密解析出账号（同一卡密命中缓存就不再请求），回填 Token登录 面板（SteamID + JWT）。</summary>
    private async Task<bool> EnsureIvanLuffyCredentialsAsync()
    {
        if (IvanLuffyKeyBox.Text.Trim().Length == 0)
        {
            ShowStatus(Loc.T("Login_Error_LicenseKeyRequired"), InfoBarSeverity.Warning);
            return false;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
        try
        {
            var account = await ResolveIvanLuffyAsync(cancellationToken);
            TokenLicenseKeyBox.Text = IvanLuffyKeyBox.Text.Trim();
            TokenSteamIdBox.Text = account.SteamId;
            TokenBox.Text = account.Token;
            UpdateAccountInfo(account.SteamId, account.Token);
            ShowStatus(
                Loc.Tf("Login_Status_LicenseResolved_Format", account.SteamId, account.SteamId),
                InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"伊万/路飞解析失败（登录前操作）：{ex.Message}");
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // ---------- 「伊万/路飞」模块（两条上游取卡 → 解析 → 一键登录） ----------

    /// <summary>下拉里铺上伊万/小岛、路飞两条上游，默认选第一条。</summary>
    private void InitializeIvanLuffyModule()
    {
        foreach (SteamUpstreamServer server in SteamLicenseClient.IvanLuffyServers)
        {
            var item = new MenuFlyoutItem
            {
                Text = server.Name,
                Tag = server
            };
            item.Click += IvanLuffyServerMenuItem_Click;
            IvanLuffyServerFlyout.Items.Add(item);
        }

        if (SteamLicenseClient.IvanLuffyServers.Count > 0)
        {
            _ivanLuffyServer = SteamLicenseClient.IvanLuffyServers[0];
            IvanLuffyServerText.Text = _ivanLuffyServer.Name;
        }
    }

    private void IvanLuffyServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: SteamUpstreamServer server } && !Equals(_ivanLuffyServer, server))
        {
            _ivanLuffyServer = server;
            IvanLuffyServerText.Text = server.Name;
            // 换了上游，之前解析出来的账号不再作数。
            ResetIvanLuffyResolution();
        }
    }

    /// <summary>卡密改动或换上游后，清掉上一次的解析结果与缓存。</summary>
    private void ResetIvanLuffyResolution()
    {
        _ivanLuffyCachedAccount = null;
        _ivanLuffyCachedKey = null;
        IvanLuffyResolvedBox.Text = string.Empty;
    }

    private void IvanLuffyKeyBox_TextChanged(object sender, TextChangedEventArgs e) => ResetIvanLuffyResolution();

    private async void IvanLuffyKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
        {
            e.Handled = true;
            await ResolveIvanLuffyInteractiveAsync();
        }
    }

    private async void IvanLuffyResolveButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveIvanLuffyInteractiveAsync();

    private void IvanLuffyClearButton_Click(object sender, RoutedEventArgs e)
    {
        IvanLuffyKeyBox.Text = string.Empty;
        ResetIvanLuffyResolution();
    }

    /// <summary>解析卡密：向上游换账号+令牌（同一卡密命中缓存就不再请求），并校验令牌是否被 Steam 接受。</summary>
    private async Task ResolveIvanLuffyInteractiveAsync()
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
        try
        {
            var account = await ResolveIvanLuffyAsync(cancellationToken);
            var validation = await ValidateTokenOnlineAsync(account.Token, cancellationToken);
            ShowStatus(
                validation.IsValid
                    ? Loc.Tf("Login_Status_LicenseResolved_Format", account.User, account.SteamId)
                    : validation.Status,
                validation.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"伊万/路飞卡密解析失败：{ex.Message}");
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    /// <summary>向上游换账号数据（与奶味那条链路同一个 LicenseClient，只是指定了别家的 BaseUrl）。</summary>
    private async Task<SteamAccountData> ResolveIvanLuffyAsync(CancellationToken cancellationToken)
    {
        var licenseKey = NormalizeLicenseKey(IvanLuffyKeyBox.Text.Trim());
        if (!string.Equals(IvanLuffyKeyBox.Text.Trim(), licenseKey, StringComparison.Ordinal))
        {
            IvanLuffyKeyBox.Text = licenseKey;
            IvanLuffyKeyBox.SelectionStart = licenseKey.Length;
        }

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        var server = _ivanLuffyServer
            ?? throw new InvalidOperationException(Loc.T("Login_Error_UpstreamServerRequired"));

        if (_ivanLuffyCachedAccount is not null &&
            string.Equals(_ivanLuffyCachedKey, licenseKey, StringComparison.Ordinal))
        {
            return _ivanLuffyCachedAccount;
        }

        var account = _ivanLuffyCachedAccount =
            await AppState.LicenseClient.GetAccountDataAsync(licenseKey, server, cancellationToken);
        _ivanLuffyCachedKey = licenseKey;
        IvanLuffyResolvedBox.Text = account.User + "  (" + account.SteamId + ")";
        UpdateAccountInfo(account.User, account.Token);
        await UpdateAccountProfileAsync(account.User, account.Token);
        return account;
    }

    /// <summary>
    /// 一键登录：与「账号核验」页的取名登录同款做法——先用本模块的卡密解析出账号（命中缓存就不再请求），
    /// 回填 Token 登录面板，再交给那套登录流程（走旧版 EYA 链路：合并写 Steam 配置，不重写 config.vdf）。
    /// 不复制也不改写既有登录逻辑。
    /// </summary>
    private async void IvanLuffyLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy)
        {
            return;
        }

        if (!await EnsureIvanLuffyCredentialsAsync())
        {
            return;
        }

        // 切到 Token登录 面板（SteamID + JWT）并交给既有登录流程。
        ApplyTokenLoginSelection();

        string? configuredSteamPath = SteamPathCoordinator.GetPersistedInstallPath();
        ShowStatus(
            configuredSteamPath is null
                ? Loc.T("Settings_SteamPath_NotSet")
                : Loc.Tf("Login_Verify_SteamPath_Format", configuredSteamPath),
            configuredSteamPath is null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);

        LoginButton_Click(LoginButton, new RoutedEventArgs());
    }

	// ---------- 「黑号存储」模块（还没登录过的卡密：导入 / 验号 / 删除 / 送去 Token 登录） ----------

	/// <summary>导入时选的来源：奶味 / 伊万·路飞（默认奶味）。</summary>
	private string _cardStoreSource = CardKeyEntry.SourceNaiwei;

	/// <summary>验号解析出来的账号，仅在内存里留给「去 Token 登录」用；令牌不落盘。</summary>
	private readonly Dictionary<string, SteamAccountData> _cardStoreResolved = new(StringComparer.Ordinal);

	private bool IsCardStoreMode => ModeSelector.SelectedItem == CardStoreModeItem;

	/// <summary>铺好来源下拉（奶味 / 伊万·路飞），并按已存内容刷一遍列表。</summary>
	private void InitializeCardStoreModule()
	{
		foreach (var source in new[] { CardKeyEntry.SourceNaiwei, CardKeyEntry.SourceIvan, CardKeyEntry.SourceLuffy })
		{
			var item = new MenuFlyoutItem
			{
				Text = Loc.T(CardStoreSourceNameKey(source)),
				Tag = source
			};
			item.Click += CardStoreSourceMenuItem_Click;
			CardStoreSourceFlyout.Items.Add(item);
		}

		UpdateCardStoreSourceText();
		RefreshCardStoreList();
	}

	private void CardStoreSourceMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: string source })
		{
			_cardStoreSource = source;
			UpdateCardStoreSourceText();
		}
	}

	private void UpdateCardStoreSourceText() =>
		CardStoreSourceText.Text = Loc.T(CardStoreSourceNameKey(_cardStoreSource));

	/// <summary>来源文案键：奶味 / 伊万 / 路飞（认不出来的历史值按奶味显示）。</summary>
	private static string CardStoreSourceNameKey(string source) => CardStoreSourceKind(source) switch
	{
		CardKeyEntry.SourceIvan => "CardStore_Source_Ivan",
		CardKeyEntry.SourceLuffy => "CardStore_Source_Luffy",
		_ => "CardStore_Source_Naiwei"
	};

	/// <summary>把来源规整成三种之一：奶味 / 伊万 / 路飞（含早期存过的 ivanluffy、tokenlogin）。</summary>
	private static string CardStoreSourceKind(string source)
	{
		if (string.Equals(source, CardKeyEntry.SourceIvan, StringComparison.Ordinal) ||
			string.Equals(source, CardKeyEntry.SourceIvanLuffyLegacy, StringComparison.Ordinal))
		{
			return CardKeyEntry.SourceIvan;
		}

		return string.Equals(source, CardKeyEntry.SourceLuffy, StringComparison.Ordinal)
			? CardKeyEntry.SourceLuffy
			: CardKeyEntry.SourceNaiwei;
	}

	/// <summary>来源 → 上游：奶味固定那条；伊万 / 路飞各对应 IvanLuffyServers 里的一条。</summary>
	private static SteamUpstreamServer CardStoreUpstream(string source)
	{
		var servers = SteamLicenseClient.IvanLuffyServers;
		var kind = CardStoreSourceKind(source);
		if (kind == CardKeyEntry.SourceIvan && servers.Count > 0)
		{
			return servers[0];
		}

		if (kind == CardKeyEntry.SourceLuffy && servers.Count > 1)
		{
			return servers[1];
		}

		return SteamLicenseClient.Upstream;
	}

	/// <summary>把已存卡密铺到列表上；来源/状态文案按当前语言现算（列表重建即刷新）。</summary>
	private void RefreshCardStoreList()
	{
		var entries = AppState.CardKeys.Entries;
		foreach (var entry in entries)
		{
			entry.SourceText = Loc.T(CardStoreSourceNameKey(entry.Source));
			entry.StatusText = BuildCardKeyStatusText(entry);
		}

		// 列表是普通 List，导入/删除不会自己通知界面：重新挂一次 ItemsSource 强制重建。
		CardStoreList.ItemsSource = null;
		CardStoreList.ItemsSource = entries;
		CardStoreEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private static string BuildCardKeyStatusText(CardKeyEntry entry)
	{
		var parts = new List<string>();
		if (entry.IsUsed)
		{
			parts.Add(Loc.T("CardStore_Status_Used"));
		}

		if (entry.CheckedAt is null)
		{
			parts.Add(Loc.T("CardStore_Status_Unchecked"));
		}
		else if (entry.CheckOk == true)
		{
			var account = string.IsNullOrWhiteSpace(entry.AccountName) ? entry.SteamId ?? string.Empty : entry.AccountName;
			parts.Add(Loc.Tf("CardStore_Status_Ok_Format", account, entry.SteamId ?? string.Empty));
			parts.AddRange(BuildCardKeyVerifySummary(entry));
		}
		else
		{
			parts.Add(Loc.Tf("CardStore_Status_Fail_Format", entry.CheckMessage ?? Loc.T("CardStore_Status_Unknown")));
		}

		return string.Join(" · ", parts);
	}

	/// <summary>奶味那条链路顺带拉回来的核验信息摘要：VAC / 游戏封禁 / 竞技冷却 / 优先 / 等级。</summary>
	private static IEnumerable<string> BuildCardKeyVerifySummary(CardKeyEntry entry)
	{
		if (entry.VacBans is int vacBans)
		{
			yield return vacBans > 0 ? Loc.Tf("CardStore_Verify_VacBanned_Format", vacBans) : Loc.T("CardStore_Verify_VacOk");
		}

		if (entry.GameBans is int gameBans)
		{
			yield return gameBans > 0 ? Loc.Tf("CardStore_Verify_GameBanned_Format", gameBans) : Loc.T("CardStore_Verify_GameOk");
		}

		if (entry.CooldownActive is bool cooldownActive)
		{
			yield return cooldownActive ? Loc.T("CardStore_Verify_CooldownOn") : Loc.T("CardStore_Verify_CooldownOff");
		}

		if (entry.Prime is bool prime)
		{
			yield return prime ? Loc.T("CardStore_Verify_PrimeYes") : Loc.T("CardStore_Verify_PrimeNo");
		}

		if (entry.ProfileRank is int profileRank)
		{
			yield return Loc.Tf("CardStore_Verify_Level_Format", profileRank);
		}
	}

	/// <summary>
	/// 登录成功后把「黑号存储」里对应的那条卡密标成「已用」（用户要求：标记，不自动删除）。
	/// 先按 Token 登录框里的卡密精确匹配，再退回按 SteamID 匹配 —— 手动把卡密粘到别的入口登录也能命中。
	/// </summary>
	private void MarkCardStoreKeyUsed(string steamId)
	{
		var entries = AppState.CardKeys.Entries;
		if (entries.Count == 0)
		{
			return;
		}

		var typedKey = TokenLicenseKeyBox.Text?.Trim() ?? string.Empty;
		var matched = entries.FirstOrDefault(entry =>
			(typedKey.Length > 0 && string.Equals(entry.Key, typedKey, StringComparison.Ordinal)) ||
			(!string.IsNullOrWhiteSpace(steamId) && string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)));

		if (matched is null || matched.IsUsed)
		{
			return;
		}

		matched.IsUsed = true;
		matched.UsedAt = DateTimeOffset.Now;
		if (string.IsNullOrWhiteSpace(matched.SteamId) && !string.IsNullOrWhiteSpace(steamId))
		{
			matched.SteamId = steamId;
		}

		AppState.CardKeys.Save();
		RefreshCardStoreList();
		AppLog.Info($"黑号存储：卡密「{matched.Key}」已标成已用（SteamID {steamId}）。");
	}

	private void CardStoreImportButton_Click(object sender, RoutedEventArgs e)
	{
		// 换行按 \r / \n 都拆（WinUI 的 TextBox 用 \r），每行一个卡密；卡密本身只去掉首尾空白。
		var lines = (CardStoreImportBox.Text ?? string.Empty)
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0)
		{
			ShowStatus(Loc.T("CardStore_Status_NothingToImport"), InfoBarSeverity.Warning);
			return;
		}

		var added = AppState.CardKeys.Import(lines, _cardStoreSource);
		CardStoreImportBox.Text = string.Empty;
		RefreshCardStoreList();
		ShowStatus(
			added > 0
				? Loc.Tf("CardStore_Status_Imported_Format", added, Loc.T(CardStoreSourceNameKey(_cardStoreSource)))
				: Loc.T("CardStore_Status_AllDuplicated"),
			added > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
	}

	private void CardStoreDeleteButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is not CardKeyEntry entry)
		{
			return;
		}

		AppState.CardKeys.Remove(entry);
		_cardStoreResolved.Remove(entry.Key);
		RefreshCardStoreList();
		ShowStatus(Loc.T("CardStore_Status_Deleted"), InfoBarSeverity.Informational);
	}

	private async void CardStoreVerifyButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is not CardKeyEntry entry || AppState.IsBusy)
		{
			return;
		}

		await VerifyStoredCardKeyAsync(entry);
	}

	/// <summary>验号：按来源取卡 → 在线校验令牌 → 结果写回该行并存盘。</summary>
	private async Task VerifyStoredCardKeyAsync(CardKeyEntry entry)
	{
		var cancellationToken = AppState.BeginBusyOperation();
		ShowStatus(Loc.Tf("CardStore_Status_Verifying_Format", entry.Key), InfoBarSeverity.Informational);
		try
		{
			var account = await ResolveStoredCardKeyAsync(entry.Key, entry.Source, cancellationToken);
			var validation = await AppState.TokenOnlineValidationService.ValidateForLoginAsync(account.Token, cancellationToken);
			_cardStoreResolved[entry.Key] = account;

			// 奶味那条上游另外还有一份「封禁 / CS2」核验信息（核验模块用的就是它）：顺带拉回来存进该行。
			// 这一步慢（实测几十秒），失败也不影响「验号」本身的结论，所以单独兜住。
			if (CardStoreSourceKind(entry.Source) == CardKeyEntry.SourceNaiwei)
			{
				ShowStatus(Loc.Tf("CardStore_Status_VerifyingExtra_Format", entry.Key), InfoBarSeverity.Informational);
				try
				{
					var payload = await AppState.VerifyService.VerifyAsync(entry.Key, cancellationToken);
					entry.VacBans = payload.Bans?.VacBans;
					entry.GameBans = payload.Bans?.GameBans;
					entry.CooldownActive = payload.Cs2?.Cooldown?.Active;
					entry.Prime = payload.Cs2?.Prime?.Status;
					entry.ProfileRank = payload.Cs2?.ProfileRank;
					if (string.IsNullOrWhiteSpace(entry.SteamId) && !string.IsNullOrWhiteSpace(payload.SteamId))
					{
						entry.SteamId = payload.SteamId;
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception verifyEx)
				{
					AppLog.Warn($"黑号存储：核验信息获取失败（{entry.Key}）：{verifyEx.Message}");
				}
			}

			entry.CheckedAt = DateTimeOffset.Now;
			entry.CheckOk = validation.IsValid;
			entry.AccountName = account.User;
			entry.SteamId = account.SteamId;
			entry.CheckMessage = validation.IsValid ? null : validation.Status;
			AppState.CardKeys.Save();
			RefreshCardStoreList();

			ShowStatus(
				validation.IsValid
					? Loc.Tf("CardStore_Status_Verified_Format", account.User, account.SteamId)
					: Loc.Tf("CardStore_Status_VerifyFailed_Format", validation.Status),
				validation.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
		}
		catch (Exception ex)
		{
			entry.CheckedAt = DateTimeOffset.Now;
			entry.CheckOk = false;
			entry.CheckMessage = ex.Message;
			AppState.CardKeys.Save();
			RefreshCardStoreList();
			AppLog.Warn($"黑号存储验号失败（{entry.Key}）：{ex.Message}");
			ShowStatus(Loc.Tf("CardStore_Status_VerifyFailed_Format", ex.Message), InfoBarSeverity.Error);
		}
		finally
		{
			AppState.EndBusyOperation();
		}
	}

	/// <summary>
	/// 按来源取卡：奶味 / 伊万 / 路飞 各走自己那条上游的取名接口（keygetdata）。
	/// 上游选错会直接报上游的错误，换个来源再验一次即可 —— 不偷偷换上游，免得存下来的来源对不上。
	/// </summary>
	private static Task<SteamAccountData> ResolveStoredCardKeyAsync(string key, string source, CancellationToken cancellationToken) =>
		AppState.LicenseClient.GetAccountDataAsync(key, CardStoreUpstream(source), cancellationToken);

	/// <summary>
	/// 「去 Token 登录」：解析出账号令牌（刚验过就直接用内存里的），切到 Token登录 页签并回填，
	/// 剩下的交给既有登录流程 —— 不自动点登录，由用户决定。
	/// </summary>
	private async void CardStoreTokenButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is not CardKeyEntry entry || AppState.IsBusy)
		{
			return;
		}

		if (!_cardStoreResolved.TryGetValue(entry.Key, out var account))
		{
			var cancellationToken = AppState.BeginBusyOperation();
			ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
			try
			{
				account = await ResolveStoredCardKeyAsync(entry.Key, entry.Source, cancellationToken);
				_cardStoreResolved[entry.Key] = account;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
				return;
			}
			catch (Exception ex)
			{
				AppLog.Warn($"黑号存储解析失败（{entry.Key}）：{ex.Message}");
				ShowStatus(ex.Message, InfoBarSeverity.Error);
				return;
			}
			finally
			{
				AppState.EndBusyOperation();
			}
		}

		// 与「伊万/路飞」一键登录同款：回填 Token 登录面板，交给既有登录流程。
		ApplyTokenLoginSelection();
		TokenLicenseKeyBox.Text = entry.Key;
		TokenSteamIdBox.Text = account.SteamId;
		TokenBox.Text = account.Token;
		UpdateAccountInfo(account.User, account.Token);
		ShowStatus(Loc.Tf("CardStore_Status_ToToken_Format", account.User, account.SteamId), InfoBarSeverity.Success);
	}
	private static string NormalizeLicenseKey(string value)
	{
		string text = value.Trim();
		int num = text.LastIndexOf("----", StringComparison.Ordinal);
		if (num < 0)
		{
			return text;
		}
		return text.Substring(num + 4).Trim();
	}

	public async Task<CsPremierScoreResult> QueryAndSaveCsStatusAsync(string accountName, string eyaToken, CancellationToken cancellationToken = default(CancellationToken), bool whiteStore = false)
	{
		eyaToken = FormatHelper.NormalizeToken(eyaToken);
		JwtTokenInfo tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
		if (!tokenInfo.IsValid)
		{
			throw new InvalidOperationException(tokenInfo.Status + Loc.T("Login_Error_CannotOneClickQuerySuffix"));
		}
		string steamId = tokenInfo.SteamId ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdQuery"));
		AccountHistoryService historyStore = (whiteStore ? AppState.WhiteAccountService : AppState.AccountHistoryService);
		UpdateAccountInfo(accountName, eyaToken);
		// 与历史页「清空无效账号」同一个校验入口（CM 登录 + 换取 App 令牌）：
		// 两边对同一账号的结论因此完全一致；不通过时给统一提示，也省掉后面整轮 CS2 查询的白等。
		SteamTokenOnlineValidationResult loginCheck = await AppState.TokenOnlineValidationService.ValidateForLoginAsync(eyaToken, cancellationToken);
		if (!loginCheck.IsValid)
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
			throw new SteamCmException(loginCheck.Result, loginCheck.Status + Loc.T("Login_Error_CannotOneClickQuerySuffix"));
		}

		SteamAccountHistoryItem prefetchedProfile = await UpdateAccountProfileAsync(accountName, eyaToken, !whiteStore);
		AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_VerifyingAndQuerying");
		ResetAvailabilityForeground();
		AccountInfoPremierScoreText.Text = Loc.T("Login_Value_Querying");
		AccountInfoCsLevelText.Text = Loc.T("Login_Value_Querying");
		AccountInfoCooldownStatusText.Text = Loc.T("Login_Value_Querying");
		AccountInfoCs2IsChinaText.Text = Loc.T("Login_Value_Querying");
		CsPremierScoreResult csPremierScoreResult;
		SteamTokenOnlineValidationResult jwtValidation;
		try
		{
			csPremierScoreResult = await AppState.PremierScoreService.QueryAsync(eyaToken, steamId, cancellationToken);
			jwtValidation = new SteamTokenOnlineValidationResult(IsValid: true, Loc.T("Token_Result_Accepted"));
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Valid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
		}
		catch (SteamCmException ex) when (ex.IsTokenFailure)
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
			throw new SteamCmException(ex.Result, ex.Message + Loc.T("Login_Error_CannotOneClickQuerySuffix"), ex);
		}
		catch
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
			ResetAvailabilityForeground();
			throw;
		}
		// 写盘要「读-改-写」整份账号文件（含逐字段 AES/DPAPI），留在 UI 线程会与后台资料刷新抢文件锁，
		// 把界面整段卡住；挪到线程池执行，异常照旧向调用方抛出（如凭据库锁定）。
		await Task.Run(() => historyStore.SaveCsAccountStatus(accountName, steamId, eyaToken, tokenInfo.ExpiresAt, csPremierScoreResult, jwtValidation, NullIfBlank(prefetchedProfile?.PersonaName), NullIfBlank(prefetchedProfile?.AvatarUrl), NullIfBlank(prefetchedProfile?.AvatarPath)), CancellationToken.None);
		if (whiteStore)
		{
			AppState.ReloadWhiteAccounts(steamId);
		}
		else
		{
			// 读盘同样离开 UI 线程（见 ReloadHistoryAsync）。
			await AppState.ReloadHistoryAsync(steamId);
		}
		AccountInfoPremierScoreText.Text = csPremierScoreResult.DisplayText;
		AccountInfoCsLevelText.Text = csPremierScoreResult.PlayerLevelText;
		AccountInfoCooldownStatusText.Text = csPremierScoreResult.CooldownStatusText;
		AccountInfoCs2IsChinaText.Text = csPremierScoreResult.Cs2IsChinaText;
		TextBlock accountInfoCs2IsChinaText = AccountInfoCs2IsChinaText;
		bool? cs2IsChina = csPremierScoreResult.Cs2IsChina;
		InfoBarSeverity severity = (cs2IsChina.HasValue ? ((cs2IsChina == true) ? InfoBarSeverity.Success : InfoBarSeverity.Error) : InfoBarSeverity.Informational);
		accountInfoCs2IsChinaText.Foreground = FormatHelper.GetStatusBrush(severity);
		ApplyStoredAccountInfoProfile(steamId);
		return csPremierScoreResult;
	}

	private async Task<string> SaveLoginHistoryAsync(LoginResult result, string eyaToken, SteamAccountHistoryItem? prefetchedProfile)
	{
		// 登录成功这条路：顺手把黑号存储里对应的卡密标成「已用」（标记，不删除）。
		MarkCardStoreKeyUsed(result.SteamId);
		try
		{
			await AppState.AccountHistoryService.SaveLoginAsync(result.AccountName, result.SteamId, eyaToken, result.ExpiresAt, NullIfBlank(prefetchedProfile?.PersonaName), NullIfBlank(prefetchedProfile?.AvatarUrl), NullIfBlank(prefetchedProfile?.AvatarPath));
			AppState.ReloadHistory(result.SteamId);
			ApplyStoredAccountInfoProfile(result.SteamId);
			StartBackgroundProfileRefresh(result.SteamId);
			_lastLoginHistorySaveFailed = false;
			return Loc.T("Login_Status_HistorySaved_Suffix");
		}
		catch (Exception ex)
		{
			_lastLoginHistorySaveFailed = true;
			return Loc.Tf("Login_Status_HistorySaveFailed_Suffix_Format", ex.Message);
		}
	}

	private static string? NullIfBlank(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return null;
	}

	private void StartBackgroundProfileRefresh(string steamId)
	{
		Task.Run(async delegate
		{
			try
			{
				if (await AppState.AccountHistoryService.RefreshProfilesAsync(new string[1] { steamId }) > 0)
				{
					base.DispatcherQueue.TryEnqueue(delegate
					{
						AppState.ReloadHistory();
						if (string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
						{
							SteamAccountHistoryItem steamAccountHistoryItem = AppState.FindHistoryAccount(steamId);
							if (steamAccountHistoryItem != null)
							{
								AccountInfoAvatar.DisplayName = steamAccountHistoryItem.AccountTitle;
								AccountInfoAvatar.ProfilePicture = steamAccountHistoryItem.AvatarImage;
								AccountInfoPersonaText.Text = steamAccountHistoryItem.PersonaDisplayName;
							}
						}
					});
				}
			}
			catch (Exception ex)
			{
				AppLog.Warn("后台刷新账号资料失败（" + steamId + "）：" + ex.Message);
			}
		});
	}

	private async Task<SteamAccountHistoryItem?> UpdateAccountProfileAsync(string accountName, string eyaToken, bool triggerBackgroundRefresh = true)
	{
		JwtTokenInfo jwtTokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
		if (string.IsNullOrWhiteSpace(jwtTokenInfo.SteamId))
		{
			return null;
		}
		SteamAccountHistoryItem storedAccount = AppState.FindHistoryAccount(jwtTokenInfo.SteamId);
		if (storedAccount != null)
		{
			ApplyAccountInfoProfile(storedAccount);
			if (!string.IsNullOrWhiteSpace(storedAccount.PersonaName) && (!string.IsNullOrWhiteSpace(storedAccount.AvatarPath) || !string.IsNullOrWhiteSpace(storedAccount.AvatarUrl)))
			{
				if (triggerBackgroundRefresh)
				{
					StartBackgroundProfileRefresh(jwtTokenInfo.SteamId);
				}
				return storedAccount;
			}
		}
		try
		{
			SteamAccountHistoryItem steamAccountHistoryItem = await AppState.AccountHistoryService.GetProfilePreviewAsync(accountName, jwtTokenInfo.SteamId, eyaToken, jwtTokenInfo.ExpiresAt);
			if (steamAccountHistoryItem != null)
			{
				ApplyAccountInfoProfile(steamAccountHistoryItem);
				return steamAccountHistoryItem;
			}
		}
		catch
		{
		}
		return storedAccount;
	}

	private void ApplyStoredAccountInfoProfile(string steamId)
	{
		SteamAccountHistoryItem steamAccountHistoryItem = AppState.FindHistoryAccount(steamId);
		if (steamAccountHistoryItem != null)
		{
			ApplyAccountInfoProfile(steamAccountHistoryItem);
		}
	}

	private void ApplyAccountInfoProfile(SteamAccountHistoryItem? account)
	{
		if (account == null)
		{
			AccountInfoAvatar.ProfilePicture = null;
			AccountInfoAvatar.DisplayName = string.Empty;
			AccountInfoPersonaText.Text = Loc.T("Login_Value_PersonaNotSynced");
			AccountInfoPremierScoreText.Text = Loc.T("Login_Value_NotQueried");
			AccountInfoCsLevelText.Text = Loc.T("Login_Value_NotQueried");
			AccountInfoCooldownStatusText.Text = Loc.T("Login_Value_NotQueried");
			AccountInfoCs2IsChinaText.Text = Loc.T("Login_Value_NotQueried");
		}
		else
		{
			AccountInfoAvatar.DisplayName = account.AccountTitle;
			AccountInfoAvatar.ProfilePicture = account.AvatarImage;
			AccountInfoPersonaText.Text = account.PersonaDisplayName;
			AccountInfoPremierScoreText.Text = account.CompetitiveScoreText;
			AccountInfoCsLevelText.Text = account.CsPlayerLevelText;
			AccountInfoCooldownStatusText.Text = account.CooldownStatusText;
			AccountInfoCs2IsChinaText.Text = account.Cs2IsChinaText;
		}
	}

	private void EnsureTokenValidForAction(string eyaToken, string actionName)
	{
		JwtTokenInfo jwtTokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
		if (jwtTokenInfo.IsValid)
		{
			return;
		}
		DateTimeOffset? expiresAt = jwtTokenInfo.ExpiresAt;
		if (expiresAt.HasValue && expiresAt.GetValueOrDefault() <= DateTimeOffset.Now && actionName == "Login_Action_ClearWorkshop")
		{
			throw new InvalidOperationException(Loc.T("Login_Error_TokenExpiredCannotClearWorkshop"));
		}
		throw new InvalidOperationException(Loc.Tf("Login_Error_StatusCannotAction_Format", jwtTokenInfo.Status, Loc.T(actionName)));
	}

	private void UpdateAccountInfoFromCurrentInputs()
	{
		if ((object)AccountInfoUserText == null)
		{
			return;
		}
		if (IsCredsMode)
		{
			UpdateAccountInfo(null, null);
		}
		else if (IsTokenLoginMode)
		{
			UpdateAccountInfo(TokenSteamIdBox.Text.Trim(), FormatHelper.NormalizeToken(TokenBox.Text.Trim()));
		}
		else if (IsLegacyEyaMode)
		{
			if ((object)_cachedLegacyAccountData != null)
			{
				UpdateAccountInfo(_cachedLegacyAccountData.User, _cachedLegacyAccountData.Token);
			}
			else
			{
				UpdateAccountInfo(null, null);
			}
		}
		else if (IsAutoMode)
		{
			if ((object)_cachedAccountData != null)
			{
				UpdateAccountInfo(_cachedAccountData.User, _cachedAccountData.Token);
			}
			else
			{
				UpdateAccountInfo(null, null);
			}
		}
		else
		{
			UpdateAccountInfo(AccountNameBox.Text.Trim(), FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim()));
		}
	}

	private void ResetAvailabilityForeground()
	{
		AccountInfoAvailabilityText.ClearValue(TextBlock.ForegroundProperty);
	}

	private void UpdateAccountInfo(string? userName, string? token)
	{
		AccountInfoUserText.Text = (string.IsNullOrWhiteSpace(userName) ? Loc.T("Login_Value_NotFilled") : userName);
		ApplyAccountInfoProfile(null);
		AccountInfoAvatar.DisplayName = (string.IsNullOrWhiteSpace(userName) ? string.Empty : userName);
		if (string.IsNullOrWhiteSpace(token))
		{
			_accountInfoPanelSteamId = null;
			AccountInfoSteamIdText.Text = Loc.T("Login_Value_NotResolved");
			AccountInfoExpiresText.Text = Loc.T("Login_Value_NotResolved");
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
			ResetAvailabilityForeground();
			return;
		}
		JwtTokenInfo jwtTokenInfo = AppState.JwtTokenService.Inspect(token);
		_accountInfoPanelSteamId = (string.IsNullOrWhiteSpace(jwtTokenInfo.SteamId) ? null : jwtTokenInfo.SteamId);
		AccountInfoSteamIdText.Text = (string.IsNullOrWhiteSpace(jwtTokenInfo.SteamId) ? Loc.T("Login_Value_NotResolved") : jwtTokenInfo.SteamId);
		AccountInfoExpiresText.Text = (jwtTokenInfo.ExpiresAt.HasValue ? jwtTokenInfo.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : Loc.T("Login_Value_NotResolved"));
		AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
		ResetAvailabilityForeground();
		if (!string.IsNullOrWhiteSpace(jwtTokenInfo.SteamId))
		{
			ApplyStoredAccountInfoProfile(jwtTokenInfo.SteamId);
		}
	}

	private async Task EnsureTokenAcceptedBySteamAsync(string eyaToken, string actionName, CancellationToken cancellationToken = default(CancellationToken))
	{
		AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
		ResetAvailabilityForeground();
		SteamTokenOnlineValidationResult steamTokenOnlineValidationResult;
		try
		{
			steamTokenOnlineValidationResult = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			AppLog.Warn("在线验证令牌时无法连接 Steam，已跳过在线验证继续" + Loc.T(actionName) + "：" + ex2.Message);
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerifiedOffline");
			ResetAvailabilityForeground();
			ShowStatus(Loc.Tf("Login_Status_SkippedOnlineValidation_Format", Loc.T(actionName)), InfoBarSeverity.Warning);
			return;
		}
		AccountInfoAvailabilityText.Text = (steamTokenOnlineValidationResult.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid"));
		AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(steamTokenOnlineValidationResult.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
		if (steamTokenOnlineValidationResult.IsValid)
		{
			return;
		}
		throw new InvalidOperationException(Loc.Tf("Login_Error_StatusCannotAction_Format", steamTokenOnlineValidationResult.Status, Loc.T(actionName)));
	}

	private async Task<SteamTokenOnlineValidationResult> ValidateTokenOnlineAsync(string eyaToken, CancellationToken cancellationToken = default(CancellationToken))
	{
		AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
		ResetAvailabilityForeground();
		try
		{
			SteamTokenOnlineValidationResult steamTokenOnlineValidationResult = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
			AccountInfoAvailabilityText.Text = (steamTokenOnlineValidationResult.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid"));
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(steamTokenOnlineValidationResult.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
			return steamTokenOnlineValidationResult;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			SteamTokenOnlineValidationResult result = new SteamTokenOnlineValidationResult(IsValid: false, Loc.Tf("Login_Status_OnlineValidationFailed_Format", ex2.Message));
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
			AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
			return result;
		}
	}
    // ---------- 账号核验（卡密） ----------
    // 复刻 https://xn--rpr1ku6kjs6f.xyz/ 的核心流程：输入卡密 → 上游建立 Steam 会话并抓取账号状态
    // → 展示封禁、CS2 竞技记录与游戏库。阶段轮播与站点同为 3.2s/步；游戏库默认折叠，
    // 展开后由列表自身滚动，页面整体高度仍适配默认窗口（1380x810）。

    private const int VerifyStageCount = 3;

    /// <summary>阶段文案键：与站点「正在校验卡密 / 正在建立 Steam 会话 / 正在读取账号、CS2 与游戏库」一一对应。</summary>
    private static string VerifyStageKey(int index) => $"Login_Verify_Stage_{index + 1}";

    private void InitializeVerifyModule()
    {
        VerifyLibraryRecentTabItem.IsSelected = true;
        VerifyLibraryTabSelector.SelectedItem = VerifyLibraryRecentTabItem;

        _verifyStageTimer = _dispatcherQueue.CreateTimer();
        // 每秒刷新一次：阶段按站点的 3.2 秒一步推进，同时刷新已等待秒数。
        _verifyStageTimer.Interval = TimeSpan.FromSeconds(1);
        _verifyStageTimer.Tick += VerifyStageTimer_Tick;

        // 切到核验模式（面板显示）时拉一次上游公告；只影响核验模块自己的提示条。
        VerifyPanel.RegisterPropertyChangedCallback(
            UIElement.VisibilityProperty,
            (_, _) =>
            {
                if (VerifyPanel.Visibility == Visibility.Visible)
                {
                    _ = RefreshVerifyServiceInfoAsync();
                }
            });

        UpdateVerifyTexts();
    }

    // ---------- 账号核验：上游服务公告（9095 /api/v1/health） ----------

    /// <summary>最近一次拉到的上游公告；null = 还没拉到，空串 = 服务在线但没有公告。</summary>
    private string? _verifyServiceAnnouncement;

    private bool _verifyServiceOnline;

    /// <summary>拉取上游服务公告并显示在核验面板顶部（失败只影响这条提示，不影响验号）。</summary>
    private async Task RefreshVerifyServiceInfoAsync()
    {
        try
        {
            var health = await VerifyRedeemClient.CheckHealthAsync();
            _verifyServiceOnline = health?.Ok == true;
            _verifyServiceAnnouncement = health?.Announcement?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _verifyServiceOnline = false;
            _verifyServiceAnnouncement = null;
            AppLog.Warn($"核验：读取上游服务公告失败：{ex.Message}");
        }

        UpdateVerifyServiceBar();
    }

    /// <summary>按当前语言和最近一次结果刷新公告条。</summary>
    private void UpdateVerifyServiceBar()
    {
        if (_verifyServiceAnnouncement is null && !_verifyServiceOnline)
        {
            VerifyServiceBar.Severity = InfoBarSeverity.Warning;
            VerifyServiceBar.Message = Loc.T("Login_Verify_Service_Offline");
            VerifyServiceBar.IsOpen = true;
            return;
        }

        // 在线时显示固定公告（服务端原文仅用于判断是否连上，不再直接展示）。
        VerifyServiceBar.Severity = InfoBarSeverity.Informational;
        VerifyServiceBar.Message = Loc.T("Login_Verify_Service_Announcement");
        VerifyServiceBar.IsOpen = true;
    }

    private void VerifyStageTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _verifyElapsedSeconds++;
        _verifyStageIndex = Math.Min((int)(_verifyElapsedSeconds / 3.2), VerifyStageCount - 1);
        UpdateVerifyProgressText();
    }

    /// <summary>
    /// 进度行 = 当前阶段 + 已等待秒数。上游冷查询实测约 35 秒，只轮播三段文案容易让人以为卡死；
    /// 秒数持续跳动可以区分「还在跑」和「真的断了」。
    /// </summary>
    private void UpdateVerifyProgressText()
    {
        VerifyProgressText.Text = Loc.Tf(
            "Login_Verify_Progress_Format",
            Loc.T(VerifyStageKey(_verifyStageIndex)),
            _verifyElapsedSeconds);
    }

    /// <summary>刷新核验模块的命令式文案（按钮、排序项、阶段）；语言切换与忙碌状态变化时调用。</summary>
    private void UpdateVerifyTexts()
    {
        VerifyQueryButtonText.Text = Loc.T(_verifyQueryRunning ? "Login_Verify_Btn_Querying" : "Login_Verify_Btn_Query");

        // 公告条的前缀文案跟随语言（有拉到过结果才重刷，避免语言切换触发一次网络请求）。
        if (_verifyServiceAnnouncement is not null || _verifyServiceOnline)
        {
            UpdateVerifyServiceBar();
        }
        UpdateVerifyLibrarySortText();

        if (_verifyQueryRunning)
        {
            UpdateVerifyProgressText();
        }

        if (_verifyPayload is not null)
        {
            ApplyVerifyReport(_verifyPayload);
        }
    }

    /// <summary>排序按钮文案与菜单项：站点按当前标签页把「时长」显示成近两周/总时长。</summary>
    private void UpdateVerifyLibrarySortText()
    {
        BuildVerifySortFlyout();
        VerifyLibrarySortText.Text = Loc.T(VerifySortKey(_verifyLibrarySort));
    }

    private string VerifySortKey(string sort) => sort switch
    {
        "name" => "Login_Verify_Library_Sort_Name",
        "lastPlayed" => "Login_Verify_Library_Sort_LastPlayed",
        _ => _verifyLibraryRecentTab ? "Login_Verify_Library_Sort_Recent" : "Login_Verify_Library_Sort_Total"
    };

    private void BuildVerifySortFlyout()
    {
        VerifyLibrarySortFlyout.Items.Clear();
        foreach (var code in new[] { "playtime", "lastPlayed", "name" })
        {
            var item = new MenuFlyoutItem
            {
                Text = Loc.T(VerifySortKey(code)),
                Tag = code
            };
            item.Click += VerifyLibrarySortMenuItem_Click;
            VerifyLibrarySortFlyout.Items.Add(item);
        }
    }

    private void VerifyLibrarySortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string code })
        {
            return;
        }

        _verifyLibrarySort = code;
        UpdateVerifyLibrarySortText();
        RefreshVerifyLibraryList();
    }

    private void VerifyLibraryTabSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _verifyLibraryRecentTab = VerifyLibraryTabSelector.SelectedItem == VerifyLibraryRecentTabItem;
        UpdateVerifyLibrarySortText();
        RefreshVerifyLibraryList();
    }

    private void VerifyLibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshVerifyLibraryList();
    }

    private void ClearVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyKeyBox.Text = string.Empty;
        ResetVerifyResult();
    }

    private async void VerifyQueryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunVerifyQueryAsync();
    }

    private async void VerifyKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !AppState.IsBusy)
        {
            e.Handled = true;
            await RunVerifyQueryAsync();
        }
    }

    /// <summary>清空上一次核验结果，回到「只显示输入框」的状态。</summary>
    private void ResetVerifyResult()
    {
        _verifyPayload = null;
        _verifyLibraryGames.Clear();
        VerifyErrorBar.IsOpen = false;
        VerifyResultPanel.Visibility = Visibility.Collapsed;
        VerifyLibraryExpander.Visibility = Visibility.Collapsed;
        VerifyLibraryErrorBar.IsOpen = false;
        VerifyRecordList.ItemsSource = null;
        VerifyLibraryList.ItemsSource = null;
    }

    private async Task RunVerifyQueryAsync()
    {
        var key = VerifyKeyBox.Text.Trim();
        if (key.Length == 0)
        {
            ShowStatus(Loc.T("Login_Error_LicenseKeyRequired"), InfoBarSeverity.Warning);
            return;
        }

        ResetVerifyResult();
        _verifyQueryRunning = true;
        _verifyStageIndex = 0;
        _verifyElapsedSeconds = 0;
        UpdateVerifyTexts();
        UpdateVerifyProgressText();
        VerifyProgressPanel.Visibility = Visibility.Visible;
        _verifyStageTimer?.Start();

        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Verify_Status_Querying"), InfoBarSeverity.Informational);
        try
        {
            var payload = await AppState.VerifyService.VerifyAsync(key, cancellationToken);
            var report = ApplyVerifyReport(payload);
            _verifyPayload = payload;
            VerifyResultPanel.Visibility = Visibility.Visible;

            var steamId = string.IsNullOrWhiteSpace(payload.SteamId)
                ? Loc.T("Login_Verify_Value_Unknown")
                : payload.SteamId;
            ShowStatus(
                Loc.Tf(report.IsOk ? "Login_Verify_Status_Done_Ok_Format" : "Login_Verify_Status_Done_Attention_Format", steamId),
                report.IsOk ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShowStatus(Loc.T("Login_Verify_Status_Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            VerifyErrorBar.Message = ex.Message;
            VerifyErrorBar.IsOpen = true;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _verifyQueryRunning = false;
            _verifyStageTimer?.Stop();
            VerifyProgressPanel.Visibility = Visibility.Collapsed;
            UpdateVerifyTexts();
            AppState.EndBusyOperation();
        }
    }

    /// <summary>把上游快照铺到界面上；返回值供调用方按状态选提示条严重度。</summary>
    private SteamVerifyReport ApplyVerifyReport(SteamVerifyPayload payload)
    {
        var report = SteamVerifyPresenter.Build(payload, ResolveVerifyToneBrush);

        VerifyHeadIcon.Glyph = report.IsOk ? "\uE73E" : "\uE7BA";
        VerifyHeadIcon.Foreground = FormatHelper.GetStatusBrush(
            report.IsOk ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        VerifyHeadlineText.Text = report.Headline;
        VerifySublineText.Text = report.Subline;
        VerifyCachedBadge.Visibility = report.IsCached ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(report.PresenceText))
        {
            VerifyPresencePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            VerifyPresenceText.Text = report.PresenceText;
            VerifyPresenceIcon.Glyph = report.PresenceGlyph;
            VerifyPresenceIcon.Foreground = report.PresenceUnknown
                ? FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
                : report.PresenceInGame
                    ? FormatHelper.GetWarningBrush()
                    : FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
            VerifyPresencePanel.Visibility = Visibility.Visible;
        }

        // 四列两行：按行优先切分，视觉顺序与站点 stat-grid 一致。
        VerifyStatsColumn0.ItemsSource = PickVerifyStats(report.Stats, 0);
        VerifyStatsColumn1.ItemsSource = PickVerifyStats(report.Stats, 1);
        VerifyStatsColumn2.ItemsSource = PickVerifyStats(report.Stats, 2);
        VerifyStatsColumn3.ItemsSource = PickVerifyStats(report.Stats, 3);

        VerifyCooldownBar.Message = report.CooldownMessage ?? string.Empty;
        VerifyCooldownBar.IsOpen = report.CooldownMessage is not null;

        VerifyRecordList.ItemsSource = report.Records;
        VerifyRecordsPanel.Visibility = report.Records.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyVerifyLibrary(report);
        return report;
    }

    /// <summary>
    /// 取该列的两格状态。刻意返回 List 而不是数组：WinUI 的 ItemsControl.ItemsSource 不接受 CLR 数组，
    /// 赋数组会抛 ArgumentException「Value does not fall within the expected range」（记录表/游戏库用 List 才没问题）。
    /// </summary>
    private static List<SteamVerifyStat> PickVerifyStats(IReadOnlyList<SteamVerifyStat> stats, int column)
    {
        var picked = new List<SteamVerifyStat>(2);
        foreach (var index in new[] { column, column + 4 })
        {
            if (index < stats.Count)
            {
                picked.Add(stats[index]);
            }
        }

        return picked;
    }

    private void ApplyVerifyLibrary(SteamVerifyReport report)
    {
        _verifyLibraryGames.Clear();
        _verifyLibraryGames.AddRange(report.LibraryGames);

        if (!string.IsNullOrEmpty(report.LibraryError))
        {
            VerifyLibraryErrorBar.Message = Loc.Tf("Login_Verify_Library_Error_Format", report.LibraryError);
            VerifyLibraryErrorBar.IsOpen = true;
            VerifyLibraryExpander.Visibility = Visibility.Collapsed;
            return;
        }

        VerifyLibraryErrorBar.IsOpen = false;
        var summary = report.LibrarySummary;
        if (summary is null)
        {
            VerifyLibraryExpander.Visibility = Visibility.Collapsed;
            return;
        }

        VerifyLibraryExpander.Visibility = Visibility.Visible;
        VerifyLibraryHeaderText.Text = Loc.Tf("Login_Verify_Library_Header_Format", _verifyLibraryGames.Count);
        VerifyLibraryFetchedText.Text = report.LibraryFetchedText;

        VerifyLibraryTotalLabel.Text = summary.TotalLabel;
        VerifyLibraryTotalValue.Text = summary.TotalValue;
        VerifyLibraryTotalNote.Text = summary.TotalNote;
        VerifyLibraryTimeLabel.Text = summary.TimeLabel;
        VerifyLibraryTimeValue.Text = summary.TimeValue;
        VerifyLibraryTimeNote.Text = summary.TimeNote;
        VerifyLibraryRecentLabel.Text = summary.RecentLabel;
        VerifyLibraryRecentValue.Text = summary.RecentValue;
        VerifyLibraryRecentNote.Text = summary.RecentNote;
        VerifyLibraryLongestLabel.Text = summary.LongestLabel;
        VerifyLibraryLongestValue.Text = summary.LongestValue;
        VerifyLibraryLongestNote.Text = summary.LongestNote;

        RefreshVerifyLibraryList();
    }

    /// <summary>按当前标签页/搜索/排序重建游戏列表；列表自身滚动，不影响页面高度。</summary>
    private void RefreshVerifyLibraryList()
    {
        var rows = SteamVerifyPresenter.BuildGameRows(
            _verifyLibraryGames,
            _verifyLibraryRecentTab,
            VerifyLibrarySearchBox.Text,
            _verifyLibrarySort);

        VerifyLibraryList.ItemsSource = rows;
        VerifyLibraryCountText.Text = Loc.Tf("Login_Verify_Library_Count_Format", rows.Count);

        if (rows.Count > 0)
        {
            VerifyLibraryEmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        VerifyLibraryEmptyText.Text = _verifyLibraryRecentTab
            ? Loc.T("Login_Verify_Library_Empty_Recent")
            : VerifyLibrarySearchBox.Text.Trim().Length > 0
                ? Loc.T("Login_Verify_Library_Empty_Search")
                : Loc.T("Login_Verify_Library_Empty_All");
        VerifyLibraryEmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>状态色调 → 主题画刷；未知档取正文色，保证值文本始终可见。</summary>
    private static Brush ResolveVerifyToneBrush(SteamVerifyTone tone) => tone switch
    {
        SteamVerifyTone.Ok => FormatHelper.GetStatusBrush(InfoBarSeverity.Success),
        SteamVerifyTone.Bad => FormatHelper.GetStatusBrush(InfoBarSeverity.Error),
        SteamVerifyTone.Warn => FormatHelper.GetWarningBrush(),
        _ => FormatHelper.GetPrimaryTextBrush()
    };
    /// <summary>
    /// 窗口尺寸变化时让内部列表跟着伸缩：宽度由星号列自适应（页面不设 MaxWidth，避免被居中漂移）；
    /// 高度上把多出来的空间给竞技记录与游戏库列表，调矮时收紧，避免「列表固定高、下方一片留白」。
    /// </summary>
    // ---------- 核验模块内的「卡密一键登录」 ----------

    /// <summary>新版取名接口客户端：只服务核验模块的一键登录，不参与既有卡密解析链路。</summary>
    private static readonly NaiweiRedeemClient VerifyRedeemClient = new();

    /// <summary>
    /// 卡密一键登录：用核验框里的同一张卡密取名（新版 SSE 接口 /api/v1/redeem），成功后切到
    /// Token登录 面板回填 SteamID/Token，再调用既有的登录按钮处理函数完成上号
    /// ——上号走的就是 Token登录 的 SteamID + JWT 逻辑（LoginButton_Click → LoginLegacyEyaAsync）。
    /// 不新增任何上号逻辑，也不清理 Steam 账号缓存。
    /// </summary>
    private async void VerifyRedeemLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy)
        {
            return;
        }

        if (!await EnsureVerifyCredentialsAsync())
        {
            return;
        }

        // 切到 Token登录 面板（SteamID + JWT）并交给既有登录流程。
        ApplyTokenLoginSelection();

        // 上号按「设置页配置的 Steam 路径」走：SteamPathCoordinator 配置有效时直接复用，
        // 这里只把将要使用的路径透明提示出来，不改动既有路径解析逻辑。
        string? configuredSteamPath = SteamPathCoordinator.GetPersistedInstallPath();
        ShowStatus(
            configuredSteamPath is null
                ? Loc.T("Settings_SteamPath_NotSet")
                : Loc.Tf("Login_Verify_SteamPath_Format", configuredSteamPath),
            configuredSteamPath is null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);

        LoginButton_Click(LoginButton, new RoutedEventArgs());
    }

    /// <summary>核验模块里的「登录前可选操作」：与 Token登录 面板那三个按钮同一套。</summary>
    private enum VerifyPreLoginAction
    {
        ClearWorkshop,
        ApplyLoadout,
        Personalize
    }

    private async void VerifyClearWorkshopButton_Click(object sender, RoutedEventArgs e) =>
        await RunVerifyPreLoginActionAsync(VerifyPreLoginAction.ClearWorkshop);

    private async void VerifyApplyLoadoutButton_Click(object sender, RoutedEventArgs e) =>
        await RunVerifyPreLoginActionAsync(VerifyPreLoginAction.ApplyLoadout);

    private async void VerifyPersonalizeButton_Click(object sender, RoutedEventArgs e) =>
        await RunVerifyPreLoginActionAsync(VerifyPreLoginAction.Personalize);

    /// <summary>
    /// 登录前可选操作：必要时先用卡密取名拿到凭据，再切到 Token登录 面板，然后调用该面板里
    /// 既有按钮的同一个处理函数——不复制也不改写既有逻辑，走的仍是 SteamID + JWT 的 Token 链路。
    /// </summary>
    private async Task RunVerifyPreLoginActionAsync(VerifyPreLoginAction action)
    {
        if (AppState.IsBusy)
        {
            return;
        }

        bool hasCredentials = !string.IsNullOrWhiteSpace(TokenSteamIdBox.Text) && !string.IsNullOrWhiteSpace(TokenBox.Text);
        if (!hasCredentials && !await EnsureVerifyCredentialsAsync())
        {
            return;
        }

        ApplyTokenLoginSelection();
        switch (action)
        {
            case VerifyPreLoginAction.ClearWorkshop:
                ClearWorkshopButton_Click(ClearWorkshopButton, new RoutedEventArgs());
                break;
            case VerifyPreLoginAction.ApplyLoadout:
                ApplyLoadoutButton_Click(ApplyLoadoutButton, new RoutedEventArgs());
                break;
            case VerifyPreLoginAction.Personalize:
                PersonalizeButton_Click(PersonalizeButton, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>用核验框里的卡密取名并回填 Token登录 面板字段（SteamID + JWT）；成功返回 true。</summary>
    private async Task<bool> EnsureVerifyCredentialsAsync()
    {
        string licenseKey = VerifyKeyBox.Text.Trim();
        if (licenseKey.Length == 0)
        {
            ShowStatus(Loc.T("Login_Error_LicenseKeyRequired"), InfoBarSeverity.Warning);
            return false;
        }

        VerifyErrorBar.IsOpen = false;
        VerifyProgressPanel.Visibility = Visibility.Visible;
        VerifyProgressText.Text = Loc.T(VerifyStageKey(0));

        CancellationToken cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ResolvingLicense"), InfoBarSeverity.Informational);
        try
        {
            NaiweiRedeemClient.RedeemAccount account = await VerifyRedeemClient.RedeemAsync(licenseKey, CreateVerifyRedeemProgressReporter(), cancellationToken);
            TokenLicenseKeyBox.Text = licenseKey;
            TokenSteamIdBox.Text = account.SteamId;
            TokenBox.Text = account.Token;
            UpdateAccountInfo(account.SteamId, account.Token);
            ShowStatus(Loc.Tf("Login_Status_LicenseResolved_Format", account.SteamId, account.SteamId), InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShowStatus(Loc.T("Login_Status_LicenseResolveCancelled"), InfoBarSeverity.Informational);
            return false;
        }
        catch (Exception ex2)
        {
            VerifyErrorBar.Message = ex2.Message;
            VerifyErrorBar.IsOpen = true;
            ShowStatus(ex2.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            VerifyProgressPanel.Visibility = Visibility.Collapsed;
            AppState.EndBusyOperation();
        }
    }

    /// <summary>取名进度：把服务端 SSE 文案写进核验模块的进度行，并在状态栏同步一条。</summary>
    private IProgress<NaiweiRedeemProgress> CreateVerifyRedeemProgressReporter() =>
        new Progress<NaiweiRedeemProgress>(progress =>
        {
            int percent = Math.Clamp(progress.Percent, 0, 100);
            string text = string.IsNullOrWhiteSpace(progress.Message)
                ? Loc.Tf("Login_Redeem_Status_Percent_Format", percent)
                : $"{progress.Message} · {percent}%";
            VerifyProgressText.Text = text;
            ShowStatus(text, InfoBarSeverity.Informational);
        });
    private void LoginPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;
        VerifyRecordList.Height = Math.Clamp(height * 0.16, 110, 300);
        VerifyLibraryList.Height = Math.Clamp(height * 0.44, 180, 560);
    }
}
