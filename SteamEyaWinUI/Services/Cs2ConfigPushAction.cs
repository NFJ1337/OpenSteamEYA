using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「推送CS2配置」：与设置页「CS2 设置同步 → 立即推送」完全同一套业务 ——
/// 把设置里选定的来源账号的 CS2 配置（准星 / 灵敏度 / 键位…）强推到当前登录的 Steam 账号云。
///
/// 各页面上的按钮只做一件事：调 <see cref="PushAsync"/>；状态反馈统一走 AppState.ShowStatus。
/// 设置页那个按钮保留自己的既有实现，本文件不去改它（避免动到既有代码路径）。
/// </summary>
internal static class Cs2ConfigPushAction
{
    private static bool _running;

    /// <summary>执行一次推送；已在推送中时只提示不重入，避免连点排队串行推多次。</summary>
    public static async Task PushAsync()
    {
        if (_running)
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
            return;
        }

        var source = AppState.SettingsService.Load().Cs2SyncSourceSteamId;
        if (string.IsNullOrWhiteSpace(source))
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Error_NoSourceSelected"), InfoBarSeverity.Error);
            return;
        }

        _running = true;
        AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
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
            _running = false;
        }
    }
}