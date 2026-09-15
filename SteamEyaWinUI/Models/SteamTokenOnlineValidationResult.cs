namespace SteamEyaWinUI.Models;

/// <summary>令牌在线校验结果。<paramref name="Result"/> = Steam EResult（0 = 未知/不适用），
/// 调用方可用它配合 SteamCmException.IsTokenFailure 判断「是令牌被拒」还是「网络/其它故障」。</summary>
public sealed record SteamTokenOnlineValidationResult(bool IsValid, string Status, int Result = 0);
