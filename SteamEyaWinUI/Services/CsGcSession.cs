using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal static class CsGcSession
{
    public const uint Cs2AppId = 730;
    public const uint ClientHello = 4006;
    public const uint ClientWelcome = 4004;
    public const uint CsClientVersion = 2_000_244;

    public static Task<byte[]> ConnectAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return RequestWelcomeAsync(
            cmClient,
            TimeSpan.FromSeconds(45),
            cancellationToken);
    }

    public static async Task<byte[]> RequestWelcomeAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // cacheUnmatched: 欢迎消息可能在这次等待之前就已经到了（GC 有时主动先发），
            // 不缓存的话第一轮要死等 4 秒才重发 —— 这就是日志里「进入 CS2 GC」花掉 4 秒多的原因。
            // 单轮只等 0.8 秒：日志实证「第一轮发 ClientHello 经常没有回应，重发那轮 300ms 内就回」，
            // 等满 4 秒纯属白等。缩短单轮 → 更快重发，整体反而更快拿到欢迎消息。
            var welcomeTask = cmClient.WaitForGcMessageAsync(
                Cs2AppId,
                ClientWelcome,
                TimeSpan.FromSeconds(0.8),
                cancellationToken,
                cacheUnmatched: true);

            await cmClient.SendGcProtobufMessageAsync(
                Cs2AppId,
                ClientHello,
                EncodeClientHello(),
                cancellationToken);

            try
            {
                var message = await welcomeTask;
                return message.Payload;
            }
            catch (TimeoutException)
            {
            
            }
        }

        throw new TimeoutException(Loc.T("Cs_Gc_ConnectTimeout"));
    }

    public static byte[] EncodeClientHello()
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, CsClientVersion);
            writer.WriteUInt32(3, 0);
            writer.WriteUInt32(4, 0);
            writer.WriteUInt32(9, 0);
        });
    }

    public static uint GetAccountId(ulong steamId64) =>
        (uint)(steamId64 & 0xFFFFFFFF);
}
