using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Clash「测试全部延迟」的唯一实现。启动静默检查和页面按钮都调用这里，
/// 确保连接、代理组选择、并发测试与结果缓存完全一致。
/// </summary>
internal static class ClashProxyWorkflowService
{
    public static async Task<ClashWarmupCacheEntry> ConnectAndTestAllAsync(
        string apiUrl,
        string apiSecret,
        string? preferredGroupName,
        CancellationToken cancellationToken)
    {
        using var proxyService = new ClashProxyService();
        var snapshot = await proxyService.GetSnapshotAsync(
            apiUrl,
            apiSecret,
            cancellationToken);
        var group =
            snapshot.Groups.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                preferredGroupName,
                StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Groups.FirstOrDefault()
            ?? throw new InvalidOperationException(Loc.T("Clash_Status_NoGroups"));
        if (!string.Equals(preferredGroupName, group.Name, StringComparison.Ordinal))
        {
            var settings = AppState.SettingsService.Load();
            settings.ClashProxyGroup = group.Name;
            AppState.SettingsService.Save(settings);
        }

        var delays = await proxyService.TestAllDelaysAsync(
            apiUrl,
            apiSecret,
            group.Nodes,
            maxConcurrency: 8,
            cancellationToken);
        var entry = new ClashWarmupCacheEntry(
            apiUrl.Trim(),
            apiSecret,
            snapshot,
            group.Name,
            delays,
            DateTimeOffset.UtcNow);
        ClashWarmupCacheService.Store(entry);
        return entry;
    }
}
