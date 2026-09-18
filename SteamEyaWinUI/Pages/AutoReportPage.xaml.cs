using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

/// <summary>队列里的一行：一个待举报的 SteamID64 与它当前的状态。</summary>
public sealed partial class AutoReportTarget : INotifyPropertyChanged
{
    private string _status = string.Empty;

    internal AutoReportTarget(int index, string steamId64)
    {
        IndexText = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SteamId64 = steamId64;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IndexText { get; }

    public string SteamId64 { get; }

    /// <summary>状态文案；x:Bind OneWay，必须通知才会重绘。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
}

/// <summary>
/// 自动举报页：填 1~5 个 SteamID64，选举报人账号与游戏（默认 CS2），一次点击把整队列提交完。
///
/// 全程后台 HTTP，不开浏览器窗口：走的是 steamcommunity 官方的举报端点
/// （资料页「Report Player」弹窗提交时打的就是它），认证用所选账号换出来的网页会话。
/// 端点、字段名与 abuseType 取值都是从线上页面实测出来的，见 <see cref="SteamReportService"/>。
/// </summary>
public sealed partial class AutoReportPage : Page, INotifyPropertyChanged
{
    private const int MaxTargets = 5;

    /// <summary>循环举报的次数范围：故意留小，循环刷举报容易被 Steam 当成滥用。</summary>
    private const int MinLoopRounds = 2;
    private const int MaxLoopRounds = 10;

    /// <summary>同一条与下一条之间的间隔：串行提交，别把请求打成连发，也留出停下来的机会。</summary>
    private const int BetweenItemDelayMs = 1200;

    /// <summary>轮与轮之间的间隔：随机 5~8 秒，避免整点连发。</summary>
    private const int MinRoundWaitMs = 5000;
    private const int MaxRoundWaitMs = 8000;

    /// <summary>
    /// 被 Steam 限流后的冷却时长：等满再试。限流按账号一段时间内的累计行为算，
    /// 已经在冷却窗口里就不叠加等待（详见 <see cref="CoolDownForRateLimitAsync"/>）。
    /// </summary>
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);
    private const int DefaultGameAppId = 730;   // Counter-Strike 2

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly TextBox[] _idBoxes;
    private readonly TextBlock[] _idHints;
    private readonly List<ReporterOption> _reporters = [];
    private readonly List<GameOption> _games = [];

    private ReporterOption? _reporter;
    private GameOption _game = new(DefaultGameAppId, "Counter-Strike 2");
    private SteamWebSession? _session;
    private bool _running;

    /// <summary>当前限流冷却的结束时刻；窗口内再被限流时共用这一次等待。</summary>
    private DateTimeOffset _rateLimitCooldownEndsAt;

    public AutoReportPage()
    {
        InitializeComponent();

        _idBoxes = [IdBox1, IdBox2, IdBox3, IdBox4, IdBox5];
        _idHints = [IdHint1, IdHint2, IdHint3, IdHint4, IdHint5];

        Loc.LanguageChanged += OnLanguageChanged;
        AppState.WhiteAccountsChanged += OnAccountsChanged;
        AppState.HistoryChanged += OnAccountsChanged;

        RebuildReporterOptions();
        RebuildGameFlyout();
        UpdateGameButtonText();
        UpdateIdHints();
        UpdateButtons();

    }
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    /// <summary>待举报队列。</summary>
    public ObservableCollection<AutoReportTarget> Targets { get; } = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RebuildReporterOptions();
        UpdateIdHints();
        UpdateButtons();

    }
    private void OnLanguageChanged() => _dispatcherQueue.TryEnqueue(() =>
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
        RebuildReporterOptions();
        RebuildGameFlyout();
        UpdateGameButtonText();
        UpdateIdHints();
    });

    private void OnAccountsChanged(string? selectSteamId)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RebuildReporterOptions);
            return;
        }

        RebuildReporterOptions();
    }

    // ---------- 举报人账号 ----------

    /// <summary>
    /// 候选账号：账号管理页（有账号+密码）在前、历史账号页（只有令牌）在后，按登录名/Steam64 保序去重。
    /// 两类都能换出 steamcommunity 网页会话（账号密码 → CM 登录换令牌；历史账号的令牌本身就是 refresh token）。
    /// </summary>
    private void RebuildReporterOptions()
    {
        _reporters.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in AppState.WhiteAccounts.Concat(AppState.HistoryAccounts))
        {
            var key = FirstNonEmpty(account.AccountName, account.SteamId);
            if (key is null || !seen.Add(key))
            {
                continue;
            }

            var hasPassword = !string.IsNullOrWhiteSpace(account.AccountName) && !string.IsNullOrWhiteSpace(account.Password);
            var hasToken = !string.IsNullOrWhiteSpace(account.EyaToken);
            if (!hasPassword && !hasToken)
            {
                continue;   // 既没密码也没令牌：换不出网页会话，列出来只会误导
            }

            _reporters.Add(new ReporterOption(account));
        }

        ReporterAccountFlyout.Items.Clear();
        foreach (var option in _reporters)
        {
            var item = new MenuFlyoutItem { Text = option.Display, Tag = option };
            item.Click += ReporterAccountMenuItem_Click;
            ReporterAccountFlyout.Items.Add(item);
        }

        // 选中的账号被删了就退回重选
        if (_reporter is not null && !_reporters.Contains(_reporter))
        {
            _reporter = null;
        }

        _reporter ??= _reporters.FirstOrDefault();
        ReporterAccountButton.IsEnabled = _reporters.Count > 0 && !_running;
        UpdateReporterButtonText();
    }

    private void ReporterAccountMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ReporterOption option })
        {
            return;
        }

        _reporter = option;
        _session = null;   // 换账号后旧会话作废
        UpdateReporterButtonText();
    }

    private void UpdateReporterButtonText() =>
        ReporterAccountText.Text = _reporter?.Display ?? Loc.T("AutoReport_Account_None");

    // ---------- 游戏（默认 CS2，可用 Steam 商店搜索换） ----------

    private void RebuildGameFlyout()
    {
        if (_games.Count == 0)
        {
            _games.Add(new GameOption(DefaultGameAppId, Loc.T("AutoReport_Game_Default")));
            _game = _games[0];
        }

        GameFlyout.Items.Clear();
        foreach (var option in _games)
        {
            var item = new MenuFlyoutItem
            {
                Text = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Loc.T("AutoReport_Game_Item_Format"),
                    option.Name,
                    option.AppId),
                Tag = option
            };
            item.Click += GameMenuItem_Click;
            GameFlyout.Items.Add(item);
        }
    }

    private void GameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: GameOption option })
        {
            return;
        }

        _game = option;
        UpdateGameButtonText();
    }

    private void UpdateGameButtonText() =>
        GameButtonText.Text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            Loc.T("AutoReport_Game_Item_Format"),
            _game.Name,
            _game.AppId);

    /// <summary>
    /// 用 Steam 商店的公开搜索接口按关键词查游戏（返回 appid + 名称，直接列进下拉）。
    /// 接口：https://store.steampowered.com/api/storesearch/（实测返回 JSON：items[].id / items[].name）。
    /// </summary>
    private async void GameSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var term = GameSearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        GameSearchButton.IsEnabled = false;
        try
        {
            var language = Loc.CurrentCode switch
            {
                "zh-Hans" => "schinese",
                "zh-Hant" => "tchinese",
                _ => "english"
            };
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc=CN&l={language}";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await httpClient.GetStringAsync(url, CancellationToken.None);

            var found = ParseStoreSearch(json);
            if (found.Count == 0)
            {
                AppState.ShowStatus(Loc.Tf("AutoReport_Game_Search_Empty_Format", term), InfoBarSeverity.Warning);
                return;
            }

            // 搜索结果排在最前，默认那条（CS2）永远留着
            var defaults = _games.Where(option => option.AppId == DefaultGameAppId).ToList();
            _games.Clear();
            _games.AddRange(found);
            foreach (var option in defaults.Where(option => _games.All(item => item.AppId != option.AppId)))
            {
                _games.Add(option);
            }

            RebuildGameFlyout();
            AppState.ShowStatus(Loc.Tf("AutoReport_Game_Search_Ok_Format", found.Count), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // 取消/超时：不改动当前选择
        }
        catch (Exception ex)
        {
            AppLog.Warn($"查询 Steam 商店游戏失败：{ex.Message}");
            AppState.ShowStatus(Loc.Tf("AutoReport_Game_Search_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            GameSearchButton.IsEnabled = true;
        }
    }

    /// <summary>解析 storesearch 的 JSON（只收 type=app，DLC / 原声带会混在里面）。</summary>
    private static List<GameOption> ParseStoreSearch(string json)
    {
        var options = new List<GameOption>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var appId))
            {
                continue;
            }

            if (!item.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (item.TryGetProperty("type", out var typeNode) &&
                typeNode.ValueKind == JsonValueKind.String &&
                !string.Equals(typeNode.GetString(), "app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = nameNode.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                options.Add(new GameOption(appId, name!));
            }
        }

        return options;
    }

    // ---------- 目标 ID ----------

    private void IdBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateIdHints();

    private void UpdateIdHints()
    {
        for (var i = 0; i < _idBoxes.Length; i++)
        {
            var text = _idBoxes[i].Text?.Trim() ?? string.Empty;
            _idHints[i].Text = text.Length == 0
                ? string.Empty
                : IsValidSteamId64(text) ? Loc.T("AutoReport_Id_Ok") : Loc.T("AutoReport_Id_Invalid");
        }
    }

    /// <summary>
    /// SteamID64：只校验「17 位数字」，不限定开头几位 —— 不同来源的 ID 前缀不总一样，
    /// 卡死前缀会把本来能举报的号挡在外面（用户要求）。
    /// </summary>
    private static bool IsValidSteamId64(string value) =>
        value.Length == 17 && value.All(char.IsAsciiDigit);

    /// <summary>把 5 个输入框里合法的 ID 收成队列（保持输入顺序，重复的只留一个）。</summary>
    private bool RebuildQueueFromBoxes()
    {
        var ids = new List<string>();
        foreach (var box in _idBoxes)
        {
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !IsValidSteamId64(text) || ids.Contains(text, StringComparer.Ordinal))
            {
                continue;
            }

            ids.Add(text);
        }

        Targets.Clear();
        for (var i = 0; i < Math.Min(ids.Count, MaxTargets); i++)
        {
            Targets.Add(new AutoReportTarget(i + 1, ids[i]) { Status = Loc.T("AutoReport_Status_Idle") });
        }

        return Targets.Count > 0;
    }

    // ---------- 开始 / 停止 / 清空 ----------

    /// <summary>单次举报：按队列跑一轮。</summary>
    private async void StartButton_Click(object sender, RoutedEventArgs e) => await RunQueueAsync(rounds: 1);

    /// <summary>循环举报：按「次数」把整队列重复跑若干轮（轮与轮之间随机等几秒，随时可停）。</summary>
    private async void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync(ReadLoopRounds());
    }

    /// <summary>读「次数」：解析不出来就回到下限，并夹在 [2,10] 之间。</summary>
    private int ReadLoopRounds()
    {
        var raw = LoopCountBox.Text?.Trim();
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rounds))
        {
            rounds = MinLoopRounds;
        }

        return Math.Clamp(rounds, MinLoopRounds, MaxLoopRounds);
    }

    private void SetLoopRounds(int rounds) =>
        LoopCountBox.Text = Math.Clamp(rounds, MinLoopRounds, MaxLoopRounds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    // 与「设置 → 窗口尺寸」那对上下按钮同一套样式与交互
    private void IncreaseLoopCountButton_Click(object sender, RoutedEventArgs e) => SetLoopRounds(ReadLoopRounds() + 1);

    private void DecreaseLoopCountButton_Click(object sender, RoutedEventArgs e) => SetLoopRounds(ReadLoopRounds() - 1);

    /// <summary>
    /// 队列执行核心：把 Targets 里的每个 SteammaID64 按「作弊」提交一次，重复 <paramref name="rounds"/> 轮。
    /// 单次举报就是 rounds = 1。全程可停（停止按钮走全局取消），一条一条提交、轮与轮之间再等一会儿，
    /// 既不给 Steam 打连发，也留出停下来的机会。
    /// </summary>
    private async Task RunQueueAsync(int rounds)
    {
        if (_running)
        {
            return;
        }

        if (!RebuildQueueFromBoxes())
        {
            AppState.ShowStatus(Loc.T("AutoReport_Status_NoId"), InfoBarSeverity.Warning);
            return;
        }

        if (_reporter is null)
        {
            AppState.ShowStatus(Loc.T("AutoReport_Status_NoAccount"), InfoBarSeverity.Warning);
            return;
        }

        rounds = Math.Clamp(rounds, 1, MaxLoopRounds);
        var cancellationToken = AppState.BeginBusyOperation();
        _running = true;
        UpdateButtons();

        var succeeded = 0;
        var failed = 0;
        var stoppedByRateLimit = false;

        // 一条提交的现场状态：状态栏 + 队列行的「正在提交…」。重试前也要再喊一次。
        void ShowProgress(int round, int index)
        {
            // 进度只写在页内状态条上（「开始举报」左侧），不再弹全局提示条。
            var roundPrefix = rounds > 1 ? Loc.Tf("AutoReport_Timer_RoundPrefix_Format", round, rounds) : string.Empty;
            ShowWaitTimer(roundPrefix + Loc.Tf("AutoReport_Timer_Progress_Format", index + 1, Targets.Count));
        }

        void ApplyResult(AutoReportTarget line, SteamReportResult result)
        {
            if (result.Ok)
            {
                succeeded++;
                line.Status = Loc.Tf("AutoReport_Status_Sent_Format", _game.Name, result.StatusCode);
                return;
            }

            failed++;
            line.Status = Loc.Tf("AutoReport_Status_Fail_Format", result.Detail);
        }

        try
        {
            ShowWaitTimer(Loc.T("AutoReport_Status_BuildingSession"));
            _session ??= await BuildSessionForReporterAsync(_reporter, cancellationToken);

            for (var round = 1; round <= rounds && !stoppedByRateLimit; round++)
            {
                for (var index = 0; index < Targets.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Targets[index];

                    target.Status = Loc.T("AutoReport_Status_Sending");
                    ShowProgress(round, index);

                    // 每次提交现拼一条描述：官方表单里描述是必填（abuseDescription），
                    // 内容随机但只跟作弊相关，避免每次发同一段文字像机器人重复提交（词库见 ReportPhraseBank）。
                    var result = await SteamReportService.ReportAsync(
                        _session,
                        target.SteamId64,
                        _game.AppId,
                        SteamReportService.AbuseTypeCheating,
                        ReportPhraseBank.NextCheatDescription(),
                        cancellationToken);

                    // 限流：冷一分钟再重试这一条。冷却完还是被限流，说明这个账号这会儿别再打了 —— 收工。
                    if (!result.Ok && SteamReportService.IsRateLimited(result))
                    {
                        target.Status = Loc.T("AutoReport_Status_RateLimitedCooling");
                        await CoolDownForRateLimitAsync(cancellationToken);

                        target.Status = Loc.T("AutoReport_Status_Sending");
                        ShowProgress(round, index);
                        // 重试也换一条新的描述（同一条文字连着发两次更容易被当成重复提交）。
                        result = await SteamReportService.ReportAsync(
                            _session,
                            target.SteamId64,
                            _game.AppId,
                            SteamReportService.AbuseTypeCheating,
                            ReportPhraseBank.NextCheatDescription(),
                            cancellationToken);

                        if (SteamReportService.IsRateLimited(result))
                        {
                            target.Status = Loc.T("AutoReport_Status_RateLimited_Fail");
                            AppLog.Warn($"举报 {target.SteamId64}：冷却后仍被 Steam 限流，本次举报停止。");
                            stoppedByRateLimit = true;
                            break;
                        }
                    }

                    ApplyResult(target, result);

                    // 一条一条来（不并发、不打连发），中间留出停下来的机会。
                    if (index + 1 < Targets.Count)
                    {
                        await Task.Delay(BetweenItemDelayMs, cancellationToken);
                    }
                }

                // 轮与轮之间多等一会儿：随机 5~8 秒，避免整点连发。
                if (!stoppedByRateLimit && round < rounds)
                {
                    var waitMs = Random.Shared.Next(MinRoundWaitMs, MaxRoundWaitMs);
                    await CountdownAsync(
                        seconds => Loc.Tf("AutoReport_Timer_Round_Format", seconds),
                        TimeSpan.FromMilliseconds(waitMs),
                        cancellationToken);
                }
            }

            ShowWaitTimer(stoppedByRateLimit
                ? Loc.T("AutoReport_Timer_RateLimitStop")
                : Loc.Tf("AutoReport_Status_Done_Format", succeeded, failed));
        }
        catch (OperationCanceledException)
        {
            ShowWaitTimer(Loc.T("AutoReport_Status_Stopped"));
        }
        catch (Exception ex)
        {
            // 会话没建起来才是「登录失败」；会话已经建好还报错，就是提交那一步的网络问题
            // （实测这条网络到 steamcommunity.com 的 TLS 会被偶发掐断），别把两件事说成一件事。
            var sessionFailed = _session is null;
            AppLog.Warn(sessionFailed ? $"举报会话建立失败：{ex.Message}" : $"举报提交失败（网络/TLS）：{ex.Message}");
            ShowWaitTimer(Loc.T(sessionFailed
                ? "AutoReport_Timer_LoginFail"
                : "AutoReport_Timer_NetworkFail"));
            AppState.ShowStatus(
                Loc.Tf(sessionFailed
                    ? "AutoReport_Status_Session_Fail_Format"
                    : "AutoReport_Status_Network_Fail_Format",
                    ex.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            _running = false;
            UpdateButtons();
            AppState.EndBusyOperation();
        }
    }

    /// <summary>
    /// 带秒级倒计时的等待：每秒把剩余秒数刷到「开始举报」左侧那个页内计时器上。
    /// 刻意不走全局提示条 —— 长时间任务里每秒弹一次提示太打扰；等待过程只留在页内。
    /// 用循环 + Task.Delay 而不是 DispatcherQueueTimer —— 这样「停止」按钮一按，取消令牌立刻中断等待。
    /// </summary>
    private async Task CountdownAsync(Func<int, string> render, TimeSpan duration, CancellationToken cancellationToken)
    {
        var endsAt = DateTimeOffset.UtcNow + duration;
        while (true)
        {
            var remaining = endsAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            ShowWaitTimer(render((int)Math.Ceiling(remaining.TotalSeconds)));
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private void ShowWaitTimer(string text)
    {
        WaitTimerText.Text = text;
        WaitTimerBadge.Visibility = Visibility.Visible;
    }

    private void HideWaitTimer()
    {
        WaitTimerText.Text = string.Empty;
        WaitTimerBadge.Visibility = Visibility.Collapsed;
    }
    /// <summary>
    /// 限流冷却：等满 <see cref="RateLimitCooldown"/> 再继续。
    /// 已经在冷却窗口里（例如同一批里连着两条都被限流）就只等剩下的那点时间，不重复叠加。
    /// </summary>
    private async Task CoolDownForRateLimitAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var endsAt = _rateLimitCooldownEndsAt > now ? _rateLimitCooldownEndsAt : now + RateLimitCooldown;
        _rateLimitCooldownEndsAt = endsAt;

        var remaining = endsAt - now;
        AppLog.Warn($"被 Steam 限流：冷却 {remaining.TotalSeconds:0} 秒后重试。");

        // 状态栏上每秒跳一次剩余秒数（忙碌期间提示常驻，不会被 3 秒自动收起）。
        await CountdownAsync(
            seconds => Loc.Tf("AutoReport_Timer_Cooling_Format", seconds),
            remaining,
            cancellationToken);
    }
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            AppState.CancelBusyOperation();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        foreach (var box in _idBoxes)
        {
            box.Text = string.Empty;
        }

        Targets.Clear();
        UpdateIdHints();
        HideWaitTimer();
    }

    private void UpdateButtons()
    {
        StartButton.IsEnabled = !_running;
        LoopButton.IsEnabled = !_running;
        LoopCountBox.IsEnabled = !_running;
        StopButton.IsEnabled = _running;
        ClearButton.IsEnabled = !_running;
        ReporterAccountButton.IsEnabled = _reporters.Count > 0 && !_running;
        GameButton.IsEnabled = !_running;
        GameSearchButton.IsEnabled = !_running;
        GameSearchBox.IsEnabled = !_running;
    }

    /// <summary>
    /// 用所选账号换一条 steamcommunity 网页会话（steamLoginSecure + sessionid）：
    /// 有账号密码走「账号密码 → CM 登录 → refresh token」，历史账号直接用它的令牌（本身就是 refresh token）。
    /// 带回的 sessionid 必须与 cookie 里的一致，举报端点的表单校验就认这个。
    /// </summary>
    private static async Task<SteamWebSession> BuildSessionForReporterAsync(ReporterOption option, CancellationToken cancellationToken)
    {
        var account = option.Account;
        var hasPassword = !string.IsNullOrWhiteSpace(account.AccountName) && !string.IsNullOrWhiteSpace(account.Password);

        // 无 2FA 的账号不需要验证码；需要时这里返回空，由上游把错误抛出来提示用户。
        static Task<string?> NoGuardCodeAsync(SteamGuardPrompt _, CancellationToken __) => Task.FromResult<string?>(null);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string refreshToken;
        string steamId;

        if (hasPassword)
        {
            var auth = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
                account.AccountName!,
                account.Password!,
                NoGuardCodeAsync,
                null,
                cancellationToken);
            refreshToken = auth.RefreshToken;
            steamId = auth.SteamId;
        }
        else
        {
            refreshToken = FormatHelper.NormalizeToken(account.EyaToken!);
            var tokenInfo = AppState.JwtTokenService.Inspect(refreshToken);
            if (!tokenInfo.IsValid || string.IsNullOrWhiteSpace(tokenInfo.SteamId))
            {
                throw new InvalidOperationException(tokenInfo.Status);
            }

            steamId = tokenInfo.SteamId;
        }

        await using var cmClient = new SteamCmClient(httpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        return await SteamWebSession.BuildAsync(cmClient, refreshToken, steamId, cancellationToken);
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    /// <summary>举报人候选：直接把账号对象带上，点「开始举报」时按它取账号密码 / 令牌。</summary>
    private sealed record ReporterOption(SteamAccountHistoryItem Account)
    {
        public string Display
        {
            get
            {
                var steamId = Account.SteamId?.Trim();
                var name = FirstNonEmpty(Account.PersonaName, Account.AccountName);
                if (string.Equals(name, steamId, StringComparison.OrdinalIgnoreCase))
                {
                    name = null;
                }

                return name is null
                    ? steamId ?? Account.AccountName ?? string.Empty
                    : string.IsNullOrWhiteSpace(steamId)
                        ? name
                        : string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            Loc.T("Settings_Cs2Sync_SourceItem_Format"),
                            name,
                            steamId);
            }
        }
    }

    /// <summary>可举报的游戏：Steam 商店的 appid + 名称。</summary>
    private sealed record GameOption(int AppId, string Name);
}