using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage : Page
{
    private const string StatsRootUrl = Cs2TrackerWebSession.HomeUrl;
    private const string RequestKind = "cs2tracker-fetch";
    private const double TrackerLandingMinimumHeight = 430;
    private const double TrackerPageVerticalPadding = 40;
    private const double TrackerLandingSpacingHeight = 28;

    private bool _requestBrowserInitialized;
    private TaskCompletionSource<bool>? _requestBrowserReady;
    private readonly Dictionary<string, TaskCompletionSource<WebFetchResult>> _pendingFetches =
        new(StringComparer.Ordinal);
    private bool _querying;
    private int _trackerViewGeneration;
    private uint? _trackerPointerId;
    private double _trackerPointerStartX;
    private double _trackerPointerStartY;
    private double _trackerPointerScrollOffset;
    private DateTimeOffset _suppressTrackerBringIntoViewUntil;

    public Cs2TrackerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        WebViewDataHost.DataMoveFinished += OnWebViewDataMoveFinished;
        TrackerContentPanel.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(TrackerContentPanel_PointerPressed),
            handledEventsToo: true);
        TrackerContentPanel.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(TrackerContentPanel_PointerReleased),
            handledEventsToo: true);
        TrackerContentPanel.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(TrackerContentPanel_PointerCanceled),
            handledEventsToo: true);
        TrackerContentPanel.AddHandler(
            UIElement.BringIntoViewRequestedEvent,
            new Windows.Foundation.TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>(
                TrackerContentPanel_BringIntoViewRequested),
            handledEventsToo: true);
    }

    private sealed record WebFetchResult(
        int Status,
        string ContentType,
        string Text,
        string? Error);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTrackerLandingHeight(TrackerScrollViewer.ActualHeight);
        try
        {
            await EnsureRequestBrowserAsync();
            QueryStatusText.Text = "已连接 CS2Tracker，可以输入 SteamID64 查询。";
        }
        catch (Exception ex)
        {
            ShowError($"CS2Tracker 初始化失败：{ex.Message}");
        }
    }

    private void TrackerScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTrackerLandingHeight(e.NewSize.Height);
    }

    private void UpdateTrackerLandingHeight(double viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return;
        }

        var queryHeight = TrackerQueryCard.ActualHeight > 0
            ? TrackerQueryCard.ActualHeight
            : TrackerQueryCard.Height;
        var examplesHeight = TrackerExamplesRow.ActualHeight > 0
            ? TrackerExamplesRow.ActualHeight
            : 30;
        var occupiedHeight =
            TrackerPageVerticalPadding + queryHeight + examplesHeight + TrackerLandingSpacingHeight;

        TrackerLandingHero.Height = Math.Max(
            TrackerLandingMinimumHeight,
            viewportHeight - occupiedHeight);
    }

    private async void CheckStatsButton_Click(object sender, RoutedEventArgs e) =>
        await RunQueryAsync();

    private async void TrackerExampleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string steamId })
        {
            return;
        }

        TrackerIdBox.Text = steamId;
        await RunQueryAsync();
    }

    private void TrackerIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(TrackerIdBox.Text))
        {
            ResetTrackerToLanding();
            return;
        }

        ClearTrackerInputError();
    }

    private async void TrackerIdBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunQueryAsync();
    }

    private void ResetTrackerToLanding()
    {
        _trackerViewGeneration++;
        _querying = false;
        CheckStatsButton.IsEnabled = true;
        QueryProgress.IsActive = false;
        QueryProgress.Visibility = Visibility.Collapsed;
        QueryStatusText.Text = _requestBrowserInitialized
            ? "已连接 CS2Tracker，可以输入 SteamID64 查询。"
            : "正在连接 CS2Tracker…";
        ClearTrackerInputError();
        ErrorCard.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Collapsed;
        TrackerModulesPanel.Visibility = Visibility.Collapsed;
        TrackerLandingPanel.Visibility = Visibility.Visible;
        TrackerScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void TrackerContentPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TrackerContentPanel);
        _trackerPointerId = point.PointerId;
        _trackerPointerStartX = point.Position.X;
        _trackerPointerStartY = point.Position.Y;
        _trackerPointerScrollOffset = TrackerScrollViewer.VerticalOffset;
        if (_trackerPointerScrollOffset > 1)
        {
            // 点击文字等非交互内容时，焦点管理器会请求把首项重新带回视口。
            _suppressTrackerBringIntoViewUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
        }
    }

    private void TrackerContentPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TrackerContentPanel);
        if (_trackerPointerId != point.PointerId)
        {
            return;
        }

        var moved = Math.Abs(point.Position.X - _trackerPointerStartX) > 4 ||
            Math.Abs(point.Position.Y - _trackerPointerStartY) > 4;
        var restoreOffset = _trackerPointerScrollOffset;
        _trackerPointerId = null;
        if (moved || restoreOffset <= 1)
        {
            _suppressTrackerBringIntoViewUntil = DateTimeOffset.MinValue;
            return;
        }

        // 单击文字等非交互区域时，WinUI 可能把焦点送回首项并触发 BringIntoView。
        // 等本次点击和焦点处理结束后，再恢复用户刚才的阅读位置。
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (TrackerScrollViewer.VerticalOffset < restoreOffset - 1)
                {
                    TrackerScrollViewer.ChangeView(
                        null,
                        restoreOffset,
                        null,
                        disableAnimation: true);
                }
            });
    }

    private void TrackerContentPanel_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _trackerPointerId = null;
        _suppressTrackerBringIntoViewUntil = DateTimeOffset.MinValue;
    }

    private void TrackerContentPanel_BringIntoViewRequested(
        UIElement sender,
        BringIntoViewRequestedEventArgs args)
    {
        if (DateTimeOffset.UtcNow <= _suppressTrackerBringIntoViewUntil)
        {
            args.Handled = true;
        }
    }

    private async Task RunQueryAsync()
    {
        if (_querying)
        {
            return;
        }

        var input = TrackerIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowTrackerInputError("请输入 SteamID64 或 Steam 个人资料链接。");
            return;
        }

        if (!TryNormalizeTrackerInput(input, out var lookupKey, out var directSteamId))
        {
            ShowTrackerInputError(
                "SteamID64 格式错误。请输入有效的 17 位 SteamID64，或 Steam 个人资料链接。");
            return;
        }

        ClearTrackerInputError();
        var viewGeneration = ++_trackerViewGeneration;
        _querying = true;
        CheckStatsButton.IsEnabled = false;
        QueryProgress.IsActive = true;
        QueryProgress.Visibility = Visibility.Visible;
        ErrorCard.Visibility = Visibility.Collapsed;
        TrackerLandingPanel.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Collapsed;
        TrackerModulesPanel.Visibility = Visibility.Collapsed;
        QueryStatusText.Text = "正在读取 CS2Tracker 数据，请稍候…";
        try
        {
            await EnsureRequestBrowserAsync();
            var prefetchedSteamId = directSteamId;
            Task<WebFetchResult>? aimTask = null;
            Task<WebFetchResult>? highlightsTask = null;
            Task<WebFetchResult>? cheatRiskTask = null;
            if (prefetchedSteamId is { Length: 17 })
            {
                aimTask = FetchWithChallengeRetryAsync(
                    $"/api/aim-stats?steamid={Uri.EscapeDataString(prefetchedSteamId)}",
                    "application/json");
                highlightsTask = FetchWithChallengeRetryAsync(
                    $"/api/stats-clips?steamid={Uri.EscapeDataString(prefetchedSteamId)}&kind=highlights",
                    "application/json");
                cheatRiskTask = FetchWithChallengeRetryAsync(
                    $"/api/cheat-v2?steamid={Uri.EscapeDataString(prefetchedSteamId)}",
                    "application/json");
            }

            var profileResponse = await FetchWithChallengeRetryAsync(
                $"/api/player/{Uri.EscapeDataString(lookupKey)}",
                "application/x-ndjson");
            var profile = ParseProfilePayload(profileResponse);
            var resolvedSteamId = GetString(profile, "steamid64") ?? directSteamId;
            if (resolvedSteamId is { Length: 17 })
            {
                SaveTrackerQueryHistory(
                    resolvedSteamId,
                    FirstNonEmpty(
                        GetString(profile, "nickname"),
                        GetString(profile, "csstatsgg", "name"),
                        "未知玩家"));
            }

            if (resolvedSteamId is { Length: 17 })
            {
                if (!string.Equals(prefetchedSteamId, resolvedSteamId, StringComparison.Ordinal))
                {
                    aimTask = FetchWithChallengeRetryAsync(
                        $"/api/aim-stats?steamid={Uri.EscapeDataString(resolvedSteamId)}",
                        "application/json");
                    highlightsTask = FetchWithChallengeRetryAsync(
                        $"/api/stats-clips?steamid={Uri.EscapeDataString(resolvedSteamId)}&kind=highlights",
                        "application/json");
                    cheatRiskTask = FetchWithChallengeRetryAsync(
                        $"/api/cheat-v2?steamid={Uri.EscapeDataString(resolvedSteamId)}",
                        "application/json");
                }
            }

            var aimStats = await ReadOptionalJsonAsync(aimTask, "Aim");
            var highlights = await ReadOptionalJsonAsync(highlightsTask, "Highlights");
            var cheatRisk = await ReadOptionalJsonAsync(cheatRiskTask, "实时作弊风险");
            if (viewGeneration != _trackerViewGeneration)
            {
                return;
            }

            RenderOverview(profile, aimStats, highlights, cheatRisk, resolvedSteamId ?? lookupKey);
            TrackerModulesPanel.Visibility = Visibility.Visible;
            QueryStatusText.Text = $"查询完成 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CS2Tracker 查询失败：{ex.Message}");
            if (viewGeneration == _trackerViewGeneration)
            {
                ShowError($"查询失败：{ex.Message}");
            }
        }
        finally
        {
            if (viewGeneration == _trackerViewGeneration)
            {
                _querying = false;
                CheckStatsButton.IsEnabled = true;
                QueryProgress.IsActive = false;
                QueryProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static async Task<JsonObject?> ReadOptionalJsonAsync(
        Task<WebFetchResult>? request,
        string label)
    {
        if (request is null)
        {
            return null;
        }

        try
        {
            var response = await request;
            return response.Status is >= 200 and < 300
                ? JsonNode.Parse(response.Text) as JsonObject
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 CS2Tracker {label} 数据失败：{ex.Message}");
            return null;
        }
    }

    private async Task EnsureRequestBrowserAsync()
    {
        if (_requestBrowserInitialized)
        {
            return;
        }

        if (_requestBrowserReady is not null)
        {
            await _requestBrowserReady.Task.WaitAsync(TimeSpan.FromSeconds(40));
            return;
        }

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requestBrowserReady = ready;
        try
        {
            var language = Loc.CurrentCode.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
                ? "zh-TW"
                : Loc.CurrentCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    ? "en-US"
                    : "zh-CN";
            var environment = await Cs2TrackerWebSession.GetEnvironmentAsync(language);
            await RequestBrowser.EnsureCoreWebView2Async(environment);
            WebViewDataHost.Track(RequestBrowser.CoreWebView2);
            RequestBrowser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                sender.NavigationCompleted -= OnNavigationCompleted;
                navigation.TrySetResult(args.IsSuccess);
            }

            RequestBrowser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            RequestBrowser.CoreWebView2.Navigate(StatsRootUrl);
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _requestBrowserInitialized = true;
            ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            throw;
        }
        finally
        {
            if (_requestBrowserReady == ready)
            {
                _requestBrowserReady = null;
            }
        }
    }

    private async Task<WebFetchResult> FetchWithChallengeRetryAsync(string path, string accept)
    {
        WebFetchResult? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            last = await FetchFromSiteAsync(path, accept);
            if (last.Status is >= 200 and < 400)
            {
                return last;
            }

            var challenge = last.Text.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                last.Text.Contains("Enable JavaScript and cookies", StringComparison.OrdinalIgnoreCase) ||
                last.Text.Contains("__cf_chl_", StringComparison.OrdinalIgnoreCase);
            if (last.Status != 403 && last.Status < 500 && !challenge)
            {
                return last;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return last ?? new WebFetchResult(0, string.Empty, string.Empty, "CS2Tracker 请求超时。");
    }

    private async Task<WebFetchResult> FetchFromSiteAsync(string path, string accept)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<WebFetchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFetches[requestId] = completion;
        try
        {
            var quotedPath = JsonQuote(path);
            var quotedAccept = JsonQuote(accept);
            var quotedRequestId = JsonQuote(requestId);
            var script = $$"""
                (async () => {
                  try {
                    const requestPath = {{quotedPath}};
                    const cacheBustedPath =
                      requestPath + (requestPath.includes('?') ? '&' : '?') + '_ts=' + Date.now();
                    const response = await fetch(cacheBustedPath, {
                      cache: 'no-store',
                      credentials: 'include',
                      headers: {
                        Accept: {{quotedAccept}},
                        'Cache-Control': 'no-cache',
                        Pragma: 'no-cache'
                      }
                    });
                    const text = await response.text();
                    window.chrome.webview.postMessage(JSON.stringify({
                      kind: {{JsonQuote(RequestKind)}},
                      requestId: {{quotedRequestId}},
                      status: response.status,
                      contentType: response.headers.get('content-type') || '',
                      text
                    }));
                  } catch (error) {
                    window.chrome.webview.postMessage(JSON.stringify({
                      kind: {{JsonQuote(RequestKind)}},
                      requestId: {{quotedRequestId}},
                      status: 0,
                      contentType: '',
                      text: '',
                      error: String(error)
                    }));
                  }
                })();
                """;
            await RequestBrowser.ExecuteScriptAsync(script);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(75));
        }
        finally
        {
            _pendingFetches.Remove(requestId);
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var text = args.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) ||
                kind.GetString() != RequestKind ||
                !root.TryGetProperty("requestId", out var requestIdElement) ||
                requestIdElement.GetString() is not { } requestId ||
                !_pendingFetches.TryGetValue(requestId, out var completion))
            {
                return;
            }

            _pendingFetches.Remove(requestId);
            var result = new WebFetchResult(
                root.TryGetProperty("status", out var status) ? status.GetInt32() : 0,
                root.TryGetProperty("contentType", out var contentType) ? contentType.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("text", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("error", out var error) ? error.GetString() : null);
            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            foreach (var completion in _pendingFetches.Values)
            {
                completion.TrySetException(ex);
            }
            _pendingFetches.Clear();
        }
    }

    private static JsonObject ParseProfilePayload(WebFetchResult response)
    {
        if (response.Error is { Length: > 0 } transportError)
        {
            throw new InvalidOperationException(transportError);
        }

        var text = response.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"CS2Tracker 返回空数据（HTTP {response.Status}）。");
        }

        if (response.ContentType.Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("{\"type\":", StringComparison.Ordinal))
        {
            var merged = new JsonObject();
            string? streamError = null;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                var type = message["type"]?.GetValue<string>();
                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    streamError = message["message"]?.GetValue<string>() ?? "CS2Tracker 返回错误。";
                    continue;
                }

                if (message["data"] is JsonObject data)
                {
                    MergeJsonObject(merged, data);
                }
            }

            if (merged.Count > 0)
            {
                return merged;
            }

            throw new InvalidOperationException(streamError ?? "CS2Tracker 没有返回玩家数据。");
        }

        var root = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("CS2Tracker 返回的数据格式无效。");
        if (root["profile_gate"] is JsonObject gate)
        {
            throw new InvalidOperationException(ExtractErrorMessage(gate));
        }

        if (response.Status is < 200 or >= 300)
        {
            throw new InvalidOperationException(ExtractErrorMessage(root));
        }

        return root;
    }

    private void RenderProfile(JsonObject root, JsonObject? aimStats, string fallbackSteamId)
    {
        var steamId = GetString(root, "steamid64") ?? fallbackSteamId;
        var name = FirstNonEmpty(
            GetString(root, "nickname"),
            GetString(root, "csstatsgg", "name"),
            steamId);
        ProfileNameText.Text = name;
        ProfileSteamIdText.Text = steamId;

        var avatarUrl = FirstNonEmpty(
            GetString(root, "avatar_url"),
            GetString(root, "csstatsgg", "avatar"));
        if (avatarUrl is not null && Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri))
        {
            ProfileAvatarImage.Source = new BitmapImage(avatarUri);
        }
        else
        {
            ProfileAvatarImage.Source = null;
        }

        var level = GetInt(root, "level");
        var friends = GetInt(root, "friends_count") ?? GetInt(root, "steam_raw", "friends_count");
        var playtimeHours = ReadPlaytimeHours(root);
        var accountAge = ReadAccountAge(root);
        ProfileMetaText.Text = string.Join(
            " · ",
            new[]
            {
                accountAge is null ? "账号年龄 —" : $"账号年龄 {accountAge}",
                level is null ? "等级 —" : $"等级 {level}",
                friends is null ? "好友 —" : $"好友 {friends}",
                playtimeHours is null ? "游戏时间 —" : $"游戏时间 {playtimeHours} 小时"
            });

        var riskScore = GetMetricNumber(GetNode(root, "gauges", "cheating")) ??
            GetMetricNumber(GetNode(root, "gauges", "cheating_details", "percent"));
        var risk = riskScore switch
        {
            null => ("风险未知", ColorFrom("#8A91A6")),
            >= 60 => ("高风险", ColorFrom("#FF5A65")),
            >= 30 => ("可疑", ColorFrom("#F7C65B")),
            _ => ("低风险", ColorFrom("#4EE3B1"))
        };
        RiskText.Text = riskScore is null ? risk.Item1 : $"{risk.Item1} · {riskScore.Value:F0}%";
        RiskText.Foreground = new SolidColorBrush(risk.Item2);
        RiskBadge.Background = new SolidColorBrush(WithAlpha(risk.Item2, 0x2E));

        var vacBans = GetInt(root, "ban_info", "number_of_vac_bans") ??
            (GetBool(root, "ban_info", "vac_banned") == true ? 1 : 0);
        var gameBans = GetInt(root, "ban_info", "number_of_game_bans") ?? 0;
        var supporter = GetBool(root, "flags", "supporter") == true;
        ProfileBadgeText.Text = string.Join(
            " · ",
            new[]
            {
                supporter ? "支持者" : null,
                vacBans > 0 ? $"VAC {vacBans}" : "VAC 0",
                gameBans > 0 ? $"游戏封禁 {gameBans}" : "游戏封禁 0"
            }.Where(value => value is not null));

        RenderLinks(root, steamId);

        var headshot = GetMetricNumber(GetNode(root, "gauges", "headshot")) ??
            GetMetricNumber(GetNode(root, "gauges", "headshot%")) ??
            GetMetricNumber(GetNode(root, "csstatsgg", "stats", "hs"));
        var winRate = GetMetricNumber(GetNode(root, "gauges", "winrate"));
        var kd = GetMetricNumber(GetNode(root, "csstatsgg", "stats", "kd"));
        if (kd is null)
        {
            var kills = GetMetricNumber(GetNode(root, "steam_userstats_730", "stats", "total_kills"));
            var deaths = GetMetricNumber(GetNode(root, "steam_userstats_730", "stats", "total_deaths"));
            if (kills is > 0 && deaths is > 0)
            {
                kd = kills / deaths;
            }
        }

        var matches = GetInt(root, "csstatsgg", "stats", "matches");
        SummaryMetaText.Text = string.Join(
            " · ",
            new[]
            {
                matches is null ? "比赛数 —" : $"比赛 {matches}",
                vacBans > 0 ? $"VAC 封禁 {vacBans}" : "VAC 正常",
                gameBans > 0 ? $"游戏封禁 {gameBans}" : "游戏封禁 0"
            });
        RenderMetrics(
            SummaryMetricsPanel,
            [
                Metric("作弊风险", riskScore is null ? "—" : $"{riskScore:F0}%", risk.Item1, risk.Item2),
                Metric("爆头率", Percent(headshot), "爆头"),
                Metric("胜率", Percent(winRate), "胜负"),
                Metric("击杀/死亡", Number(kd, "F2"), "CS2"),
                Metric("比赛", matches?.ToString(CultureInfo.InvariantCulture) ?? "—", "近期"),
                Metric("游戏时间", playtimeHours is null ? "—" : $"{playtimeHours} h", "CS2")
            ],
            columns: 3);

        RenderLeetify(root);
        RenderPremier(root);
        RenderFaceit(root);
        RenderAim(aimStats);
        RenderWeapons(root);
        RenderRecentMatches(root);
    }

    private void RenderLinks(JsonObject root, string steamId)
    {
        TrackerLinksPanel.Children.Clear();
        var profileUrl = GetString(root, "profile_url") ??
            $"https://steamcommunity.com/profiles/{steamId}";
        AddLink("Steam", profileUrl);
        AddLink("Leetify", $"https://leetify.com/public/profile/{steamId}");

        var faceitNickname = GetString(root, "faceit", "nickname");
        if (!string.IsNullOrWhiteSpace(faceitNickname))
        {
            AddLink("FACEIT", $"https://www.faceit.com/en/players/{Uri.EscapeDataString(faceitNickname)}");
        }

        AddLink("Scope.gg", $"https://app.scope.gg/profile/{steamId}");
    }

    private void AddLink(string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return;
        }

        var link = new HyperlinkButton
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4)
        };
        link.Click += async (_, _) => await AppState.OpenUrlAsync(url);
        TrackerLinksPanel.Children.Add(link);
    }

    private void RenderLeetify(JsonObject root)
    {
        var ratings = GetNode(root, "leetify_extra", "recentGameRatings") as JsonObject;
        var gamesPlayed = ratings is null ? null : GetMetricNumber(ratings["gamesPlayed"]);
        LeetifyMetaText.Text = gamesPlayed is null
            ? "未找到 Leetify 最近比赛评分。"
            : $"最近 {gamesPlayed:F0} 场评分";
        if (ratings is null)
        {
            RenderMetrics(LeetifyMetricsPanel, []);
            return;
        }

        RenderMetrics(
            LeetifyMetricsPanel,
            [
                Metric("瞄准", Number(GetMetricNumber(ratings["aim"]), "F1")),
                Metric("站位", Number(GetMetricNumber(ratings["positioning"]), "F1")),
                Metric("道具", Number(GetMetricNumber(ratings["utility"]), "F1")),
                Metric("首杀", PercentScaled(GetMetricNumber(ratings["opening"]))),
                Metric("残局", PercentScaled(GetMetricNumber(ratings["clutch"]))),
                Metric("Leetify 评分", SignedNumber(GetMetricNumber(ratings["leetify"]), "F2"))
            ],
            columns: 3);
    }

    private void RenderPremier(JsonObject root)
    {
        PremierMetricsPanel.Children.Clear();

        var seasons = ReadPremierSeasons(root);
        var current = seasons.FirstOrDefault();
        var rating = current?.Rank is > 0 ? current.Rank : null;
        var seasonWins = Math.Max(0, current?.Wins ?? 0);
        var recentResults = ReadPremierRecentResults(root);

        PremierMetricsPanel.Children.Add(CreatePremierRankBadge(rating));
        if (rating is null && current is not null && seasonWins < 10)
        {
            PremierMetricsPanel.Children.Add(
                CreatePremierRankProgress(current.Season!.Value, seasonWins, 10));
        }

        PremierMetricsPanel.Children.Add(CreatePremierRecentPerformance(recentResults));
        PremierMetricsPanel.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeBrush("AuroraGlassBorderBrush")
        });

        var totalMatches =
            GetMetricNumber(root, "steam_userstats_730", "stats", "total_matches_played") ??
            GetMetricNumber(root, "stats", "total_matches_played") ??
            GetMetricNumber(root, "csstatsgg", "stats", "matches");
        var kd = ReadPremierKd(root);
        var headshot = ReadPremierHeadshot(root);
        PremierMetricsPanel.Children.Add(CreatePremierStatsGrid(
            [
                ("ELO", rating is null ? "—" : rating.Value.ToString("N0", CultureInfo.InvariantCulture)),
                ("比赛场次", totalMatches is null ? "—" : totalMatches.Value.ToString("N0", CultureInfo.InvariantCulture)),
                ("K/D", Number(kd, "F2")),
                ("爆头率", PremierPercent(headshot))
            ]));

        if (rating is not null && current?.BestRank is > 0)
        {
            var peak = Math.Max(rating.Value, current.BestRank.Value);
            var previous = seasons.FirstOrDefault(item =>
                item.Season < current.Season && item.Rank is > 0);
            var best = seasons
                .Select(item => item.BestRank ?? item.Rank ?? 0)
                .Append(peak)
                .Max();
            var recentWins = recentResults.Count(result => result == "W");
            double? winRate = recentResults.Count == 0
                ? null
                : recentWins * 100d / recentResults.Count;

            PremierMetricsPanel.Children.Add(CreatePremierStatsGrid(
                [
                    ("胜率", PremierPercent(winRate)),
                    ("峰值", peak.ToString("N0", CultureInfo.InvariantCulture)),
                    ("上赛季", previous?.Rank?.ToString("N0", CultureInfo.InvariantCulture) ?? "—"),
                    ("历史最高", best.ToString("N0", CultureInfo.InvariantCulture))
                ]));
        }
    }

    private static IReadOnlyList<PremierSeasonInfo> ReadPremierSeasons(JsonObject root)
    {
        var ranks = GetNode(root, "csstatsgg", "ranks") as JsonArray;
        if (ranks is null || ranks.Count == 0)
        {
            ranks = GetNode(root, "ranks_fallback") as JsonArray;
        }

        if (ranks is null)
        {
            return [];
        }

        return ranks
            .OfType<JsonObject>()
            .Where(entry => string.Equals(
                GetString(entry, "mode", "type"),
                "Premier",
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => new PremierSeasonInfo(
                GetInt(entry, "mode", "season"),
                GetInt(entry, "wins"),
                GetInt(entry, "rank"),
                GetInt(entry, "best_rank")))
            .Where(entry => entry.Season is > 0)
            .OrderByDescending(entry => entry.Season)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadPremierRecentResults(JsonObject root)
    {
        var games = GetNode(root, "leetify", "games") as JsonArray;
        if (games is { Count: > 0 })
        {
            var hasPremierRankType = games
                .OfType<JsonObject>()
                .Any(game => GetMetricNumber(game["rankType"]) is not null);
            var results = games
                .OfType<JsonObject>()
                .Where(game =>
                    string.Equals(GetString(game, "dataSource"), "matchmaking", StringComparison.OrdinalIgnoreCase) &&
                    GetBool(game, "isCs2") != false &&
                    DateTimeOffset.TryParse(
                        GetString(game, "gameFinishedAt"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out _) &&
                    (!hasPremierRankType || GetMetricNumber(game["rankType"]) == 11))
                .OrderByDescending(game => DateTimeOffset.TryParse(
                    GetString(game, "gameFinishedAt"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var date)
                    ? date
                    : DateTimeOffset.MinValue)
                .Select(game => GetString(game, "matchResult")?.ToLowerInvariant())
                .Where(result => result is "win" or "loss")
                .Select(result => result == "win" ? "W" : "L")
                .Take(10)
                .ToArray();
            if (results.Length > 0)
            {
                return results;
            }
        }

        var matches = GetNode(root, "csstatsgg", "recent_matches") as JsonArray;
        if (matches is null)
        {
            return [];
        }

        return matches
            .OfType<JsonObject>()
            .Select(match =>
            {
                var scoreFor = GetMetricNumber(match["score_for"]);
                var scoreAgainst = GetMetricNumber(match["score_against"]);
                return scoreFor is null || scoreAgainst is null
                    ? null
                    : scoreFor > scoreAgainst ? "W" : scoreFor < scoreAgainst ? "L" : null;
            })
            .Where(result => result is not null)
            .Select(result => result!)
            .Take(10)
            .ToArray();
    }

    private Border CreatePremierRankBadge(int? rating)
    {
        var isUnranked = rating is null;
        var badge = new Grid
        {
            Width = isUnranked ? 92 : 138,
            Height = isUnranked ? 33 : 50,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var asset = isUnranked
            ? "ms-appx:///Assets/Cs2Tracker/premier_rank_gray.svg"
            : $"ms-appx:///Assets/Cs2Tracker/premier_rank_{PremierRankColor(rating!.Value)}.svg";
        badge.Children.Add(new Image
        {
            Source = new SvgImageSource(new Uri(asset, UriKind.Absolute)),
            Stretch = Stretch.Fill
        });
        badge.Children.Add(new TextBlock
        {
            Text = isUnranked ? "未定级" : rating!.Value.ToString("N0", CultureInfo.InvariantCulture),
            FontSize = isUnranked ? 11 : 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFrom("#E6ECFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("AuroraPaneBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = badge
        };
    }

    private Border CreatePremierRankProgress(int season, int wins, int requiredWins)
    {
        var content = new StackPanel { Spacing = 8 };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = $"第 {season} 赛季",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextBodyBrush")
        });
        var record = new TextBlock
        {
            Text = $"{wins} / {requiredWins} 胜",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };
        Grid.SetColumn(record, 1);
        heading.Children.Add(record);
        content.Children.Add(heading);

        var bar = new Grid { ColumnSpacing = 5, Height = 5 };
        for (var index = 0; index < requiredWins; index++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            var segment = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = index < wins
                    ? new SolidColorBrush(ColorFrom("#2AA6FF"))
                    : ThemeBrush("AuroraGlassBorderBrush")
            };
            Grid.SetColumn(segment, index);
            bar.Children.Add(segment);
        }

        content.Children.Add(bar);
        if (wins < requiredWins)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"再赢 {requiredWins - wins} 场优先模式即可获得 CS 评分",
                FontSize = 11,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
        }

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrush("AuroraGlassButtonBrush"),
            BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    private StackPanel CreatePremierRecentPerformance(IReadOnlyList<string> results)
    {
        var content = new StackPanel { Spacing = 7 };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "最近表现",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        var summary = new TextBlock
        {
            Text = results.Count == 0
                ? "N/A"
                : $"{results.Count(result => result == "L")} 负 {results.Count(result => result == "W")} 胜",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        };
        Grid.SetColumn(summary, 1);
        heading.Children.Add(summary);
        content.Children.Add(heading);

        var resultRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };
        for (var index = 0; index < 10; index++)
        {
            var result = index < results.Count ? results[index] : index % 2 == 0 ? "W" : "L";
            resultRow.Children.Add(new Image
            {
                Source = new SvgImageSource(new Uri(
                    result == "W"
                        ? "ms-appx:///Assets/Cs2Tracker/win.svg"
                        : "ms-appx:///Assets/Cs2Tracker/lose.svg",
                    UriKind.Absolute)),
                Width = 22,
                Height = 22,
                Opacity = index < results.Count ? 1 : 0.2,
                Stretch = Stretch.Uniform
            });
        }

        content.Children.Add(resultRow);
        return content;
    }

    private Grid CreatePremierStatsGrid(
        (string Label, string Value)[] items)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < items.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock
            {
                Text = items[index].Label,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 70,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = items[index].Value,
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrush("AuroraTextHeadingBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(panel, index);
            grid.Children.Add(panel);
        }

        return grid;
    }

    private static double? ReadPremierKd(JsonObject root)
    {
        var kills = GetMetricNumber(root, "steam_userstats_730", "stats", "total_kills");
        var deaths = GetMetricNumber(root, "steam_userstats_730", "stats", "total_deaths");
        if (kills is >= 100 && deaths is > 0)
        {
            return kills.Value / deaths.Value;
        }

        return GetMetricNumber(root, "csstatsgg", "stats", "kd");
    }

    private static double? ReadPremierHeadshot(JsonObject root)
    {
        var kills = GetMetricNumber(root, "steam_userstats_730", "stats", "total_kills");
        var headshots = GetMetricNumber(root, "steam_userstats_730", "stats", "total_kills_headshot");
        var deaths = GetMetricNumber(root, "steam_userstats_730", "stats", "total_deaths");
        if (kills is >= 100 && deaths is > 0 && headshots is >= 0 && headshots <= kills)
        {
            return headshots.Value / kills.Value * 100;
        }

        return GetMetricNumber(root, "csstatsgg", "stats", "hs");
    }

    private static string PremierPercent(double? value) =>
        value is null ? "—" : $"{value.Value:F0}%";

    private static string PremierRankColor(int rating) => rating switch
    {
        >= 30_000 => "yellow",
        >= 25_000 => "red",
        >= 20_000 => "pink",
        >= 15_000 => "purple",
        >= 10_000 => "dark_blue",
        >= 5_000 => "light_blue",
        _ => "gray"
    };

    private void RenderFaceit(JsonObject root)
    {
        var faceit = GetNode(root, "faceit") as JsonObject;
        if (faceit is null)
        {
            FaceitMetaText.Text = "暂无 FACEIT 数据。";
            RenderMetrics(FaceitMetricsPanel, []);
            return;
        }

        var cs2 = faceit["cs2"] as JsonObject;
        var stats = faceit["cs2_stats"] as JsonObject;
        var nickname = GetString(faceit, "nickname");
        var country = GetString(faceit, "country");
        FaceitMetaText.Text = string.Join(
            " · ",
            new[]
            {
                nickname,
                country?.ToUpperInvariant()
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        RenderMetrics(
            FaceitMetricsPanel,
            [
                Metric("ELO", Number(cs2 is null ? null : GetMetricNumber(cs2["elo"]), "F0")),
                Metric("等级", Number(cs2 is null ? null : GetMetricNumber(cs2["skill_level"]), "F0")),
                Metric("比赛", Number(stats is null ? null : GetMetricNumber(stats["matches"]), "F0")),
                Metric("胜率", Percent(stats is null ? null : GetMetricNumber(stats["win_rate_percent"]))),
                Metric("击杀/死亡", Number(stats is null ? null : GetMetricNumber(stats["average_kd_ratio"]), "F2")),
                Metric("爆头率", Percent(stats is null ? null : GetMetricNumber(stats["average_headshots_percent"])))
            ],
            columns: 3);
    }

    private void RenderAim(JsonObject? aimStats)
    {
        var aim = aimStats?["aim"] as JsonObject;
        if (aimStats is null || aim is null || GetBool(aimStats, "hasData") == false)
        {
            AimMetaText.Text = "暂无 CS2Tracker Aim 数据。";
            RenderMetrics(AimMetricsPanel, []);
            return;
        }

        var matches = GetMetricNumber(aimStats["matches"]);
        var rounds = GetMetricNumber(aimStats["rounds"]);
        AimMetaText.Text = string.Join(
            " · ",
            new[]
            {
                matches is null ? null : $"最近 {matches:F0} 场比赛",
                rounds is null ? null : $"{rounds:F0} 回合"
            }.Where(value => value is not null));

        var benchmarks = aimStats["benchmarks"] as JsonObject;
        RenderMetrics(
            AimMetricsPanel,
            [
                Metric("预瞄", Number(GetMetricNumber(aim["preaimDeg"]), "F1", "°"), Benchmark(benchmarks, "preaimDeg")),
                Metric("伤害耗时", Number(GetMetricNumber(aim["ttdSeenMs"]), "F0", " 毫秒"), Benchmark(benchmarks, "ttdSeenMs")),
                Metric("反应时间", Number(GetMetricNumber(aim["reactionTimeMs"]), "F0", " 毫秒"), Benchmark(benchmarks, "reactionTimeMs")),
                Metric("命中率", Percent(GetMetricNumber(aim["accuracyPct"])), Benchmark(benchmarks, "accuracyPct")),
                Metric("爆头命中率", Percent(GetMetricNumber(aim["headAccuracyPct"])), Benchmark(benchmarks, "headAccuracyPct")),
                Metric("扫射命中率", Percent(GetMetricNumber(aim["sprayAccuracyPct"])), Benchmark(benchmarks, "sprayAccuracyPct")),
                Metric("急停", Percent(GetMetricNumber(aim["counterStrafePct"])), Benchmark(benchmarks, "counterStrafePct"))
            ],
            columns: 3);
    }

    private void RenderWeapons(JsonObject root)
    {
        WeaponsPanel.Children.Clear();
        var weaponStats = GetNode(root, "steam_userstats_730", "stats") as JsonObject;
        var weapons = GetNode(root, "csstatsgg", "weapons", "weapons") as JsonArray;
        var result = new Dictionary<string, WeaponUsage>(StringComparer.OrdinalIgnoreCase);

        if (weapons is not null)
        {
            foreach (var entry in weapons.OfType<JsonObject>())
            {
                var id = NormalizeWeaponId(
                    GetString(entry, "icon_name") ??
                    GetString(entry, "name"));
                if (id is null)
                {
                    continue;
                }

                var kills = GetMetricNumber(entry["kills"]) ??
                    GetMetricNumber(weaponStats?[$"total_kills_{id}"]) ?? 0;
                var shots = GetMetricNumber(weaponStats?[$"total_shots_{id}"]);
                var hits = GetMetricNumber(weaponStats?[$"total_hits_{id}"]);
                double? accuracy = hits is not null && shots is > 0 ? hits.Value / shots.Value * 100 : null;
                result[id] = new WeaponUsage(
                    id,
                    WeaponDisplayName(id, GetString(entry, "name")),
                    kills,
                    shots,
                    hits,
                    accuracy);
            }
        }

        if (weaponStats is not null)
        {
            foreach (var pair in weaponStats)
            {
                if (!pair.Key.StartsWith("total_kills_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = NormalizeWeaponId(pair.Key["total_kills_".Length..]);
                if (id is null or "headshot" or "enemy_weapon" or "against_zoomed_sniper")
                {
                    continue;
                }

                var kills = GetMetricNumber(pair.Value) ?? 0;
                var shots = GetMetricNumber(weaponStats[$"total_shots_{id}"]);
                var hits = GetMetricNumber(weaponStats[$"total_hits_{id}"]);
                double? accuracy = hits is not null && shots is > 0 ? hits.Value / shots.Value * 100 : null;
                if (!result.TryGetValue(id, out var existing) || kills > existing.Kills)
                {
                    result[id] = new WeaponUsage(
                        id,
                        WeaponDisplayName(id, null),
                        kills,
                        shots,
                        hits,
                        accuracy);
                }
            }
        }

        var items = result.Values
            .Where(item => item.Kills > 0)
            .OrderByDescending(item => item.Kills)
            .ToArray();
        if (items.Length == 0)
        {
            WeaponMetaText.Text = string.Empty;
            WeaponsPanel.Children.Add(new TextBlock
            {
                Text = "暂无武器数据。",
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
            return;
        }

        var totalKills = items.Sum(item => item.Kills);
        WeaponMetaText.Text = $"{items.Length} 种武器 · 总击杀 {totalKills:F0}";
        WeaponsPanel.Children.Add(CreateWeaponTableHeader());
        foreach (var item in items.Take(10))
        {
            WeaponsPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeBrush("AuroraPaneBrush"),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = CreateWeaponRow(item)
            });
        }
    }

    private Grid CreateWeaponTableHeader()
    {
        var grid = CreateWeaponGrid();
        AddWeaponCell(grid, "武器", 0, ThemeBrush("AuroraTextMutedBrush"), 11, true);
        AddWeaponCell(grid, "击杀", 1, ThemeBrush("AuroraTextMutedBrush"), 11, true, HorizontalAlignment.Right);
        AddWeaponCell(grid, "射击", 2, ThemeBrush("AuroraTextMutedBrush"), 11, true, HorizontalAlignment.Right);
        AddWeaponCell(grid, "命中", 3, ThemeBrush("AuroraTextMutedBrush"), 11, true, HorizontalAlignment.Right);
        AddWeaponCell(grid, "命中率", 4, ThemeBrush("AuroraTextMutedBrush"), 11, true, HorizontalAlignment.Right);
        return grid;
    }

    private Grid CreateWeaponRow(WeaponUsage usage)
    {
        var grid = CreateWeaponGrid();
        var namePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        var catalog = FindCatalogWeapon(usage.Id);
        if (catalog is not null &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "weapons", $"{catalog.IconFile}.svg")))
        {
            namePanel.Children.Add(new Image
            {
                Source = new SvgImageSource(new Uri(catalog.IconUri, UriKind.Absolute)),
                Width = 52,
                Height = 24,
                Stretch = Stretch.Uniform
            });
        }

        namePanel.Children.Add(new TextBlock
        {
            Text = usage.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);
        AddWeaponCell(grid, Number(usage.Kills, "F0"), 1, ThemeBrush("AuroraTextBodyBrush"), 13, false, HorizontalAlignment.Right);
        AddWeaponCell(grid, Number(usage.Shots, "F0"), 2, ThemeBrush("AuroraTextBodyBrush"), 13, false, HorizontalAlignment.Right);
        AddWeaponCell(grid, Number(usage.Hits, "F0"), 3, ThemeBrush("AuroraTextBodyBrush"), 13, false, HorizontalAlignment.Right);
        AddWeaponCell(grid, Percent(usage.Accuracy), 4, ThemeBrush("AuroraTextBodyBrush"), 13, false, HorizontalAlignment.Right);
        return grid;
    }

    private static Grid CreateWeaponGrid()
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 10, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        }

        return grid;
    }

    private static void AddWeaponCell(
        Grid grid,
        string text,
        int column,
        Brush foreground,
        double fontSize,
        bool semiBold,
        HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize,
            FontWeight = semiBold
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private string WeaponDisplayName(string id, string? sourceName)
    {
        var normalized = NormalizeWeaponId(id);
        if (normalized == "enemy_blinded")
        {
            return "致盲击杀";
        }

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            return sourceName;
        }

        var catalog = FindCatalogWeapon(id);
        if (catalog is not null)
        {
            return catalog.LocalizedName;
        }

        return (normalized ?? id).Replace('_', ' ').ToUpperInvariant();
    }

    private static CsWeapon? FindCatalogWeapon(string id)
    {
        var normalized = NormalizeWeaponId(id);
        return CsWeaponCatalog.All.FirstOrDefault(weapon =>
            string.Equals(weapon.IconFile, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(weapon.ClassName, $"weapon_{normalized}", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeWeaponId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("weapon_", StringComparison.Ordinal))
        {
            normalized = normalized["weapon_".Length..];
        }

        normalized = Regex.Replace(normalized, "[^a-z0-9_]", string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private void RenderRecentMatches(JsonObject root)
    {
        RecentMatchesPanel.Children.Clear();
        var matches = GetNode(root, "csstatsgg", "recent_matches") as JsonArray;
        if (matches is null || matches.Count == 0)
        {
            RecentMatchesPanel.Children.Add(new TextBlock
            {
                Text = "暂无最近比赛。",
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
            return;
        }

        foreach (var match in matches.OfType<JsonObject>().Take(12))
        {
            var scoreFor = GetMetricNumber(match["score_for"]) ?? 0;
            var scoreAgainst = GetMetricNumber(match["score_against"]) ?? 0;
            var result = scoreFor > scoreAgainst ? "W" : scoreFor < scoreAgainst ? "L" : "T";
            var map = GetString(match, "map") ?? "—";
            map = map.Replace("de_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("cs_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .ToUpperInvariant();
            var resultColor = result == "W" ? ColorFrom("#4EE3B1") : result == "L" ? ColorFrom("#FF5A65") : ColorFrom("#AAB3D4");

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = result,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(resultColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            var mapText = new TextBlock
            {
                Text = map,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(mapText, 1);
            row.Children.Add(mapText);
            var score = new TextBlock
            {
                Text = $"{scoreFor:F0}-{scoreAgainst:F0}",
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(score, 2);
            row.Children.Add(score);
            var date = new TextBlock
            {
                Text = FormatMatchDate(GetString(match, "date")) ?? "—",
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(date, 3);
            row.Children.Add(date);

            RecentMatchesPanel.Children.Add(new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(10),
                Background = ThemeBrush("AuroraPaneBrush"),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = row
            });
        }
    }

    private void RenderMetrics(
        StackPanel host,
        MetricItem[] items,
        int columns = 3)
    {
        host.Children.Clear();
        if (items.Length == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "暂无数据。",
                Foreground = ThemeBrush("AuroraTextMutedBrush")
            });
            return;
        }

        var grid = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var rows = (items.Length + columns - 1) / columns;
        for (var i = 0; i < rows; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var cardContent = new StackPanel { Spacing = 3 };
            cardContent.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 70,
                Foreground = ThemeBrush("AuroraTextMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            var value = new TextBlock
            {
                Text = item.Value,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.Color is { } valueColor
                    ? new SolidColorBrush(valueColor)
                    : ThemeBrush("AuroraTextHeadingBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            cardContent.Children.Add(value);
            if (!string.IsNullOrWhiteSpace(item.Sub))
            {
                cardContent.Children.Add(new TextBlock
                {
                    Text = item.Sub,
                    FontSize = 11,
                    Foreground = ThemeBrush("AuroraTextMutedBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var card = new Border
            {
                Padding = new Thickness(11, 9, 11, 9),
                CornerRadius = new CornerRadius(12),
                Background = ThemeBrush("AuroraPaneBrush"),
                BorderBrush = ThemeBrush("AuroraGlassBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = cardContent
            };
            Grid.SetRow(card, i / columns);
            Grid.SetColumn(card, i % columns);
            grid.Children.Add(card);
        }

        host.Children.Add(grid);
    }

    private static MetricItem Metric(
        string label,
        string value,
        string? sub = null,
        Windows.UI.Color? color = null) =>
        new(label, value, sub) { Color = color };

    private sealed record MetricItem(string Label, string Value, string? Sub = null)
    {
        public Windows.UI.Color? Color { get; init; }
    }

    private sealed record WeaponUsage(
        string Id,
        string Name,
        double Kills,
        double? Shots,
        double? Hits,
        double? Accuracy);

    private sealed record PremierSeasonInfo(
        int? Season,
        int? Wins,
        int? Rank,
        int? BestRank);

    private static JsonNode? GetNode(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var part in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? GetString(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        if (node is null)
        {
            return null;
        }

        try
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean ? "true" : "false";
            }

            return null;
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

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                double.IsFinite(number))
            {
                return number;
            }
        }

        if (node is JsonObject obj)
        {
            return GetMetricNumber(obj["normal"]) ??
                GetMetricNumber(obj["value"]) ??
                GetMetricNumber(obj["percent"]) ??
                GetMetricNumber(obj["cs2"]) ??
                GetMetricNumber(obj["csgo"]);
        }

        return null;
    }

    private static double? GetMetricNumber(JsonObject root, params string[] path) =>
        GetMetricNumber(GetNode(root, path));

    private static int? GetInt(JsonObject root, params string[] path)
    {
        var number = GetMetricNumber(root, path);
        return number is null ? null : (int)Math.Round(number.Value);
    }

    private static bool? GetBool(JsonObject root, params string[] path)
    {
        var node = GetNode(root, path);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
            {
                return boolean;
            }
        }

        return null;
    }

    private static void MergeJsonObject(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (target[pair.Key] is JsonObject targetObject && pair.Value is JsonObject sourceObject)
            {
                MergeJsonObject(targetObject, sourceObject);
                continue;
            }

            target[pair.Key] = pair.Value is null ? null : JsonNode.Parse(pair.Value.ToJsonString());
        }
    }

    private static string ExtractErrorMessage(JsonObject root)
    {
        foreach (var key in new[] { "message", "error", "statusMessage" })
        {
            if (root[key] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (root["profile_gate"] is JsonObject gate)
        {
            return ExtractErrorMessage(gate);
        }

        return "CS2Tracker 没有返回可用的玩家数据。";
    }

    private static bool TryNormalizeTrackerInput(
        string input,
        out string lookupKey,
        out string? steamId64)
    {
        var trimmed = input.Trim();
        lookupKey = trimmed;
        steamId64 = null;

        if (IsSteamId64(trimmed))
        {
            steamId64 = trimmed;
            return true;
        }

        var candidate = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"https://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host is not ("steamcommunity.com" or "www.steamcommunity.com" or
            "steamcommunity.ai" or "www.steamcommunity.ai"))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        if (segments[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsSteamId64(segments[1]))
            {
                return false;
            }

            steamId64 = segments[1];
            lookupKey = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }

        if (!segments[0].Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var vanityName = Uri.UnescapeDataString(segments[1]);
        if (!Regex.IsMatch(
                vanityName,
                @"^[A-Za-z0-9_.-]{1,64}$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        lookupKey = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private static bool IsSteamId64(string value)
    {
        return Regex.IsMatch(value, @"^\d{17}$", RegexOptions.CultureInvariant) &&
            ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamId) &&
            steamId is >= 76561197960265728UL and <= 76561202255233023UL;
    }

    private static string? ExtractSteamId64(string input)
    {
        var match = Regex.Match(input, @"(?<!\d)\d{17}(?!\d)");
        return match.Success && IsSteamId64(match.Value) ? match.Value : null;
    }

    private void ShowTrackerInputError(string message)
    {
        TrackerInputErrorText.Text = message;
        TrackerInputErrorText.Visibility = Visibility.Visible;
    }

    private void ClearTrackerInputError()
    {
        TrackerInputErrorText.Text = string.Empty;
        TrackerInputErrorText.Visibility = Visibility.Collapsed;
    }

    private static string JsonQuote(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Number(double? value, string format, string suffix = "") =>
        value is null ? "—" : value.Value.ToString(format, CultureInfo.InvariantCulture) + suffix;

    private static string SignedNumber(double? value, string format) =>
        value is null ? "—" : (value.Value > 0 ? "+" : string.Empty) +
            value.Value.ToString(format, CultureInfo.InvariantCulture);

    private static string Percent(double? value) =>
        value is null ? "—" : $"{value.Value:F1}%";

    private static string PercentScaled(double? value)
    {
        if (value is null)
        {
            return "—";
        }

        var percent = Math.Abs(value.Value) <= 1 ? value.Value * 100 : value.Value;
        return $"{percent:F1}%";
    }

    private static string? Benchmark(JsonObject? benchmarks, string key)
    {
        if (benchmarks?[key] is not JsonObject value)
        {
            return null;
        }

        var rank = GetMetricNumber(value["rank"]) ?? GetMetricNumber(value["topPercent"]);
        return rank is null ? null : $"前 {rank.Value:F0}%";
    }

    private static int? ReadPlaytimeHours(JsonObject root)
    {
        if (GetNode(root, "recently_played_games") is JsonArray directGames)
        {
            var value = FindCs2Playtime(directGames);
            if (value is not null)
            {
                return (int)Math.Round(value.Value / 60);
            }
        }

        if (GetNode(root, "steam_raw", "recently_played_games") is JsonArray steamGames)
        {
            var value = FindCs2Playtime(steamGames);
            if (value is not null)
            {
                return (int)Math.Round(value.Value / 60);
            }
        }

        var hours = GetMetricNumber(root, "total_time_played_hours") ??
            GetMetricNumber(root, "steam", "total_time_played_hours");
        if (hours is not null)
        {
            return (int)Math.Round(hours.Value);
        }

        var seconds = GetMetricNumber(root, "total_time_played_seconds") ??
            GetMetricNumber(root, "steam", "total_time_played_seconds") ??
            GetMetricNumber(root, "steam_raw", "total_time_played_seconds");
        return seconds is null ? null : (int)Math.Round(seconds.Value / 3600);
    }

    private static double? FindCs2Playtime(JsonArray games)
    {
        foreach (var game in games.OfType<JsonObject>())
        {
            if (GetMetricNumber(game["appid"]) == 730)
            {
                return GetMetricNumber(game["playtime_forever"]);
            }
        }

        return null;
    }

    private static string? ReadAccountAge(JsonObject root)
    {
        foreach (var path in new[]
                 {
                     new[] { "time_created" },
                     new[] { "steam", "time_created" },
                     new[] { "steam_raw", "time_created" },
                     new[] { "created_at" },
                     new[] { "steam", "created_at" }
                 })
        {
            var rawDate = GetString(root, path);
            if (DateTimeOffset.TryParse(
                    rawDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return FormatAccountAge(parsed);
            }
        }

        var raw = GetMetricNumber(root, "time_created") ??
            GetMetricNumber(root, "steam", "time_created") ??
            GetMetricNumber(root, "steam_raw", "time_created") ??
            GetMetricNumber(root, "created_at") ??
            GetMetricNumber(root, "steam", "created_at");
        if (raw is null)
        {
            return null;
        }

        var timestamp = raw.Value < 10_000_000_000 ? raw.Value * 1000 : raw.Value;
        DateTimeOffset created;
        try
        {
            created = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp);
        }
        catch
        {
            return null;
        }

        return FormatAccountAge(created);
    }

    private static string FormatAccountAge(DateTimeOffset created)
    {
        var elapsed = DateTimeOffset.Now - created;
        if (elapsed.TotalDays >= 365)
        {
            return $"{Math.Floor(elapsed.TotalDays / 365.25):F0} 年";
        }

        if (elapsed.TotalDays >= 30)
        {
            return $"{Math.Floor(elapsed.TotalDays / 30.44):F0} 个月";
        }

        return $"{Math.Max(0, Math.Floor(elapsed.TotalDays)):F0} 天";
    }

    private static string? FormatMatchDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : raw;
    }

    private static Windows.UI.Color ColorFrom(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(
            value > 0xFFFFFF ? (byte)(value >> 24) : (byte)0xFF,
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void ShowError(string message)
    {
        QueryStatusText.Text = string.Empty;
        ErrorText.Text = message;
        if (DetailsPanel.Visibility != Visibility.Visible &&
            TrackerModulesPanel.Visibility != Visibility.Visible)
        {
            TrackerLandingPanel.Visibility = Visibility.Visible;
        }
        ErrorCard.Visibility = Visibility.Visible;
    }

    private void OnWebViewDataMoveFinished()
    {
        _requestBrowserInitialized = false;
        _requestBrowserReady = null;
        foreach (var completion in _pendingFetches.Values)
        {
            completion.TrySetException(new InvalidOperationException("CS2Tracker 会话已重建。"));
        }
        _pendingFetches.Clear();
    }
}
