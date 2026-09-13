using System.IO;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

internal enum DataDirectoryMoveFailure
{
    None,
    SameDirectory,
    DestinationInsideSource,
    DestinationContainsSource,
    DestinationNotEmpty
}

internal sealed record DataDirectoryMoveResult(
    bool Success,
    DataDirectoryMoveFailure Failure,
    bool CleanupFailed,
    string? Error);

/// <summary>
/// 将当前数据根目录整体迁移到用户选择的空目录。先完整复制并记录新位置，再删除旧目录，
/// 因此复制或记录位置失败时不会丢失原数据。
/// </summary>
internal static class DataDirectoryService
{
    public static async Task<DataDirectoryMoveResult> MoveAsync(string destination)
    {
        var validation = ValidateDestination(destination);
        if (validation != DataDirectoryMoveFailure.None)
        {
            return new DataDirectoryMoveResult(false, validation, false, null);
        }

        return await Task.Run(() => MoveCore(destination));
    }

    public static async Task<DataDirectoryMoveResult> MoveToDefaultAsync()
    {
        var validation = ValidateDefaultDestination();
        if (validation != DataDirectoryMoveFailure.None)
        {
            return new DataDirectoryMoveResult(false, validation, false, null);
        }

        return await Task.Run(MoveToDefaultCore);
    }
    public static DataDirectoryMoveFailure ValidateDestination(string destination)
    {
        var source = AppPaths.NormalizePath(AppPaths.DataRoot);
        var target = AppPaths.NormalizePath(destination);

        if (PathEquals(source, target))
        {
            return DataDirectoryMoveFailure.SameDirectory;
        }

        if (IsSubdirectory(target, source))
        {
            return DataDirectoryMoveFailure.DestinationInsideSource;
        }

        if (IsSubdirectory(source, target))
        {
            return DataDirectoryMoveFailure.DestinationContainsSource;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return DataDirectoryMoveFailure.DestinationNotEmpty;
        }

        return DataDirectoryMoveFailure.None;
    }

    public static DataDirectoryMoveFailure ValidateDefaultDestination()
    {
        var source = AppPaths.NormalizePath(AppPaths.DataRoot);
        var target = AppPaths.NormalizePath(AppPaths.DefaultDataRoot);

        if (PathEquals(source, target))
        {
            return DataDirectoryMoveFailure.SameDirectory;
        }

        if (IsSubdirectory(target, source))
        {
            return DataDirectoryMoveFailure.DestinationInsideSource;
        }

        return DataDirectoryMoveFailure.None;
    }
    private static DataDirectoryMoveResult MoveCore(string destination)
    {
        var source = AppPaths.NormalizePath(AppPaths.DataRoot);
        var target = AppPaths.NormalizePath(destination);
        var destinationExisted = Directory.Exists(target);

        try
        {
            CopyDirectory(source, target);
            RewriteStoredPaths(source, target);
        }
        catch (Exception ex)
        {
            TryCleanupDestination(target, destinationExisted);
            return new DataDirectoryMoveResult(false, DataDirectoryMoveFailure.None, false, ex.Message);
        }

        try
        {
            // 新位置写入注册表成功后才切换内存路径；之后所有服务立即读写新目录。
            AppPaths.SetDataRoot(target);
        }
        catch (Exception ex)
        {
            TryCleanupDestination(target, destinationExisted);
            return new DataDirectoryMoveResult(false, DataDirectoryMoveFailure.None, false, ex.Message);
        }

        try
        {
            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 新位置已经生效，旧目录残留不会影响使用；把清理警告回报给 UI。
            return new DataDirectoryMoveResult(true, DataDirectoryMoveFailure.None, true, ex.Message);
        }

        return new DataDirectoryMoveResult(true, DataDirectoryMoveFailure.None, false, null);
    }

    private static DataDirectoryMoveResult MoveToDefaultCore()
    {
        var source = AppPaths.NormalizePath(AppPaths.DataRoot);
        var target = AppPaths.NormalizePath(AppPaths.DefaultDataRoot);
        var sourceInsideTarget = IsSubdirectory(source, target);
        var copiedFiles = new List<string>();
        var createdDirectories = new List<string>();

        try
        {
            EnsureDirectory(target, createdDirectories);
            MergeDirectory(
                source,
                target,
                sourceInsideTarget ? source : null,
                copiedFiles,
                createdDirectories);
            RewriteStoredPaths(source, target);
        }
        catch (Exception ex)
        {
            TryRollbackMerge(copiedFiles, createdDirectories);
            return new DataDirectoryMoveResult(false, DataDirectoryMoveFailure.None, false, ex.Message);
        }

        try
        {
            // 默认目录本身包含程序文件，只能在数据合并成功后再切换当前数据根。
            AppPaths.SetDataRoot(target);
        }
        catch (Exception ex)
        {
            TryRollbackMerge(copiedFiles, createdDirectories);
            return new DataDirectoryMoveResult(false, DataDirectoryMoveFailure.None, false, ex.Message);
        }

        try
        {
            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
        }
        catch (Exception ex)
        {
            return new DataDirectoryMoveResult(true, DataDirectoryMoveFailure.None, true, ex.Message);
        }

        return new DataDirectoryMoveResult(true, DataDirectoryMoveFailure.None, false, null);
    }

    private static void MergeDirectory(
        string source,
        string destination,
        string? excludedSourceDirectory,
        List<string> copiedFiles,
        List<string> createdDirectories)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Cannot move reparse point: {sourceDirectory}");
            }

            if (excludedSourceDirectory is not null &&
                PathEquals(sourceDirectory, excludedSourceDirectory))
            {
                continue;
            }

            var destinationDirectory = Path.Combine(destination, Path.GetFileName(sourceDirectory));
            EnsureDirectory(destinationDirectory, createdDirectories);
            MergeDirectory(
                sourceDirectory,
                destinationDirectory,
                excludedSourceDirectory,
                copiedFiles,
                createdDirectories);
        }

        foreach (var sourceFile in Directory.EnumerateFiles(source))
        {
            var destinationFile = Path.Combine(destination, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: false);
            copiedFiles.Add(destinationFile);
        }
    }

    private static void EnsureDirectory(string path, List<string> createdDirectories)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        createdDirectories.Add(path);
    }

    private static void TryRollbackMerge(List<string> copiedFiles, List<string> createdDirectories)
    {
        foreach (var file in copiedFiles.AsEnumerable().Reverse())
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // 保留清理失败的文件，避免掩盖原始迁移错误。
            }
        }

        foreach (var directory in createdDirectories
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch
            {
                // 回滚是尽力而为，源数据未被修改。
            }
        }
    }
    private static void RewriteStoredPaths(string source, string target)
    {
        RewriteAccountHistoryPaths(source, target, Path.Combine("history", "accounts.json"), "avatars");
        RewriteAccountHistoryPaths(source, target, "white-accounts.json", "white-avatars");
        RewriteLoginCachePaths(source, target);
    }

    private static void RewriteAccountHistoryPaths(
        string source,
        string target,
        string relativeFilePath,
        string avatarFolderName)
    {
        var filePath = Path.Combine(target, relativeFilePath);
        if (!File.Exists(filePath))
        {
            return;
        }

        var json = File.ReadAllText(filePath);
        var document = JsonSerializer.Deserialize(json, AccountHistoryJsonContext.Default.AccountHistoryDocument);
        if (document?.Accounts is null)
        {
            return;
        }

        var changed = false;
        foreach (var account in document.Accounts)
        {
            var remapped = RemapStoredPath(
                account.AvatarPath,
                source,
                target,
                Path.Combine(target, avatarFolderName));
            if (!string.Equals(account.AvatarPath, remapped, StringComparison.Ordinal))
            {
                account.AvatarPath = remapped;
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(document, AccountHistoryJsonContext.Default.AccountHistoryDocument));
        }
    }

    private static void RewriteLoginCachePaths(string source, string target)
    {
        var filePath = Path.Combine(target, "cached-login.json");
        if (!File.Exists(filePath))
        {
            return;
        }

        var json = File.ReadAllText(filePath);
        var document = JsonSerializer.Deserialize(json, SteamLoginCacheJsonContext.Default.CachedSteamLoginDocument);
        if (document is null)
        {
            return;
        }

        var changed = false;
        var avatarFolder = Path.Combine(target, "cached-avatars");
        foreach (var account in (document.Accounts ?? []).Concat(document.EyaAccounts ?? []))
        {
            var remapped = RemapStoredPath(account.AvatarPath, source, target, avatarFolder);
            if (!string.Equals(account.AvatarPath, remapped, StringComparison.Ordinal))
            {
                account.AvatarPath = remapped;
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(document, SteamLoginCacheJsonContext.Default.CachedSteamLoginDocument));
        }
    }

    private static string? RemapStoredPath(
        string? storedPath,
        string source,
        string target,
        string targetAvatarFolder)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return storedPath;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(storedPath);
        }
        catch
        {
            return storedPath;
        }

        if (IsSubdirectory(fullPath, source))
        {
            var relative = Path.GetRelativePath(source, fullPath);
            var movedPath = Path.Combine(target, relative);
            if (File.Exists(movedPath))
            {
                return movedPath;
            }
        }

        // 兼容旧版本曾使用相对路径或目录结构变化过的数据；按头像文件名回退查找。
        var byFileName = Path.Combine(targetAvatarFolder, Path.GetFileName(fullPath));
        return File.Exists(byFileName) ? byFileName : storedPath;
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Cannot move reparse point: {directory}");
            }

            var name = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(destination, name));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destination, name), overwrite: false);
        }
    }

    private static void TryCleanupDestination(string destination, bool destinationExisted)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            if (destinationExisted)
            {
                Directory.CreateDirectory(destination);
            }
        }
        catch
        {
            // 回滚清理失败不影响源目录；下次移动时会再次要求目标为空。
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSubdirectory(string candidate, string parent)
    {
        var prefix = parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
