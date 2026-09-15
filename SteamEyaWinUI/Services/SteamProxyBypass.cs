using System.Net;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Steam 侧请求（CM / api.steampowered.com / store）的代理策略：
///   · 我们自己接管了系统代理（VPN 开关打开）→ <b>直连</b>。实测把 Steam 登录绕去节点，
///     「登录往返」会从 0.5 秒涨到 11.8 秒，而 Steam 的 CM 与这些接口直连本来就通；
///   · 没有接管时 → 沿用系统代理设置（那是用户自己的代理，程序不去动它）。
/// 注意只用在 Steam 的 CM/API/商店请求上；steamcommunity.com 那类页面（个人资料、创意工坊）
/// 仍按原样走系统代理，避免在需要代理的环境下反而访问不了。
/// </summary>
internal sealed class SteamProxyBypass : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) =>
        SystemProxyService.IsApplied ? null : WebRequest.GetSystemWebProxy().GetProxy(destination);

    public bool IsBypassed(Uri host) => SystemProxyService.IsApplied;
}