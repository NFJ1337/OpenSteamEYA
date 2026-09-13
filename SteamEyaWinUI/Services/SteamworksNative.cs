using System.Runtime.InteropServices;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Steamworks SDK 扁平 C API 的最小 P/Invoke 绑定（仅「强推 cfg 到 Steam 云」所需）。
///
/// 重要：这些 API 只能在短命辅助进程（<see cref="Cs2CloudPushWorker"/>）里调用，绝不可在 GUI 主进程调用——
/// Steam 把「以 730 身份 SteamAPI_Init 的进程」当作 CS2 游戏进程，直到该进程退出前商店/库都显示「正在运行」，
/// 用户点「停止」时 Steam 还会强杀该进程（SteamAPI_Shutdown 只断开 API，不结束游戏会话）。
///
/// 运行前提（缺任一都会让 <see cref="SteamAPI_InitFlat"/> 返回非 0 或抛 DllNotFound）：
///   · 运行目录存在 <c>steam_api64.dll</c>（Steamworks SDK 再分发库，已随仓库入库并由构建拷入输出目录）；
///   · 运行目录存在 <c>steam_appid.txt</c>（内容为 730，令调用进程以 CS2 身份初始化，由 Cs2CloudPushWorker 自动写入）；
///   · Steam 正在运行且已登录，且当前登录账号拥有 CS2(730)。
///
/// 绑定的接口版本为 STEAMREMOTESTORAGE_INTERFACE_VERSION016（对应 Steamworks SDK ~1.5x/1.6x）。
/// 若换用不同版本的 steam_api64.dll、访问器 <c>SteamAPI_SteamRemoteStorage_v016</c> 符号名不匹配，
/// 会抛 EntryPointNotFoundException（已在上层捕获并提示）。
/// LibraryImport 为 Native AOT 友好的源生成 P/Invoke；DLL 在运行期解析，缺失不影响编译。
///
/// 每个导入都标 <see cref="DefaultDllImportSearchPaths"/>(ApplicationDirectory | System32)：只从应用目录 + System32
/// 解析 steam_api64.dll，绝不回退到当前工作目录 / PATH——否则攻击者在 CWD/PATH 植入同名 DLL 即可在这个持有
/// Steam 刷新令牌/凭据的进程里执行任意原生代码（合法 DLL 由构建拷进应用目录）。
/// </summary>
internal static partial class SteamworksNative
{
    private const string Lib = "steam_api64";
    private const DllImportSearchPath SafeSearch =
        DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32;

    /// <summary>
    /// 初始化 Steamworks API（连接正在运行的 Steam 客户端，以 SteamAppId 指定的 App 身份）。
    /// 返回 ESteamAPIInitResult：0 = k_ESteamAPIInitResult_OK；pOutErrMsg 为 char[1024] 错误信息缓冲。
    /// 注意：SDK 1.6x 的 steam_api64.dll 不再导出裸 <c>SteamAPI_Init</c>（头文件里那是内联包装，实测 dumpbin
    /// 无此导出），必须用导出的 <c>SteamAPI_InitFlat</c>，否则运行期抛 EntryPointNotFoundException。
    /// </summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial int SteamAPI_InitFlat(byte[] pOutErrMsg);

    /// <summary>释放 Steamworks API。与每次成功的 <see cref="SteamAPI_InitFlat"/> 配对调用。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial void SteamAPI_Shutdown();

    /// <summary>取当前用户的云存储接口（STEAMREMOTESTORAGE_INTERFACE_VERSION016）。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial IntPtr SteamAPI_SteamRemoteStorage_v016();

    /// <summary>取当前用户接口（STEAMUSER_INTERFACE_VERSION023，与 RemoteStorage v016 同属 SDK ~1.5x/1.6x）。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial IntPtr SteamAPI_SteamUser_v023();

    /// <summary>当前登录用户的 SteamID64（用于写云前核对确实是目标账号，避免覆盖到别的账号云端）。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);

    /// <summary>把一份文件同步写入当前账号该 App 的 Steam 云（覆盖同名文件）。</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_FileWrite(
        IntPtr self, string pchFile, byte[] pvData, int cubData);

    /// <summary>为当前 App 打开云开关（账号级云仍需开启，否则不实际上云）。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial void SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(
        IntPtr self, [MarshalAs(UnmanagedType.I1)] bool bEnabled);

    /// <summary>当前 App 是否已开启云同步。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(IntPtr self);

    /// <summary>账号级是否开启云同步。</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(IntPtr self);
}
