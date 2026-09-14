using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class GitHubUpdateService
{
    public const string RepositoryUrl = "https://github.com/NFJ1337/OpenSteamEYA";
    public const string ReleasesUrl = $"{RepositoryUrl}/releases";
    private const string LatestMetadataUrl = "https://github.com/NFJ1337/OpenSteamEYA/releases/latest/download/latest.json";

    // latest.json 还没随 release 上传时的兜底数据源（GitHub Releases API，无需令牌）。
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/NFJ1337/OpenSteamEYA/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly IReadOnlyList<GitHubProxySite> ProxySites =
    [
        new("direct", "Direct", null),
        new("gh-proxy.org", "gh-proxy.org", "https://gh-proxy.org/"),
        new("v4.gh-proxy.org", "v4.gh-proxy.org", "https://v4.gh-proxy.org/"),
        new("v6.gh-proxy.org", "v6.gh-proxy.org", "https://v6.gh-proxy.org/"),
        new("cdn.gh-proxy.org", "cdn.gh-proxy.org", "https://cdn.gh-proxy.org/")
    ];

    private string _selectedProxyCode = "direct";

    public static string CurrentVersion { get; } = GetCurrentVersion();

    public string SelectedProxyCode => _selectedProxyCode;

    public IReadOnlyList<GitHubProxySite> GetProxySites() => ProxySites;

    public void SetProxySite(string? proxyCode)
    {
        _selectedProxyCode = ResolveSite(proxyCode).Code;
    }

    public async Task<GitHubUpdateInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var site = ResolveSite(_selectedProxyCode);

        using var metadataResponse = await HttpClient.GetAsync(BuildUrl(site, LatestMetadataUrl), cancellationToken);

        // 仓库手工发版（没跑 release 工作流）时没有 latest.json：退一步用 Releases API 读最新 release，
        // 避免把原始 404 直接甩给用户。
        if (metadataResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return await CheckLatestViaReleasesApiAsync(site, cancellationToken);
        }

        metadataResponse.EnsureSuccessStatusCode();

        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseMetadataDto metadata = await JsonSerializer.DeserializeAsync(
            metadataStream,
            GitHubUpdateJsonContext.Default.GitHubReleaseMetadataDto,
            cancellationToken)
            ?? throw new InvalidOperationException(Loc.T("Update_EmptyResponse"));

        var latestTag = string.IsNullOrWhiteSpace(metadata.Tag)
            ? "latest"
            : metadata.Tag!;
        var latestVersion = NormalizeVersion(metadata.Version ?? metadata.Tag);
        var currentVersion = CurrentVersion;
        var changelog = metadata.Changelog?.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            ?? [];
        var artifactName = metadata.ArtifactName;
        var artifactUrl = BuildArtifactUrl(site, latestTag, artifactName);
        var releaseUrl = BuildReleasePageUrl();

        return new GitHubUpdateInfo(
            currentVersion,
            latestVersion,
            latestTag,
            IsNewerVersion(latestVersion, currentVersion),
            releaseUrl,   // 发布页是给浏览器看的，不能用下载代理前缀
            artifactName,
            artifactUrl,
            metadata.ArtifactSize,
            metadata.ArtifactType,
            metadata.ArtifactSha256,
            changelog,
            DateTimeOffset.Now);
    }

    /// <summary>
    /// 兜底路径：用 GitHub Releases API 读「最新 release」。
    /// · tag 能解析成版本号 → 与 latest.json 同款结果（能比较版本、能用直链下载安装包）；
    /// · tag 不是版本号（例如「正式exe」）→ 不谎报「已是最新」，只给出提示与发布页入口，也不提供下载按钮
    ///   （避免把比本机更旧的安装包当更新推下去）。
    /// </summary>
    private async Task<GitHubUpdateInfo> CheckLatestViaReleasesApiAsync(GitHubProxySite site, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(BuildUrl(site, LatestReleaseApiUrl), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync(
            stream,
            GitHubUpdateJsonContext.Default.GitHubReleaseDto,
            cancellationToken)
            ?? throw new InvalidOperationException(Loc.T("Update_EmptyResponse"));

        var tag = string.IsNullOrWhiteSpace(release.TagName) ? "latest" : release.TagName!;
        var version = NormalizeVersion(release.TagName);
        var currentVersion = CurrentVersion;
        var hasVersion = TryParseVersion(version, out _);

        var asset = release.Assets?.FirstOrDefault(item =>
            item.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

        // tag 起成非版本号（例如「正式exe」）时，退一步从安装包文件名里认版本
        // （SteamEYA-1.2.3-win-x64-setup.exe → 1.2.3）：这样照样能比较版本、该自动更新就自动更新。
        if (!hasVersion && asset?.Name is { Length: > 0 } assetFileName &&
            TryParseVersionFromFileName(assetFileName, out var versionFromFileName))
        {
            version = versionFromFileName;
            hasVersion = true;
        }

        var isNewer = hasVersion && IsNewerVersion(version, currentVersion);

        var artifactUrl = asset?.Name is { Length: > 0 } assetName
            ? BuildArtifactUrl(site, tag, assetName)
            : null;

        return new GitHubUpdateInfo(
            currentVersion,
            version,
            tag,
            isNewer,
            BuildReleasePageUrl(),
            asset?.Name,
            artifactUrl,
            asset?.Size,
            asset is null ? null : "exe-installer",
            null,   // API 不提供 sha256：留空，下载完成后按大小校验即可
            SplitChangelog(release.Body),
            DateTimeOffset.Now,
            hasVersion ? null : Loc.Tf("About_Update_TagNotVersion_Format", tag));
    }

    /// <summary>从文件名里认版本号（如 SteamEYA-1.2.3-win-x64-setup.exe → 1.2.3）；认不出返回 false。</summary>
    private static bool TryParseVersionFromFileName(string fileName, out string version)
    {
        version = string.Empty;
        for (var index = 0; index < fileName.Length; index++)
        {
            if (!char.IsAsciiDigit(fileName[index]))
            {
                continue;
            }

            var end = index;
            while (end < fileName.Length && (char.IsAsciiDigit(fileName[end]) || fileName[end] == '.'))
            {
                end++;
            }

            var candidate = fileName[index..end].Trim('.');
            if (candidate.Count(character => character == '.') >= 2 && TryParseVersion(candidate, out _))
            {
                version = candidate;
                return true;
            }

            index = end;
        }

        return false;
    }

    /// <summary>把 release 说明按行拆成更新日志（去掉空行与 Markdown 标题符号）。</summary>
    private static string[] SplitChangelog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        return body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
    }

    public async Task<TimeSpan> ProbeLatencyAsync(string? proxyCode = null, CancellationToken cancellationToken = default)
    {
        var site = ResolveSite(proxyCode ?? _selectedProxyCode);
        var probeUrl = BuildUrl(site, LatestMetadataUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await HttpClient.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static GitHubProxySite ResolveSite(string? code)
    {
        return ProxySites.FirstOrDefault(item =>
            string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? ProxySites[0];
    }

    private static string BuildUrl(GitHubProxySite site, string url)
    {
        if (string.IsNullOrWhiteSpace(site.UrlPrefix) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        // 这些代理是“前缀 + 完整 GitHub URL”模式，仅代理 github.com 相关链接。
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return site.UrlPrefix + url;
    }

    /// <summary>
    /// 发布页地址（浏览器打开用）：固定指 releases 列表页 —— 这个地址永远存在，最新一版排在最上面，
    /// 不会因为 release tag 不是版本号（例如「正式exe」）而跳到不存在的 /releases/tag/xxx 页面。
    /// 也不套下载代理前缀：gh-proxy 这类站点只加速下载，套上去页面反而打不开。
    /// </summary>
    internal static string BuildReleasePageUrl() => ReleasesUrl;

    private static string? BuildArtifactUrl(GitHubProxySite site, string latestTag, string? artifactName)
    {
        if (string.IsNullOrWhiteSpace(artifactName))
        {
            return null;
        }

        var baseUrl = string.Equals(latestTag, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"{ReleasesUrl}/latest/download/{Uri.EscapeDataString(artifactName)}"
            : $"{ReleasesUrl}/download/{Uri.EscapeDataString(latestTag)}/{Uri.EscapeDataString(artifactName)}";
        return BuildUrl(site, baseUrl);
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return NormalizeVersion(informational);
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        return TryParseVersion(latestVersion, out var latest) &&
            TryParseVersion(currentVersion, out var current) &&
            latest.CompareTo(current) > 0;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var normalized = NormalizeVersion(value);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        while (parts.Length < 3)
        {
            parts = [.. parts, "0"];
        }

        return Version.TryParse(string.Join('.', parts.Take(4)), out version!);
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOfAny(['+', '-']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }

    internal sealed record GitHubReleaseMetadataDto(
        string? Version,
        string? Tag,
        string? ArtifactName,
        long? ArtifactSize,
        string? ArtifactType,
        string? ArtifactSha256,
        IReadOnlyList<string>? Changelog);

    /// <summary>GitHub Releases API 的部分字段（snake_case 需要显式映射）。</summary>
    internal sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        string? Body,
        IReadOnlyList<GitHubReleaseAssetDto>? Assets);

    internal sealed record GitHubReleaseAssetDto(
        string? Name,
        long? Size);

    internal sealed record GitHubProxySite(
        string Code,
        string DisplayName,
        string? UrlPrefix);
}

// JsonSerializerDefaults.Web 保持旧版反射序列化语义：camelCase + 大小写不敏感，
// latest.json 的字段（version/tag/artifactName...）依赖该命名策略。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GitHubUpdateService.GitHubReleaseMetadataDto))]
[JsonSerializable(typeof(GitHubUpdateService.GitHubReleaseDto))]
internal sealed partial class GitHubUpdateJsonContext : JsonSerializerContext;
