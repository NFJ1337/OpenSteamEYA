using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed record ClashProxyGroup(
    string Name,
    string Type,
    string Current,
    IReadOnlyList<string> Nodes)
{
    public string DisplayName => $"{Name} ({Type})";

    public bool CanSelectNode =>
        string.Equals(Type, "Selector", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ClashProxySnapshot(
    IReadOnlyList<ClashProxyGroup> Groups,
    IReadOnlyDictionary<string, string> NodeTypes);

internal sealed partial class ClashProxyService : IDisposable
{
    private const string DelayTestUrl = "http://www.gstatic.com/generate_204";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        UseProxy = true
    })
    {
        Timeout = RequestTimeout
    };

    public async Task<ClashProxySnapshot> GetSnapshotAsync(
        string apiBaseUrl,
        string apiSecret,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, apiBaseUrl, "proxies", apiSecret);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("proxies", out var proxies) ||
            proxies.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(Loc.T("Clash_Error_InvalidResponse"));
        }

        var nodeTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new List<ClashProxyGroup>();
        foreach (var property in proxies.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(property.Value, "type") ?? "Unknown";
            nodeTypes[property.Name] = type;
            if (!property.Value.TryGetProperty("all", out var all) ||
                all.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var nodes = all
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            groups.Add(new ClashProxyGroup(
                property.Name,
                type,
                GetString(property.Value, "now") ?? Loc.T("Clash_CurrentProxy_None"),
                nodes));
        }

        return new ClashProxySnapshot(
            groups
                .OrderBy(group => string.Equals(group.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList(),
            nodeTypes);
    }

    public async Task<int?> TestDelayAsync(
        string apiBaseUrl,
        string apiSecret,
        string proxyName,
        CancellationToken cancellationToken)
    {
        var escapedName = Uri.EscapeDataString(proxyName);
        var escapedTestUrl = Uri.EscapeDataString(DelayTestUrl);
        var relativePath = $"proxies/{escapedName}/delay?timeout=5000&url={escapedTestUrl}";
        using var request = CreateRequest(HttpMethod.Get, apiBaseUrl, relativePath, apiSecret);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("delay", out var delay) &&
            delay.TryGetInt32(out var milliseconds) &&
            milliseconds > 0
            ? milliseconds
            : null;
    }

    /// <summary>
    /// 对一组节点并发测试延迟。单个节点失败只记录为 null，不影响其它节点。
    /// 供启动后的静默预热使用，不涉及任何 UI 提示。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int?>> TestAllDelaysAsync(
        string apiBaseUrl,
        string apiSecret,
        IEnumerable<string> proxyNames,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var names = proxyNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var delays = new Dictionary<string, int?>(StringComparer.Ordinal);
        using var concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        await Task.WhenAll(names.Select(async name =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                int? delay;
                try
                {
                    delay = await TestDelayAsync(
                        apiBaseUrl,
                        apiSecret,
                        name,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    delay = null;
                }

                lock (delays)
                {
                    delays[name] = delay;
                }
            }
            finally
            {
                concurrency.Release();
            }
        }));

        return delays;
    }

    public async Task SelectProxyAsync(
        string apiBaseUrl,
        string apiSecret,
        string groupName,
        string proxyName,
        CancellationToken cancellationToken)
    {
        var relativePath = $"proxies/{Uri.EscapeDataString(groupName)}";
        using var request = CreateRequest(HttpMethod.Put, apiBaseUrl, relativePath, apiSecret);
        var payload = JsonSerializer.Serialize(
            new ClashSelectProxyRequest { Name = proxyName },
            ClashProxyJsonContext.Default.ClashSelectProxyRequest);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string apiBaseUrl,
        string relativePath,
        string apiSecret)
    {
        var trimmedUrl = apiBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(trimmedUrl) ||
            !Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(Loc.T("Clash_Error_InvalidUrl"));
        }

        var normalizedBase = trimmedUrl.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(normalizedBase, UriKind.Absolute), relativePath));
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "SteamEYANFJ-ClashProxy/1.0");

        var normalizedSecret = apiSecret.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSecret))
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                normalizedSecret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? normalizedSecret
                    : $"Bearer {normalizedSecret}");
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(Loc.T("Clash_Error_Unauthorized"));
        }

        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        await response.Content.ReadAsByteArrayAsync(cancellationToken);
        throw new HttpRequestException(
            Loc.Tf("Clash_Error_Http_Format", (int)response.StatusCode, reason));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

}

internal sealed class ClashSelectProxyRequest
{
    public string Name { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ClashSelectProxyRequest))]
internal sealed partial class ClashProxyJsonContext : JsonSerializerContext;
