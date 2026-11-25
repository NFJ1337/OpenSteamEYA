using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 直接读取 Leetify 公开资料接口，避免 CS2Tracker 聚合缓存导致近期评分滞后。
/// </summary>
internal static class LeetifyFreshProfileService
{
    private const string ApiRoot = "https://api.cs-prod.leetify.com";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<JsonObject?> FetchRecentRatingsAsync(
        string steamId64,
        CancellationToken cancellationToken = default)
    {
        if (steamId64.Length != 17 ||
            !steamId64.All(char.IsAsciiDigit))
        {
            return null;
        }

        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var requestUri =
            $"{ApiRoot}/api/profile/{Uri.EscapeDataString(steamId64)}/recent-games/5v5" +
            $"?_={cacheBuster}&nonce={Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.Referrer = new Uri($"https://leetify.com/public/profile/{steamId64}");
        request.Headers.TryAddWithoutValidation("Origin", "https://leetify.com");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var payload = JsonNode.Parse(text) as JsonObject;
        if (payload is null)
        {
            return null;
        }

        var categories = payload["leetifyRatingCategories"] as JsonObject;
        var ratings = new JsonObject();
        AddNumber(ratings, "gamesPlayed", ReadNumber(payload["matchesPlayed"]));
        AddNumber(ratings, "aim", ReadNumber(payload["aimRating"]));
        AddNumber(
            ratings,
            "positioning",
            ReadNumber(payload["positioningRating"]) ?? ReadNumber(payload["positioning"]));
        AddNumber(ratings, "utility", ReadNumber(payload["utilityRating"]));
        AddNumber(ratings, "opening", ReadNumber(categories?["Opening Duels"]));
        AddNumber(ratings, "clutch", ReadNumber(categories?["Clutches"]));
        AddNumber(ratings, "leetify", ReadNumber(payload["leetifyRating"]));
        return ratings.Count == 0 ? null : ratings;
    }

    public static void ApplyRecentRatings(JsonObject profile, JsonObject freshRatings)
    {
        if (profile["leetify_extra"] is not JsonObject leetifyExtra)
        {
            leetifyExtra = new JsonObject();
            profile["leetify_extra"] = leetifyExtra;
        }

        if (leetifyExtra["recentGameRatings"] is not JsonObject currentRatings)
        {
            currentRatings = new JsonObject();
            leetifyExtra["recentGameRatings"] = currentRatings;
        }

        foreach (var pair in freshRatings)
        {
            currentRatings[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static void AddNumber(JsonObject target, string key, double? value)
    {
        if (value is not null)
        {
            target[key] = value.Value;
        }
    }

    private static double? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value ||
            !value.TryGetValue<double>(out var number) ||
            !double.IsFinite(number))
        {
            return null;
        }

        return number;
    }
}
