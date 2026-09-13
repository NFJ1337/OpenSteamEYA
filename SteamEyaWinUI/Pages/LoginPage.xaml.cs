using System.ComponentModel;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private SteamUpstreamServer? _cachedServer;
    private SteamUpstreamServer? _selectedServer;
    private SteamAccountData? _cachedLegacyAccountData;
    private string? _cachedLegacyLicenseKey;

    // 历史账号保存是否失败：SaveLoginHistoryAsync 置位，登录消息据此决定 Warning/Success 严重度
    // （本地化后历史后缀文案不再含固定中文前缀，故改用此标志替代原先的字符串前缀判断）。
    private bool _lastLoginHistorySaveFailed;
    private bool _pendingTokenLoginSelection;
    private string? _accountInfoPanelSteamId;

    public LoginPage()
    {
        InitializeComponent();

        InitializeUpstreamServers();

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Loaded += LoginPage_Loaded;

        // SelectorBarItem.IsSelected 在 XAML 解析期不可靠，显式设定初始模式。
        ModeSelector.SelectedItem = LegacyEyaModeItem;
        ApplyLanguageModeAvailability();

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

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；右侧账号信息面板的命令式文本（用户名/SteamID/过期/可用状态/分数/等级/冷却）
            // 重跑下面两个方法即可让已显示的“未填写/未解析/未验证”等占位换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            ApplyLanguageModeAvailability();
            UpdateAccountInfoFromCurrentInputs();
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
        AccountNameBox.IsEnabled = enabled;
        EyaTokenBox.IsEnabled = enabled;
        ClearManualButton.IsEnabled = enabled;
        UpstreamServerButton.IsEnabled = enabled;
        LicenseKeyBox.IsEnabled = enabled;
        ClearLicenseButton.IsEnabled = enabled;
        ResolveLicenseButton.IsEnabled = enabled;
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

        _cachedAccountData = null;
        _cachedLicenseKey = null;
        _cachedServer = null;
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
		if ((object)ManualPanel != null && (object)LegacyEyaPanel != null && (object)TokenLoginPanel != null && (object)AutoPanel != null && (object)CredsPanel != null && (object)ActionButtonGrid != null)
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
		ManualPanel.Visibility = ((isAutoMode | isLegacyEyaMode | isTokenLoginMode | isCredsMode) ? Visibility.Collapsed : Visibility.Visible);
		LegacyEyaPanel.Visibility = ((!isLegacyEyaMode) ? Visibility.Collapsed : Visibility.Visible);
		TokenLoginPanel.Visibility = ((!isTokenLoginMode) ? Visibility.Collapsed : Visibility.Visible);
		AutoPanel.Visibility = ((!isAutoMode) ? Visibility.Collapsed : Visibility.Visible);
		CredsPanel.Visibility = ((!isCredsMode) ? Visibility.Collapsed : Visibility.Visible);
		ActionButtonGrid.Visibility = (isCredsMode ? Visibility.Collapsed : Visibility.Visible);
	}

	private void ApplyLanguageModeAvailability()
	{
		AutoModeItem.Visibility = Visibility.Collapsed;
		ManualModeItem.Visibility = Visibility.Collapsed;
		CredsModeItem.Visibility = Visibility.Collapsed;
		bool flag = !string.Equals(Loc.CurrentCode, "en", StringComparison.OrdinalIgnoreCase);
		LegacyEyaModeItem.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		if ((!flag && IsLegacyEyaMode) || IsAutoMode || IsCredsMode || ModeSelector.SelectedItem == ManualModeItem)
		{
			ModeSelector.SelectedItem = (flag ? LegacyEyaModeItem : TokenLoginModeItem);
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

	private void InitializeUpstreamServers()
	{
		foreach (SteamUpstreamServer server in SteamLicenseClient.Servers)
		{
			MenuFlyoutItem menuFlyoutItem = new MenuFlyoutItem
			{
				Text = server.Name,
				Tag = server
			};
			menuFlyoutItem.Click += UpstreamServerMenuItem_Click;
			UpstreamServerFlyout.Items.Add(menuFlyoutItem);
		}
		if (SteamLicenseClient.Servers.Count > 0)
		{
			_selectedServer = SteamLicenseClient.Servers[0];
			UpstreamServerText.Text = _selectedServer.Name;
		}
	}

	private void UpstreamServerMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: SteamUpstreamServer tag } && !object.Equals(_selectedServer, tag))
		{
			_selectedServer = tag;
			UpstreamServerText.Text = tag.Name;
			_cachedAccountData = null;
			_cachedLicenseKey = null;
			ResolvedAccountBox.Text = "";
			if (IsAutoMode)
			{
				UpdateAccountInfoFromCurrentInputs();
			}
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
		SteamUpstreamServer server = GetSelectedServer();
		if ((object)_cachedAccountData != null && string.Equals(_cachedLicenseKey, licenseKey, StringComparison.Ordinal) && object.Equals(_cachedServer, server))
		{
			return _cachedAccountData;
		}
		SteamAccountData account = (_cachedAccountData = await AppState.LicenseClient.GetAccountDataAsync(licenseKey, server, cancellationToken));
		_cachedLicenseKey = licenseKey;
		_cachedServer = server;
		ResolvedAccountBox.Text = account.User + "  (" + account.SteamId + ")";
		UpdateAccountInfo(account.User, account.Token);
		await UpdateAccountProfileAsync(account.User, account.Token);
		return account;
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

	private SteamUpstreamServer GetSelectedServer()
	{
		return _selectedServer ?? throw new InvalidOperationException(Loc.T("Login_Error_UpstreamServerRequired"));
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
			throw new InvalidOperationException(ex.Message + Loc.T("Login_Error_CannotOneClickQuerySuffix"), ex);
		}
		catch
		{
			AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
			ResetAvailabilityForeground();
			throw;
		}
		historyStore.SaveCsAccountStatus(accountName, steamId, eyaToken, tokenInfo.ExpiresAt, csPremierScoreResult, jwtValidation, NullIfBlank(prefetchedProfile?.PersonaName), NullIfBlank(prefetchedProfile?.AvatarUrl), NullIfBlank(prefetchedProfile?.AvatarPath));
		if (whiteStore)
		{
			AppState.ReloadWhiteAccounts(steamId);
		}
		else
		{
			AppState.ReloadHistory(steamId);
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
}
