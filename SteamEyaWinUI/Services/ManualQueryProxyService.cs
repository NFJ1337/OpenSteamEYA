using System.Diagnostics;
using System.Net;
using System.Text;

namespace SteamEyaWinUI.Services;

internal sealed record ManualProxyEndpoint(
    string DisplayName,
    Uri Address,
    string? UserName,
    string? Password)
{
    public IWebProxy CreateWebProxy()
    {
        var proxy = new WebProxy(Address)
        {
            BypassProxyOnLocal = false
        };
        if (!string.IsNullOrWhiteSpace(UserName))
        {
            proxy.Credentials = new NetworkCredential(UserName, Password ?? string.Empty);
        }

        return proxy;
    }
}

internal sealed record ManualProxyParseResult(
    IReadOnlyList<ManualProxyEndpoint> Endpoints,
    int InvalidCount);

internal sealed record ManualProxyTestFailure(string DisplayName, string Error);

internal sealed record ManualProxyTestSummary(
    int ConfiguredCount,
    int ValidCount,
    double AverageLatencyMilliseconds,
    IReadOnlyList<ManualProxyTestFailure> Failures);

/// <summary>一次账号查询专用的 HTTP/CM 代理上下文；未启用时沿用系统代理。</summary>
internal sealed partial class ManualQueryProxyTransport : IDisposable
{
    private readonly bool _ownsHttpClient;

    public ManualQueryProxyTransport(
        HttpClient httpClient,
        IWebProxy? webProxy,
        string? displayName,
        bool ownsHttpClient)
    {
        HttpClient = httpClient;
        WebProxy = webProxy;
        DisplayName = displayName;
        _ownsHttpClient = ownsHttpClient;
    }

    public HttpClient HttpClient { get; }

    public IWebProxy? WebProxy { get; }

    public string? DisplayName { get; }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            HttpClient.Dispose();
        }
    }
}

/// <summary>
/// 「选项配置 → 代理设置」的解析、轮换和连通性检测。
/// 该服务只服务账号查询链路，与 Clash 节点配置和账号个性化流程相互独立。
/// </summary>
internal static class ManualQueryProxyService
{
    private const int TestTimeoutSeconds = 8;
    private const int TestConcurrency = 8;
    private const string TestUrl =
        "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v0001/?format=json&cellid=0&cmtype=websockets";

    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "socks4", "socks4a", "socks5" };

    private static readonly object StateGate = new();
    private static readonly object WarningGate = new();
    private static readonly HttpClient SystemHttpClient = CreateHttpClient(proxy: null);
    private static string _stateSignature = string.Empty;
    private static string? _lastWarningSignature;
    private static int _currentIndex;
    private static int _queriesOnCurrentProxy;

    public static ManualQueryProxyTransport CreateTransportForAccountQuery()
    {
        var settings = AppState.SettingsService.Load();
        var endpoint = SelectEndpointForQuery(settings);
        IWebProxy? proxy = endpoint?.CreateWebProxy();
        var client = proxy is null ? SystemHttpClient : CreateHttpClient(proxy);
        return new ManualQueryProxyTransport(
            client,
            proxy,
            endpoint?.DisplayName,
            ownsHttpClient: proxy is not null);
    }

    public static ManualProxyParseResult Parse(IEnumerable<string>? values)
    {
        var endpoints = new List<ManualProxyEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidCount = 0;

        foreach (var line in EnumerateLines(values ?? Array.Empty<string>()))
        {
            if (!TryParseLine(line, out var endpoint))
            {
                invalidCount++;
                continue;
            }

            var identity =
                $"{endpoint.Address.AbsoluteUri}\n{endpoint.UserName}\n{endpoint.Password}";
            if (seen.Add(identity))
            {
                endpoints.Add(endpoint);
            }
        }

        return new ManualProxyParseResult(endpoints, invalidCount);
    }

    public static IReadOnlyList<string> NormalizeLinesForStorage(string? text) =>
        EnumerateLines(text is null ? Array.Empty<string>() : new[] { text }).ToList();

    public static string FormatLines(IEnumerable<string>? values) =>
        string.Join(Environment.NewLine, NormalizeLinesForStorage(
            string.Join(Environment.NewLine, values ?? Array.Empty<string>())));

    public static async Task<ManualProxyTestSummary> TestProxiesAsync(
        string? text,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(text is null ? Array.Empty<string>() : new[] { text });
        if (parsed.Endpoints.Count == 0)
        {
            return new ManualProxyTestSummary(
                parsed.InvalidCount,
                0,
                0,
                []);
        }

        using var gate = new SemaphoreSlim(TestConcurrency, TestConcurrency);
        var tasks = parsed.Endpoints
            .Select(endpoint => TestEndpointAsync(endpoint, gate, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var valid = results.Where(result => result.Success).ToArray();
        var failures = results
            .Where(result => !result.Success)
            .Select(result => new ManualProxyTestFailure(
                result.Endpoint.DisplayName,
                result.Error ?? "unknown"))
            .ToArray();

        return new ManualProxyTestSummary(
            parsed.Endpoints.Count + parsed.InvalidCount,
            valid.Length,
            valid.Length == 0 ? 0 : valid.Average(result => result.ElapsedMilliseconds),
            failures);
    }

    private static ManualProxyEndpoint? SelectEndpointForQuery(AppSettings settings)
    {
        if (!settings.ManualQueryProxyEnabled)
        {
            lock (StateGate)
            {
                _stateSignature = string.Empty;
                _currentIndex = 0;
                _queriesOnCurrentProxy = 0;
            }

            return null;
        }

        var parsed = Parse(settings.ManualQueryProxyAddresses);
        if (parsed.Endpoints.Count == 0)
        {
            WarnOnce(
                $"{settings.ManualQueryProxyRotationCount}|{string.Join('\n', settings.ManualQueryProxyAddresses ?? [])}",
                "手动代理已启用，但代理地址为空或格式无效；本次账号查询回退到系统代理。");
            return null;
        }

        var interval = Math.Max(1, settings.ManualQueryProxyRotationCount);
        var signature = BuildStateSignature(parsed.Endpoints, interval);

        lock (StateGate)
        {
            if (!string.Equals(_stateSignature, signature, StringComparison.Ordinal))
            {
                _stateSignature = signature;
                _currentIndex = 0;
                _queriesOnCurrentProxy = 0;
            }

            if (_queriesOnCurrentProxy >= interval)
            {
                _currentIndex = (_currentIndex + 1) % parsed.Endpoints.Count;
                _queriesOnCurrentProxy = 0;
            }

            var endpoint = parsed.Endpoints[_currentIndex];
            _queriesOnCurrentProxy++;
            return endpoint;
        }
    }

    private static void WarnOnce(string signature, string message)
    {
        lock (WarningGate)
        {
            if (string.Equals(_lastWarningSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastWarningSignature = signature;
        }

        AppLog.Warn(message);
    }

    private static string BuildStateSignature(
        IReadOnlyList<ManualProxyEndpoint> endpoints,
        int interval)
    {
        var builder = new StringBuilder(interval.ToString());
        foreach (var endpoint in endpoints)
        {
            builder
                .Append('\n')
                .Append(endpoint.Address.AbsoluteUri)
                .Append('|')
                .Append(endpoint.UserName)
                .Append('|')
                .Append(endpoint.Password);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateLines(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var rawLine in value.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace('\r', '\n')
                         .Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith('#') ||
                    line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return line;
            }
        }
    }

    private static bool TryParseLine(string line, out ManualProxyEndpoint endpoint)
    {
        endpoint = null!;
        var candidate = line.Trim();
        if (candidate.Contains(';'))
        {
            // 分号列表通常是系统代理的“绕过地址”，不是可连接的代理服务器。
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var address) ||
            !AllowedSchemes.Contains(address.Scheme) ||
            string.IsNullOrWhiteSpace(address.Host) ||
            address.Host.Contains('*') ||
            address.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            (!string.IsNullOrEmpty(address.AbsolutePath) &&
             !string.Equals(address.AbsolutePath, "/", StringComparison.Ordinal)))
        {
            return false;
        }

        string? userName = null;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(address.UserInfo))
        {
            var parts = address.UserInfo.Split(':', 2);
            userName = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        var displayName = string.Equals(address.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            ? $"{address.Host}:{address.Port}"
            : $"{address.Scheme}://{address.Host}:{address.Port}";
        endpoint = new ManualProxyEndpoint(displayName, address, userName, password);
        return true;
    }

    private static HttpClient CreateHttpClient(IWebProxy? proxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = true
        };
        if (proxy is not null)
        {
            handler.Proxy = proxy;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static async Task<ProxyTestResult> TestEndpointAsync(
        ManualProxyEndpoint endpoint,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var client = CreateHttpClient(endpoint.CreateWebProxy());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, TestUrl);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/123 Safari/537.36");

            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            watch.Stop();

            return response.IsSuccessStatusCode
                ? new ProxyTestResult(endpoint, true, watch.Elapsed.TotalMilliseconds, null)
                : new ProxyTestResult(
                    endpoint,
                    false,
                    0,
                    $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyTestResult(endpoint, false, 0, "timeout");
        }
        catch (Exception ex)
        {
            return new ProxyTestResult(
                endpoint,
                false,
                0,
                ex.GetBaseException().Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record ProxyTestResult(
        ManualProxyEndpoint Endpoint,
        bool Success,
        double ElapsedMilliseconds,
        string? Error);
}
