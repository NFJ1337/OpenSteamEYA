using System.Net;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

internal sealed record AlcyBackgroundType(
    string Name,
    string ApiPath,
    string PreviewUrl,
    string Description,
    bool IsAdaptive);

internal static class AlcyBackgroundService
{
    private const string SiteRoot = "https://t.alcy.cc/";
    private const int CacheImageCount = 10;
    private const int DownloadBatchSize = 5;
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly Regex CardRegex = new(
        @"<div\s+class=""card"">(?<body>.*?)(?=<div\s+class=""card"">|</section>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PreviewRegex = new(
        @"data-src=""(?<value>[^""]+)""",
        RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new(
        @"class=""tag"">(?<value>[^<]+)",
        RegexOptions.IgnoreCase);
    private static readonly Regex UrlRegex = new(
        @"class=""url"">(?<value>[^<]+)",
        RegexOptions.IgnoreCase);
    private static readonly Regex DescriptionRegex = new(
        @"class=""desc"">(?<value>[^<]+)",
        RegexOptions.IgnoreCase);
    private static readonly Regex ApiPathRegex = new(
        @"^/[a-z0-9]{2,24}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<AlcyBackgroundType>> GetImageTypesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(SiteRoot, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = new List<AlcyBackgroundType>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match card in CardRegex.Matches(html))
        {
            var body = card.Groups["body"].Value;
            var name = WebUtility.HtmlDecode(ReadValue(TagRegex, body)).Trim();
            var url = WebUtility.HtmlDecode(ReadValue(UrlRegex, body)).Trim();
            var description = WebUtility.HtmlDecode(ReadValue(DescriptionRegex, body)).Trim();
            var preview = WebUtility.HtmlDecode(ReadValue(PreviewRegex, body)).Trim();
            if (!TryNormalizeApiPath(url, out var apiPath) ||
                !seen.Add(apiPath) ||
                !Uri.TryCreate(new Uri(SiteRoot), preview, out var previewUri))
            {
                continue;
            }

            var isAdaptive =
                name.Contains("自适应", StringComparison.Ordinal) ||
                description.Contains("自适应", StringComparison.Ordinal);
            var isLandscape =
                !isAdaptive &&
                (name.Contains("横图", StringComparison.Ordinal) ||
                 description.Contains("横图", StringComparison.Ordinal) ||
                 description.Contains("横版", StringComparison.Ordinal));
            if (!isAdaptive && !isLandscape)
            {
                continue;
            }

            result.Add(new AlcyBackgroundType(
                string.IsNullOrWhiteSpace(name) ? apiPath : name,
                apiPath,
                AppendCacheBuster(previewUri).ToString(),
                description,
                isAdaptive));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("t.alcy.cc 没有返回可用的自适应或横图类型。");
        }

        return result;
    }

    public static bool TryNormalizeApiPath(string? value, out string apiPath)
    {
        apiPath = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(new Uri(SiteRoot), text, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "t.alcy.cc", StringComparison.OrdinalIgnoreCase) ||
            !ApiPathRegex.IsMatch(uri.AbsolutePath))
        {
            return false;
        }

        apiPath = uri.AbsolutePath;
        return true;
    }

    public static Uri CreateImageUri(string apiPath)
    {
        if (!TryNormalizeApiPath(apiPath, out var normalized))
        {
            throw new ArgumentException("Invalid t.alcy.cc image path.", nameof(apiPath));
        }

        return new Uri(
            $"{SiteRoot.TrimEnd('/')}{normalized}?w=1920&r={DateTimeOffset.UtcNow.Ticks}");
    }

    public static IReadOnlyList<string> NormalizeApiPaths(IEnumerable<string?>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        return paths
            .Select(path => TryNormalizeApiPath(path, out var normalized) ? normalized : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? PickRandomApiPath(
        IEnumerable<string?>? paths,
        string? legacyPath = null)
    {
        var normalized = NormalizeApiPaths(paths).ToList();
        if (normalized.Count == 0 && TryNormalizeApiPath(legacyPath, out var legacy))
        {
            normalized.Add(legacy);
        }

        return normalized.Count == 0
            ? null
            : normalized[Random.Shared.Next(normalized.Count)];
    }

    public static IReadOnlyList<string> GetCachedBackgroundFiles(string cacheFolder)
    {
        if (!Directory.Exists(cacheFolder))
        {
            return [];
        }

        var files = Directory
            .EnumerateFiles(cacheFolder, "auto-bg-*")
            .Where(IsSupportedImageFile)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(CacheImageCount)
            .ToArray();
        PruneOldCacheFiles(cacheFolder, files);
        return files;
    }

    public static async Task<IReadOnlyList<string>> RefreshBackgroundCacheAsync(
        IReadOnlyList<string> apiPaths,
        string cacheFolder,
        CancellationToken cancellationToken = default)
    {
        var normalizedPaths = NormalizeApiPaths(apiPaths);
        if (normalizedPaths.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(cacheFolder);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var downloaded = new List<string>(CacheImageCount);
        var sequence = 0;
        for (var attempt = 0; attempt < 4 && downloaded.Count < CacheImageCount; attempt++)
        {
            var batchSize = Math.Min(
                DownloadBatchSize,
                CacheImageCount - downloaded.Count);
            var tasks = Enumerable.Range(sequence, batchSize)
                .Select(index => DownloadBackgroundAsync(
                    normalizedPaths[Random.Shared.Next(normalizedPaths.Count)],
                    cacheFolder,
                    stamp,
                    index,
                    cancellationToken))
                .ToArray();
            sequence += batchSize;
            downloaded.AddRange(
                (await Task.WhenAll(tasks)).OfType<string>());
        }

        if (downloaded.Count < CacheImageCount)
        {
            foreach (var file in downloaded)
            {
                TryDelete(file);
            }

            throw new InvalidOperationException(
                $"自动背景图未能完整预下载 {CacheImageCount} 张，继续使用上一次缓存。");
        }

        var result = downloaded.Take(CacheImageCount).ToArray();
        PruneOldCacheFiles(cacheFolder, result);
        return result;
    }

    public static void ClearCache(string cacheFolder)
    {
        if (!Directory.Exists(cacheFolder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(cacheFolder, "auto-bg-*"))
        {
            TryDelete(file);
        }
    }

    private static string ReadValue(Regex regex, string html)
    {
        var match = regex.Match(html);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static Uri AppendCacheBuster(Uri uri)
    {
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri($"{uri}{separator}r={DateTimeOffset.UtcNow.Ticks}");
    }

    private static async Task<string?> DownloadBackgroundAsync(
        string apiPath,
        string cacheFolder,
        long stamp,
        int index,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(
            cacheFolder,
            $"auto-bg-{stamp}-{index}.{Guid.NewGuid():N}.tmp");
        try
        {
            var uri = new Uri(
                $"{SiteRoot.TrimEnd('/')}{apiPath}?w=1920&r={stamp}-{index}");
            using var response = await Client.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var extension = mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => ".webp"
            };
            var finalPath = Path.Combine(cacheFolder, $"auto-bg-{stamp}-{index}{extension}");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"下载自动背景图失败：{ex.Message}");
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void PruneOldCacheFiles(
        string cacheFolder,
        IEnumerable<string> keepFiles)
    {
        var keep = new HashSet<string>(
            keepFiles.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(cacheFolder, "auto-bg-*"))
        {
            if (!keep.Contains(Path.GetFullPath(file)))
            {
                TryDelete(file);
            }
        }
    }

    private static bool IsSupportedImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".webp" or ".jpg" or ".jpeg" or ".png";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理自动背景缓存失败：{ex.Message}");
        }
    }
}
