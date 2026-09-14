namespace SteamEyaWinUI.Services;

/// <summary>
/// 本地凭据库「第 4 层：可选口令」的总开关。
///
///   · 两个凭据库（历史账号 accounts.json、白号 white-accounts.json）共用同一个口令；
///     每个文件各自保存自己的盐与 KEK，因此输入一次即可解锁全部。
///   · 默认关闭：没有设置口令时行为与之前完全一致（程序自动解密，不弹任何输入框）。
///   · 口令只保存在内存里、不落盘，忘记后无法找回，只能删除凭据库重新导入账号。
///   · 由于第 2 层绑定「当前 Windows 用户 + 本机」，口令也只在同一台机器、同一个 Windows 用户下有效。
/// </summary>
internal static class CredentialVault
{
    private static AccountHistoryService[] Vaults => [AppState.AccountHistoryService, AppState.WhiteAccountService];

    /// <summary>任一凭据库启用了口令层。</summary>
    public static bool IsPassphraseEnabled => Vaults.Any(vault => vault.IsVaultPassphraseEnabled);

    /// <summary>启用了口令层、但还有凭据库没解锁。</summary>
    public static bool IsLocked => Vaults.Any(vault => vault.IsVaultPassphraseLocked);

    /// <summary>用口令解锁所有已启用口令层的凭据库；全部成功才返回 true。</summary>
    public static bool TryUnlock(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return false;
        }

        foreach (var vault in Vaults)
        {
            if (vault.IsVaultPassphraseLocked && !vault.TryUnlockVault(passphrase))
            {
                return false;
            }
        }

        return !IsLocked;
    }

    /// <summary>校验口令是否正确（不改状态）；只检查启用了口令层的凭据库。</summary>
    public static bool Verify(string passphrase) =>
        !string.IsNullOrEmpty(passphrase) &&
        Vaults.Where(vault => vault.IsVaultPassphraseEnabled).All(vault => vault.VerifyVaultPassphrase(passphrase));

    /// <summary>设置/更换口令（两个凭据库同时生效）。中途失败会尽量回滚，避免只启用一半。</summary>
    public static void SetPassphrase(string passphrase)
    {
        var applied = new List<AccountHistoryService>();
        try
        {
            foreach (var vault in Vaults)
            {
                vault.SetVaultPassphrase(passphrase);
                applied.Add(vault);
            }
        }
        catch
        {
            foreach (var vault in applied)
            {
                try
                {
                    vault.ClearVaultPassphrase();
                }
                catch (Exception rollbackError)
                {
                    AppLog.Warn($"口令层回滚失败：{rollbackError.Message}");
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 取消口令（两个凭据库同时生效），恢复为程序自动解密。
    /// 先校验口令并解锁：即使本次会话还没解过锁，只要口令正确也能直接取消。
    /// </summary>
    public static bool ClearPassphrase(string passphrase)
    {
        if (!Verify(passphrase) || !TryUnlock(passphrase))
        {
            return false;
        }

        foreach (var vault in Vaults)
        {
            vault.ClearVaultPassphrase();
        }

        return true;
    }
}
