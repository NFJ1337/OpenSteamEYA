using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 把「个性化」面板里设置的昵称 / 真名 / 概要 / 头像应用到目标 Steam 账号，并可在最后清空曾用名记录——
/// 全程走 Steam Web API / steamcommunity，不连 CM WebSocket：用 EYA refresh token 经 web 换 access token
/// （<see cref="SteamWebSession.BuildViaWebApiAsync"/>），昵称 / 真名 / 概要走 profile 表单（/edit/），
/// 头像走 FileUploader，曾用名走 ajaxclearaliashistory。
/// 注意：web 保存资料提交的是整张 profile 表单，故先读回当前资料，用户留空的字段以现值回填，避免被清空。
/// </summary>
internal sealed partial class SteamProfileService
{
    private const string FileUploaderUrl = "https://steamcommunity.com/actions/FileUploader";

    private readonly JwtTokenService _jwtTokenService = new();

    // 与 SteamWorkshopService 同样的强约束 client：steamLoginSecure（含 access token）手动挂在 Cookie 头，
    // 关闭自动重定向，避免 3xx 指向外部域名时 token 随 Cookie 头外泄。
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public async Task<SteamProfileApplyResult> ApplyAsync(
        string eyaToken,
        SteamProfileApplyRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = NullIfBlank(request.PersonaName);
        var trimmedRealName = NullIfBlank(request.RealName);
        // TextBox 内部用 \r 作换行符；Steam 概要按 \n 存，提交前统一。
        var trimmedSummary = NullIfBlank(request.Summary)?.Replace("\r\n", "\n").Replace('\r', '\n');
        var hasProfileFields = trimmedName is not null || trimmedRealName is not null || trimmedSummary is not null;
        var hasAvatar = !string.IsNullOrWhiteSpace(request.AvatarImagePath) && File.Exists(request.AvatarImagePath);

        if (!hasProfileFields && !hasAvatar && !request.ClearAliasHistory)
        {
            throw new InvalidOperationException(Loc.T("Profile_Error_NothingToApply"));
        }

        progress?.Report(Loc.T("Profile_Progress_ValidatingToken"));
        var token = _jwtTokenService.Validate(eyaToken);

        // 必须经 CM 已登录会话换 web access token：GenerateAccessTokenForApp / finalizelogin 在匿名 web 上
        // 一律 AccessDenied(15)，这类 token 需要一个已认证会话来激活。与「清创意工坊订阅」同一套机制。
        progress?.Report(Loc.T("Profile_Progress_Connecting"));
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.T("Profile_Progress_GettingWebSession"));
        var session = await SteamWebSession.BuildAsync(cmClient, eyaToken, token.SteamId, cancellationToken);

        var profileApplied = false;
        string? profileError = null;
        if (hasProfileFields)
        {
            progress?.Report(Loc.T("Profile_Progress_SavingProfile"));
            (profileApplied, profileError) = await RunStepAsync(
                () => SaveProfileAsync(token.SteamId, trimmedName, trimmedRealName, trimmedSummary, session, cancellationToken),
                cancellationToken);
        }

        var avatarApplied = false;
        string? avatarError = null;
        if (hasAvatar)
        {
            progress?.Report(Loc.T("Profile_Progress_UploadingAvatar"));
            (avatarApplied, avatarError) = await RunStepAsync(
                () => UploadAvatarAsync(token.SteamId, request.AvatarImagePath!, session, cancellationToken),
                cancellationToken);
        }

        // 曾用名清理放最后：改名成功后旧昵称刚被计入历史，此时清掉才能把它一并抹去。
        var aliasesCleared = false;
        string? aliasClearError = null;
        if (request.ClearAliasHistory)
        {
            progress?.Report(Loc.T("Profile_Progress_ClearingAliases"));
            (aliasesCleared, aliasClearError) = await RunStepAsync(
                () => ClearAliasHistoryAsync(token.SteamId, session, cancellationToken),
                cancellationToken);
        }

        return new SteamProfileApplyResult(
            hasProfileFields, profileApplied, profileError,
            trimmedName is not null,
            hasAvatar, avatarApplied, avatarError,
            request.ClearAliasHistory, aliasesCleared, aliasClearError);
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // 单步异常隔离：网络级异常（HttpRequestException / 超时）折叠为该步骤的失败原因，让调用方拿到
    // 部分结果并回写已成功的项——否则靠后的步骤一抛错，前面已生效的改名/头像就丢失本地记录。
    // 真正的用户取消照常上抛（超时的 TaskCanceledException 不带请求取消标记，会被折叠而非误判为取消）。
    private static async Task<(bool Success, string? Error)> RunStepAsync(
        Func<Task<(bool Success, string? Error)>> step,
        CancellationToken cancellationToken)
    {
        try
        {
            return await step();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("个性化步骤执行失败。", ex);
            return (false, ex.Message);
        }
    }

    // ---- 昵称 / 真名 / 概要（profile 表单）----

    private static async Task<(bool Success, string? Error)> SaveProfileAsync(
        string steamId,
        string? personaName,
        string? realName,
        string? summary,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        // 先读当前资料：web 保存走整张 profile 表单，用户留空的字段以现值回填，避免被清空。
        var current = await TryGetCurrentProfileAsync(steamId, session, cancellationToken);

        // 用户没改昵称且现值也读不到时中止：昵称是必填字段，空 personaName 会让 Steam 整体拒绝这张表单
        // （万一被接受反而会清空昵称）。
        var effectivePersonaName = personaName ?? current.PersonaName;
        if (string.IsNullOrEmpty(effectivePersonaName))
        {
            return (false, Loc.T("Profile_Error_CurrentProfileUnavailable"));
        }

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sessionID", session.SessionId),
            new KeyValuePair<string, string>("type", "profileSave"),
            new KeyValuePair<string, string>("personaName", effectivePersonaName),
            new KeyValuePair<string, string>("real_name", realName ?? current.RealName),
            new KeyValuePair<string, string>("customURL", current.CustomUrl),
            new KeyValuePair<string, string>("summary", summary ?? current.Summary),
            new KeyValuePair<string, string>("hide_profile_awards", "0"),
            new KeyValuePair<string, string>("json", "1")
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://steamcommunity.com/profiles/{steamId}/edit/") { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        // 3xx：会话不被接受时会被导向登录页等；按会话失效处理，绝不跟随到外部域名。
        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_ProfileSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_ProfileHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseProfileSaveResponse(responseText);
    }

    private static async Task<ProfileFields> TryGetCurrentProfileAsync(
        string steamId, SteamWebSession session, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetFollowingRedirectsAsync(
                new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/info"),
                session.CookieHeader,
                cancellationToken);
            var fields = ParseProfileFields(html);
            if (fields == ProfileFields.Empty)
            {
                // 正常账号昵称必非空：全空说明页面结构变了或没拿到编辑页，记录下来便于诊断。
                AppLog.Warn("Steam 资料编辑页未解析到任何现值（页面结构可能已变更），保存将不保留未填写的字段。");
            }

            return fields;
        }
        catch (Exception ex)
        {
            // 读不到当前资料时不阻断保存，但记录——此时用户留空的字段可能被清空。
            AppLog.Error("读取当前 Steam 资料失败，保存资料将不保留未填写的字段。", ex);
            return ProfileFields.Empty;
        }
    }

    private static ProfileFields ParseProfileFields(string html)
    {
        // 现代资料编辑页是 React 应用：现值在 data-profile-edit-config 属性的 HTML 转义 JSON 里
        // （strPersonaName / strRealName / strSummary / strCustomURL）。旧版表单字段仅作兜底。
        var fromConfig = TryParseProfileEditConfig(html);
        if (fromConfig is not null && fromConfig != ProfileFields.Empty)
        {
            return fromConfig;
        }

        string Extract(Regex regex)
        {
            var match = regex.Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
        }

        return new ProfileFields(
            PersonaName: Extract(LegacyPersonaNameRegex()),
            RealName: Extract(RealNameRegex()),
            Summary: Extract(SummaryRegex()),
            CustomUrl: Extract(CustomUrlRegex()));
    }

    private static ProfileFields? TryParseProfileEditConfig(string html)
    {
        var match = ProfileEditConfigRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var json = WebUtility.HtmlDecode(match.Groups[1].Value);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string Get(string name) =>
                root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : string.Empty;

            return new ProfileFields(
                PersonaName: Get("strPersonaName"),
                RealName: Get("strRealName"),
                Summary: Get("strSummary"),
                CustomUrl: Get("strCustomURL"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool Success, string? Error) ParseProfileSaveResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag == 1));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("errmsg", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_ProfileRejected") : message);
        }
        catch (JsonException)
        {
            // 非 JSON（多半是被重定向到登录页的 HTML）→ 视为失败。
            return (false, Loc.T("Profile_Error_ProfileRejected"));
        }
    }

    // ---- 头像（FileUploader）----

    private static async Task<(bool Success, string? Error)> UploadAvatarAsync(
        string steamId,
        string imagePath,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "avatar", "avatar.jpg");
        form.Add(new StringContent("player_avatar_image"), "type");
        form.Add(new StringContent(steamId), "sId");
        form.Add(new StringContent(session.SessionId), "sessionid");
        form.Add(new StringContent("1"), "doSub");
        form.Add(new StringContent("1"), "json");

        using var request = new HttpRequestMessage(HttpMethod.Post, FileUploaderUrl) { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/avatar");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_AvatarSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_AvatarHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseUploadResponse(responseText);
    }

    private static (bool Success, string? Error) ParseUploadResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag != 0));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_AvatarRejected") : message);
        }
        catch (JsonException)
        {
            return (false, Loc.T("Profile_Error_AvatarBadResponse"));
        }
    }

    // ---- 曾用名（ajaxclearaliashistory）----

    private static async Task<(bool Success, string? Error)> ClearAliasHistoryAsync(
        string steamId,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sessionid", session.SessionId)
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://steamcommunity.com/profiles/{steamId}/ajaxclearaliashistory/") { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_AliasSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_AliasHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        // 响应为 {"success": <EResult>}，1 = OK；非 JSON（多半是登录页 HTML）视为失败。
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var result) && result == 1));

            return success ? (true, null) : (false, Loc.T("Profile_Error_AliasRejected"));
        }
        catch (JsonException)
        {
            return (false, Loc.T("Profile_Error_AliasRejected"));
        }
    }

    // ---- 共用：手动跟随 steamcommunity 内部重定向（带 vanity 的账号 GET 资料页会 302）----

    private static async Task<string> GetFollowingRedirectsAsync(
        Uri uri, string cookieHeader, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is not (>= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return body;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
            {
                throw new InvalidOperationException("redirect without location");
            }

            var target = new Uri(current, location);
            if (!IsTrustedSteamCommunityUri(target))
            {
                throw new InvalidOperationException("redirected to external host");
            }

            current = target;
        }

        throw new InvalidOperationException("too many redirects");
    }

    private static bool IsTrustedSteamCommunityUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase);
    }

    // React 编辑页的现值 JSON（HTML 属性转义）。
    [GeneratedRegex(@"data-profile-edit-config=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileEditConfigRegex();

    // 旧版表单编辑页里这几个字段的现值（兜底用，避免保存时清空）。
    [GeneratedRegex(@"id=""personaName""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyPersonaNameRegex();

    [GeneratedRegex(@"id=""real_name""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex RealNameRegex();

    [GeneratedRegex(@"id=""customURL""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex CustomUrlRegex();

    [GeneratedRegex(@"id=""summary""[^>]*?>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryRegex();

    private sealed record ProfileFields(string PersonaName, string RealName, string Summary, string CustomUrl)
    {
        public static ProfileFields Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    }
}

/// <summary>一键个性化要应用的内容。字段为 null/空白表示不改该项；ClearAliasHistory 在其它步骤之后执行。</summary>
internal sealed record SteamProfileApplyRequest(
    string? PersonaName,
    string? RealName,
    string? Summary,
    string? AvatarImagePath,
    bool ClearAliasHistory);

/// <summary>
/// 个性化应用结果：分别记录资料表单（昵称 / 真名 / 概要）、头像、曾用名清理是否被请求、是否成功及失败原因。
/// NameRequested 单独记录昵称是否在表单里被修改，供调用方决定是否更新本地账号记录。
/// </summary>
internal sealed record SteamProfileApplyResult(
    bool ProfileRequested,
    bool ProfileApplied,
    string? ProfileError,
    bool NameRequested,
    bool AvatarRequested,
    bool AvatarApplied,
    string? AvatarError,
    bool AliasClearRequested,
    bool AliasesCleared,
    string? AliasClearError)
{
    /// <summary>昵称确实被请求修改且资料表单保存成功。</summary>
    public bool NameApplied => NameRequested && ProfileApplied;

    public bool IsFullSuccess =>
        (!ProfileRequested || ProfileApplied) &&
        (!AvatarRequested || AvatarApplied) &&
        (!AliasClearRequested || AliasesCleared);
}
