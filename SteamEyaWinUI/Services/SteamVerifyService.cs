using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「Steam 账号核验」站（xn--rpr1ku6kjs6f.xyz）的 /api/verify 客户端：提交卡密，
/// 返回该账号的封禁状态、CS2 竞技记录与游戏库快照。
///
/// 上游契约取自站点前端 bundle（POST application/json，body {"key":"卡密"}）：
///   · HTTP 200 → 结果对象（status = ok / failed；failed 时以 reason 作为错误文案）
///   · HTTP 400 → {"error":"卡密校验失败：卡密无效"} 确定性答复，不重试
///   · HTTP 503 → {"error":"卡密服务暂时不可用，请稍后重试"} 上游抖动（实测有效卡密约 1/6 概率遇到），重试即恢复
/// 冷查询实测约 35 秒（响应里的 durationMs），故超时给得很宽，并在界面上显示已等待秒数。
/// </summary>
internal sealed class SteamVerifyService
{
    private const string VerifyEndpoint = "https://xn--rpr1ku6kjs6f.xyz/api/verify";

    /// <summary>总尝试次数。上游偶发 TLS 重置与 5xx，单次失败不代表卡密有问题。</summary>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 单次尝试的超时。上游核验要串行做「校验卡密 → 建立 Steam 会话 → 拉 CS2 与游戏库」，
    /// 冷查询实测 35 秒以上、上游拥塞时会更久（实测出现过 60 秒仍未返回）；
    /// 给单次尝试设上限，卡死时按瞬时故障重试或尽快报错，而不是让用户干等 5 分钟。
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(150);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<SteamVerifyPayload> VerifyAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var key = licenseKey.Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LicenseKeyRequired"));
        }

        var (response, body) = await SendWithRetryAsync(key, cancellationToken);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    ReadErrorMessage(body) ?? Loc.Tf("Login_Verify_Error_Http_Format", (int)response.StatusCode));
            }

            SteamVerifyPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize(body, SteamVerifyJsonContext.Default.SteamVerifyPayload)
                    ?? throw new InvalidOperationException(Loc.T("Login_Verify_Error_EmptyResponse"));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(Loc.Tf("Login_Verify_Error_BadResponse_Format", ex.Message), ex);
            }

            // 上游用 200 + status=failed 表达「卡密无效 / 账号取不到」这类业务失败。
            if (string.Equals(payload.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(payload.Reason)
                        ? Loc.T("Login_Verify_Error_FailedNoReason")
                        : payload.Reason);
            }

            return payload;
        }
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendWithRetryAsync(
        string key,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt < MaxAttempts && !cancellationToken.IsCancellationRequested;

            // 每次尝试都新建请求：HttpRequestMessage 不允许重复发送。
            using var request = CreateRequest(key);
            // 单次尝试独立计时：与调用方的取消令牌联动，任一触发都结束本次尝试。
            // 放在 try 外声明，catch 里才能判断「本轮是否是自己计时超时」。
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(AttemptTimeout);
            try
            {
                var response = await HttpClient.SendAsync(request, attemptCts.Token);
                var body = await response.Content.ReadAsStringAsync(attemptCts.Token);

                // 5xx 是上游/边缘节点的瞬时故障（实测同一卡密重试即成功）；4xx 是确定性答复，直接交给调用方。
                if (canRetry && IsTransientStatus(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(RetryDelay, cancellationToken);
                    continue;
                }

                return (response, body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                // 本次尝试是自己计时超时的：说明上游真的卡住了，重试只会再干等一轮，直接报超时。
                throw new InvalidOperationException(Loc.T("Login_Verify_Error_Timeout"));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && canRetry)
            {
                // 其它中断（非本轮计时超时）：按瞬时故障重试。
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (HttpRequestException) when (canRetry)
            {
                // 偶发 TLS/连接被重置（"Received an unexpected EOF or 0 bytes from the transport stream"），与卡密无关。
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpRequestMessage CreateRequest(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, VerifyEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new SteamVerifyRequest(key), SteamVerifyJsonContext.Default.SteamVerifyRequest),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static string? ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var error = JsonSerializer.Deserialize(body, SteamVerifyJsonContext.Default.SteamVerifyErrorDto);
            return string.IsNullOrWhiteSpace(error?.Error) ? null : error.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // 核验要串行做「校验卡密 → 建立 Steam 会话 → 拉 CS2 与游戏库」：站点实测冷查询约 35 秒，
        // 缓存命中也要 1-8 秒，故超时给到 300 秒；HttpClient.Timeout 同时覆盖响应体读取。
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(300)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Verify");
        return client;
    }
}

// ---- 上游 DTO：字段全部可空（上游可能只返回部分字段），命名与站点 JSON 一致 ----
// 时间字段统一用 JsonElement：上游混用两种形态——checkedAt / cooldown.until / lastMatchAt 是
// epoch 毫秒数字，library.fetchedAt / games.lastPlayed 是 ISO 串，交给 SteamVerifyPresenter.ParseTime 容错。

internal sealed record SteamVerifyRequest(string Key);

internal sealed record SteamVerifyErrorDto(string? Error);

internal sealed record SteamVerifyPayload(
    string? Status,
    string? Reason,
    string? SteamId,
    JsonElement CheckedAt,
    JsonElement CacheExpiresAt,
    bool? Cached,
    long? DurationMs,
    IReadOnlyList<string>? Flags,
    SteamVerifyPresenceDto? Presence,
    SteamVerifyBansDto? Bans,
    SteamVerifyCs2Dto? Cs2,
    SteamVerifyLibraryDto? Library);

internal sealed record SteamVerifyPresenceDto(
    bool? InGame,
    string? GameName,
    long? GameAppId,
    JsonElement CapturedAt,
    string? Error);

internal sealed record SteamVerifyBansDto(
    int? VacBans,
    int? GameBans,
    bool? TradeBanned,
    bool? CommunityBanned,
    int? DaysSinceLastBan,
    IReadOnlyList<SteamVerifyBanGameDto>? Games);

internal sealed record SteamVerifyBanGameDto(
    string? Name,
    string? Type,
    string? Detail);

internal sealed record SteamVerifyCooldownDto(
    bool? Active,
    string? Text,
    string? Reason,
    string? Severity,
    JsonElement Until);

internal sealed record SteamVerifyPrimeDto(bool? Status);

internal sealed record SteamVerifyRecordDto(
    string? Map,
    int? Wins,
    int? Ties,
    int? Losses,
    string? RatingLabel,
    string? SkillGroupLabel,
    JsonElement LastMatchAt);

internal sealed record SteamVerifyCs2Dto(
    SteamVerifyCooldownDto? Cooldown,
    SteamVerifyPrimeDto? Prime,
    int? ProfileRank,
    string? ProfileRankName,
    bool? ServiceMedal,
    SteamVerifyRecordDto? Premier,
    IReadOnlyList<SteamVerifyRecordDto>? PerMap);

internal sealed record SteamVerifyLibraryDto(
    int? Total,
    long? TotalPlaytimeMinutes,
    JsonElement FetchedAt,
    string? Error,
    IReadOnlyList<SteamVerifyGameDto>? Games);

internal sealed record SteamVerifyGameDto(
    long? AppId,
    string? Name,
    long? PlaytimeMinutes,
    long? PlaytimeTwoWeeksMinutes,
    JsonElement LastPlayed,
    long? PlaytimeWindowsMinutes,
    long? PlaytimeMacMinutes,
    long? PlaytimeLinuxMinutes);

// JsonSerializerDefaults.Web：camelCase + 大小写不敏感，与站点返回的 JSON 字段一致。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SteamVerifyRequest))]
[JsonSerializable(typeof(SteamVerifyPayload))]
[JsonSerializable(typeof(SteamVerifyErrorDto))]
internal sealed partial class SteamVerifyJsonContext : JsonSerializerContext;