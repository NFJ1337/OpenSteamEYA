using System.Globalization;
using System.IO;
using System.Text;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「CS2 设置云强推」辅助进程（<c>SteamEyaWinUI.exe --cs2-cloud-push …</c>，见 Program.cs）。
///
/// 为什么要单独一个进程：Steam 客户端把「以 AppID 730 调用 SteamAPI_Init 的进程」当作 CS2 游戏进程，
/// 且「正在运行」状态要等该进程退出才会清除（SteamAPI_Shutdown 只断开 API、不结束游戏会话）。
/// 早期实现直接在上号器 GUI 进程里 init，导致 CS2 在 Steam 里永远显示「正在运行」，
/// 用户点「停止」时 Steam 会强杀它认定的游戏进程——也就是上号器本身。
/// 故 init/写云/shutdown 全部放进本短命进程：推完立即退出，Steam 数秒内自动清除「正在运行」，
/// 「停止」按钮最多杀掉本辅助进程、绝不伤及主界面。
///
/// 协议：argv = [switch, payloadDir, maxWaitSeconds, expectedSteamId64|"-"]。
/// payloadDir 内是待推送的 cfg 文件（文件名即云端名）；结果写回 payloadDir\result.txt（key=value 行）。
/// 本进程不做本地化：错误以 i18n 键（errorKey）或原始文本（errorText）回传，由主进程翻译展示。
/// </summary>
internal static class Cs2CloudPushWorker
{
    public const string CommandLineSwitch = "--cs2-cloud-push";
    public const string ResultFileName = "result.txt";
    public const string NoExpectedSteamIdToken = "-";

    private const uint Cs2AppId = 730;

    /// <summary>辅助进程主体。返回进程退出码：0=已写出结果文件（成败看文件内容），非 0=连结果都没能落盘。</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 4 || !Directory.Exists(args[1]))
        {
            AppLog.Warn("[cs2push] 辅助进程参数不足或 payload 目录不存在，直接退出。");
            return 2;
        }

        var payloadDir = args[1];
        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxWaitSeconds))
        {
            maxWaitSeconds = 4;
        }

        ulong? expectedSteamId = null;
        if (args[3] != NoExpectedSteamIdToken &&
            ulong.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            expectedSteamId = parsed;
        }

        AppLog.Info($"[cs2push] 辅助进程启动：maxWait={maxWaitSeconds}s expected={args[3]}");

        WorkerOutcome outcome;
        try
        {
            var files = LoadPayload(payloadDir);
            outcome = files.Count == 0
                ? WorkerOutcome.Fail("Cs2Cloud_Error_NoSource")
                : PushWithRetry(files, maxWaitSeconds, expectedSteamId);
        }
        catch (Exception ex)
        {
            AppLog.Error("[cs2push] 推送异常。", ex);
            outcome = WorkerOutcome.FailText(ex.Message);
        }

        var written = TryWriteResult(payloadDir, outcome);
        AppLog.Info($"[cs2push] 辅助进程结束：ok={outcome.Ok} pushed={outcome.Pushed} 结果文件={(written ? "已写出" : "写出失败")}");
        return written ? 0 : 3;
    }

    private sealed record WorkerOutcome(
        bool Ok, int Pushed, int PartialFailed, bool AccountCloudDisabled, string? ErrorKey, string? ErrorText)
    {
        public static WorkerOutcome Fail(string errorKey) => new(false, 0, 0, false, errorKey, null);

        public static WorkerOutcome FailText(string errorText) => new(false, 0, 0, false, null, errorText);
    }

    private static IReadOnlyList<Cs2CfgFile> LoadPayload(string payloadDir)
    {
        var result = new List<Cs2CfgFile>();
        foreach (var path in Directory.GetFiles(payloadDir))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, ResultFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new Cs2CfgFile(name, File.ReadAllBytes(path)));
        }

        return result;
    }

    // maxWaitSeconds 内以 2s 间隔重试（等 Steam 启动登录就绪 / 登到目标账号），与旧 in-process 版语义一致。
    private static WorkerOutcome PushWithRetry(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, ulong? expectedSteamId)
    {
        EnsureAppIdContext();

        var attempts = Math.Max(1, maxWaitSeconds / 2);
        var lastRetryOutcome = WorkerOutcome.Fail("Cs2Cloud_Error_InitFailed");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var (retry, outcome) = TryPushOnce(files, expectedSteamId);
            if (!retry)
            {
                return outcome;
            }

            lastRetryOutcome = outcome; // 保留最后一次重试原因（init 失败 / 账号不符），超时后据此告知用户。
            if (attempt < attempts - 1)
            {
                Thread.Sleep(2000);
            }
        }

        return lastRetryOutcome;
    }

    // 单次尝试。返回 (retry, outcome)：retry=true 表示「值得等 Steam 登好/登到目标账号再试」
    //（SteamAPI_Init 失败，或当前登录账号还不是目标账号）；
    // 其余情况（DLL 缺失/接口拿不到/写入完成/异常）都 retry=false，直接以 outcome 返回，不再重试。
    private static (bool Retry, WorkerOutcome Outcome) TryPushOnce(
        IReadOnlyList<Cs2CfgFile> files, ulong? expectedSteamId)
    {
        var inited = false;
        try
        {
            var errMsg = new byte[1024]; // SteamErrMsg = char[k_cchMaxSteamErrMsg=1024]
            var initResult = SteamworksNative.SteamAPI_InitFlat(errMsg);
            inited = initResult == 0; // 0 = k_ESteamAPIInitResult_OK
            if (!inited)
            {
                AppLog.Warn($"[cs2push] SteamAPI_InitFlat 失败（result={initResult}）：{DecodeErrMsg(errMsg)}");
                return (true, WorkerOutcome.Fail("Cs2Cloud_Error_InitFailed"));
            }

            // 登录时推送：核对当前登录账号确为目标账号，不符则继续等（宁可超时放弃也不写错账号）。
            if (expectedSteamId is { } expected && !IsLoggedInAs(expected))
            {
                return (true, WorkerOutcome.Fail("Cs2Cloud_Error_WrongAccount"));
            }

            var remoteStorage = SteamworksNative.SteamAPI_SteamRemoteStorage_v016();
            if (remoteStorage == IntPtr.Zero)
            {
                return (false, WorkerOutcome.Fail("Cs2Cloud_Error_NoInterface"));
            }

            // 应用级云（CS2 属性→通用「在 Steam 云中保存游戏」，注册表 Apps\730\Cloud）——这一层能用 SDK 代开，
            // 这里主动开启并回读确认是否生效（未生效多半是账号级总开关关着，见下）。
            SteamworksNative.SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(remoteStorage, true);
            if (!SteamworksNative.SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(remoteStorage))
            {
                AppLog.Warn("[cs2push] 已请求开启 CS2 应用级云，但回读仍为关闭（通常是账号级总开关未开导致应用级无效）。");
            }

            // 账号级云是用户在 Steam 设置→云 里的总开关，属服务器端账户设置，SDK/本地都无法代开；
            // 关着时 FileWrite 只写本地、不上云。此处只能检测并如实回报，由 UI 提示用户去开这一个开关。
            var accountCloudDisabled =
                !SteamworksNative.SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(remoteStorage);
            if (accountCloudDisabled)
            {
                AppLog.Warn("[cs2push] 当前账号未开启账号级 Steam 云（Steam 设置→云→启用 Steam 云），FileWrite 只写本地、不会上云。");
            }

            var pushed = 0;
            foreach (var file in files)
            {
                if (SteamworksNative.SteamAPI_ISteamRemoteStorage_FileWrite(
                        remoteStorage, file.Name, file.Data, file.Data.Length))
                {
                    pushed++;
                }
                else
                {
                    AppLog.Warn($"[cs2push] CS2 云 FileWrite 失败：{file.Name}");
                }
            }

            AppLog.Info($"[cs2push] CS2 云强推：成功写入 {pushed}/{files.Count} 个文件（账号云禁用={accountCloudDisabled}）。");
            if (pushed == 0)
            {
                return (false, WorkerOutcome.Fail("Cs2Cloud_Error_WriteFailed"));
            }

            // 部分文件写入失败：仍算已推送但明确告知用户不完整。
            return (false, new WorkerOutcome(true, pushed, files.Count - pushed, accountCloudDisabled, null, null));
        }
        catch (DllNotFoundException)
        {
            return (false, WorkerOutcome.Fail("Cs2Cloud_Error_DllMissing"));
        }
        catch (EntryPointNotFoundException)
        {
            return (false, WorkerOutcome.Fail("Cs2Cloud_Error_DllVersion"));
        }
        catch (Exception ex)
        {
            AppLog.Error("[cs2push] CS2 云强推异常。", ex);
            return (false, WorkerOutcome.FailText(ex.Message));
        }
        finally
        {
            // Shutdown 只断开 API；「正在运行」状态靠本进程随后立即退出来清除。
            // 环境变量无需清理：本进程不再拉起任何子进程，退出即消亡。
            if (inited)
            {
                SteamworksNative.SteamAPI_Shutdown();
            }
        }
    }

    // 核对当前登录 Steam 账号的 SteamID64 是否等于期望目标。拿不到用户接口（DLL 版本不符等）时返回 true 放行，
    // 保持“不因核对能力缺失而彻底失效”，此时退化为“推给当前登录账号”的行为。
    private static bool IsLoggedInAs(ulong expectedSteamId64)
    {
        try
        {
            var user = SteamworksNative.SteamAPI_SteamUser_v023();
            if (user == IntPtr.Zero)
            {
                AppLog.Warn("[cs2push] 拿不到 ISteamUser 接口，跳过登录账号核对。");
                return true;
            }

            var actual = SteamworksNative.SteamAPI_ISteamUser_GetSteamID(user);
            if (actual == expectedSteamId64)
            {
                return true;
            }

            AppLog.Warn($"[cs2push] 当前登录账号（{actual}）与目标账号（{expectedSteamId64}）不符，暂不写云、继续等待。");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            AppLog.Warn("[cs2push] steam_api64.dll 无 ISteamUser v023 导出，跳过登录账号核对。");
            return true;
        }
    }

    // 让本进程以 CS2 身份初始化 Steamworks：设置 SteamAppId 环境变量（SDK 优先读它，最可靠），
    // 并在 exe 目录写一份 steam_appid.txt 兜底（部分 SDK 版本按工作目录读该文件，
    // 主进程启动本辅助进程时已把工作目录设为 exe 目录）。
    private static void EnsureAppIdContext()
    {
        var appId = Cs2AppId.ToString(CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", appId);
            Environment.SetEnvironmentVariable("SteamGameId", appId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] 设置 SteamAppId 环境变量失败：{ex.Message}");
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, appId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] 写 steam_appid.txt 失败：{ex.Message}");
        }
    }

    // 从 SteamErrMsg（char[1024]，ASCII、null 结尾）解出错误文字，用于日志诊断。
    private static string DecodeErrMsg(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static bool TryWriteResult(string payloadDir, WorkerOutcome outcome)
    {
        try
        {
            var builder = new StringBuilder()
                .Append("ok=").Append(outcome.Ok ? '1' : '0').Append('\n')
                .Append("pushed=").Append(outcome.Pushed.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("partialFailed=").Append(outcome.PartialFailed.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("accountCloudDisabled=").Append(outcome.AccountCloudDisabled ? '1' : '0').Append('\n');
            if (!string.IsNullOrEmpty(outcome.ErrorKey))
            {
                builder.Append("errorKey=").Append(outcome.ErrorKey).Append('\n');
            }

            if (!string.IsNullOrEmpty(outcome.ErrorText))
            {
                builder.Append("errorText=").Append(SingleLine(outcome.ErrorText)).Append('\n');
            }

            // 不带 BOM：带 BOM 时首行会被主进程解析成「﻿ok」而丢失 ok 键。
            File.WriteAllText(
                Path.Combine(payloadDir, ResultFileName), builder.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] 写结果文件失败：{ex.Message}");
            return false;
        }
    }

    // 结果文件按行解析，多行异常消息压成单行。
    private static string SingleLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
