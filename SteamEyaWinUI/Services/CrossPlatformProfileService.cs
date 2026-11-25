using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

internal sealed record CrossPlatformProfileResult(
    bool IsReady,
    JsonObject? Data,
    string? Message,
    string Source);

internal static class CrossPlatformProfileService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly SemaphoreSlim BridgeDiscoveryGate = new(1, 1);
    private static string? _bridgeBaseUrl;
    private static DateTimeOffset _bridgeCheckedAt;

    public static async Task<CrossPlatformProfileResult> QueryFiveEAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        string? domain = null;
        try
        {
            var bridge = await GetBridgeBaseUrlAsync(cancellationToken);
            if (bridge is not null)
            {
                var identity = await PostBridgeAsync(
                    bridge,
                    "/api/identity/resolve",
                    new JsonObject { ["input"] = steamId },
                    cancellationToken);
                domain = GetString(identity, "data", "fiveDomain");
                if (string.IsNullOrWhiteSpace(domain))
                {
                    domain = await ResolveFiveEDomainFromPublicAsync(
                        steamId,
                        cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(domain))
                {
                    var part = await GetBridgeAsync(
                        bridge,
                        $"/api/5e/player/{Uri.EscapeDataString(domain)}/core" +
                        $"?year={DateTime.Now.Year}&refresh=1",
                        cancellationToken);
                    var result = ReadBridgePart(part);
                    if (result.IsReady && result.Data is not null)
                    {
                        return result with { Source = "成分局 · 5E" };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("读取成分局 5E 数据超时，尝试公开接口。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"读取成分局 5E 数据失败，尝试公开接口：{ex.Message}");
        }

        try
        {
            return await QueryFiveEPublicAsync(
                steamId,
                domain,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                "5E 公开接口请求超时。",
                "5E 公开接口");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                $"5E 数据读取失败：{ex.Message}",
                "5E 公开接口");
        }
    }

    public static async Task<CrossPlatformProfileResult> QueryPerfectAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        string? bridgeMessage = null;
        try
        {
            var bridge = await GetBridgeBaseUrlAsync(cancellationToken);
            if (bridge is not null)
            {
                var part = await GetBridgeAsync(
                    bridge,
                    $"/api/pwa/user/{Uri.EscapeDataString(steamId)}/core" +
                    "?size=20&refresh=1",
                    cancellationToken);
                var result = ReadBridgePart(part);
                if (result.IsReady && result.Data is not null)
                {
                    return result with { Source = "成分局 · 完美" };
                }

                bridgeMessage = result.Message;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("读取成分局完美数据超时，尝试公开接口。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"读取成分局完美数据失败，尝试公开接口：{ex.Message}");
        }

        try
        {
            var result = await QueryPerfectPublicAsync(steamId, cancellationToken);
            if (result.IsReady)
            {
                return result with
                {
                    Message = bridgeMessage ?? "当前仅显示完美公开资料；完整赛季战绩需要完美客户端登录态。"
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            bridgeMessage = "完美公开资料请求超时。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bridgeMessage = $"完美公开资料读取失败：{ex.Message}";
        }

        return new CrossPlatformProfileResult(
            false,
            null,
            bridgeMessage ?? "未读取到完美平台数据。",
            "完美世界竞技平台");
    }

    private static CrossPlatformProfileResult ReadBridgePart(JsonObject envelope)
    {
        var part = envelope["data"] as JsonObject;
        if (part is null)
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                "成分局接口没有返回平台数据。",
                "成分局");
        }

        var status = GetString(part, "status");
        var message = GetString(part, "message");
        var data = part?["data"] as JsonObject;
        return status == "ready" && data is not null
            ? new CrossPlatformProfileResult(true, data, null, "成分局")
            : new CrossPlatformProfileResult(false, null, message, "成分局");
    }

    private static async Task<JsonObject> GetBridgeAsync(
        string baseUrl,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            baseUrl + path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("成分局返回了无效 JSON。");
    }

    private static async Task<JsonObject> PostBridgeAsync(
        string baseUrl,
        string path,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        using var response = await HttpClient.PostAsync(
            baseUrl + path,
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("成分局返回了无效 JSON。");
    }

    private static async Task<string?> GetBridgeBaseUrlAsync(
        CancellationToken cancellationToken)
    {
        if (_bridgeBaseUrl is not null &&
            DateTimeOffset.UtcNow - _bridgeCheckedAt < TimeSpan.FromSeconds(30))
        {
            return _bridgeBaseUrl;
        }

        await BridgeDiscoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (_bridgeBaseUrl is not null &&
                DateTimeOffset.UtcNow - _bridgeCheckedAt < TimeSpan.FromSeconds(30))
            {
                return _bridgeBaseUrl;
            }

            var processIds = Process.GetProcessesByName("node")
                .Where(process =>
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        return path is not null &&
                            path.Contains("成分局", StringComparison.OrdinalIgnoreCase) &&
                            path.Contains("runtime", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(process => process.Id)
                .ToHashSet();
            if (processIds.Count == 0)
            {
                ResetBridgeCache();
                return null;
            }

            foreach (var port in await GetListeningPortsAsync(processIds, cancellationToken))
            {
                var candidate = $"http://127.0.0.1:{port}";
                try
                {
                    using var response = await HttpClient.GetAsync(
                        candidate + "/api/capabilities",
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    var root = JsonNode.Parse(text) as JsonObject;
                    if (GetString(root ?? new JsonObject(), "data", "release", "appName") == "成分局")
                    {
                        _bridgeBaseUrl = candidate;
                        _bridgeCheckedAt = DateTimeOffset.UtcNow;
                        return candidate;
                    }
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException &&
                    ex is not HttpRequestException)
                {
                    AppLog.Warn($"检测成分局本地接口失败：{ex.Message}");
                }
                catch (HttpRequestException)
                {
                }
            }

            ResetBridgeCache();
            return null;
        }
        finally
        {
            BridgeDiscoveryGate.Release();
        }
    }

    private static async Task<IReadOnlyList<int>> GetListeningPortsAsync(
        IReadOnlySet<int> processIds,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        process.Start();
        var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var ports = new HashSet<int>();
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(
                line.Trim(),
                @"^TCP\s+(?<endpoint>\S+)\s+\S+\s+LISTENING\s+(?<pid>\d+)$",
                RegexOptions.IgnoreCase);
            if (!match.Success ||
                !processIds.Contains(int.Parse(
                    match.Groups["pid"].Value,
                    CultureInfo.InvariantCulture)))
            {
                continue;
            }

            var endpoint = match.Groups["endpoint"].Value;
            var separator = endpoint.LastIndexOf(':');
            if (separator < 0 ||
                !int.TryParse(
                    endpoint[(separator + 1)..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var port))
            {
                continue;
            }

            var host = endpoint[..separator].Trim('[', ']');
            if (host is "127.0.0.1" or "0.0.0.0" or "::1" or "::")
            {
                ports.Add(port);
            }
        }

        return ports.ToArray();
    }

    private static void ResetBridgeCache()
    {
        _bridgeBaseUrl = null;
        _bridgeCheckedAt = DateTimeOffset.MinValue;
    }

    private static async Task<string?> ResolveFiveEDomainFromPublicAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        var identityText = await HttpClient.GetStringAsync(
            $"https://api-client-arena.5eplay.com/api/user/steam_username/{Uri.EscapeDataString(steamId)}",
            cancellationToken);
        var identity = JsonNode.Parse(identityText) as JsonObject;
        var username = GetString(identity ?? new JsonObject(), "data", "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var searchText = await HttpClient.GetStringAsync(
            $"https://arena.5eplay.com/api/search/player/1/16?keywords={Uri.EscapeDataString(username)}",
            cancellationToken);
        var search = JsonNode.Parse(searchText) as JsonObject;
        var users = GetNode(search ?? new JsonObject(), "data", "user", "list") as JsonArray;
        return users?
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
                string.Equals(
                    GetString(item, "username"),
                    username,
                    StringComparison.Ordinal))
            ?["domain"]?
            .GetValue<string>();
    }

    private static async Task<CrossPlatformProfileResult> QueryFiveEPublicAsync(
        string steamId,
        string? domain,
        CancellationToken cancellationToken)
    {
        domain ??= await ResolveFiveEDomainFromPublicAsync(steamId, cancellationToken);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                "未找到唯一对应的 5E 绑定账号。",
                "5E 公开接口");
        }

        using var transferContent = new StringContent(
            new JsonObject
            {
                ["trans"] = new JsonObject { ["domain"] = domain }
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        using var transferResponse = await HttpClient.PostAsync(
            "https://gate.5eplay.com/userinterface/http/v1/userinterface/idTransfer",
            transferContent,
            cancellationToken);
        transferResponse.EnsureSuccessStatusCode();
        var transfer = JsonNode.Parse(
            await transferResponse.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        var uuid = GetString(transfer ?? new JsonObject(), "data", "uuid");
        if (string.IsNullOrWhiteSpace(uuid))
        {
            throw new InvalidDataException("5E 账号映射失败。");
        }

        var homeText = await HttpClient.GetStringAsync(
            $"https://gate.5eplay.com/crane/http/api/data/v3/player/home?uuid={Uri.EscapeDataString(uuid)}",
            cancellationToken);
        var home = JsonNode.Parse(homeText) as JsonObject;
        var data = home?["data"] as JsonObject
            ?? throw new InvalidDataException("5E 玩家数据为空。");
        var career = data["career"] as JsonObject ?? new JsonObject();
        var season = data["season_data"] as JsonObject ?? new JsonObject();
        var modes = GetNode(data, "elo_info", "modes") as JsonObject;
        var currentMode = modes?
            .Select(pair => pair.Value as JsonObject)
            .FirstOrDefault(mode => mode is not null) ?? new JsonObject();
        var matches = await QueryFiveERecentMatchesAsync(uuid, cancellationToken);
        var elo = GetMetricNumber(currentMode["elo"]) ??
            GetMetricNumber(career["elo"]);
        var levelId = GetMetricNumber(currentMode["level_id"]) ??
            GetMetricNumber(career["level_id"]);
        var result = new JsonObject
        {
            ["username"] = GetString(data, "uinfo", "username") ?? domain,
            ["domain"] = domain,
            ["avatarUrl"] = GetString(data, "uinfo", "avatar") is { } avatar
                ? avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? avatar
                    : $"https://oss-arena.5eplay.com/{avatar.TrimStart('/')}"
                : null,
            ["elo"] = elo,
            ["rank"] = GetMetricNumber(currentMode["rank"]) ??
                GetMetricNumber(career["rank"]),
            ["matchTypeName"] = "优先排位",
            ["tier"] = new JsonObject
            {
                ["label"] = FiveELevelLabel(levelId),
                ["name"] = string.Empty
            },
            ["stats"] = new JsonObject
            {
                ["matches"] = GetMetricNumber(season["match_total"]) ??
                    GetMetricNumber(career["match_total"]),
                ["wins"] = GetMetricNumber(season["match_win"]) ??
                    GetMetricNumber(career["match_win"]),
                ["losses"] = GetMetricNumber(season["match_loss"]) ??
                    GetMetricNumber(career["match_loss"]),
                ["winRate"] = Ratio(
                    GetMetricNumber(season["match_win"]) ?? GetMetricNumber(career["match_win"]),
                    GetMetricNumber(season["match_total"]) ?? GetMetricNumber(career["match_total"])),
                ["rating"] = GetMetricNumber(season["avg_rating"]),
                ["rws"] = GetMetricNumber(season["avg_rws"]),
                ["adr"] = GetMetricNumber(season["avg_adr"]),
                ["kast"] = GetMetricNumber(season["kast"]),
                ["kd"] = Ratio(
                    GetMetricNumber(season["kill"]),
                    GetMetricNumber(season["death"])),
                ["hsRate"] = GetMetricNumber(season["headshot"]) is { } headshots &&
                    GetMetricNumber(season["kill"]) is { } kills && kills > 0
                        ? headshots / kills
                        : null,
                ["mvp"] = GetMetricNumber(season["mvp"]),
                ["rounds"] = GetMetricNumber(season["rounds"])
            },
            ["matches"] = matches,
            ["analysis"] = new JsonObject
            {
                ["tier"] = FiveELevelLabel(levelId),
                ["tags"] = new JsonArray()
            }
        };
        return new CrossPlatformProfileResult(
            true,
            result,
            "公开接口数据",
            "5E 公开接口");
    }

    private static async Task<JsonArray> QueryFiveERecentMatchesAsync(
        string uuid,
        CancellationToken cancellationToken)
    {
        var text = await HttpClient.GetStringAsync(
            "https://gate.5eplay.com/crane/http/api/data/match/list" +
            $"?match_type=-1&page=1&data=0&start_time=0&end_time=0&uuid={Uri.EscapeDataString(uuid)}&limit=10&cs_type=0",
            cancellationToken);
        var root = JsonNode.Parse(text) as JsonObject;
        if (GetNode(root ?? new JsonObject(), "data", "data") is not JsonArray source)
        {
            return [];
        }

        var result = new JsonArray();
        foreach (var match in source.OfType<JsonObject>().Take(10))
        {
            var score = Regex.Match(
                GetString(match, "score") ?? string.Empty,
                @"(\d+)\s*:\s*(\d+)");
            result.Add((JsonNode)new JsonObject
            {
                ["matchCode"] = GetString(match, "match_id") ?? GetString(match, "match_code"),
                ["startTime"] = GetString(match, "start_time"),
                ["map"] = GetString(match, "map"),
                ["mapName"] = GetString(match, "map_name"),
                ["score"] = GetString(match, "score"),
                ["isWin"] = GetBool(match, "is_win"),
                ["isTie"] = score.Success &&
                    score.Groups[1].Value == score.Groups[2].Value,
                ["kills"] = GetMetricNumber(match["kill"]),
                ["deaths"] = GetMetricNumber(match["death"]),
                ["assists"] = GetMetricNumber(match["assist"]),
                ["rating"] = GetMetricNumber(match["rating"]),
                ["rws"] = GetMetricNumber(match["rws"]),
                ["adr"] = GetMetricNumber(match["adr"]),
                ["eloChange"] = GetMetricNumber(match["elo_change"])
            });
        }

        return result;
    }

    private static async Task<CrossPlatformProfileResult> QueryPerfectPublicAsync(
        string steamId,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            new JsonObject
            {
                ["keyword"] = steamId,
                ["page"] = 1,
                ["pageSize"] = 1,
                ["searchType"] = 1
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        using var response = await HttpClient.PostAsync(
            "https://appengine.wmpvp.com/steamcn/app/search/user",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        if (root?["result"] is not JsonArray users)
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                "完美公开资料接口没有返回数据。",
                "完美公开接口");
        }

        var player = users
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
                string.Equals(GetString(item, "steamId"), steamId, StringComparison.Ordinal));
        if (player is null)
        {
            return new CrossPlatformProfileResult(
                false,
                null,
                "未找到该 SteamID 对应的完美平台账号。",
                "完美公开接口");
        }

        var data = new JsonObject
        {
            ["steamId"] = steamId,
            ["nickname"] = GetString(player, "pvpNickName"),
            ["avatar"] = GetString(player, "pvpAvatar"),
            ["latestScore"] = GetMetricNumber(player["pvpScore"]),
            ["card"] = new JsonObject
            {
                ["nickname"] = GetString(player, "pvpNickName"),
                ["avatar"] = GetString(player, "pvpAvatar"),
                ["score"] = GetMetricNumber(player["pvpScore"])
            },
            ["stats"] = new JsonObject(),
            ["matches"] = new JsonArray(),
            ["radar"] = new JsonArray(),
            ["maps"] = new JsonArray(),
            ["weapons"] = new JsonArray(),
            ["sourceMode"] = "public"
        };
        return new CrossPlatformProfileResult(
            true,
            data,
            "当前仅显示完美公开资料；完整赛季战绩需要完美客户端登录态。",
            "完美公开接口");
    }

    private static string FiveELevelLabel(double? levelId) =>
        levelId switch
        {
            >= 55 => "SSS",
            >= 54 => "SS",
            >= 51 => "S",
            >= 50 => "精英A+",
            >= 49 => "A+",
            >= 48 => "A",
            >= 47 => "精英B+",
            >= 46 => "B+",
            >= 45 => "B",
            >= 44 => "精英C+",
            >= 43 => "C+",
            >= 42 => "C",
            >= 41 => "D",
            _ => "未定级"
        };

    private static double? Ratio(double? numerator, double? denominator) =>
        numerator is not null && denominator is > 0
            ? numerator / denominator
            : null;

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
            return node?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static double? GetMetricNumber(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return double.TryParse(
                node.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null;
        }
    }

    private static bool? GetBool(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(node.ToString(), out var parsed)
                ? parsed
                : null;
        }
    }
}
