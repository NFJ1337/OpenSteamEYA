using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

internal enum LegacyCredentialMigrationStatus
{
    NotNeeded,
    Migrated,
    NeedsPassphrase,
    WrongPassphrase,
    Failed
}

internal sealed record LegacyCredentialMigrationResult(
    string FilePath,
    LegacyCredentialMigrationStatus Status,
    int DecryptedFields,
    string? Error);

/// <summary>
/// 从旧加密版本升级时执行的一次性账号迁移。
/// 这里只有旧格式解密能力，不提供任何新的加密写入路径；迁移成功后立即改写成明文 JSON。
/// </summary>
internal static class LegacyCredentialMigration
{
    private const string Prefix = "enc:v3:";
    private const string LegacyPrefix = "dpapi:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int CryptprotectUiForbidden = 0x1;

    private static readonly string[] SensitiveFields =
        ["eyaToken", "password", "sharedSecret", "emailPassword"];

    private static readonly string[] BuiltInSecrets =
    [
        "SteamEYA.BuiltIn.Key.v1|4c1f9a37e6b8d205f7a4c93e15d8b06a"
    ];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static IReadOnlyList<LegacyCredentialMigrationResult> MigrateAccountFiles(string? passphrase = null)
    {
        var files = new[]
        {
            Path.Combine(AppPaths.DataRoot, "history", "accounts.json"),
            Path.Combine(AppPaths.DataRoot, "white-accounts.json")
        };

        return files
            .Where(File.Exists)
            .Select(file => MigrateFile(file, passphrase))
            .ToList();
    }

    public static bool HasPassphraseProtectedAccountFiles()
    {
        var files = new[]
        {
            Path.Combine(AppPaths.DataRoot, "history", "accounts.json"),
            Path.Combine(AppPaths.DataRoot, "white-accounts.json")
        };

        return files.Where(File.Exists).Any(IsPassphraseProtected);
    }

    /// <summary>防止没有完成迁移的密文被新版当作明文再次保存。</summary>
    public static bool ContainsEncryptedCredentialData(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return json.Contains(Prefix, StringComparison.Ordinal) ||
                       json.Contains(LegacyPrefix, StringComparison.Ordinal);
            }

            if (root["vault"] is not null)
            {
                return true;
            }

            if (root["accounts"] is not JsonArray accounts)
            {
                return false;
            }

            return accounts
                .OfType<JsonObject>()
                .Any(account => SensitiveFields.Any(field =>
                    TryGetString(account, field, out var value) &&
                    IsEncrypted(value)));
        }
        catch (JsonException)
        {
            return json.Contains(Prefix, StringComparison.Ordinal) ||
                   json.Contains(LegacyPrefix, StringComparison.Ordinal);
        }
    }

    private static LegacyCredentialMigrationResult MigrateFile(string filePath, string? passphrase)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            if (!ContainsEncryptedCredentialData(json))
            {
                return new LegacyCredentialMigrationResult(
                    filePath, LegacyCredentialMigrationStatus.NotNeeded, 0, null);
            }

            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return Failed(filePath, "账号文件不是有效的 JSON 对象。");
            }

            var header = root["vault"] as JsonObject;
            byte[]? dek = null;
            if (header is not null)
            {
                var hasPassphrase = TryReadPassphraseParameters(
                    header, out var passphraseSalt, out var passphraseIterations);
                if (hasPassphrase && string.IsNullOrEmpty(passphrase))
                {
                    return new LegacyCredentialMigrationResult(
                        filePath, LegacyCredentialMigrationStatus.NeedsPassphrase, 0, null);
                }

                dek = OpenDataKey(header, passphrase);
                if (dek is null)
                {
                    if (hasPassphrase)
                    {
                        return new LegacyCredentialMigrationResult(
                            filePath, LegacyCredentialMigrationStatus.WrongPassphrase, 0, null);
                    }

                    return Failed(filePath, "旧凭据库数据密钥无法解封。");
                }
            }

            var decryptedFields = 0;
            if (root["accounts"] is JsonArray accounts)
            {
                foreach (var account in accounts.OfType<JsonObject>())
                {
                    foreach (var field in SensitiveFields)
                    {
                        if (!TryGetString(account, field, out var value) || !IsEncrypted(value))
                        {
                            continue;
                        }

                        var plaintext = value!.StartsWith(Prefix, StringComparison.Ordinal)
                            ? DecryptV3Value(value, dek)
                            : DecryptLegacyValue(value);
                        if (plaintext is null)
                        {
                            return Failed(filePath, "旧账号密文无法解密。");
                        }

                        account[field] = plaintext;
                        decryptedFields++;
                    }
                }
            }

            // 如果还有字段没解开，上一步已经中止；确认无残留后再删除头部。
            root.Remove("vault");
            var plaintextJson = root.ToJsonString(WriteOptions);
            if (ContainsEncryptedCredentialData(plaintextJson))
            {
                return Failed(filePath, "迁移后仍检测到加密字段。");
            }

            var tempPath = filePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                File.WriteAllText(tempPath, plaintextJson, new UTF8Encoding(false));
                File.Move(tempPath, filePath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return new LegacyCredentialMigrationResult(
                filePath, LegacyCredentialMigrationStatus.Migrated, decryptedFields, null);
        }
        catch (Exception ex)
        {
            return Failed(filePath, ex.Message);
        }
    }

    private static byte[]? OpenDataKey(JsonObject header, string? passphrase)
    {
        if (!TryGetString(header, "wrappedDek", out var wrappedText) ||
            string.IsNullOrWhiteSpace(wrappedText))
        {
            return null;
        }

        var wrapped = Convert.FromBase64String(wrappedText);
        if (TryReadPassphraseParameters(header, out var passphraseSalt, out var passphraseIterations))
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                return null;
            }

            var passphraseKey = Rfc2898DeriveBytes.Pbkdf2(
                passphrase,
                passphraseSalt,
                passphraseIterations,
                HashAlgorithmName.SHA256,
                KeySize);
            wrapped = AesGcmOpen(passphraseKey, wrapped);
            if (wrapped is null)
            {
                return null;
            }
        }

        var entropy = Encoding.UTF8.GetBytes(
            "SteamEYA.Credentials.v3|" + ReadMachineGuid());
        var inner = DpapiUnprotect(wrapped, entropy);
        if (inner.Length == KeySize)
        {
            // 极早期格式：DPAPI 内层直接就是 DEK。
            return inner;
        }

        if (!TryGetInt(header, "kid", out var keyId) ||
            keyId < 1 ||
            keyId > BuiltInSecrets.Length ||
            !TryGetString(header, "salt", out var saltText) ||
            string.IsNullOrWhiteSpace(saltText))
        {
            return null;
        }

        var iterations = TryGetInt(header, "iterations", out var value) && value > 0
            ? value
            : 600_000;
        var key = Rfc2898DeriveBytes.Pbkdf2(
            BuiltInSecrets[keyId - 1],
            Convert.FromBase64String(saltText),
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        var dek = AesGcmOpen(key, inner);
        return dek is { Length: KeySize } ? dek : null;
    }

    private static bool IsPassphraseProtected(string filePath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(filePath)) is not JsonObject root ||
                root["vault"] is not JsonObject header)
            {
                return false;
            }

            return TryReadPassphraseParameters(header, out _, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPassphraseParameters(
        JsonObject header,
        out byte[] salt,
        out int iterations)
    {
        salt = [];
        iterations = 600_000;
        if (header["pw"] is not JsonObject passphrase ||
            !TryGetString(passphrase, "salt", out var saltText) ||
            string.IsNullOrWhiteSpace(saltText))
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(saltText);
        }
        catch (FormatException)
        {
            return false;
        }

        iterations = TryGetInt(passphrase, "iterations", out var value) && value > 0
            ? value
            : 600_000;
        return salt.Length >= 8;
    }

    private static string? DecryptV3Value(string value, byte[]? dek)
    {
        if (dek is null)
        {
            return null;
        }

        var payload = Convert.FromBase64String(value[Prefix.Length..]);
        var plaintext = AesGcmOpen(dek, payload);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }

    private static string? DecryptLegacyValue(string value)
    {
        try
        {
            var cipher = Convert.FromBase64String(value[LegacyPrefix.Length..]);
            var plaintext = DpapiUnprotect(
                cipher,
                Encoding.UTF8.GetBytes("SteamEYA.Credentials.v1"));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? AesGcmOpen(byte[] key, byte[] payload)
    {
        if (payload.Length < NonceSize + TagSize)
        {
            return null;
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plaintext, associatedData: null);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] DpapiUnprotect(byte[] cipher, byte[] entropy)
    {
        var dataBlob = CreateBlob(cipher);
        var entropyBlob = CreateBlob(entropy);
        try
        {
            if (!CryptUnprotectData(
                    ref dataBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptprotectUiForbidden,
                    out var plainBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "旧账号数据解密失败。");
            }

            try
            {
                var output = new byte[plainBlob.cbData];
                Marshal.Copy(plainBlob.pbData, output, 0, output.Length);
                return output;
            }
            finally
            {
                LocalFree(plainBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetString(JsonObject obj, string name, out string? value)
    {
        value = null;
        return obj[name] is JsonValue node &&
               node.TryGetValue(out value);
    }

    private static bool TryGetInt(JsonObject obj, string name, out int value)
    {
        value = 0;
        return obj[name] is JsonValue node &&
               node.TryGetValue(out value);
    }

    private static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith(Prefix, StringComparison.Ordinal) ||
         value.StartsWith(LegacyPrefix, StringComparison.Ordinal));

    private static LegacyCredentialMigrationResult Failed(string filePath, string error) =>
        new(filePath, LegacyCredentialMigrationStatus.Failed, 0, error);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响迁移结果。
        }
    }

    private static DATA_BLOB CreateBlob(byte[] data)
    {
        var blob = new DATA_BLOB
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
