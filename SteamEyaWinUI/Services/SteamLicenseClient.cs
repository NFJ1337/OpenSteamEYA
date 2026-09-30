using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed record SteamAccountData(string Token, string User, string SteamId);

// partial：实例会作为菜单项 Tag 跨越 WinRT ABI，需要 CsWinRT 源生成 vtable（AOT）。
internal sealed partial record SteamUpstreamServer(string Name, string BaseUrl)
{
    public override string ToString() => Name;
}

internal sealed class SteamLicenseClient
{
    private const string KeyDataPath = "/keygetdata?key=";
    private const int HeaderSkipBytes = 8;
    private const string KeyTokenPath = "/keygettoken?key=";

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // 「伊万/路飞」模块用的两条上游（2026-09-17 用户要求把之前删掉的恢复回来，
    // 作为登录页上的独立模块，不影响下面奶味那条链路）。
    public static IReadOnlyList<SteamUpstreamServer> IvanLuffyServers { get; } = new List<SteamUpstreamServer>
    {
        new("伊万/小岛", "http://70.39.201.195:9099"),
        new("路飞", "http://38.76.193.80:9099")
    };

    // 唯一上游：奶味（111.170.18.37:9099）。
    // 用户要求删掉「伊万/小岛」「路飞」两条上游的取卡逻辑；只剩一家，登录页也不再显示上游选择。
    public static SteamUpstreamServer Upstream { get; } = new("奶味", "http://111.170.18.37:9099");

    /// <summary>
    /// 「小谢」平台：只有新版取名接口（GET /api/v1/health、GET /api/v1/redeem?key=，
    /// 服务端 111.170.18.31:9095 —— 主机与端口由「小谢平台上号器.exe」解包后确认：
    /// 该镜像里唯一的服务器字面量是 111.170.18.31，同一函数里紧跟 "http://"、":" 与端口常量 9095）。
    ///
    /// 注意：它没有旧的 keygetdata / keygettoken 链路，所以这里的 BaseUrl 是「新版接口基址」，
    /// 只在 Token 登录里选中小谢时使用（走 NaiweiRedeemClient 那套 SSE 协议）。
    /// </summary>
    public static SteamUpstreamServer Xiaoxie { get; } = new("小谢", "http://111.170.18.31:9095");

    /// <summary>
    /// 「小泽」平台：上游记录取自 SteamEYAToolNew（D:\APP\SteamEYAToolNew\SteamEyaWinUI.exe，未加壳，
    /// RVA 0xD4688 的上游初始化器里「小泽」那条）＝ BaseUrl http://111.170.18.37:9095 + 路径 /keygettoken。
    /// 该程序的 /keygettoken 分支把正文按 "----" 切成 SteamID64 与令牌，所以这里走
    /// <see cref="GetAccountDataViaKeyTokenAsync"/>，而不是奶味新版那套 /api/v1/redeem 的 SSE。
    ///
    /// 注意：主机与奶味新版接口相同（同一个平台上号器），差别只在路由，所以两者不能互相回退。
    /// </summary>
    public static SteamUpstreamServer Xiaozhe { get; } = new("小泽", "http://111.170.18.37:9095");

    /// <summary>
    /// 「Apex杨小美」：把 SteamEYAToolNew 里的「杨小美1」（111.170.150.94:9099）与「杨小美2」
    /// （45.205.17.32:5000）两条上游合成一个入口（2026-09-30 用户要求：两个端口的登录合一块、
    /// 排在小泽后面、改名 Apex杨小美）。两家走的都是经典 /keygetdata（8 字节头 + zlib JSON）。
    ///
    /// 下拉里显示的就是这一条；实际取名按 ApexYangXiaomeiServers 依次尝试。
    /// </summary>
    public static SteamUpstreamServer ApexYangXiaomei { get; } = new("Apex杨小美", "http://111.170.150.94:9099");

    /// <summary>Apex杨小美 的线路（先 杨小美1，再 杨小美2）。</summary>
    public static IReadOnlyList<SteamUpstreamServer> ApexYangXiaomeiServers { get; } = new List<SteamUpstreamServer>
    {
        new("Apex杨小美(杨小美1)", "http://111.170.150.94:9099"),
        new("Apex杨小美(杨小美2)", "http://45.205.17.32:5000")
    };

    /// <summary>
    /// 依次尝试同一上游的多条线路，返回第一个成功的账号；全部失败时抛出最后一个异常
    /// （两条线都试过的原因交给调用方展示；取消则原样抛出）。
    /// </summary>
    public async Task<SteamAccountData> GetAccountDataFromAnyAsync(
        string licenseKey,
        IReadOnlyList<SteamUpstreamServer> servers,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        foreach (var server in servers)
        {
            try
            {
                return await GetAccountDataAsync(licenseKey, server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppLog.Warn($"上游「{server.Name}」取名失败（{server.BaseUrl}）：{ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException(Loc.T("License_Error_NoAccountData"));
    }

    public async Task<SteamAccountData> GetAccountDataAsync(
        string licenseKey,
        SteamUpstreamServer server,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new ArgumentException(Loc.T("License_Error_EmptyKey"), nameof(licenseKey));
        }

        var url = $"{server.BaseUrl.TrimEnd('/')}{KeyDataPath}{Uri.EscapeDataString(licenseKey.Trim())}";
        using var response = await DefaultHttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException(Loc.T("License_Error_InvalidKeyOrServer"));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(Loc.T("License_Error_WrongKeyOrServer"));
        }

        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var json = DecompressKeyDataResponse(raw);
        return ParseAccountJson(json);
    }

    /// <summary>
    /// 「小泽」以及同平台的 /keygettoken 路由取名：GET {BaseUrl}/keygettoken?key=&lt;卡密&gt;。
    /// 成功时正文是纯文本 `SteamID64----Token`；HTTP 400 / 404 与 keygetdata 用同一套映射
    /// （SteamEYAToolNew 的 /keygettoken 分支就是这么切的：先找 "----"，取不到 SteamID 时再回退令牌的 sub）。
    /// </summary>
    public async Task<SteamAccountData> GetAccountDataViaKeyTokenAsync(
        string licenseKey,
        SteamUpstreamServer server,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new ArgumentException(Loc.T("License_Error_EmptyKey"), nameof(licenseKey));
        }

        var url = $"{server.BaseUrl.TrimEnd('/')}{KeyTokenPath}{Uri.EscapeDataString(licenseKey.Trim())}";
        using var response = await DefaultHttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException(Loc.T("License_Error_InvalidKeyOrServer"));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(Loc.T("License_Error_WrongKeyOrServer"));
        }

        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Trim('\uFEFF');
        if (body.Length == 0)
        {
            throw new InvalidDataException(Loc.T("License_Error_ResponseTooShort"));
        }

        // 与 LegacyEyaLicenseClient 同一套文本解析：`SteamID64----Token`；没有分隔符时整串当令牌。
        var separator = body.IndexOf("----", StringComparison.Ordinal);
        var steamId = separator > 0 ? body[..separator].Trim() : string.Empty;
        var token = separator > 0 ? body[(separator + 4)..].Trim() : body;
        if (token.Length == 0)
        {
            throw new InvalidOperationException(Loc.T("Login_Error_LegacyEyaTokenMissing"));
        }

        if (steamId.Length == 0)
        {
            steamId = AppState.JwtTokenService.Inspect(token).SteamId ?? string.Empty;
        }

        return new SteamAccountData(token, steamId.Length > 0 ? steamId : token, steamId);
    }

    private static string DecompressKeyDataResponse(byte[] raw)
    {
        if (raw.Length <= HeaderSkipBytes)
        {
            throw new InvalidDataException(Loc.T("License_Error_ResponseTooShort"));
        }

        using var input = new MemoryStream(raw, HeaderSkipBytes, raw.Length - HeaderSkipBytes, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static SteamAccountData ParseAccountJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array when document.RootElement.GetArrayLength() > 0 =>
                document.RootElement[0],
            JsonValueKind.Array =>
                throw new JsonException(Loc.T("License_Error_NoAccountData")),
            _ => document.RootElement
        };

        return new SteamAccountData(
            Token: RequireString(root, "token"),
            User: RequireString(root, "user"),
            SteamId: RequireString(root, "steamid"));
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(Loc.Tf("License_Error_MissingField_Format", name));
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException(Loc.Tf("License_Error_EmptyField_Format", name));
        }

        return text;
    }
}
