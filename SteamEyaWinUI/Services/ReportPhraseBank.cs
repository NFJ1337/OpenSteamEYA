using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamEyaWinUI.Services;

/// <summary>一份举报描述词库（文件格式见 Assets\ReportPhrases\*.json）。</summary>
internal sealed class ReportLexicon
{
    /// <summary>这份词库服务的 abuseType，目前只有 Cheating。</summary>
    public string? AbuseType { get; set; }

    /// <summary>词库代号，仅用于日志/排查。</summary>
    public string? Code { get; set; }

    public string? Name { get; set; }

    /// <summary>作弊术语：自瞄、透视、wallhack…</summary>
    public List<string> Terms { get; set; } = [];

    /// <summary>证据场景：对局回放、死亡视角…</summary>
    public List<string> Scenes { get; set; } = [];

    /// <summary>时间说法：最近几局、昨晚的排位里…</summary>
    public List<string> Times { get; set; } = [];

    /// <summary>描述模板，占位符：{term} {scene} {time} {rounds}。</summary>
    public List<string> Templates { get; set; } = [];

    /// <summary>可选开头（含空串，用来拉开差异）。</summary>
    public List<string> Openers { get; set; } = [];

    /// <summary>可选收尾（同上）。</summary>
    public List<string> Closers { get; set; } = [];
}

// 与账号历史/语言包一致用 source generator：Native AOT 下也能读写，不依赖反射。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ReportLexicon))]
internal sealed partial class ReportLexiconJsonContext : JsonSerializerContext;

/// <summary>
/// 举报描述词库：给「自动举报」拼一条随机的、内容始终跟作弊相关的中文描述。
///
/// 为什么需要它：官方 profile 举报表单里 <c>abuseDescription</c> 是必填（textarea 的 placeholder 写着 required），
/// 而且每次提交都发同一段文字很容易被当成机器人重复提交，所以这里每次现拼一条不一样的说法。
///
/// 词库来源（后者覆盖前者，与 <see cref="LanguageCatalog"/> 一套思路）：
///   1. 内嵌资源（随程序集打包，发布漏拷文件时的保底）；
///   2. 程序目录 Assets\ReportPhrases\*.json（随发布拷贝，改词改这里）；
///   3. 数据目录\ReportPhrases\*.json（自己丢一份进去即可覆盖，不需要重新编译）。
/// 都拿不到时退回内置的一小组词，保证举报功能不会因为词库坏了而不可用。
/// </summary>
internal static class ReportPhraseBank
{
    private const string FolderName = "ReportPhrases";

    /// <summary>描述长度上限：官方那个框只有 3 行，写太长也没人看。</summary>
    private const int MaxLength = 200;

    /// <summary>记住最近用过的若干条，避免同一次运行里连着重样。</summary>
    private const int RecentMemory = 16;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static ReportLexicon? _cached;

    /// <summary>拼一条新的「作弊」描述。线程安全，每次调用都尽量与最近用过的不同。</summary>
    public static string NextCheatDescription()
    {
        lock (Gate)
        {
            var lexicon = _cached ??= Load();
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var text = Compose(lexicon);
                if (text.Length == 0)
                {
                    return string.Empty;
                }

                if (!Recent.Contains(text))
                {
                    Remember(text);
                    return text;
                }
            }

            // 词库太小、凑不出新花样时就用重复的，总比发空描述强。
            var repeat = Compose(lexicon);
            Remember(repeat);
            return repeat;
        }
    }

    private static void Remember(string text)
    {
        Recent.Enqueue(text);
        while (Recent.Count > RecentMemory)
        {
            Recent.Dequeue();
        }
    }

    private static ReportLexicon Load()
    {
        ReportLexicon? loaded = null;
        foreach (var candidate in EnumerateCandidates())
        {
            loaded = candidate;
        }

        if (loaded is null)
        {
            AppLog.Warn($"没读到举报描述词库（{FolderName}），临时用内置的一小组词。");
            return BuiltInFallback();
        }

        return loaded;
    }

    /// <summary>按「内嵌 → 程序目录 → 数据目录」的顺序找词库，最后一个可用的胜出。</summary>
    private static IEnumerable<ReportLexicon?> EnumerateCandidates()
    {
        var assembly = typeof(ReportPhraseBank).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains($".{FolderName}.", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return TryParse(() =>
            {
                using var stream = assembly.GetManifestResourceStream(name);
                return stream is null
                    ? null
                    : JsonSerializer.Deserialize(stream, ReportLexiconJsonContext.Default.ReportLexicon);
            }, name);
        }

        var directories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", FolderName),
            Path.Combine(AppPaths.DataRoot, FolderName),
        };

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                yield return TryParse(() => JsonSerializer.Deserialize(File.ReadAllText(file), ReportLexiconJsonContext.Default.ReportLexicon), file);
            }
        }
    }

    private static ReportLexicon? TryParse(Func<ReportLexicon?> read, string source)
    {
        try
        {
            var lexicon = read();
            if (lexicon is null || lexicon.Templates.Count == 0)
            {
                return null;
            }

            return lexicon;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"举报描述词库 {source} 解析失败，已跳过：{ex.Message}");
            return null;
        }
    }

    /// <summary>按模板拼一条描述：开头 + 模板（替换占位符）+ 收尾，压成一行并截断。</summary>
    private static string Compose(ReportLexicon lexicon)
    {
        var body = Pick(lexicon.Templates);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        // 词库里可能有纯手工写的模板（不含占位符），所以每个占位符都只在模板用到时才替换。
        body = body.Replace("{term}", Pick(lexicon.Terms), StringComparison.Ordinal)
                   .Replace("{scene}", Pick(lexicon.Scenes), StringComparison.Ordinal)
                   .Replace("{time}", Pick(lexicon.Times), StringComparison.Ordinal)
                   .Replace("{rounds}", Random.Shared.Next(2, 6).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // 模板自己已经说过「疑似 / 麻烦 / 谢谢」时就不再叠开头收尾，免得读起来像复读：
        //   「麻烦看下这个号：…麻烦查一下。麻烦复核录像，谢谢。」这种。
        var opener = body.Contains("疑似", StringComparison.Ordinal) ? string.Empty : Pick(lexicon.Openers);
        var closer = body.Contains("麻烦", StringComparison.Ordinal) || body.Contains("谢谢", StringComparison.Ordinal)
            ? string.Empty
            : Pick(lexicon.Closers);

        var text = $"{opener}{body}{closer}";
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    private static string Pick(List<string> pool) =>
        pool.Count == 0 ? string.Empty : pool[Random.Shared.Next(pool.Count)];

    /// <summary>词库文件缺失/损坏时的兜底词：内容同样只跟作弊相关。</summary>
    private static ReportLexicon BuiltInFallback() => new()
    {
        AbuseType = "Cheating",
        Code = "builtin",
        Name = "内置兜底词库",
        Terms = ["自瞄", "透视", "锁头", "穿墙", "无后座", "透视预瞄", "aimbot", "wallhack"],
        Scenes = ["对局回放", "死亡视角", "赛后数据"],
        Times = ["最近几局", "刚才那局"],
        Templates =
        [
            "{time}该账号明显在用{term}，{scene}里看得很清楚。",
            "疑似{term}，{scene}里他的预瞄一直卡在墙后。",
            "{scene}里他的弹道完全不散，基本可以确定是{term}。",
        ],
        Openers = ["", "疑似作弊："],
        Closers = ["", "麻烦复核录像，谢谢。"],
    };
}