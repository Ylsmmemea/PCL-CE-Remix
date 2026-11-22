using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL.Core.Server.Network;

// ChmlFrp 的登录 走的是 OAuth 设备码流程
// 面板上那串 usertoken 已经不能当接口凭证用了 只能拿浏览器授权换 access token
// 参数来自他们官方启动器的 api.ts
public static class ChmlFrpAuth
{
    private const string AccountHost = "https://account-api.qzhua.net";
    private const string ClientId = "019d4334b34972ca9fd41513e5703dfd";
    private const string Scope = "profile email offline_access chmlfrp_api";

    // 存进配置的凭据 字段跟官方启动器的本地存储对齐
    private sealed class StoredTokens
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
        [JsonPropertyName("username")] public string Username { get; set; } = "";
    }

    public sealed record DeviceLogin(string UserCode, string DeviceCode, string VerifyUrl, int ExpiresIn, int Interval);

    public static string SignedInUser
    {
        get
        {
            var stored = _Read();
            return stored?.Username ?? "";
        }
    }

    public static bool IsSignedIn => _Read() is not null;

    // 申请一个设备码 用户在浏览器里输它就算授权
    public static async Task<(DeviceLogin? Code, string? Error)> StartAsync(CancellationToken token = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope
        };

        var (body, error) = await _PostFormAsync("/oauth2/device_authorization", form, token).ConfigureAwait(false);
        if (body is null) return (null, error);

        var parsed = JsonSerializer.Deserialize<DeviceAuthResponse>(body, JsonCompat.SerializerOptions);
        if (parsed?.DeviceCode is null || parsed.UserCode is null)
            return (null, "账户服务返回了无法解析的响应");

        // verification_uri_complete 里已经带了用户码 直接打开省一步输入
        var url = parsed.VerificationUriComplete
                  ?? $"{parsed.VerificationUri ?? $"{AccountHost}/oauth-device-verify"}?user_code={parsed.UserCode}";

        return (new DeviceLogin(parsed.UserCode, parsed.DeviceCode, url,
            parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 300, parsed.Interval > 0 ? parsed.Interval : 5), null);
    }

    // 等用户在浏览器里点完授权 没点完会一直返回 pending
    public static async Task<(bool Done, string? Error)> WaitAsync(DeviceLogin code, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = code.Interval;

        while (DateTimeOffset.UtcNow < deadline && !token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = code.DeviceCode,
                ["client_id"] = ClientId
            };

            var (body, error) = await _PostFormAsync("/oauth2/token", form, token).ConfigureAwait(false);
            if (body is null) return (false, error);

            var parsed = JsonSerializer.Deserialize<DeviceTokenResponse>(body, JsonCompat.SerializerOptions);
            if (parsed?.AccessToken is { Length: > 0 })
            {
                _Save(parsed);
                return (true, null);
            }

            switch (parsed?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "access_denied":
                    return (false, "授权被拒绝");
                case "expired_token":
                    return (false, "设备码已过期，请重新登录");
                default:
                    return (false, parsed?.ErrorDescription ?? parsed?.Error ?? "登录失败");
            }
        }

        return (false, "等待授权超时，请重新登录");
    }

    // 刷新令牌是一次性的 两个请求同时刷新会互相把对方挤掉 同一时刻只放一个进去
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    // 拿可用的 access token 快过期就用 refresh token 换新的
    public static async Task<(string? Token, string? Error)> GetAccessTokenAsync(CancellationToken token = default)
    {
        await RefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var stored = _Read();
            if (stored is null) return (null, "还没有登录 ChmlFrp");

            // 等锁的时候可能已经被别的请求换过了 重新看一眼
            if (DateTimeOffset.UtcNow.AddSeconds(60) < stored.ExpiresAt) return (stored.AccessToken, null);
            if (stored.RefreshToken.Length == 0) return (null, "登录已过期，请重新登录");

            var (parsed, requestError) = await _RequestRefreshAsync(stored.RefreshToken, token).ConfigureAwait(false);
            // 网络不通不能当成登录失效 免得用户白白重登一次
            if (parsed is null) return (null, requestError);

            if (parsed.AccessToken is not { Length: > 0 })
            {
                LogWrapper.Warn("Server", $"ChmlFrp 刷新令牌被拒：{parsed.Error} {parsed.ErrorDescription}");
                Clear();
                return (null, "登录已过期，请重新登录");
            }

            _Save(parsed);
            return (parsed.AccessToken, null);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    // 不管过没过期都拿 refresh token 换一把新的
    // 接口说无效但本地看着没过期时用 服务端可能提前失效了
    public static async Task<string?> ForceRefreshAsync(CancellationToken token = default)
    {
        await RefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var stored = _Read();
            if (stored is null || stored.RefreshToken.Length == 0) return null;

            var (parsed, _) = await _RequestRefreshAsync(stored.RefreshToken, token).ConfigureAwait(false);
            if (parsed?.AccessToken is not { Length: > 0 })
            {
                LogWrapper.Warn("Server", $"ChmlFrp 强制刷新失败：{parsed?.Error} {parsed?.ErrorDescription}");
                return null;
            }

            _Save(parsed);
            return parsed.AccessToken;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<(DeviceTokenResponse? Response, string? Error)> _RequestRefreshAsync(string refreshToken,
        CancellationToken token)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        };

        var (body, error) = await _PostFormAsync("/oauth2/token", form, token).ConfigureAwait(false);
        if (body is null) return (null, error);

        try
        {
            return (JsonSerializer.Deserialize<DeviceTokenResponse>(body, JsonCompat.SerializerOptions), null);
        }
        catch (JsonException)
        {
            return (null, "账户服务返回了无法解析的响应");
        }
    }

    public static void Clear() => Config.Server.TunnelChmlFrpToken = "";

    #region 存取与请求

    // 凭据整串以加密方式存进配置 面板那串 usertoken 已经没用了 这个键现在放的是登录凭据
    private static StoredTokens? _Read()
    {
        var raw = Config.Server.TunnelChmlFrpToken;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<StoredTokens>(raw, JsonCompat.SerializerOptions);
            return parsed?.AccessToken is { Length: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void _Save(DeviceTokenResponse response)
    {
        var existing = _Read();
        var stored = new StoredTokens
        {
            AccessToken = response.AccessToken!,
            RefreshToken = response.RefreshToken is { Length: > 0 } ? response.RefreshToken : existing?.RefreshToken ?? "",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn > 0 ? response.ExpiresIn : 3600),
            Username = existing?.Username ?? ""
        };

        Config.Server.TunnelChmlFrpToken = JsonSerializer.Serialize(stored, JsonCompat.SerializerOptions);
    }

    // 登录成功后补上用户名 界面要显示登录的是哪个账号
    public static void SaveUsername(string username)
    {
        var stored = _Read();
        if (stored is null) return;

        stored.Username = username;
        Config.Server.TunnelChmlFrpToken = JsonSerializer.Serialize(stored, JsonCompat.SerializerOptions);
    }

    private static async Task<(string? Body, string? Error)> _PostFormAsync(string path,
        Dictionary<string, string> form, CancellationToken token)
    {
        try
        {
            using var response = await HttpRequest.CreatePost(AccountHost + path)
                .WithFormContent(form)
                .WithHeader("Accept", "application/json")
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 1,
                    cancellationToken: token).ConfigureAwait(false);

            // 授权还没完成时返回的 4xx 里才是我们要的状态字段 所以正文一律读出来交给调用方判断
            return (await response.AsStringAsync(token).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, $"连接账户服务失败（{ex.Message}）");
        }
    }

    #endregion

    #region 报文结构

    private sealed class DeviceAuthResponse
    {
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private sealed class DeviceTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    #endregion
}
