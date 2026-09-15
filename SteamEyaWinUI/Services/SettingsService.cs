using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 应用级设置（语言、主题）的持久化。存于当前数据目录的 settings.json，与账号历史同目录。
/// 读写以同步小文件为主，调用方不多（启动读一次、设置页改动时写），故用简单锁而非账号历史那套文件门。
/// </summary>
internal sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";

    private readonly object _gate = new();

    /// <summary>当前数据根目录，供“打开/移动数据目录”使用。</summary>
    public string AppFolderPath => AppPaths.DataRoot;

    private static string SettingsFilePath => Path.Combine(AppPaths.DataRoot, SettingsFileName);

    /// <summary>「个性化」头像的固定落盘路径（裁剪后的 512² JPEG）。</summary>
    public string PersonalizationAvatarPath => Path.Combine(AppFolderPath, "personalization", "avatar.jpg");

    private static readonly HashSet<string> AllowedCustomIntroVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv" };

    // 自定义背景可选类型：图片（含 GIF）+ 视频（由主窗口用 MediaPlayerElement 静音循环播放）。
    private static readonly HashSet<string> AllowedCustomBackgroundExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp",
            ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"
        };

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings is not null)
                    {
                        // JSON 里显式的 "groups": null 会覆盖属性初始值，消费方（LoadGroups 等）直接 .Groups 会 NRE。
                        settings.Groups ??= [];
                        // 首次从旧版本升级时，为账号管理复制一份独立分组定义；之后两边互不影响。
                        settings.WhiteAccountGroups ??= CloneGroups(settings.Groups);
                        // 同理防护配装预设：显式 null 会让配装页导航、一键配装直接 NRE。
                        settings.Loadout ??= CsLoadoutPreset.Default();
                        settings.Loadout.T ??= new Dictionary<uint, uint>();
                        settings.Loadout.Ct ??= new Dictionary<uint, uint>();
                        // 同理防护 VPN 选择：旧版本 JSON 没有这两个键（或显式写了 null），回落到默认值。
                        settings.VpnTakeover = VpnCoreService.NormalizeTakeover(settings.VpnTakeover);
                        settings.VpnMode = VpnCoreService.NormalizeMode(settings.VpnMode);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("读取应用设置失败，按默认设置处理。", ex);
            }

            return new AppSettings { WhiteAccountGroups = [] };
        }
    }

    /// <summary>解析可用的自定义背景图片路径；文件缺失或设置被篡改时返回 null。</summary>
    public string? GetCustomBackgroundImagePath(AppSettings settings)
    {
        var storedName = settings.CustomBackgroundImageFileName;
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var fileName = Path.GetFileName(storedName);
        if (!string.Equals(fileName, storedName, StringComparison.Ordinal) ||
            !AllowedCustomBackgroundExtensions.Contains(Path.GetExtension(fileName)))
        {
            return null;
        }

        var path = Path.Combine(AppFolderPath, "personalization", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>把选择器返回的图片复制到数据目录，返回持久化的唯一文件名。</summary>
    public string ImportCustomBackgroundImage(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected background image does not exist.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedCustomBackgroundExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Unsupported background image type: {extension}");
        }

        var folder = Path.Combine(AppFolderPath, "personalization");
        Directory.CreateDirectory(folder);
        var fileName = "custom-background-" + Guid.NewGuid().ToString("N") + extension;
        var destinationPath = Path.Combine(folder, fileName);
        var tempPath = destinationPath + "." + Path.GetRandomFileName() + ".tmp";

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 清理临时文件失败不影响已复制背景的可用性。
            }
        }

        return fileName;
    }

    /// <summary>删除除当前背景外的旧图片；文件被动画解码器占用时留待下次清理。</summary>
    public void DeleteOtherCustomBackgroundImages(string? keepFileName)
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-background-*"))
        {
            if (!string.IsNullOrWhiteSpace(keepFileName) &&
                string.Equals(Path.GetFileName(path), Path.GetFileName(keepFileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteBackgroundFile(path);
        }
    }

    /// <summary>删除所有已保存的自定义背景图片。</summary>
    public void DeleteCustomBackgroundImages()
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-background-*"))
        {
            TryDeleteBackgroundFile(path);
        }
    }

    private static void TryDeleteBackgroundFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除背景图片失败：{ex.Message}");
        }
    }

    /// <summary>解析可用的自定义入场动画路径；未设置或文件缺失时返回 null。</summary>
    public string? GetCustomIntroVideoPath(AppSettings settings)
    {
        var storedName = settings.CustomIntroVideoFileName;
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var fileName = Path.GetFileName(storedName);
        if (!string.Equals(fileName, storedName, StringComparison.Ordinal) ||
            !AllowedCustomIntroVideoExtensions.Contains(Path.GetExtension(fileName)))
        {
            return null;
        }

        var path = Path.Combine(AppFolderPath, "personalization", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>把用户选择的本地视频复制到数据目录，返回持久化文件名。</summary>
    public string ImportCustomIntroVideo(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected intro video does not exist.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedCustomIntroVideoExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Unsupported intro video type: {extension}");
        }

        var folder = Path.Combine(AppFolderPath, "personalization");
        Directory.CreateDirectory(folder);
        var fileName = "custom-intro-video-" + Guid.NewGuid().ToString("N") + extension;
        var destinationPath = Path.Combine(folder, fileName);
        var tempPath = destinationPath + "." + Path.GetRandomFileName() + ".tmp";

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 清理临时文件失败不影响已复制视频的可用性。
            }
        }

        return fileName;
    }

    /// <summary>删除除当前视频外的旧自定义入场动画。</summary>
    public void DeleteOtherCustomIntroVideos(string? keepFileName)
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-intro-video-*"))
        {
            if (!string.IsNullOrWhiteSpace(keepFileName) &&
                string.Equals(Path.GetFileName(path), Path.GetFileName(keepFileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteCustomIntroVideo(path);
        }
    }

    /// <summary>删除全部自定义入场动画。</summary>
    public void DeleteCustomIntroVideos()
    {
        var folder = Path.Combine(AppFolderPath, "personalization");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "custom-intro-video-*"))
        {
            TryDeleteCustomIntroVideo(path);
        }
    }

    private static void TryDeleteCustomIntroVideo(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除自定义入场动画失败：{ex.Message}");
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            // 先写临时文件再原子替换，避免写入中断留下半截 settings.json。
            var tempPath = SettingsFilePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                Directory.CreateDirectory(AppFolderPath);
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                File.WriteAllText(tempPath, json);
                if (File.Exists(SettingsFilePath))
                {
                    File.Replace(tempPath, SettingsFilePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, SettingsFilePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("保存应用设置失败。", ex);
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 清理残留临时文件失败无需上报。
                }
            }
        }
    }

    private static List<AccountGroup> CloneGroups(IEnumerable<AccountGroup> groups) =>
        groups.Select(group => new AccountGroup
        {
            Id = group.Id,
            Name = group.Name,
            Order = group.Order
        }).ToList();
}

internal sealed class AppSettings
{
    /// <summary>界面语言代码：zh-Hans / en / zh-Hant。null 表示尚未选择（首次启动按系统语言推断）。</summary>
    public string? Language { get; set; }

    /// <summary>主题：Default（跟随系统）/ Light / Dark / Custom（自定义主题色）。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>表格行分隔线自定义颜色，格式 #AARRGGBB 或 #RRGGBB；null 表示跟随主题。</summary>
    public string? TableSeparatorColor { get; set; }

    /// <summary>是否显示账号表格的行分隔线。默认关闭。</summary>
    public bool ShowTableSeparators { get; set; }

    /// <summary>自定义界面主题色，格式 #RRGGBB；null 表示跟随系统强调色。</summary>
    public string? UiColor { get; set; }

    /// <summary>是否每秒自动旋转界面主题色。</summary>
    public bool UiColorAnimated { get; set; }

    /// <summary>自定义背景默认不透明度。</summary>
    public const double DefaultCustomBackgroundOpacity = 0.4;

    /// <summary>入场动画是否默认启用。</summary>
    public const bool DefaultIntroVideoEnabled = true;

    /// <summary>数据目录 personalization 文件夹内的自定义入场动画文件名；null 表示使用内置动画。</summary>
    public string? CustomIntroVideoFileName { get; set; }

    /// <summary>自定义入场动画的原始文件名，仅用于设置页展示。</summary>
    public string? CustomIntroVideoDisplayName { get; set; }

    /// <summary>是否在下次启动时播放入场动画。</summary>
    public bool IntroVideoEnabled { get; set; } = DefaultIntroVideoEnabled;

    /// <summary>是否启用用户自定义图片背景。</summary>
    public bool CustomBackgroundEnabled { get; set; }

    /// <summary>数据目录 personalization 文件夹内的自定义背景图片文件名。</summary>
    public string? CustomBackgroundImageFileName { get; set; }

    /// <summary>自定义背景不透明度，范围 0.1～0.9。</summary>
    public double CustomBackgroundOpacity { get; set; } = DefaultCustomBackgroundOpacity;
    /// <summary>主窗口逻辑宽度（DIP）；null 表示使用默认宽度。</summary>
    public int? WindowWidth { get; set; }
    /// <summary>主窗口逻辑高度（DIP）；null 表示使用默认高度。</summary>
    public int? WindowHeight { get; set; }
    /// <summary>是否记住主窗口上次关闭时的屏幕位置。</summary>
    public bool RememberWindowPosition { get; set; }
    /// <summary>记住的主窗口屏幕 X 坐标（物理像素）。</summary>
    public int? WindowX { get; set; }
    /// <summary>记住的主窗口屏幕 Y 坐标（物理像素）。</summary>
    public int? WindowY { get; set; }

    /// <summary>
    /// 已解析并持久化的 Steam 安装目录（含 steam.exe 的根目录）。首次启动自动检测后写入，之后直接复用，
    /// 不必每次上号重新检测。null/空表示尚未解析；失效（目录里找不到 steam.exe）时会重新自动检测，
    /// 仍找不到则弹框让用户手动选择。详见 <see cref="SteamPathCoordinator"/>。
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>唯一的 CS2 配装预设，供装备页面编辑与登录页一键装配。新用户用项目内置默认配装。</summary>
    public CsLoadoutPreset Loadout { get; set; } = CsLoadoutPreset.Default();

    /// <summary>「个性化」面板里设置的昵称，供登录页一键把账号资料设为该值。null/空表示不改昵称。</summary>
    public string? PersonaName { get; set; }

    /// <summary>「个性化」面板里设置的真实姓名（资料页 real_name 字段）。null/空表示不改。</summary>
    public string? ProfileRealName { get; set; }

    /// <summary>「个性化」面板里设置的概要（资料页 summary 字段，可多行）。null/空表示不改。</summary>
    public string? ProfileSummary { get; set; }

    /// <summary>是否在「一键个性化」完成后清空账号的曾用名记录。</summary>
    public bool ClearAliasHistoryOnPersonalize { get; set; }

    /// <summary>用户自定义账号分组定义（名称/排序）。成员关系存于各账号的 GroupIds，此处只存定义。</summary>
    public List<AccountGroup> Groups { get; set; } = [];

    /// <summary>新版账号管理页面独立使用的分组定义，与历史账号页面的 Groups 完全分开。</summary>
    public List<AccountGroup>? WhiteAccountGroups { get; set; }

    /// <summary>是否在登录时把「来源账号」的 CS2 本地设置复制到要登录的账号。</summary>
    public bool Cs2SyncOnLogin { get; set; }

    /// <summary>CS2 设置同步的「来源账号」SteamID64；空表示未选择。详见 <see cref="Cs2CloudService"/>。</summary>
    public string? Cs2SyncSourceSteamId { get; set; }

    /// <summary>更新检查使用的 GitHub 站点代码（direct / gh-proxy.org / v4.gh-proxy.org / v6.gh-proxy.org / cdn.gh-proxy.org），与 GitHubUpdateService.ProxySites 保持一致；未知值回退 direct。</summary>
    public string UpdateProxySite { get; set; } = "direct";

    /// <summary>VPN（Clash Verge）可执行文件路径；空表示尚未探测。详见 <see cref="VpnProxyService"/>。</summary>
    public string? VpnExecutablePath { get; set; }

    /// <summary>是否让本程序访问 GitHub（更新检查/下载安装包）时走 VPN 的本地代理端口；默认关闭。</summary>
    public bool VpnProxyEnabled { get; set; }

    /// <summary>VPN 本地代理端口；0 表示自动探测常见端口（7897/7890/…）。</summary>
    public int VpnProxyPort { get; set; }

    /// <summary>Clash 订阅链接：本程序用它自己拉起内核（不需要启动 Clash 软件）。详见 <see cref="VpnCoreService"/>。</summary>
    public string? VpnSubscriptionUrl { get; set; }

    /// <summary>本程序内置内核监听的本地端口；0 = 用默认 17897。</summary>
    public int VpnCorePort { get; set; }

    /// <summary>
    /// 代理接管方式：system = 系统代理（把 Windows 系统代理指向本程序内核端口，全系统流量都走代理）；
    /// tun = 虚拟网卡（TUN，接管本机全部流量，需要管理员权限与 wintun.dll）。默认 system。
    /// 用户在设置页选的这一项会被记住，下次启动按上次的选择来。详见 <see cref="VpnCoreService"/>。
    /// </summary>
    public string VpnTakeover { get; set; } = VpnCoreService.TakeoverSystem;

    /// <summary>代理模式：rule = 规则（按订阅 rules 分流）；global = 全局（所有流量走代理）。默认 rule。</summary>
    public string VpnMode { get; set; } = VpnCoreService.ModeRule;

    /// <summary>
    /// 内核本地控制口的 secret（首次使用时自动生成）。控制口只监听 127.0.0.1，用它查节点延迟。
    /// </summary>
    public string? VpnControllerSecret { get; set; }

    /// <summary>
    /// 用户在设置页选的节点名（会排进自建分组首位，规则/全局模式都优先走它）。
    /// null/空 = 自动：按订阅自己的节点顺序。订阅里没有该节点时按自动处理（见 <see cref="VpnCoreService.RewriteConfig"/>）。
    /// </summary>
    public string? VpnNode { get; set; }
}

// 与账号历史一致用 source generator：JsonSerializerDefaults.Web（camelCase、大小写不敏感），AOT 下可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CsLoadoutPreset))]
[JsonSerializable(typeof(AccountGroup))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

