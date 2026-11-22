using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL.Core.Server.Network;

// 樱花内网穿透 开放 API 文档 https://api.natfrp.com/docs/
// 认证用面板里生成的访问密钥 走 Bearer
// 它家能直接吐一份可用的 frpc 配置文件 上游官方 frpc 也认 所以二进制复用我们下载的那份
public static class SakuraFrpApi
{
    private const string BaseUrl = "https://api.natfrp.com";

    // 请求配置时告诉它我们要给哪个版本用 官方 0.61 会拿到 toml
    public const string FrpcVersion = "0.61.1";

    public sealed class TunnelInfo
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("node")] public int Node { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
        [JsonPropertyName("remote")] public string Remote { get; set; } = "";
        [JsonPropertyName("local_port")] public int LocalPort { get; set; }

        // 状态 2 是封禁 界面上要能看出来
        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("status_reason")] public string? StatusReason { get; set; }

        [JsonIgnore] public string Display => Online ? $"{Name}（在线）" : Name;
    }

    public sealed class UserInfo
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("email")] public string Email { get; set; } = "";
        [JsonPropertyName("realname")] public bool RealName { get; set; }
        [JsonPropertyName("traffic")] public long Traffic { get; set; }
        [JsonPropertyName("traffic_used")] public long TrafficUsed { get; set; }
    }

    // 验证密钥对不对的同时把用户名带回来
    public static async Task<(UserInfo? User, string? Error)> GetUserAsync(string accessKey,
        CancellationToken token = default)
        => await _GetAsync<UserInfo>("/user/info", accessKey, token).ConfigureAwait(false);

    public static async Task<(List<TunnelInfo>? Tunnels, string? Error)> ListTunnelsAsync(string accessKey,
        CancellationToken token = default)
        => await _GetAsync<List<TunnelInfo>>("/tunnels", accessKey, token).ConfigureAwait(false);

    // 让它按我们的 frpc 版本生成配置文件 返回的是文件原文
    public static async Task<(string? Content, string? Error)> GetConfigAsync(string accessKey, int tunnelId,
        CancellationToken token = default)
    {
        var form = new Dictionary<string, string>
        {
            ["query"] = tunnelId.ToString(),
            ["frpc"] = FrpcVersion
        };

        try
        {
            using var response = await HttpRequest.CreatePost(BaseUrl + "/tunnel/config")
                .WithFormContent(form)
                .WithBearerToken(accessKey)
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 1,
                    cancellationToken: token).ConfigureAwait(false);

            var body = await response.AsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, _Describe(body, (int)response.StatusCode));

            return (body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, $"请求失败（{ex.Message}）");
        }
    }

    public static async Task<(bool Ok, string? Error)> CreateTunnelAsync(string accessKey, string name, int nodeId,
        int localPort, CancellationToken token = default)
    {
        var form = new Dictionary<string, string>
        {
            ["name"] = name,
            ["type"] = "tcp",
            ["node"] = nodeId.ToString(),
            ["local_ip"] = "127.0.0.1",
            ["local_port"] = localPort.ToString()
        };

        try
        {
            using var response = await HttpRequest.CreatePost(BaseUrl + "/tunnels")
                .WithFormContent(form)
                .WithBearerToken(accessKey)
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 1,
                    cancellationToken: token).ConfigureAwait(false);

            var body = await response.AsStringAsync(token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, _Describe(body, (int)response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"请求失败（{ex.Message}）");
        }
    }

    // 节点列表是公开的 建隧道时用它挑
    public static async Task<(List<NodeInfo>? Nodes, string? Error)> ListNodesAsync(string accessKey,
        CancellationToken token = default)
        => await _GetAsync<List<NodeInfo>>("/nodes", accessKey, token).ConfigureAwait(false);

    public sealed class NodeInfo
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("group")] public string Group { get; set; } = "";
        [JsonPropertyName("need_realname")] public bool NeedRealName { get; set; }
        [JsonPropertyName("flag")] public string Flag { get; set; } = "";
    }

    private static async Task<(T? Data, string? Error)> _GetAsync<T>(string endpoint, string accessKey,
        CancellationToken token)
    {
        try
        {
            using var response = await HttpRequest.Create(BaseUrl + endpoint)
                .WithBearerToken(accessKey)
                .WithHeader("Accept", "application/json")
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 1,
                    cancellationToken: token).ConfigureAwait(false);

            var body = await response.AsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (default, _Describe(body, (int)response.StatusCode));

            // 列表接口直接返回数组 详情接口返回对象 这里按目标类型反序列化
            return (JsonSerializer.Deserialize<T>(body, JsonCompat.SerializerOptions), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (default, $"请求失败（{ex.Message}）");
        }
    }

    // 出错时优先把服务端给的原因显示出来 拿不到就退回状态码
    private static string _Describe(string body, int statusCode)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ErrorResponse>(body, JsonCompat.SerializerOptions);
            if (!string.IsNullOrWhiteSpace(parsed?.Message)) return parsed.Message;
        }
        catch (JsonException)
        {
            // 不是 json 就走下面的兜底
        }

        return statusCode == 403
            ? "访问密钥无效或没有权限"
            : $"请求失败（HTTP {statusCode}）";
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("msg")] public string? Short { get; set; }
        [JsonPropertyName("message")] public string? Long { get; set; }

        [JsonIgnore] public string? Message => Long is { Length: > 0 } ? Long : Short;
    }
}
