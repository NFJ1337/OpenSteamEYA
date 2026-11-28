namespace SteamEyaWinUI.Services;

/// <summary>
/// 账号查询的 Clash 节点自动轮换。设置页与账号查询共用同一份配置；
/// 每完成指定次数查询后，从当前代理组选择下一个可用节点。
/// </summary>
internal static class ClashAutoRotateService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _completedQueries;
    private static string? _lastGroupName;

    public static async Task NotifyAccountQueryCompletedAsync()
    {
        var settings = AppState.SettingsService.Load();
        if (!settings.ClashAutoRotateEnabled ||
            string.IsNullOrWhiteSpace(settings.ClashProxyGroup))
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (!string.Equals(
                    _lastGroupName,
                    settings.ClashProxyGroup,
                    StringComparison.OrdinalIgnoreCase))
            {
                _lastGroupName = settings.ClashProxyGroup;
                _completedQueries = 0;
            }

            var rotationCount = Math.Max(1, settings.ClashRotationCount);
            _completedQueries++;
            if (_completedQueries < rotationCount)
            {
                return;
            }

            _completedQueries = 0;
            if (string.IsNullOrWhiteSpace(settings.ClashApiUrl))
            {
                AppLog.Warn("Clash 自动轮换已启用，但尚未配置 API 地址。");
                return;
            }

            using var service = new ClashProxyService();
            var snapshot = await service.GetSnapshotAsync(
                settings.ClashApiUrl,
                settings.ClashApiSecret ?? string.Empty,
                CancellationToken.None);
            var group = snapshot.Groups.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                settings.ClashProxyGroup,
                StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                AppLog.Warn($"Clash 自动轮换找不到代理组：{settings.ClashProxyGroup}");
                return;
            }

            var nodes = group.Nodes
                .Where(name =>
                {
                    var type = snapshot.NodeTypes.TryGetValue(name, out var value) ? value : string.Empty;
                    return !string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "Reject", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "DIRECT", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "REJECT", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (nodes.Count <= 1)
            {
                return;
            }

            var currentIndex = nodes.FindIndex(name => string.Equals(
                name,
                group.Current,
                StringComparison.Ordinal));
            var nextNode = nodes[(currentIndex + 1) % nodes.Count];
            await service.SelectProxyAsync(
                settings.ClashApiUrl,
                settings.ClashApiSecret ?? string.Empty,
                group.Name,
                nextNode,
                CancellationToken.None);
            AppLog.Info(
                $"Clash 自动轮换：代理组「{group.Name}」已从「{group.Current}」切换到「{nextNode}」。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clash 自动轮换失败：{ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }
}
