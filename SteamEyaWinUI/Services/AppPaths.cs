using System.IO;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用数据目录解析。默认位于当前用户的漫游目录下 SteamEYA，
/// 用户仍可按注册表中保存的自定义路径迁移并定位数据。
/// </summary>
internal static class AppPaths
{
    private const string AppFolderName = "SteamEYA";
    private const string RegistryKeyPath = @"Software\SteamEYA";
    private const string DataRootValueName = "DataRoot";

    private static readonly object Gate = new();
    private static string _dataRoot;

    static AppPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DefaultDataRoot = Path.Combine(appData, AppFolderName);
        _dataRoot = LoadDataRoot() ?? DefaultDataRoot;
    }

    /// <summary>首次启动及未配置自定义位置时使用的默认数据目录。</summary>
    public static string DefaultDataRoot { get; }

    /// <summary>当前数据根目录。</summary>
    public static string DataRoot
    {
        get
        {
            lock (Gate)
            {
                return _dataRoot;
            }
        }
    }

    /// <summary>持久化并立即切换数据根目录。</summary>
    public static void SetDataRoot(string path)
    {
        var normalized = NormalizePath(path);
        lock (Gate)
        {
            PersistDataRoot(normalized);
            _dataRoot = normalized;
        }
    }

    /// <summary>将用户选择的路径规整为完整、无尾随分隔符的绝对路径。</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Data directory path cannot be empty.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    private static string? LoadDataRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            var value = key?.GetValue(DataRootValueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : NormalizePath(value);
        }
        catch
        {
            // 注册表不可用时回退默认目录，用户可以再次移动。
            return null;
        }
    }

    private static void PersistDataRoot(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Data location registry key is unavailable.");
        key.SetValue(DataRootValueName, path, RegistryValueKind.String);
    }
}
