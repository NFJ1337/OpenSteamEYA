using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>落盘结构（单独一层，方便以后加字段不破坏旧文件）。</summary>
internal sealed class CardKeyStoreFile
{
    public List<CardKeyEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CardKeyStoreFile))]
internal sealed partial class CardKeyStoreJsonContext : JsonSerializerContext;

/// <summary>
/// 「黑号存储」：把还没登录过的卡密攒在本地（数据目录的 card-keys.json），
/// 支持按来源（奶味 / 伊万·路飞）导入、验号、删除，以及把某条送去 Token 登录。
///
/// 写盘与账号历史那套一致：先写 .tmp 再原子替换，替换前留一份 .bak —— 中途断电也不会把文件写坏。
/// 卡密原样保存（只去首尾空白），不做大小写/分隔符之类的规范化。
/// </summary>
internal sealed class CardKeyStore
{
    private const string FileName = "card-keys.json";

    private readonly object _gate = new();
    private List<CardKeyEntry>? _entries;

    /// <summary>卡密文件路径（数据目录下）。</summary>
    public string FilePath => Path.Combine(AppPaths.DataRoot, FileName);

    /// <summary>当前全部条目（首次访问时读盘；返回值可直接改，改完调 <see cref="Save"/>）。</summary>
    public List<CardKeyEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries ??= LoadFromDisk();
            }
        }
    }

    /// <summary>
    /// 按行导入：跳过空行与重复项（同一来源下卡密相同即视为重复），返回真正新增的条数。
    /// 卡密只做 Trim，其它字符一律照原样存。
    /// </summary>
    public int Import(IEnumerable<string> keys, string source)
    {
        lock (_gate)
        {
            var entries = _entries ??= LoadFromDisk();
            var existing = new HashSet<string>(
                entries.Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal)).Select(entry => entry.Key),
                StringComparer.Ordinal);

            var added = 0;
            foreach (var raw in keys)
            {
                var key = raw.Trim();
                if (key.Length == 0 || !existing.Add(key))
                {
                    continue;
                }

                entries.Add(new CardKeyEntry
                {
                    Key = key,
                    Source = source,
                    AddedAt = DateTimeOffset.Now
                });
                added++;
            }

            if (added > 0)
            {
                SaveLocked(entries);
            }

            return added;
        }
    }

    /// <summary>删除一条；顺手存盘（删空表就是空表，不做特殊处理）。</summary>
    public void Remove(CardKeyEntry entry)
    {
        lock (_gate)
        {
            var entries = _entries ??= LoadFromDisk();
            if (!entries.Remove(entry))
            {
                return;
            }

            SaveLocked(entries);
        }
    }

    /// <summary>把内存里的改动写盘（验号结果更新后调用）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            SaveLocked(_entries ??= LoadFromDisk());
        }
    }

    private void SaveLocked(List<CardKeyEntry> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(new CardKeyStoreFile { Entries = entries }, CardKeyStoreJsonContext.Default.CardKeyStoreFile);
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            // 存不下去也不能把界面搞崩：告警留在日志里，内存里的列表照常用。
            AppLog.Warn($"黑号存储保存失败：{ex.Message}");
        }
    }

    private List<CardKeyEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var json = File.ReadAllText(FilePath);
            var file = JsonSerializer.Deserialize(json, CardKeyStoreJsonContext.Default.CardKeyStoreFile);
            return file?.Entries ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn($"黑号存储读取失败（按空列表处理）：{ex.Message}");
            return [];
        }
    }
}