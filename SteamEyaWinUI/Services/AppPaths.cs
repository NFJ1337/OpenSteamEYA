using System.IO;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用数据目录解析。默认位于当前用户的漫游目录下 OpenSteamEYANFJ，
/// 用户仍可按注册表中保存的自定义路径迁移并定位数据。
///
/// 与旧版 EYA（hvh-software/OpenSteamEYA）刻意分开：旧版也用 %APPDATA%\SteamEYA 与同名文件，
/// 但它不做加密、格式也不同 —— 两者共用一个目录时，旧版一旦保存就会把加密的账号文件写坏。
/// 因此换成自己的目录名与注册表键（统一后缀 NFJ），并做兼容读取：
///   · 老键里记过的自定义路径 → 直接沿用，不做任何搬迁；
///   · 只有旧目录、但里面确有数据（含上游 EYA 的明文数据）→ 复制一份到我们自己的目录再用，
///     原目录原样留给旧版；复制失败则退回原地沿用，绝不让数据「看不见」。
/// </summary>
internal static class AppPaths
{
    private const string AppFolderName = "OpenSteamEYANFJ";
    private const string RegistryKeyPath = @"Software\OpenSteamEYANFJ";
    private const string DataRootValueName = "DataRoot";

    /// <summary>
    /// 历史位置的注册表键（只读兼容，不再写入）：
    /// OpenSteamEYA 是改名过程中的中间名，SteamEYA 是本项目 1.6.4 及更早、以及上游 EYA 用的键。
    /// </summary>
    private static readonly string[] LegacyRegistryKeyPaths =
        [@"Software\OpenSteamEYA", @"Software\SteamEYA"];

    /// <summary>历史目录名，与上面的键一一对应。</summary>
    private static readonly string[] LegacyAppFolderNames = ["OpenSteamEYA", "SteamEYA"];

    /// <summary>导入旧目录时带过来的文件（只搬用户数据，不搬日志与各类缓存）。</summary>
    private static readonly string[] LegacyImportFiles =
        ["settings.json", "white-accounts.json", "white-accounts.json.bak", "cached-login.json"];

    /// <summary>导入旧目录时带过来的子目录。</summary>
    private static readonly string[] LegacyImportDirectories =
        ["history", "avatars", "white-avatars", "cached-avatars", "personalization"];

    private static readonly object Gate = new();
    private static string _dataRoot;

    static AppPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DefaultDataRoot = Path.Combine(appData, AppFolderName);

        var stored = LoadDataRoot();
        if (stored is not null)
        {
            _dataRoot = stored;
            return;
        }

        // 既没有新键也没有旧键时：先看哪个旧目录里真的有数据（按新→旧的顺序找）。
        if (!Directory.Exists(DefaultDataRoot))
        {
            foreach (var legacyName in LegacyAppFolderNames)
            {
                var legacyFolder = Path.Combine(appData, legacyName);
                if (!HasLegacyData(legacyFolder))
                {
                    continue;
                }

                if (TryImportLegacyData(legacyFolder, DefaultDataRoot))
                {
                    ImportedFromLegacy = legacyFolder;
                    _dataRoot = DefaultDataRoot;
                }
                else
                {
                    // 复制失败（磁盘满 / 权限）就继续用旧目录，绝不能让用户看不到自己的数据。
                    _dataRoot = legacyFolder;
                }

                TryPersistDataRoot(_dataRoot);
                return;
            }
        }

        _dataRoot = DefaultDataRoot;
    }

    /// <summary>
    /// 首次启动时若从旧目录导入过数据，这里记下来源路径（AppPaths 自身不能写日志 ——
    /// AppLog 依赖 DataRoot，会递归），由启动流程读一次写进日志。
    /// </summary>
    public static string? ImportedFromLegacy { get; private set; }

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
        var stored = ReadStoredDataRoot(RegistryKeyPath);
        if (stored is not null)
        {
            return stored;
        }

        // 兼容：老用户的位置记在旧键里 —— 读出来直接采用，并写进新键，之后只认新键。
        foreach (var legacyKeyPath in LegacyRegistryKeyPaths)
        {
            var legacy = ReadStoredDataRoot(legacyKeyPath);
            if (legacy is not null)
            {
                TryPersistDataRoot(legacy);
                return legacy;
            }
        }

        return null;
    }

    private static string? ReadStoredDataRoot(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var value = key?.GetValue(DataRootValueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : NormalizePath(value);
        }
        catch
        {
            // 注册表不可用时回退默认目录，用户可以再次移动。
            return null;
        }
    }

    /// <summary>
    /// 旧目录里是否真的有本项目的数据。只看 settings.json 不够 —— 一次「空目录启动」也会留下它；
    /// 必须有账号/登录缓存这类真实数据，才值得把它当成老用户的数据目录。
    /// </summary>
    private static bool HasLegacyData(string folder)
    {
        try
        {
            return Directory.Exists(folder) &&
                (File.Exists(Path.Combine(folder, "white-accounts.json")) ||
                 File.Exists(Path.Combine(folder, "history", "accounts.json")) ||
                 File.Exists(Path.Combine(folder, "cached-login.json")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把旧数据目录里真正属于用户的数据复制到我们的目录。先落到 staging 再整体改名：
    /// 中途失败不会留下半成品目录，也绝不会动到源目录。
    /// </summary>
    private static bool TryImportLegacyData(string source, string target)
    {
        var staging = target + ".importing";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);

            foreach (var file in LegacyImportFiles)
            {
                var from = Path.Combine(source, file);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(staging, file), overwrite: true);
                }
            }

            foreach (var directory in LegacyImportDirectories)
            {
                var from = Path.Combine(source, directory);
                if (Directory.Exists(from))
                {
                    CopyDirectory(from, Path.Combine(staging, directory));
                }
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            return true;
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch
            {
                // 清理失败也无妨：下次启动会重新覆盖 staging。
            }

            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void TryPersistDataRoot(string path)
    {
        try
        {
            PersistDataRoot(path);
        }
        catch
        {
            // 写不进去也能继续用，下次启动再试。
        }
    }

    private static void PersistDataRoot(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Data location registry key is unavailable.");
        key.SetValue(DataRootValueName, path, RegistryValueKind.String);
    }
}
