using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

/// <summary>举报端点的一次结果。</summary>
internal readonly record struct SteamReportResult(int StatusCode, string Detail, string Raw)
{
    /// <summary>
    /// Steam 返回的 EResult 码（响应体就是一个数字时解析出来）：1 = OK，25 = LimitExceeded…
    /// 响应体不是纯数字时为 null（例如被导向登录页的整页 HTML）。
    /// </summary>
    public int? EResult { get; init; }

    /// <summary>
    /// 成功 = HTTP 2xx **且** EResult 为 1；响应体不是数字（取不到码）时退回只看状态码。
    ///
    /// 实测（2026-09-18）：这个端点在「被限流」时同样回 HTTP 200，响应体是数字 25（LimitExceeded）。
    /// 早先只看状态码，会把限流当成提交成功 —— 界面显示「已提交举报」但那条根本没报上去。
    /// 另注：实测目标 ID 不存在时端点也回 200 + 数字，所以 1 只代表这一条被受理，不代表目标一定存在。
    /// </summary>
    public bool Ok => StatusCode is >= 200 and < 300 && EResult is null or 1;
}

/// <summary>
/// 走 steamcommunity 官方的举报端点：资料页「Report Player」弹窗提交时打的就是它。
///
/// 端点与字段都不是猜的——是从线上页面实测出来的（<c>#abuseForm</c> 表单 + <c>checkAbuseSub</c> 的提交逻辑）：
///   POST https://steamcommunity.com/actions/ReportAbuse/
///   abuseID=&lt;被举报的 SteamID64&gt;  sessionid=&lt;与 cookie 一致&gt;  ingameAppID=&lt;游戏 appid，可空&gt;
///   abuseType=&lt;Harassment | Spoofing | Offensive Profile | Offensive UGC | Spamming | Advertisement |
///              Suspected Hijacker | Trade Scam | Cheating&gt;  json=1
/// 其中 <c>Cheating</c> 在页面上显示为「Suspected Cheater」，就是「作弊」这一档。
/// 表单里还有一个 textarea <c>abuseDescription</c>（placeholder 写着 required），内容由 <see cref="ReportPhraseBank"/> 随机生成。
/// 认证用的是同一条 steamcommunity 会话（steamLoginSecure + sessionid），全程不需要开浏览器窗口。
/// </summary>
internal static class SteamReportService
{
    private const string Endpoint = "https://steamcommunity.com/actions/ReportAbuse/";

    /// <summary>「作弊」档（页面显示为 Suspected Cheater）。</summary>
    public const string AbuseTypeCheating = "Cheating";

    /// <summary>
    /// 打开自动解压：实测无效 abuseID 那类 400 响应是 gzip 压缩的，不自动解压就只能拿到二进制乱码，
    /// 队列里会显示成乱字符、日志也没法看。
    /// </summary>
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// 是否被 Steam 限流：EResult 25（LimitExceeded）/ 84（RateLimitExceeded）或 HTTP 429，
    /// 另外兜一层响应体关键字（限流也可能以 4xx + 说明文字的形式回来）。
    /// </summary>
    public static bool IsRateLimited(SteamReportResult result)
    {
        // 主判定：EResult 25 = LimitExceeded（实测被限流时返回的就是它）、84 = RateLimitExceeded；
        // HTTP 429 一并算，关键字只作兜底。
        if (result.StatusCode == 429 || result.EResult is 25 or 84)
        {
            return true;
        }

        var text = result.Raw + " " + result.Detail;
        return RateLimitMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把 EResult 码写成一行可读说明：枚举名沿用 Steam 自己的叫法，方便对着日志搜。</summary>
    private static string DescribeSteamCode(int code) =>
        $"EResult {code}（{EResultName(code)}）";

    private static string EResultName(int code) =>
        EResultNames.TryGetValue(code, out var name) ? name : "Unknown";

    /// <summary>常见 EResult（数值取自 Steam 自己的枚举定义）。</summary>
    private static readonly Dictionary<int, string> EResultNames = new()
    {
        [1] = "OK",
        [2] = "Fail",
        [3] = "NoConnection",
        [5] = "InvalidPassword",
        [8] = "InvalidParam",
        [11] = "InvalidState",
        [15] = "AccessDenied",
        [16] = "Timeout",
        [17] = "Banned",
        [19] = "InvalidSteamID",
        [20] = "ServiceUnavailable",
        [21] = "NotLoggedOn",
        [24] = "InsufficientPrivilege",
        [25] = "LimitExceeded",
        [29] = "DuplicateRequest",
        [84] = "RateLimitExceeded",
    };

    /// <summary>限流文案特征：只收明确表达「太频繁 / 限流」的，避免把普通报错也当成限流去白等一分钟。</summary>
    private static readonly string[] RateLimitMarkers =
    [
        "rate limit", "rate-limit", "ratelimit",
        "too many requests", "too many reports", "too many attempts",
        "slow down", "限流", "过于频繁", "過於頻繁", "频繁"
    ];

    /// <summary>
    /// 传输层重试间隔（第 1 次立即、之后退避）。只用来兜「没连上」这类问题：
    /// 实测这条网络到 steamcommunity.com 的 TLS 握手会偶发被掐断（同一份日志里预热、举报都遇到过
    /// "The SSL connection could not be established"），一次失败就报错太脆。
    /// 已经收到 HTTP 响应的（含 25 / 429）绝不重试 —— 那是服务器明确答复。
    /// </summary>
    private static readonly TimeSpan[] TransportRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(4),
    ];

    private static async Task<(HttpResponseMessage Response, string Body)> SendWithTransportRetryAsync(
        SteamWebSession session,
        string targetSteamId64,
        int ingameAppId,
        string abuseType,
        string abuseDescription,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (TransportRetryDelays[attempt] > TimeSpan.Zero)
            {
                await Task.Delay(TransportRetryDelays[attempt], cancellationToken);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Add("Cookie", session.CookieHeader);
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                request.Headers.Add("Referer", $"https://steamcommunity.com/profiles/{targetSteamId64}/");
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var form = new Dictionary<string, string>
                {
                    ["abuseID"] = targetSteamId64,
                    ["sessionid"] = session.SessionId,
                    ["ingameAppID"] = ingameAppId > 0 ? ingameAppId.ToString() : string.Empty,
                    ["abuseType"] = abuseType,
                    ["json"] = "1"
                };

                if (!string.IsNullOrWhiteSpace(abuseDescription))
                {
                    form["abuseDescription"] = abuseDescription;
                }

                request.Content = new FormUrlEncodedContent(form);

                var response = await HttpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return (response, body);
            }
            catch (Exception ex) when (attempt + 1 < TransportRetryDelays.Length && IsTransportFailure(ex, cancellationToken))
            {
                AppLog.Warn(
                    $"举报 {targetSteamId64} 第 {attempt + 1} 次提交没连上（{ex.Message}），" +
                    $"{TransportRetryDelays[attempt + 1].TotalSeconds:0.#} 秒后重试。");
            }
        }
    }

    /// <summary>是不是「没连上」这一类传输层失败（TLS 握手失败 / 连接被重置 / DNS / 超时）。</summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken)
    {
        // 用户按了「停止」不算网络失败，直接抛出去交给上层当取消处理。
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            // TLS 握手失败的真正原因（AuthenticationException）就包在内层；Socket/IO 失败同理。
            if (current is SocketException or AuthenticationException or IOException or HttpRequestException)
            {
                return true;
            }
        }

        // 超时（HttpClient.Timeout 触发）也当传输层问题重试；用户取消已在上面排除。
        return ex is TaskCanceledException;
    }
    /// <summary>日志与队列里最多展示的响应体长度。</summary>
    private const int MaxLoggedBodyLength = 400;

    /// <summary>
    /// 把响应体压成一行可读文本：控制字符与替换字符（非文本/解压失败的痕迹）换成空格，
    /// 折叠连续空白，再按 <see cref="MaxLoggedBodyLength"/> 截断 —— 免得队列里和日志里出现乱码或换行。
    /// </summary>
    private static string ToReadableText(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(body.Length, MaxLoggedBodyLength));
        foreach (var ch in body)
        {
            if (builder.Length >= MaxLoggedBodyLength)
            {
                break;
            }

            builder.Append(char.IsControl(ch) || ch == '\uFFFD' ? ' ' : ch);
        }

        var text = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxLoggedBodyLength ? text[..MaxLoggedBodyLength] : text;
    }
    /// <param name="abuseDescription">
    /// 文字描述。官方表单里这个框是必填（textarea 的 placeholder 就写着 required），
    /// 字段名 abuseDescription 也是从登录态资料页的 #abuseForm 里读出来的；空字符串就不带这个字段。
    /// </param>
    public static async Task<SteamReportResult> ReportAsync(
        SteamWebSession session,
        string targetSteamId64,
        int ingameAppId,
        string abuseType,
        string abuseDescription,
        CancellationToken cancellationToken)
    {
        var (response, body) = await SendWithTransportRetryAsync(
            session,
            targetSteamId64,
            ingameAppId,
            abuseType,
            abuseDescription,
            cancellationToken);
        using var responseScope = response;

        var raw = ToReadableText(body);

        var status = (int)response.StatusCode;

        // 响应体就是一个数字：Steam 的 EResult（1 = OK，25 = LimitExceeded…）。
        // 这是判断「到底有没有报上去」的唯一依据 —— 限流时状态码照样是 200。
        var steamCode = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode)
            ? parsedCode
            : (int?)null;

        // 成功/失败都留一条：HTTP 状态 + EResult + 返回原文（便于事后核对这一条到底成没成）。
        AppLog.Info(
            $"举报 {targetSteamId64}（类型={abuseType}，游戏={ingameAppId}，描述=\"{abuseDescription}\"）：HTTP {status}" +
            $"{(steamCode is int loggedCode ? $"，EResult {loggedCode}（{EResultName(loggedCode)}）" : string.Empty)}" +
            $"{(raw.Length == 0 ? string.Empty : $"，Steam 返回：{raw}")}");

        if (status is >= 200 and < 300)
        {
            var okDetail = steamCode is int code && code != 1 ? DescribeSteamCode(code) : string.Empty;
            return new SteamReportResult(status, okDetail, raw) { EResult = steamCode };
        }

        // 失败时尽量给出人话的原因：对象型响应里常见 message / error / errordesc。
        var message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "message", "error", "errordesc" })
                {
                    if (document.RootElement.TryGetProperty(key, out var node) &&
                        node.ValueKind == JsonValueKind.String)
                    {
                        message = node.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON（例如被导向登录页的 HTML）：原文就够定位了。
        }

        return new SteamReportResult(status, string.IsNullOrWhiteSpace(message) ? raw : message, raw) { EResult = steamCode };
    }
}