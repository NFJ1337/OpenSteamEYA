namespace SteamEyaWinUI.Services;

internal sealed record WhiteAccountImportEntry(
    string AccountName,
    string Password,
    string? SharedSecret,
    string? Email,
    string? EmailPassword);

/// <summary>
/// 白号文本行解析：每行「账号----密码」，并兼容后续的「手机令牌」或「邮箱 + 邮箱密码」。
/// </summary>
internal static class WhiteAccountLineParser
{
    public static List<WhiteAccountImportEntry> Parse(string? text, bool allowMissingPassword = false)
    {
        var entries = new List<WhiteAccountImportEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return entries;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Contains("----", StringComparison.Ordinal)
                ? line.Split("----", StringSplitOptions.None)
                : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || (!allowMissingPassword && parts.Length < 2))
            {
                continue;
            }

            var accountName = parts[0].Trim();
            var password = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (accountName.Length == 0 ||
                (!allowMissingPassword && password.Length == 0) ||
                !seen.Add(accountName))
            {
                continue;
            }

            ParseOptionalFields(parts, Math.Min(2, parts.Length), out var sharedSecret, out var email, out var emailPassword);
            entries.Add(new WhiteAccountImportEntry(accountName, password, sharedSecret, email, emailPassword));
        }

        return entries;
    }

    /// <summary>
    /// 修复旧版「添加账号」把整行剩余字段塞进密码的问题：
    /// 原密码实际是第一个字段，后续邮箱与邮箱密码（或手机令牌）需要拆回各自字段。
    /// 只有确实识别到邮箱字段时才修复，避免误伤合法密码里恰好出现的 "----"。
    /// </summary>
    public static bool TryRepairStoredCredential(
        string? storedCredential,
        out string password,
        out string? sharedSecret,
        out string? email,
        out string? emailPassword)
    {
        password = string.Empty;
        sharedSecret = null;
        email = null;
        emailPassword = null;
        if (string.IsNullOrEmpty(storedCredential) ||
            !storedCredential.Contains("----", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = storedCredential.Split("----", StringSplitOptions.None);
        if (parts.Length < 3 || !parts.Skip(1).Any(part => part.Contains('@')))
        {
            return false;
        }

        password = parts[0].Trim();
        if (password.Length == 0)
        {
            return false;
        }

        ParseOptionalFields(parts, 1, out sharedSecret, out email, out emailPassword);
        return email is not null;
    }

    private static void ParseOptionalFields(
        string[] parts,
        int start,
        out string? sharedSecret,
        out string? email,
        out string? emailPassword)
    {
        sharedSecret = null;
        email = null;
        emailPassword = null;

        for (var i = start; i < parts.Length; i++)
        {
            var field = parts[i].Trim();
            if (field.Length == 0)
            {
                continue;
            }

            if (email is null && field.Contains('@'))
            {
                email = field;
                if (i + 1 < parts.Length)
                {
                    var next = parts[i + 1].Trim();
                    emailPassword = next.Length == 0 ? null : next;
                    i++;
                }
            }
            else
            {
                if (email is null && LooksLikeSharedSecret(field))
                {
                    sharedSecret ??= field;
                }
            }
        }
    }

    private static bool LooksLikeSharedSecret(string value)
    {
        if (value.Length is < 20 or > 64)
        {
            return false;
        }

        return value.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '_' or '-');
    }
}
