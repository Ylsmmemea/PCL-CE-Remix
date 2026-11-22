using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;
using PCL.Core.Minecraft.IdentityModel.Extensions.JsonWebToken;
using PCL.Core.Utils.Exts;

namespace PCL.Core.AI.ChatGpt;

// ChatGPT 订阅登录 走 OpenAI 给开源工具开的 token 共享通道
// 不需要 client secret 首次用 dynamic_agent_client 注册 之后一直用回调发下来的那个 oaiapp_ 开头的
public static class ChatGptAuth
{
    private const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    private const string RevokeEndpoint = "https://auth.openai.com/api/accounts/oauth/revoke";
    private const string JwksUri = "https://auth.openai.com/.well-known/jwks.json";
    private const string Issuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private const string RegistrationClientId = "dynamic_agent_client";
    private const string AgentName = "PCL Community Edition";
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string PlanScope = "chatgpt.tokens.use.direct";

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public static ChatGptCredentials? Current => ChatGptCredentials.Parse(Config.Ai.ChatGptCredentials);

    public static bool IsSignedIn => Current is { CanRefresh: true };

    public static async Task<ChatGptCredentials> SignInAsync(CancellationToken token = default)
    {
        var hostId = _HostId();

        var attempt = await _AuthorizeOnceAsync(Current, hostId, token).ConfigureAwait(false);
        // 首次注册发的 code 换不动是文档预告过的 拿发下来的 client_id 再授权一轮
        if (attempt.Tokens is null && !string.IsNullOrWhiteSpace(attempt.IssuedClientId))
        {
            LogWrapper.Info("AI", "首次注册的授权码被拒 用发下来的 client_id 重新授权");
            attempt = await _AuthorizeOnceAsync(
                    new ChatGptCredentials { ClientId = attempt.IssuedClientId, HostId = hostId }, hostId, token)
                .ConfigureAwait(false);
        }

        if (attempt.Tokens is null)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        var tokens = attempt.Tokens.Value;
        var identity = await _ValidateIdTokenAsync(tokens.IdToken, attempt.ClientId, attempt.Nonce, token)
            .ConfigureAwait(false);

        // id token 有效不代表有订阅额度 得单独看授权下来的 scope
        if (!tokens.Scopes.Contains(PlanScope))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoPlanScope"));

        var credentials = new ChatGptCredentials
        {
            Email = identity.Email,
            Issuer = Issuer,
            Subject = identity.Subject,
            ClientId = attempt.ClientId,
            HostId = hostId,
            IdToken = tokens.IdToken,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            TokenType = tokens.TokenType,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
            Scopes = tokens.Scopes,
            SavedAt = DateTimeOffset.UtcNow
        };

        _Save(credentials);
        LogWrapper.Info("AI", $"ChatGPT 订阅登录成功：{credentials.Email}");
        return credentials;
    }

    // 一轮授权加兑换 Tokens 为 null 表示 code 被拒 调用方按文档拿 IssuedClientId 再走一轮
    private static async Task<SignInAttempt> _AuthorizeOnceAsync(
        ChatGptCredentials? existing,
        string hostId,
        CancellationToken token)
    {
        var verifier = _RandomBase64Url(32);
        var challenge = _ChallengeOf(verifier);
        var state = _RandomBase64Url(16);
        var nonce = _RandomBase64Url(16);

        using var server = new ChatGptCallbackServer();
        server.Start();
        var redirectUri = server.RedirectUri;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SignInTimeout);

        _OpenBrowser(_BuildAuthorizeUrl(existing, hostId, redirectUri, state, nonce, challenge));

        IDictionary<string, string> callback;
        try
        {
            callback = await server.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInTimeout"));
        }
        finally
        {
            server.Stop();
        }

        if (callback.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"{Lang.Text("Setup.Ai.Error.SignInFailed")}：{error}");

        // state 对不上说明这不是我们发起的那次授权 直接拒掉
        if (!callback.TryGetValue("state", out var gotState) || gotState != state)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInStateMismatch"));

        if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        // 注册那轮回调会把发下来的 client_id 带回来 再授权可能不带就沿用存着的
        var issued = callback.TryGetValue("client_id", out var fromCallback) ? fromCallback : null;
        var clientId = !string.IsNullOrWhiteSpace(issued) ? issued : existing?.ClientId ?? "";
        if (string.IsNullOrWhiteSpace(clientId) || clientId == RegistrationClientId)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        // 回调发下来的注册跟手里存的对不上不能顶掉 文档明确要求拒绝
        if (!string.IsNullOrWhiteSpace(issued) && existing is { ClientId.Length: > 0 } && issued != existing.ClientId)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        LogWrapper.Debug("AI", $"授权回调参数：{string.Join("&", callback.Select(pair => $"{pair.Key}={pair.Value}"))}");

        var tokens = await _PostFormAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Resource
        }, token).ConfigureAwait(false);

        return new SignInAttempt(clientId, nonce, issued, tokens);
    }

    // 一轮授权的产物 兑换失败时只有 Tokens 是 null 其它字段照样有效
    private sealed record SignInAttempt(
        string ClientId,
        string Nonce,
        string? IssuedClientId,
        TokenResponse? Tokens);

    // 未过期直接用 过期前拿 refresh token 换新的
    public static async Task<string> GetAccessTokenAsync(CancellationToken token = default)
    {
        var current = Current ?? throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NotSignedIn"));
        if (!current.IsExpired) return current.AccessToken;

        if (!current.CanRefresh)
        {
            _Clear();
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.TokenExpired"));
        }

        var refreshed = await _PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = current.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["resource"] = Resource
        }, token).ConfigureAwait(false);

        // null 表示 invalid_grant 凭据废了 其他错误会直接抛出去
        if (refreshed is null)
        {
            _Clear();
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.TokenExpired"));
        }

        // refresh token 会轮换 access 和 refresh 必须一起换掉
        var updated = current with
        {
            AccessToken = refreshed.Value.AccessToken,
            RefreshToken = string.IsNullOrEmpty(refreshed.Value.RefreshToken)
                ? current.RefreshToken
                : refreshed.Value.RefreshToken,
            TokenType = refreshed.Value.TokenType,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.Value.ExpiresIn),
            Scopes = refreshed.Value.Scopes.Length == 0 ? current.Scopes : refreshed.Value.Scopes,
            SavedAt = DateTimeOffset.UtcNow
        };

        _Save(updated);
        return updated.AccessToken;
    }

    public static void SignOut()
    {
        var current = Current;
        _Clear();

        if (current is null) return;

        // 撤销失败无所谓 本地凭据已经清掉了
        _ = Task.Run(async () =>
        {
            try
            {
                await _PostFormAsync(new Dictionary<string, string>
                {
                    ["client_id"] = current.ClientId,
                    ["token"] = current.RefreshToken.Length > 0 ? current.RefreshToken : current.AccessToken
                }, CancellationToken.None, RevokeEndpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "AI", "撤销 ChatGPT 凭据失败");
            }
        });
    }

    #region 凭据存取

    private static void _Save(ChatGptCredentials credentials)
        => Config.Ai.ChatGptCredentials = credentials.ToJson();

    private static void _Clear()
        => Config.Ai.ChatGptCredentials = "";

    // 每台机器一个 不含任何用户信息 OpenAI 要求它稳定且不随用户变
    private static string _HostId()
    {
        var saved = Config.Ai.HostId;
        if (!string.IsNullOrWhiteSpace(saved)) return saved;

        var created = $"urn:uuid:{Guid.NewGuid():D}";
        Config.Ai.HostId = created;
        return created;
    }

    #endregion

    #region 请求

    private readonly record struct TokenResponse(
        string AccessToken,
        string RefreshToken,
        string IdToken,
        string TokenType,
        int ExpiresIn,
        string[] Scopes);

    private sealed record TokenResponseModel
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("id_token")] public string? IdToken { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    }

    // 返回 null 专门表示 invalid_grant 也就是 refresh token 已经无效 调用方要清凭据
    private static async Task<TokenResponse?> _PostFormAsync(
        Dictionary<string, string> form,
        CancellationToken token,
        string endpoint = TokenEndpoint)
    {
        // 授权码换 token 出过 invalid_grant 换之前先把表单原样留痕 排查就靠这一行
        LogWrapper.Debug("AI",
            $"令牌请求 {endpoint} 表单：{string.Join("&", form.Select(pair => $"{pair.Key}={pair.Value}"))}");

        using var response = await HttpRequest.CreatePost(endpoint)
            .WithFormContent(form)
            .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 0,
                cancellationToken: token)
            .ConfigureAwait(false);

        var body = await response.AsStringAsync(token).ConfigureAwait(false);
        // 失败时把原文也打出来 这个端点出错经常只给一个 error 码 不带描述
        if (!response.IsSuccess) LogWrapper.Debug("AI", $"令牌端点返回：{body}");
        var model = string.IsNullOrWhiteSpace(body)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<TokenResponseModel>(
                body, PCL.Core.Utils.JsonCompat.SerializerOptions);

        if (!response.IsSuccess)
        {
            if (model?.Error == "invalid_grant" && form.ContainsKey("grant_type")) return null;

            var reason = model?.ErrorDescription ?? model?.Error ?? body;
            throw new InvalidOperationException(
                $"{Lang.Text("Setup.Ai.Error.SignInFailed")}：{(int)response.StatusCode} {reason}".Trim());
        }

        if (model?.AccessToken is null)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        return new TokenResponse(
            model.AccessToken,
            model.RefreshToken ?? "",
            model.IdToken ?? "",
            model.TokenType ?? "Bearer",
            model.ExpiresIn,
            (model.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<(string Subject, string Email)> _ValidateIdTokenAsync(
        string idToken,
        string clientId,
        string nonce,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(idToken);

        using var response = await HttpRequest.Create(JwksUri)
            .SendAsync(retryTimes: 0, cancellationToken: token)
            .ConfigureAwait(false);
        var jwks = await response.AsJsonAsync<JsonWebKeys>(cancellationToken: token).ConfigureAwait(false);
        var key = jwks?.Keys.FirstOrDefault(k => k.Kid == parsed.Header.Kid)
                  ?? throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInFailed"));

        // 这一句同时校验签名 issuer audience 和有效期
        handler.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key
        }, out _);

        var gotNonce = parsed.Claims.FirstOrDefault(claim => claim.Type == "nonce")?.Value;
        if (gotNonce != nonce)
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.SignInStateMismatch"));

        return (
            parsed.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value ?? "",
            parsed.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value ?? "");
    }

    #endregion

    #region 授权地址与 PKCE

    private static string _BuildAuthorizeUrl(
        ChatGptCredentials? existing,
        string hostId,
        string redirectUri,
        string state,
        string nonce,
        string challenge)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", Scopes),
            new("resource", Resource),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge_method", "S256"),
            new("code_challenge", challenge),
            new("ext_agent_host_id", hostId)
        };

        if (existing is { ClientId.Length: > 0 })
        {
            // 已经注册过 再登录时带提示可以直接跳到账号选择 且不能带 agent_name_hint
            query.Add(new("client_id", existing.ClientId));
            if (existing.IdToken.Length > 0) query.Add(new("id_token_hint", existing.IdToken));
            if (existing.Email.Length > 0) query.Add(new("login_hint", existing.Email));
        }
        else
        {
            query.Add(new("client_id", RegistrationClientId));
            query.Add(new("agent_name_hint", AgentName));
        }

        var encoded = string.Join("&", query.Select(
            pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{AuthorizeEndpoint}?{encoded}";
    }

    // OpenAI 只认 S256 而且 challenge 必须是 base64url 没有 padding
    // 项目里的 PkceClient 用的是 hex 不符合 RFC 7636 所以这里自己算
    private static string _ChallengeOf(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string _RandomBase64Url(int bytes)
    {
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void _OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "AI", $"无法打开浏览器：{url}");
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.BrowserFailed"));
        }
    }

    #endregion
}
