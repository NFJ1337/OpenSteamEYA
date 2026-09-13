using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SteamEyaWinUI.Services;

/// <summary>
/// Steam 手机令牌（Steam Guard 移动验证器）TOTP 验证码生成：由 shared_secret（base64）算出 5 位码。
/// 算法与社区标准实现（node-steam-totp 等）一致：HMAC-SHA1(secret, floor(unixTime/30) 大端) → 动态截断
/// → 逐位映射到 Steam 字母表。供批量导入白号时对带 shared_secret 的账号自动过 2FA（无需手动输码）。
/// </summary>
internal static class SteamTotp
{
    // Steam 验证码专用字母表（26 个字符），与手机令牌显示一致。
    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>由 base64 的 shared_secret 生成当前 5 位 Steam Guard 验证码；密钥空/非法返回 null。</summary>
    public static string? GenerateAuthCode(string? sharedSecretBase64, long? unixTimeSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(sharedSecretBase64))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(sharedSecretBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (key.Length == 0)
        {
            return null;
        }

        var time = unixTimeSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, time / 30L);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        // 动态截断：末字节低 4 位作偏移，取 4 字节 31 位整数。
        var offset = hash[19] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24)
                 | ((hash[offset + 1] & 0xFF) << 16)
                 | ((hash[offset + 2] & 0xFF) << 8)
                 | (hash[offset + 3] & 0xFF);

        Span<char> chars = stackalloc char[5];
        for (var i = 0; i < 5; i++)
        {
            chars[i] = Alphabet[code % Alphabet.Length];
            code /= Alphabet.Length;
        }

        return new string(chars);
    }
}
