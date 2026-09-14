namespace SteamEyaWinUI.Models;

internal sealed record GitHubUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    string? ArtifactName,
    string? ArtifactUrl,
    long? ArtifactSize,
    string? ArtifactType,
    string? ArtifactSha256,
    IReadOnlyList<string> Changelog,
    DateTimeOffset CheckedAt,
    // 需要额外说明时（例如仓库还没发布 latest.json、tag 又不是版本号）给 UI 的一句提示；
    // 为空表示走正常流程，与上游行为一致。
    string? MetadataNotice = null);
