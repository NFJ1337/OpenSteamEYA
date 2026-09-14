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

    /// <summary>单次检查最多尝试几个站点（首选 + 兜底），避免全部超时把启动拖太久。</summary>
    private const int MaxSitesPerCheck = 3;

    /// <summary>单站点请求超时；比 HttpClient 的 20 秒短，好在站点不通时尽快换下一个。</summary>
    private static readonly TimeSpan SiteAttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 检查更新：先走用户选的站点；遇到网络/TLS 这类瞬时故障就换下一个站点再试（最多 3 个）。
    /// 这是「第一次打开没提示更新、第二次才有」的主因之一：启动瞬间经代理站的 TLS 握手偶发失败，
    /// 而自动检查失败是完全静默的，用户就以为没有更新。
    /// </summary>
    public async Task<GitHubUpdateInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var primary = ResolveSite(_selectedProxyCode);
        var candidates = new List<GitHubProxySite> { primary };
        candidates.AddRange(ProxySites.Where(site => site.Code != primary.Code));

        return await CheckLatestWithSitesAsync(candidates.Take(MaxSitesPerCheck).ToArray(), cancellationToken);
    }

    /// <summary>按给定站点顺序依次尝试；只在瞬时网络故障时换站点，业务性错误直接抛出。</summary>
    internal async Task<GitHubUpdateInfo> CheckLatestWithSitesAsync(
        IReadOnlyList<GitHubProxySite> sites,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await CheckLatestViaSiteAsync(site, cancellationToken);
            }
            catch (Exception ex) when (IsTransientNetworkFailure(ex))
            {
                lastError = ex;
                AppLog.Warn($"更新检查经 {site.DisplayName} 失败（{ex.Message}），换下一个站点重试。");
            }
        }

        throw lastError ?? new InvalidOperationException(Loc.T("Update_EmptyResponse"));
    }

    /// <summary>瞬时网络/TLS 故障（可以换站点重试）；其它异常（例如响应格式不对）不重试。</summary>
    private static bool IsTransientNetworkFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or IOException or System.Net.Sockets.SocketException;

    private async Task<GitHubUpdateInfo> CheckLatestViaSiteAsync(GitHubProxySite site, CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(SiteAttemptTimeout);
        var token = attemptCts.Token;

        using var metadataResponse = await HttpClient.SendAsync(
            CreateNoCacheRequest(BuildMetadataUrl(site)),
            token);

        // 仓库手工发版（没跑 release 工作流）时没有 latest.json：退一步用 Releases API 读最新 release，
        // 避免把原始 404 直接甩给用户。
        if (metadataResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return await CheckLatestViaReleasesApiAsync(site, token);
        }

        metadataResponse.EnsureSuccessStatusCode();

        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(token);
        GitHubReleaseMetadataDto metadata = await JsonSerializer.DeserializeAsync(
            metadataStream,
            GitHubUpdateJsonContext.Default.GitHubReleaseMetadataDto,
            token)
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
        using var response = await HttpClient.SendAsync(CreateNoCacheRequest(BuildReleaseApiUrl(site)), cancellationToken);
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
        var probeUrl = BuildMetadataUrl(site);
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

    /// <summary>
    /// 元数据地址：附带一个每次都不同的查询串，并声明不缓存。
    /// gh-proxy / CDN 会按完整 URL 缓存 latest.json，刚发布新版本时客户端可能读到旧元数据 ——
    /// 表现就是「第一次打开说已是最新，第二次打开才提示有新版本」。
    /// </summary>
    internal static string BuildMetadataUrl(GitHubProxySite site) =>
        $"{BuildUrl(site, LatestMetadataUrl)}{(LatestMetadataUrl.Contains('?') ? '&' : '?')}_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    /// <summary>同一个套路用于 Releases API 兜底地址。</summary>
    private static string BuildReleaseApiUrl(GitHubProxySite site) =>
        $"{BuildUrl(site, LatestReleaseApiUrl)}?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    /// <summary>构造「不吃缓存」的 GET 请求（no-cache + no-store）。</summary>
    private static HttpRequestMessage CreateNoCacheRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        return request;
    }

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
