using System.IO;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 读取随安装包分发的本地更新日志。文件固定为 Assets\ReleaseNotes.txt，
/// 不依赖网络，保证首次安装后也能立即显示当前版本的更新内容。
/// </summary>
internal static class LocalReleaseNotesService
{
    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ReleaseNotes.txt");

    public static string InstallNoticeFlagPath =>
        Path.Combine(AppContext.BaseDirectory, "show-release-notes.flag");

    /// <summary>安装器每次安装/重装都会重建此标记，确保保留旧设置时仍会再次提示。</summary>
    public static bool HasPendingInstallNotice() => File.Exists(InstallNoticeFlagPath);

    public static void ClearPendingInstallNotice()
    {
        try
        {
            if (File.Exists(InstallNoticeFlagPath))
            {
                File.Delete(InstallNoticeFlagPath);
            }
        }
        catch
        {
            // 留到下次启动再试；不能因为清理标记失败影响主程序。
        }
    }

    public static GitHubUpdateService.ReleaseChangelogInfo? TryLoad()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var version = GitHubUpdateService.CurrentVersion;
        var lines = new List<string>();
        foreach (var rawLine in File.ReadLines(FilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            line = line.TrimStart('#', ' ', '\t');
            if (TryReadTitleVersion(line, out var titleVersion))
            {
                version = titleVersion;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("• ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return new GitHubUpdateService.ReleaseChangelogInfo(
            version,
            $"SteamEYA v{version}",
            lines,
            null,
            $"v{version}");
    }

    private static bool TryReadTitleVersion(string line, out string version)
    {
        const string prefix = "SteamEYA v";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            version = line[prefix.Length..].Trim();
            return version.Length > 0;
        }

        version = string.Empty;
        return false;
    }
}
