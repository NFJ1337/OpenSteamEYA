using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace SteamEyaWinUI.Services;

/// <summary>完美页专用 5E 登录态；令牌使用 Windows DPAPI 加密后保存在本机。</summary>
internal static class PerfectFiveESessionService
{
    private const int CryptprotectUiForbidden = 0x1;
    private const string FileName = "perfect-5e-session.json";
    private const string EntropyText = "OpenSteamEYA.PerfectFiveE.Session";

    private static string SessionPath => Path.Combine(AppPaths.DataRoot, FileName);

    public static bool HasToken() => !string.IsNullOrWhiteSpace(LoadToken());

    public static bool SaveToken(string token, string? userId)
    {
        var normalized = token?.Trim() ?? string.Empty;
        if (normalized.Length < 16)
        {
            return false;
        }

        try
        {
            var root = new JsonObject
            {
                ["protectedToken"] = EncryptToHex(normalized),
                ["userId"] = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(),
                ["ownerSteamId"] = null,
                ["savedAt"] = DateTimeOffset.Now.ToString("O")
            };
            Directory.CreateDirectory(AppPaths.DataRoot);
            var tempPath = SessionPath + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(), new UTF8Encoding(false));
            File.Move(tempPath, SessionPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存完美 5E 登录态失败：{ex.Message}");
            return false;
        }
    }

    public static bool ClearToken()
    {
        try
        {
            if (File.Exists(SessionPath))
            {
                File.Delete(SessionPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清除完美 5E 登录态失败：{ex.Message}");
            return false;
        }
    }

    public static string? LoadToken()
    {
        try
        {
            var protectedToken = ReadSession()?["protectedToken"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(protectedToken)
                ? null
                : DecryptFromHex(protectedToken);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取完美 5E 登录态失败：{ex.Message}");
            return null;
        }
    }

    public static string? LoadUserId() => ReadString("userId");

    public static string? LoadOwnerSteamId() => ReadString("ownerSteamId");

    public static bool UpdateOwnerSteamId(string ownerSteamId)
    {
        if (string.IsNullOrWhiteSpace(ownerSteamId))
        {
            return false;
        }

        try
        {
            var root = ReadSession();
            if (root is null)
            {
                return false;
            }

            root["ownerSteamId"] = ownerSteamId.Trim();
            WriteSession(root);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新完美 5E 主账号失败：{ex.Message}");
            return false;
        }
    }

    private static string? ReadString(string property)
    {
        try
        {
            return ReadSession()?[property]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ReadSession()
    {
        if (!File.Exists(SessionPath))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(SessionPath)) as JsonObject;
    }

    private static void WriteSession(JsonObject root)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var tempPath = SessionPath + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(), new UTF8Encoding(false));
        File.Move(tempPath, SessionPath, overwrite: true);
    }

    private static string EncryptToHex(string token)
    {
        var data = Encoding.UTF8.GetBytes(token);
        var entropy = Encoding.UTF8.GetBytes(EntropyText);
        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(entropy);
        try
        {
            if (!CryptProtectData(
                    ref dataBlob,
                    "OpenSteamEYA Perfect 5E session",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptprotectUiForbidden,
                    out var protectedBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var output = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, output, 0, output.Length);
                return Convert.ToHexString(output).ToLowerInvariant();
            }
            finally
            {
                LocalFree(protectedBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static string DecryptFromHex(string protectedHex)
    {
        var data = Convert.FromHexString(protectedHex);
        var entropy = Encoding.UTF8.GetBytes(EntropyText);
        var dataBlob = CreateBlob(data);
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
                    out var clearBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var output = new byte[clearBlob.cbData];
                Marshal.Copy(clearBlob.pbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                LocalFree(clearBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }
}
