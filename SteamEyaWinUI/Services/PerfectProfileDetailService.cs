using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「完美」页使用的独立资料补全服务：公共 5E、国服、库存与 SteamRepCN 数据并行读取。
/// 任一来源失败都只降级对应字段，不影响整轮完美查询。
/// </summary>
internal static class PerfectProfileDetailService
{
    private const string SearchApiUrl =
        "https://appengine.wmpvp.com/steamcn/app/search/user";
    private const string AccountsApiUrl =
        "https://appengine.wmpvp.com/steamcn/community/user/getNewUserList";
    private const string ForbidApiUrl =
        "https://api.wmpvp.com/api/csgo/home/user/forbid";
    private const string PvpStatsApiUrl =
        "https://api.wmpvp.com/api/csgo/home/pvp/detailStats";
    private const string MatchesApiUrl =
        "https://api.wmpvp.com/api/csgo/home/match/list";
    private const string OfficialStatsApiUrl =
        "https://api.wmpvp.com/api/csgo/home/official/detailStats";
    private const string CsInfoApiUrl =
        "https://gwapi.pwesports.cn/appdatacenter/csgo/official/fall/userCsgoInfo";
    private const string InventoryApiUrl =
        "https://gwapi.pwesports.cn/appdecoration/steamcn/csgo/decoration/getSteamInventoryPreview";
    private const string SteamRepProfileUrl = "https://steamrepcn.com/profiles/";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly Regex SteamRepNameRegex = new(
        @"<p\s+class=""mb-2\s+truncate[^""]*""\s+title=""(?<value>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SteamRepAvatarRegex = new(
        @"<img\s+class=""[^""]*size-32[^""]*""\s+src=""(?<value>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SteamRepFieldRegex = new(
        @"<p\s+class=""ml-2"">(?<key>[^<]+)</p>\s*" +
        @"<p\s+class=""ml-2\s+text-(?:gray-400|red)\s+text-sm"">(?<value>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly SemaphoreSlim AccountEnrichmentGate = new(3, 3);

    public static async Task EnrichAsync(
        JsonObject data,
        string steamId,
        CancellationToken cancellationToken)
    {
        var primaryLookup = await TryLookupPlayerAsync(steamId, cancellationToken);
        var targetUserId = GetMetricNumber(primaryLookup?["userId"]);
        var token = PerfectFiveESessionService.LoadToken();
        var integrationUserId = PerfectFiveESessionService.LoadUserId();
        var integrationSteamId = PerfectFiveESessionService.LoadOwnerSteamId();

        JsonArray? boundAccounts = null;
        if (!string.IsNullOrWhiteSpace(token) &&
            !string.IsNullOrWhiteSpace(integrationUserId) &&
            targetUserId is not null)
        {
            boundAccounts = await TryGetBoundAccountsAsync(
                token,
                integrationUserId,
                targetUserId.Value.ToString("F0", CultureInfo.InvariantCulture),
                cancellationToken);
        }

        var accountSeeds = boundAccounts?
            .OfType<JsonObject>()
            .ToArray() ?? [];
        if (accountSeeds.Length == 0)
        {
            accountSeeds =
            [
                new JsonObject
                {
                    ["steamId"] = steamId,
                    ["pvpNickName"] = GetString(primaryLookup ?? new JsonObject(), "pvpNickName"),
                    ["mainAccount"] = true
                }
            ];
        }

        var apiOwnerSteamId = FirstNonEmpty(
            integrationSteamId,
            steamId) ?? steamId;
        var enriched = await Task.WhenAll(accountSeeds.Select(async seed =>
        {
            await AccountEnrichmentGate.WaitAsync(cancellationToken);
            try
            {
                var accountSteamId =
                    FirstNonEmpty(GetString(seed, "steamId"), steamId) ??
                    steamId;
                var lookup = string.Equals(accountSteamId, steamId, StringComparison.Ordinal)
                    ? primaryLookup
                    : await TryLookupPlayerAsync(accountSteamId, cancellationToken);
                return await EnrichBoundAccountAsync(
                    seed,
                    accountSteamId,
                    apiOwnerSteamId,
                    token,
                    lookup,
                    cancellationToken);
            }
            finally
            {
                AccountEnrichmentGate.Release();
            }
        }));

        var primaryAccount = enriched.FirstOrDefault(account =>
            string.Equals(GetString(account, "steamId"), steamId, StringComparison.Ordinal)) ??
            enriched.First();
        var perfectName = FirstNonEmpty(
            GetString(primaryAccount, "perfectName"),
            GetString(primaryLookup ?? new JsonObject(), "pvpNickName"));
        var appName = FirstNonEmpty(
            GetString(primaryAccount, "appName"),
            GetString(primaryLookup ?? new JsonObject(), "appNickName"),
            perfectName);

        data["steamId"] = steamId;
        data["userId"] = targetUserId;
        data["perfectNickname"] = perfectName;
        data["appNickname"] = appName;
        data["latestScore"] = GetMetricNumber(primaryAccount["maxPvpScore"]);
        data["bestPvpScore"] = enriched
            .Select(account => GetMetricNumber(account["maxPvpScore"]))
            .OfType<double>()
            .DefaultIfEmpty(GetMetricNumber(primaryLookup?["pvpScore"]) ?? 0)
            .Max();
        data["avatar"] = FirstNonEmpty(
            GetString(primaryLookup ?? new JsonObject(), "pvpAvatar"),
            GetString(primaryAccount, "avatar"));
        data["lookup"] = primaryLookup?.DeepClone() ?? new JsonObject();
        data["steamProfile"] = primaryAccount["steamProfile"]?.DeepClone() ?? new JsonObject();
        data["officialStats"] = primaryAccount["officialStats"]?.DeepClone() ?? new JsonObject();
        data["csInfo"] = primaryAccount["csInfo"]?.DeepClone() ?? new JsonObject();
        data["inventory"] = primaryAccount["inventory"]?.DeepClone() ?? new JsonObject();
        data["stats"] = primaryAccount["pvpStats"]?.DeepClone() ?? new JsonObject();
        data["boundAccounts"] = new JsonArray(enriched
            .Select(account => (JsonNode)account)
            .ToArray());
    }

    private static async Task<JsonObject> EnrichBoundAccountAsync(
        JsonObject seed,
        string steamId,
        string apiOwnerSteamId,
        string? token,
        JsonObject? lookup,
        CancellationToken cancellationToken)
    {
        var officialTask = TryGetOfficialStatsAsync(steamId, cancellationToken);
        var csInfoTask = TryGetCsInfoAsync(steamId, cancellationToken);
        var inventoryTask = TryGetInventoryAsync(steamId, cancellationToken);
        var steamRepTask = TryGetSteamRepProfileAsync(steamId, cancellationToken);
        var pvpStatsTask = !string.IsNullOrWhiteSpace(token)
            ? TryGetPvpStatsAsync(token, steamId, apiOwnerSteamId, cancellationToken)
            : Task.FromResult<JsonObject?>(null);
        var forbidTask = !string.IsNullOrWhiteSpace(token)
            ? TryGetForbidStatusAsync(token, steamId, apiOwnerSteamId, cancellationToken)
            : Task.FromResult<string?>(null);
        var matchesTask = !string.IsNullOrWhiteSpace(token)
            ? TryGetRecentMatchesAsync(token, steamId, apiOwnerSteamId, cancellationToken)
            : Task.FromResult<JsonArray?>(null);

        var official = await officialTask;
        var csInfo = await csInfoTask;
        var inventory = await inventoryTask;
        var steamProfile = await steamRepTask;
        var pvpStats = await pvpStatsTask ?? new JsonObject();
        var forbidStatus = await forbidTask;
        var matches = await matchesTask ?? [];

        var steamFields = steamProfile?["fields"] as JsonObject ?? new JsonObject();
        var maxPvpScore = GetMaximumPvpScore(matches) ??
            GetMetricNumber(lookup?["pvpScore"]) ??
            GetMetricNumber(pvpStats["pvpScore"]);
        var (isBlack, blackDetails) = DetectBlackAccount(matches);
        return new JsonObject
        {
            ["steamId"] = steamId,
            ["steamName"] = FirstNonEmpty(
                official is null ? null : GetString(official, "nickName"),
                GetString(steamProfile ?? new JsonObject(), "steamName"),
                GetString(seed, "nickName"),
                GetString(seed, "pvpNickName"),
                GetString(lookup ?? new JsonObject(), "steamNickName"),
                steamId),
            ["perfectName"] = FirstNonEmpty(
                GetString(lookup ?? new JsonObject(), "pvpNickName"),
                GetString(seed, "pvpNickName")),
            ["appName"] = FirstNonEmpty(
                GetString(lookup ?? new JsonObject(), "appNickName"),
                GetString(seed, "appNickName")),
            ["avatar"] = FirstNonEmpty(
                GetString(lookup ?? new JsonObject(), "pvpAvatar"),
                GetString(seed, "avatar")),
            ["mainAccount"] = GetBool(seed, "mainAccount") == true,
            ["maxPvpScore"] = maxPvpScore,
            ["steamProfile"] = steamProfile?.DeepClone() ?? new JsonObject(),
            ["officialStats"] = official?.DeepClone() ?? new JsonObject(),
            ["csInfo"] = csInfo?.DeepClone() ?? new JsonObject(),
            ["inventory"] = inventory?.DeepClone() ?? new JsonObject(),
            ["pvpStats"] = pvpStats.DeepClone(),
            ["forbidStatus"] = forbidStatus ?? BuildForbidStatus(steamFields),
            ["isBlack"] = isBlack,
            ["blackDetails"] = blackDetails
        };
    }

    private static async Task<JsonArray?> TryGetBoundAccountsAsync(
        string token,
        string integrationUserId,
        string targetUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var query =
                $"?appId=2&token={Uri.EscapeDataString(token)}" +
                $"&userId={Uri.EscapeDataString(integrationUserId)}" +
                $"&toUserId={Uri.EscapeDataString(targetUserId)}&needDouyu=false";
            var root = await GetJsonAsync(
                AccountsApiUrl + query,
                cancellationToken,
                token);
            return (root?["result"] as JsonObject)?["userList"] as JsonArray;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"5E 绑定账号读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryGetPvpStatsAsync(
        string token,
        string steamId,
        string ownerSteamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await PostJsonAsync(
                PvpStatsApiUrl,
                new JsonObject
                {
                    ["toSteamId"] = steamId,
                    ["csgoSeasonId"] = "S21",
                    ["mySteamId"] = ownerSteamId,
                    ["accessToken"] = token,
                    ["pvpType"] = -1,
                    ["page"] = 1,
                    ["pageSize"] = 3,
                    ["dataSource"] = 3
                },
                cancellationToken,
                token);
            return root?["statusCode"]?.GetValue<int>() == 0
                ? root["data"] as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美平台数据读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<string?> TryGetForbidStatusAsync(
        string token,
        string steamId,
        string ownerSteamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await PostJsonAsync(
                ForbidApiUrl,
                new JsonObject
                {
                    ["toSteamId"] = steamId,
                    ["mySteamId"] = ownerSteamId
                },
                cancellationToken,
                token);
            if (root?["statusCode"]?.GetValue<int>() != 0)
            {
                return null;
            }

            var info = root["data"] as JsonObject;
            var banType = info is null
                ? null
                : GetMetricNumber(info["pwaBanType"]) ??
                  GetMetricNumber(info["banType"]);
            if (info is null || banType is null or <= 0)
            {
                return "账号状态正常";
            }

            var parts = new List<string>
            {
                "存在封禁记录",
                $"原因：{FirstNonEmpty(GetString(info, "desc"), "未说明封禁原因")}"
            };
            var createTime = FormatTimestamp(info["createTime"]);
            var expireTime = FormatTimestamp(info["expireTime"]);
            if (!string.IsNullOrWhiteSpace(createTime))
            {
                parts.Add($"开始时间：{createTime}");
            }
            if (!string.IsNullOrWhiteSpace(expireTime))
            {
                parts.Add($"到期时间：{expireTime}");
            }
            return string.Join('\n', parts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美封禁状态读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonArray?> TryGetRecentMatchesAsync(
        string token,
        string steamId,
        string ownerSteamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await PostJsonAsync(
                MatchesApiUrl,
                new JsonObject
                {
                    ["mySteamId"] = ownerSteamId,
                    ["toSteamId"] = steamId,
                    ["csgoSeasonId"] = "recent",
                    ["pvpType"] = -1,
                    ["page"] = 1,
                    ["pageSize"] = 9999,
                    ["dataSource"] = 3
                },
                cancellationToken,
                token);
            return root?["statusCode"]?.GetValue<int>() == 0
                ? (root["data"] as JsonObject)?["matchList"] as JsonArray
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美对局数据读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryLookupPlayerAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await PostJsonAsync(
                SearchApiUrl,
                new JsonObject
                {
                    ["keyword"] = steamId,
                    ["page"] = 1
                },
                cancellationToken);
            return (root?["result"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(item =>
                    string.Equals(
                        GetString(item, "steamId"),
                        steamId,
                        StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("完美资料搜索超时。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美资料搜索失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryGetOfficialStatsAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await PostJsonAsync(
                OfficialStatsApiUrl,
                new JsonObject
                {
                    ["toSteamId"] = steamId,
                    ["mySteamId"] = string.Empty,
                    ["accessToken"] = string.Empty
                },
                cancellationToken);
            return root?["data"] as JsonObject;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("完美国服统计读取超时。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美国服统计读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryGetCsInfoAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri =
                $"{CsInfoApiUrl}?steamId={Uri.EscapeDataString(steamId)}" +
                "&token=&darkType=0";
            var root = await GetJsonAsync(uri, cancellationToken);
            return root?["result"] as JsonObject;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("完美国服等级读取超时。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"完美国服等级读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryGetInventoryAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri =
                $"{InventoryApiUrl}?steamId={Uri.EscapeDataString(steamId)}" +
                "&previewSize=10&darkType=0";
            var root = await GetJsonAsync(uri, cancellationToken);
            return root?["result"] as JsonObject;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("CS2 库存读取超时。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"CS2 库存读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> TryGetSteamRepProfileAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                SteamRepProfileUrl + Uri.EscapeDataString(steamId));
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            request.Headers.TryAddWithoutValidation("Referer", "https://steamrepcn.com/top-rated");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/132.0.0.0 Safari/537.36");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var fields = new JsonObject();
            foreach (Match match in SteamRepFieldRegex.Matches(html))
            {
                var key = CleanHtmlText(match.Groups["key"].Value);
                var value = CleanHtmlText(match.Groups["value"].Value);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    fields[key] = value;
                }
            }

            var steamName = CleanHtmlText(
                SteamRepNameRegex.Match(html).Groups["value"].Value);
            var avatar = WebUtility.HtmlDecode(
                SteamRepAvatarRegex.Match(html).Groups["value"].Value.Trim());
            return new JsonObject
            {
                ["steamName"] = string.IsNullOrWhiteSpace(steamName) ? null : steamName,
                ["avatar"] = string.IsNullOrWhiteSpace(avatar) ? null : avatar,
                ["fields"] = fields
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("SteamRepCN 资料读取超时。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"SteamRepCN 资料读取失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonObject?> PostJsonAsync(
        string url,
        JsonObject body,
        CancellationToken cancellationToken,
        string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        AddWmpvpHeaders(request, token);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
    }

    private static async Task<JsonObject?> GetJsonAsync(
        string url,
        CancellationToken cancellationToken,
        string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        AddWmpvpHeaders(request, token);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
    }

    private static void AddWmpvpHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.TryAddWithoutValidation("appversion", "3.6.2.189");
        request.Headers.TryAddWithoutValidation(
            "device",
            "KGPSz1754474645YpsvmPeeMtb");
        request.Headers.TryAddWithoutValidation("gameType", "2");
        request.Headers.TryAddWithoutValidation("gameTypeStr", "2");
        request.Headers.TryAddWithoutValidation("platform", "android");
        request.Headers.TryAddWithoutValidation("appTheme", "0");
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.11.0");
        request.Headers.TryAddWithoutValidation("Connection", "Keep-Alive");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("token", token);
        }
    }

    private static double? GetMaximumPvpScore(JsonArray matches)
    {
        var scores = matches
            .OfType<JsonObject>()
            .Select(match =>
                GetMetricNumber(match["pvpScore"]) ??
                GetMetricNumber(match["ladderScore"]))
            .OfType<double>()
            .ToArray();
        return scores.Length > 0 ? scores.Max() : null;
    }

    private static (bool IsBlack, string Details) DetectBlackAccount(JsonArray matches)
    {
        if (matches.Count == 0)
        {
            return (false, "无比赛数据，无法检测。");
        }

        var suspiciousDays = new List<string>();
        foreach (var match in matches.OfType<JsonObject>())
        {
            var kills = GetMetricNumber(match["kill"]) ??
                GetMetricNumber(match["kills"]);
            var assists = GetMetricNumber(match["assist"]) ??
                GetMetricNumber(match["assists"]);
            var rating = GetMetricNumber(match["pwRating"]) ??
                GetMetricNumber(match["rating"]);
            if (rating is null or > 0.7 ||
                assists is null or > 1 ||
                kills is null or >= 3)
            {
                continue;
            }

            var dateText = GetString(match, "startTime") ??
                GetString(match, "start_time") ??
                GetString(match, "date");
            var day = dateText?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(day))
            {
                suspiciousDays.Add(day);
            }
        }

        if (suspiciousDays.Count < 4)
        {
            return (false, "未检测到内部刷分行为。");
        }

        var frequentDay = suspiciousDays
            .GroupBy(day => day, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault(group => group.Count() >= 3);
        return frequentDay is null
            ? (false, "未检测到内部刷分行为。")
            : (true, $"检测到内部小号刷分行为，刷分日期：{frequentDay.Key}");
    }

    private static string BuildForbidStatus(JsonObject steamFields)
    {
        var values = steamFields
            .Select(pair => pair.Value?.ToString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return values.Any(value =>
            value.Contains("封禁", StringComparison.Ordinal) ||
            value.Contains("受限", StringComparison.Ordinal) ||
            value.Contains("异常", StringComparison.Ordinal))
            ? "公开资料存在封禁或异常记录"
            : "账号状态正常";
    }

    private static string? FormatTimestamp(JsonNode? node)
    {
        var raw = node?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numeric) &&
            numeric > 0)
        {
            try
            {
                var timestamp = numeric >= 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)numeric)
                    : DateTimeOffset.FromUnixTimeSeconds((long)numeric);
                return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : raw;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string CleanHtmlText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
        return Regex.Replace(decoded, "<[^>]+>", string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static JsonNode? GetNode(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var part in path)
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            current = obj[part];
        }

        return current;
    }

    private static string? GetString(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        try
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }
        catch
        {
        }

        return node?.ToString();
    }

    private static double? GetMetricNumber(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            if (node is JsonValue value &&
                value.TryGetValue<double>(out var number) &&
                double.IsFinite(number))
            {
                return number;
            }
        }
        catch
        {
        }

        return double.TryParse(
            node.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetBool(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        if (node is not JsonValue value)
        {
            return null;
        }

        try
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
        }
        catch
        {
        }

        return bool.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }
}
