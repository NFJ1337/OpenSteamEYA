using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamEyaWinUI.Localization;
using Microsoft.Win32;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 本地凭据的三层加密（信封加密 / envelope encryption）。
///
///   L1 数据层：AES-256-GCM 逐字段加密（随机 nonce + 16 字节 tag）——机密性 + 完整性（改一字节即解密失败）
///   L2 绑定层：DPAPI(CryptProtectData, 当前 Windows 用户) 包住数据密钥 DEK，
///              熵里混入本机 MachineGuid —— 拷到别的机器/别的 Windows 用户都解不开
///   L3 密钥层：内置密钥（不是用户口令）。KEK = PBKDF2-HMAC-SHA256(BuiltInSecrets[kid], 随机盐, 60 万次)，
///              再用 AES-256-GCM 封住 DEK —— 默认就是三层加密，程序自动可解，用户无需设置任何口令。
///   L4 口令层：**可选、默认关闭**。用户在设置页设置口令后，在最外层再套一层
///              AES-256-GCM(PBKDF2-SHA256(口令, 每文件随机盐, 60 万次), L3 的产物)。口令不落盘、无法找回；
///              启用后每次启动都要输入口令才能读出账号。注意 L2 绑定「当前 Windows 用户 + 本机」，
///              所以口令只在同一台机器、同一个 Windows 用户下有效（数据目录换机后连口令也解不开）。
///
/// 落盘形态（每个凭据库文件一份 DEK，头部写在文档根上）：
///   { "vault": { "v":3, "kid":1, "kdf":"PBKDF2-SHA256", "salt":"<b64>", "iterations":600000, "wrappedDek":"<b64>",
///                "pw": { "kdf":"PBKDF2-SHA256", "salt":"<b64>", "iterations":600000 } },
///   （"pw" 只在启用第 4 层时出现；此时 wrappedDek = AES-GCM(口令 KEK, DPAPI(AES-GCM(内置 KEK, DEK)))）
///     "accounts": [ { "eyaToken":"enc:v3:<b64(nonce|tag|ciphertext)>", ... } ] }
///
/// 兼容：旧版 <c>dpapi:v1:</c> 值仍可读，读取后由调用方回写即完成迁移。
/// 密钥不可用（DEK 取不出）或存在解不开的密文时：只把密文原样读出，绝不把密文当明文再加密（避免二次加密
/// 损坏数据），也不写明文，并拒绝任何整文件写入（否则会把解不出的账号写没）。
/// </summary>
internal static class CredentialProtector
{
    /// <summary>当前版本密文前缀。</summary>
    internal const string Prefix = "enc:v3:";

    /// <summary>旧版（单层 DPAPI）密文前缀，仅用于识别与迁移。</summary>
    internal const string LegacyPrefix = "dpapi:v1:";

    /// <summary>密钥层 KDF 参数（迭代次数可随年份上调；已有文件按自身头部记录解）。</summary>
    internal const int DefaultIterations = 600_000;

    /// <summary>当前内置密钥版本（写入头部 kid）。内置密钥意味着：默认就是三层加密，但不需要用户设置任何口令。</summary>
    private const int CurrentKeyId = 1;

    /// <summary>内置密钥表（按 kid 索引，便于将来轮换；旧版本的密钥必须保留，否则旧文件读不出来）。</summary>
    private static readonly string[] BuiltInSecrets =
    [
        "SteamEYA.BuiltIn.Key.v1|4c1f9a37e6b8d205f7a4c93e15d8b06a"
    ];

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>落盘时加密的字段（camelCase，与 accounts.json 一致）。</summary>
    private static readonly string[] SensitiveFields = ["eyaToken", "password", "sharedSecret", "emailPassword"];

    private const int CryptprotectUiForbidden = 0x1;

    /// <summary>每个凭据库文件一份会话（DEK 只缓存在内存，进程退出即消失）。</summary>
    private sealed class VaultSession
    {
        public byte[]? Dek;
        public int Undecryptable;

        /// <summary>本会话是否解析过该文件的 vault 头部（没解析过 ≠ 锁定，例如文件还不存在）。</summary>
        public bool HeaderParsed;

        /// <summary>头部声明启用了 L4 口令层（文件里存在 "pw" 块）。</summary>
        public bool PassphraseEnabled;

        /// <summary>已解锁时内存里的口令派生密钥（KEK）：进程退出即消失，绝不落盘。</summary>
        public byte[]? PassphraseKek;

        /// <summary>与 PassphraseKek 匹配的盐与迭代次数（写回头部时沿用，保证 KEK 与文件对得上）。</summary>
        public byte[]? PassphraseSalt;
        public int PassphraseIterations = DefaultIterations;

        /// <summary>口令层刚被设置/取消：下一次落盘必须重建 vault 头部。</summary>
        public bool HeaderDirty;
    }

    private static readonly Dictionary<string, VaultSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    // ---------- 状态查询（供设置页 / 启动解锁提示使用）----------

    internal static bool IsUnlocked(string vaultPath) => GetSession(vaultPath).Dek is not null;

    /// <summary>
    /// 本次读取里是否存在"解不开"的密文（文件被篡改、或来自别的机器/Windows 用户）。
    /// 这种情况内存里的账号是空壳，任何整文件写入都必须拒绝，否则会把它们写没。
    /// </summary>
    internal static bool HasUndecryptableData(string vaultPath) => GetSession(vaultPath).Undecryptable > 0;

    /// <summary>
    /// 密钥取不到（口令没输、文件来自别的密钥方案或被篡改）——此时任何写入都必须拒绝，
    /// 否则会把解不出来的账号写没。注意：文件不存在（全新安装）或从未解析过头部，都不算锁定。
    /// </summary>
    internal static bool IsLocked(string vaultPath)
    {
        var session = GetSession(vaultPath);
        return session.HeaderParsed && session.Dek is null;
    }

    // ---------- L4 口令层：查询 / 解锁 / 设置 / 取消（可选，默认关闭） ----------

    /// <summary>该凭据库是否启用了 L4 口令层（只看文件头部，不需要先解锁）。</summary>
    internal static bool IsPassphraseEnabled(string vaultPath)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            EnsureHeaderParsed(vaultPath, session);
            return session.PassphraseEnabled;
        }
    }

    /// <summary>启用了口令层、但本次会话还没解开。</summary>
    internal static bool IsPassphraseLocked(string vaultPath)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            EnsureHeaderParsed(vaultPath, session);
            return session.PassphraseEnabled && session.Dek is null;
        }
    }

    /// <summary>
    /// 用口令解锁：解出 DEK 缓存在会话里。口令不对 / 文件被改返回 false；
    /// 文件不存在或未启用口令层时直接返回 true（无需口令）。
    /// </summary>
    internal static bool TryUnlockWithPassphrase(string vaultPath, string passphrase)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            var rawJson = ReadVaultText(vaultPath);
            if (rawJson is null)
            {
                return true;   // 文件还不存在：没有需要解锁的东西
            }

            if (!TryParseVaultHeader(rawJson, out var header) ||
                !TryReadPassphraseParameters(header, out var salt, out var iterations))
            {
                // 未启用口令层：正常解一次即可（不消耗口令）。
                return OpenDekCore(rawJson, session, null) is not null;
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                return false;
            }

            var kek = DeriveKey(passphrase, salt, iterations);
            var dek = OpenDekCore(rawJson, session, kek);
            if (dek is null)
            {
                return false;   // GCM tag 校验失败 = 口令不对（或文件被篡改）
            }

            session.PassphraseKek = kek;
            session.PassphraseSalt = salt;
            session.PassphraseIterations = iterations;
            session.Dek = dek;
            session.Undecryptable = 0;
            return true;
        }
    }

    /// <summary>只校验口令是否正确（不改会话状态），用于「取消口令」前的身份确认。</summary>
    internal static bool VerifyPassphrase(string vaultPath, string passphrase)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            var rawJson = ReadVaultText(vaultPath);
            if (rawJson is null)
            {
                return true;
            }

            if (!TryParseVaultHeader(rawJson, out var header) ||
                !TryReadPassphraseParameters(header, out var salt, out var iterations))
            {
                return true;   // 未启用口令层：没有口令可校验
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                return false;
            }

            return OpenDekCore(rawJson, session, DeriveKey(passphrase, salt, iterations)) is not null;
        }
    }

    /// <summary>
    /// 设置/更换口令：只换最外层的包装，DEK 与已有密文都不变。要求当前已解锁。
    /// 调用方随后必须落盘一次（AccountHistoryService.SetVaultPassphrase），新头部才会生效。
    /// </summary>
    internal static void SetPassphrase(string vaultPath, string passphrase)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            if (session.Dek is null)
            {
                if (session.HeaderParsed)
                {
                    throw new InvalidOperationException(Loc.T("Account_Error_VaultLocked"));
                }

                // 还没有任何凭据库文件（全新安装）：先造一个数据密钥，落盘时头部会一并写出。
                session.Dek = RandomNumberGenerator.GetBytes(KeySize);
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            session.PassphraseKek = DeriveKey(passphrase, salt, DefaultIterations);
            session.PassphraseSalt = salt;
            session.PassphraseIterations = DefaultIterations;
            session.PassphraseEnabled = true;
            session.HeaderDirty = true;
        }
    }

    /// <summary>取消口令层（要求已解锁）。调用方随后落盘一次即恢复为「程序自动解密」。</summary>
    internal static void ClearPassphrase(string vaultPath)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            if (session.Dek is null)
            {
                throw new InvalidOperationException(Loc.T("Account_Error_VaultLocked"));
            }

            session.PassphraseKek = null;
            session.PassphraseSalt = null;
            session.PassphraseIterations = DefaultIterations;
            session.PassphraseEnabled = false;
            session.HeaderDirty = true;
        }
    }

    /// <summary>
    /// .bak 是否需要被主文件覆盖：备份还是旧版明文，或主文件与备份的口令层状态不一致。
    /// 前者避免磁盘上残留明文凭据（否则旧的 .bak 就是绕过口令的后门）；
    /// 后者保证备份与主文件用同一套包装，恢复备份时不需要另一个口令。
    /// </summary>
    internal static bool NeedsBackupRefresh(string mainJson, string backupJson)
    {
        if (NeedsUpgradeOrPlaintext(backupJson))
        {
            return true;
        }

        return HasPassphraseLayer(mainJson) != HasPassphraseLayer(backupJson);
    }

    /// <summary>该文件头部是否启用了口令层（"pw" 块存在且可解析）。</summary>
    private static bool HasPassphraseLayer(string json) =>
        TryParseVaultHeader(json, out var header) && TryReadPassphraseParameters(header, out _, out _);

    // ---------- 落盘 / 读取 ----------

    /// <summary>序列化后的 JSON：确保有 vault 头部，并把敏感字段换成 enc:v3 密文。</summary>
    internal static string ProtectSensitiveFields(string json, string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        lock (Gate)
        {
            try
            {
                if (JsonNode.Parse(json) is not JsonObject root)
                {
                    return json;
                }

                var session = GetSession(vaultPath);
                var needsVault = root["vault"] is not JsonObject;
                var hasPlaintext = root["accounts"] is JsonArray a0 &&
                    a0.OfType<JsonObject>().Any(account => SensitiveFields.Any(field =>
                        account[field] is JsonValue v && v.TryGetValue(out string? text) && NeedsProtection(text)));

                // 安全闸：文件里已有本程序密文、却没有 vault 密钥头 —— 说明文件被外部改坏/被裁剪，
                // 此时绝不能"新造一个密钥"再写回去（那会把已有密文永久变成解不开的垃圾），直接拒绝写入。
                if (needsVault && root["accounts"] is JsonArray orphanAccounts &&
                    orphanAccounts.OfType<JsonObject>().Any(o => SensitiveFields.Any(f =>
                        o[f] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) && IsProtected(s!))))
                {
                    AppLog.Error($"凭据库缺少密钥头，已拒绝写入以避免损坏数据：{Path.GetFileName(vaultPath)}");
                    throw new InvalidOperationException(Loc.T("Account_Error_VaultHeaderMissing"));
                }

                if (session.Dek is null && needsVault)
                {
                    // 全新凭据库：首次写入就生成数据密钥并落头部（不设口令时无需解锁）。
                    session.Dek = RandomNumberGenerator.GetBytes(KeySize);
                }

                if (session.Dek is null)
                {
                    // 头部存在但密钥取不到：保持原样——绝不写明文，也不把密文当明文二次加密。
                    AppLog.Warn($"凭据库密钥不可用，跳过本次加密写入：{Path.GetFileName(vaultPath)}");
                    return json;
                }

                // 没有头部（新库）、头部未用内置密钥封层（旧格式），或刚设置/取消过口令层 → 都重建为标准头部。
                if (root["vault"] is not JsonObject header || header["kid"] is null || session.HeaderDirty)
                {
                    header = BuildHeader(session.Dek!, session);
                    root["vault"] = header;
                    session.HeaderDirty = false;
                }

                if (session.Dek is null)
                {
                    // 头部已存在且本次没有明文需要加密：不用碰敏感字段。
                    return root.ToJsonString();
                }

                if (root["accounts"] is JsonArray accounts)
                {
                    foreach (var node in accounts)
                    {
                        if (node is not JsonObject account)
                        {
                            continue;
                        }

                        foreach (var field in SensitiveFields)
                        {
                            if (account[field] is JsonValue value && value.TryGetValue(out string? text) && NeedsProtection(text))
                            {
                                account[field] = EncryptValue(text!, session.Dek);
                            }
                        }
                    }
                }

                return root.ToJsonString();
            }
            catch (Exception ex)
            {
                // 加密失败宁可本次仍按原样写，也不能丢数据；但要留痕。
                AppLog.Warn($"凭据加密失败，本次按原值写入：{ex.Message}");
                return json;
            }
        }
    }

    /// <summary>读取后的文档：解出明文；返回 true 表示检测到旧版明文（调用方应回写完成迁移）。</summary>
    internal static bool UnprotectInPlace(AccountHistoryDocument document, string vaultPath, string rawJson)
    {
        lock (Gate)
        {
            var session = GetSession(vaultPath);
            try
            {
                // 首次读取时建立会话：用内置密钥解出 DEK（不需要用户输入任何口令）
                if (session.Dek is null)
                {
                    session.Dek = OpenDek(rawJson, session);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"凭据库打开失败：{ex.Message}");
            }

            var needsMigration = false;
            session.Undecryptable = 0;
            foreach (var account in document.Accounts ?? [])
            {
                // 旧版明文，或旧版单层 dpapi:v1 密文，都要标记为待迁移（升级成带完整性校验的 enc:v3）。
                if (NeedsProtection(account.EyaToken) || NeedsProtection(account.Password) ||
                    NeedsProtection(account.SharedSecret) || NeedsProtection(account.EmailPassword) ||
                    IsLegacy(account.EyaToken) || IsLegacy(account.Password) ||
                    IsLegacy(account.SharedSecret) || IsLegacy(account.EmailPassword))
                {
                    needsMigration = true;
                }

                account.EyaToken = DecryptValue(account.EyaToken, session, out var failed1);
                account.Password = DecryptValue(account.Password, session, out var failed2);
                account.SharedSecret = NullIfEmpty(DecryptValue(account.SharedSecret, session, out var failed3));
                account.EmailPassword = NullIfEmpty(DecryptValue(account.EmailPassword, session, out var failed4));
                if (failed1 || failed2 || failed3 || failed4)
                {
                    session.Undecryptable++;
                }
            }

            // 头部存在但没有 kid（密钥只由 DPAPI 包裹）→ 也需要回写一次，升级为"内置密钥封层"格式。
            if (!needsMigration && HasUnsealedHeader(rawJson))
            {
                needsMigration = true;
            }

            return needsMigration;
        }
    }

    /// <summary>vault 头存在、但缺少 kid（即密钥未用内置密钥 AES-GCM 封层）时返回 true。</summary>
    private static bool HasUnsealedHeader(string json)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(json) &&
                   JsonNode.Parse(json) is JsonObject root &&
                   root["vault"] is JsonObject header &&
                   header["wrappedDek"] is not null &&
                   header["kid"] is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>JSON 里是否存在未加密的敏感字段值（识别迁移前遗留的明文文件/备份）。</summary>
    /// <summary>是否需要「升级/加固」：存在明文，或存在旧版 dpapi:v1 密文。</summary>
    internal static bool NeedsUpgradeOrPlaintext(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["accounts"] is not JsonArray accounts)
            {
                return false;
            }

            return accounts.OfType<JsonObject>().Any(account => SensitiveFields.Any(field =>
                account[field] is JsonValue value && value.TryGetValue(out string? text) && (NeedsProtection(text) || IsLegacy(text))));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool ContainsPlaintextCredentials(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject root &&
                   root["accounts"] is JsonArray accounts &&
                   accounts.OfType<JsonObject>().Any(account => SensitiveFields.Any(field =>
                       account[field] is JsonValue value && value.TryGetValue(out string? text) && NeedsProtection(text)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- 内部：密钥包裹 / 字段加解密 ----------

    private static VaultSession GetSession(string vaultPath)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(vaultPath, out var session))
            {
                session = new VaultSession();
                Sessions[vaultPath] = session;
            }

            return session;
        }
    }

    /// <summary>只解析 vault 头部状态（口令层开关），不尝试解 DEK。</summary>
    private static void EnsureHeaderParsed(string vaultPath, VaultSession session)
    {
        if (session.HeaderParsed)
        {
            return;
        }

        var json = ReadVaultText(vaultPath);
        if (json is null)
        {
            return;   // 文件不存在：既不算启用口令，也不算锁定
        }

        session.HeaderParsed = true;
        session.PassphraseEnabled = HasPassphraseLayer(json);
    }

    private static string? ReadVaultText(string vaultPath)
    {
        try
        {
            return File.Exists(vaultPath) ? File.ReadAllText(vaultPath) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取凭据库失败：{ex.Message}");
            return null;
        }
    }

    private static bool TryParseVaultHeader(string json, [NotNullWhen(true)] out JsonObject? header)
    {
        header = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject root && root["vault"] is JsonObject vault)
            {
                header = vault;
                return true;
            }
        }
        catch (JsonException)
        {
            // 头部不可解析：按「没有头部」处理（是否拒绝写入由调用方判断）。
        }

        return false;
    }

    /// <summary>读取 L4 口令层参数（salt / 迭代次数）。没有 "pw" 块即表示未启用口令层。</summary>
    private static bool TryReadPassphraseParameters(JsonObject header, out byte[] salt, out int iterations)
    {
        salt = [];
        iterations = DefaultIterations;
        if (header["pw"] is not JsonObject pw ||
            pw["salt"] is not JsonValue saltValue ||
            !saltValue.TryGetValue(out string? saltText) ||
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

        iterations = pw["iterations"] is JsonValue iterValue && iterValue.TryGetValue(out int iters) && iters > 0
            ? iters
            : DefaultIterations;
        return salt.Length >= 8;
    }

    /// <summary>
    /// 构造 vault 头部：先用 PBKDF2+AES-GCM 封一层 DEK，KEK 由**程序内置**密钥派生
    /// （kid 标识密钥版本，便于将来轮换；salt 随机、随文件保存），再用 DPAPI 绑定 用户+机器。
    /// 因此默认就是三层加密，且不需要用户设置任何口令。
    /// 若本会话设置了口令（L4），再在最外层套一层口令 KEK 的 AES-GCM，并把盐/迭代次数写进 "pw"。
    /// </summary>
    private static JsonObject BuildHeader(byte[] dek, VaultSession session)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DeriveKey(BuiltInSecrets[CurrentKeyId - 1], salt, DefaultIterations);
        var wrapped = DpapiProtect(AesGcmSeal(kek, dek));

        var header = new JsonObject
        {
            ["v"] = 3,
            ["kid"] = CurrentKeyId,
            ["kdf"] = "PBKDF2-SHA256",
            ["salt"] = Convert.ToBase64String(salt),
            ["iterations"] = DefaultIterations
        };

        // L4（可选，默认关闭）：口令层包在最外层，只存盐与迭代次数，口令本身不落盘。
        if (session.PassphraseKek is not null && session.PassphraseSalt is not null)
        {
            wrapped = AesGcmSeal(session.PassphraseKek, wrapped);
            header["pw"] = new JsonObject
            {
                ["kdf"] = "PBKDF2-SHA256",
                ["salt"] = Convert.ToBase64String(session.PassphraseSalt),
                ["iterations"] = session.PassphraseIterations
            };
        }

        header["wrappedDek"] = Convert.ToBase64String(wrapped);
        return header;
    }
    private static byte[]? OpenDek(string json, VaultSession session) => OpenDekCore(json, session, session.PassphraseKek);

    /// <summary>
    /// 从文件头解出 DEK：先按需剥掉 L4 口令层 → DPAPI 解出内层 → 若带 kid 则用内置密钥 AES-GCM 解封；
    /// 无 kid 的旧文件内层就是 DEK 本身（读后由迁移升级）；
    /// 若是"用户口令时代"的文件（有 salt/kdf 但没有 kid），当前版本无法解开，返回 null（只读拒绝写入）。
    /// </summary>
    /// <param name="passphraseKek">L4 口令派生密钥；null 表示本次拿不到口令（启用了口令层时即视为未解锁）。</param>
    private static byte[]? OpenDekCore(string json, VaultSession session, byte[]? passphraseKek)
    {
        session.HeaderParsed = true;
        session.PassphraseEnabled = false;

        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject root ||
            root["vault"] is not JsonObject header ||
            header["wrappedDek"] is not JsonValue wrappedValue ||
            !wrappedValue.TryGetValue(out string? wrapped) || string.IsNullOrWhiteSpace(wrapped))
        {
            return RandomNumberGenerator.GetBytes(KeySize);   // 空库/新库
        }

        var payload = Convert.FromBase64String(wrapped);

        // L4：头部声明了口令层 → 先用口令 KEK 剥掉最外层（口令不对则整层解不开）。
        if (TryReadPassphraseParameters(header, out var pwSalt, out var pwIterations))
        {
            session.PassphraseEnabled = true;
            if (passphraseKek is null)
            {
                return null;   // 有口令层但本次没解锁：字段按空值读出，写盘一律拒绝
            }

            var unsealed = AesGcmOpen(passphraseKek, payload);
            if (unsealed is null)
            {
                AppLog.Warn("凭据库口令层解封失败（口令不对，或文件被篡改）。");
                return null;
            }

            // 解封成功说明头部里的盐与本次 KEK 匹配，记下来供下次落盘沿用。
            session.PassphraseSalt = pwSalt;
            session.PassphraseIterations = pwIterations;
            payload = unsealed;
        }

        var inner = DpapiUnprotect(payload);
        if (header["kid"] is JsonValue kidValue && kidValue.TryGetValue(out int kid) && kid >= 1 && kid <= BuiltInSecrets.Length &&
            header["salt"] is JsonValue saltValue && saltValue.TryGetValue(out string? salt) && !string.IsNullOrEmpty(salt))
        {
            var iterations = header["iterations"] is JsonValue iterValue && iterValue.TryGetValue(out int iters) && iters > 0
                ? iters
                : DefaultIterations;
            var kek = DeriveKey(BuiltInSecrets[kid - 1], Convert.FromBase64String(salt), iterations);
            var dek = AesGcmOpen(kek, inner);
            if (dek is null)
            {
                AppLog.Warn("凭据库密钥解封失败（文件可能被篡改或密钥版本不匹配）。");
            }

            return dek;
        }

        if (inner.Length == KeySize)
        {
            return inner;   // 旧版：DPAPI 直接包 DEK，读取后迁移升级
        }

        AppLog.Warn("凭据库来自其它密钥方案（例如曾用用户口令保护），当前版本无法解开。");
        return null;
    }
    private static bool NeedsProtection(string? value) =>
        !string.IsNullOrEmpty(value) && !IsProtected(value);

    /// <summary>是否旧版 dpapi:v1 密文（可解，但缺完整性校验、未用内置密钥封层，需要升级）。</summary>
    internal static bool IsLegacy(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    private static bool IsProtected(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal) || value.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    private static string EncryptValue(string plaintext, byte[] dek) => Prefix + Convert.ToBase64String(AesGcmSeal(dek, Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>解密字段；旧版 dpapi:v1 走单层 DPAPI；未解锁/解不开则返回空串（不抛，避免整页读不出）。</summary>
    private static string DecryptValue(string? value, VaultSession session, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            if (value.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                return DpapiUnprotectLegacyV1(value);
            }

            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return value;   // 旧版明文：原样返回（随后由迁移写回加密）
            }

            if (session.Dek is null)
            {
                // DEK 不可用（口令没输、文件来自其它密钥方案或被篡改）：不要把密文当明文返回
                // （否则可能被误当成令牌使用），统一按空值处理，并标记失败以便拒绝整文件写入。
                failed = true;
                AppLog.Warn("凭据库未解锁，加密字段按空值处理。");
                return string.Empty;
            }

            var payload = AesGcmOpen(session.Dek, Convert.FromBase64String(value[Prefix.Length..]));
            if (payload is null)
            {
                failed = true;
                return string.Empty;
            }

            return Encoding.UTF8.GetString(payload);
        }
        catch (Exception ex)
        {
            failed = true;
            AppLog.Warn($"凭据解密失败（可能是其它机器/用户加密，或文件被篡改）：{ex.Message}");
            return string.Empty;
        }
    }

    private static byte[] AesGcmSeal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, cipher, tag, associatedData: null);
        }

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return output;
    }

    /// <summary>AES-GCM 解封；tag 校验失败（被篡改或密钥不匹配）返回 null。</summary>
    private static byte[]? AesGcmOpen(byte[] key, byte[] payload)
    {
        if (payload.Length < NonceSize + TagSize)
        {
            return null;
        }

        var nonce = payload.AsSpan(0, NonceSize).ToArray();
        var tag = payload.AsSpan(NonceSize, TagSize).ToArray();
        var cipher = payload.AsSpan(NonceSize + TagSize).ToArray();
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

    private static byte[] DeriveKey(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>L2：DPAPI（当前用户）+ 熵里混入本机 MachineGuid → 绑定 用户 + 机器。</summary>
    private static byte[] DpapiProtect(byte[] data)
    {
        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(BuildEntropy());
        try
        {
            if (!CryptProtectData(ref dataBlob, "SteamEYA.Credentials.v3", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var protectedBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI 加密失败。");
            }

            try
            {
                var output = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, output, 0, output.Length);
                return output;
            }
            finally
            {
                LocalFree(protectedBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static byte[] DpapiUnprotect(byte[] cipher)
    {
        var dataBlob = CreateBlob(cipher);
        var entropyBlob = CreateBlob(BuildEntropy());
        try
        {
            if (!CryptUnprotectData(ref dataBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var plainBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI 解密失败。");
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

    /// <summary>旧版 dpapi:v1 的熵与文案（保持原样，保证历史文件可解）。</summary>
    private static string DpapiUnprotectLegacyV1(string value)
    {
        var cipher = Convert.FromBase64String(value[LegacyPrefix.Length..]);
        var dataBlob = CreateBlob(cipher);
        var entropyBlob = CreateBlob(Encoding.UTF8.GetBytes("SteamEYA.Credentials.v1"));
        try
        {
            if (!CryptUnprotectData(ref dataBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var plainBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var output = new byte[plainBlob.cbData];
                Marshal.Copy(plainBlob.pbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
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

    private static byte[] BuildEntropy()
    {
        var machineId = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineId = key?.GetValue("MachineGuid") as string ?? string.Empty;
        }
        catch
        {
            // 读不到就退化成「仅用户绑定」，不影响可用性。
        }

        return Encoding.UTF8.GetBytes("SteamEYA.Credentials.v3|" + machineId);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

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

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
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
