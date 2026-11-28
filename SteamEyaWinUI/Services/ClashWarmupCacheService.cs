namespace SteamEyaWinUI.Services;

internal sealed record ClashWarmupCacheEntry(
    string ApiUrl,
    string ApiSecret,
    ClashProxySnapshot Snapshot,
    string GroupName,
    IReadOnlyDictionary<string, int?> Delays,
    DateTimeOffset CompletedAt);

/// <summary>
/// 启动时 Clash 静默检查的结果缓存。只保存在进程内存中，不落盘、不记录密钥；
/// Clash 页面打开时可以直接恢复代理组和节点延迟，避免重复联网。
/// </summary>
internal static class ClashWarmupCacheService
{
    private static readonly object Gate = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private static ClashWarmupCacheEntry? _last;

    public static event Action<ClashWarmupCacheEntry>? Updated;

    public static void Store(ClashWarmupCacheEntry entry)
    {
        lock (Gate)
        {
            _last = entry;
        }

        Updated?.Invoke(entry);
    }

    public static bool TryGet(
        string? apiUrl,
        string? apiSecret,
        out ClashWarmupCacheEntry entry)
    {
        lock (Gate)
        {
            if (_last is { } cached &&
                DateTimeOffset.UtcNow - cached.CompletedAt <= MaxAge &&
                string.Equals(
                    cached.ApiUrl,
                    apiUrl?.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    cached.ApiSecret,
                    apiSecret ?? string.Empty,
                    StringComparison.Ordinal))
            {
                entry = cached;
                return true;
            }
        }

        entry = null!;
        return false;
    }
}
