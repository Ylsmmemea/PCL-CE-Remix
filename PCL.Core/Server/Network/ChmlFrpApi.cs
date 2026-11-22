using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL.Core.Server.Network;

// ChmlFrp 的开放 API 逆向自它官方启动器的 src/services/api.ts
// 认证用面板里的用户 token 走 Bearer 响应统一是 {code, data, msg}
public static class ChmlFrpApi
{
    private const string BaseUrl = "https://cf-v2.uapis.cn";

    // 官方发布的 frpc 与我们用的上游版本不通用 他们的服务端认 user + token 双 token
    // 这个接口会连版本和哈希一起给出来 用它的 不用把二进制打进包里重新分发
    private const string FrpcInfoUrl = "https://cf-v1.uapis.cn/download/frpc/frpc_info.json";

    public sealed class TunnelInfo
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("node")] public string Node { get; set; } = "";

        // nport 是本地端口 不是外网端口 外网端口在 dorp 里 接口给的是字符串
        [JsonPropertyName("nport")] public int LocalPort { get; set; }
        [JsonPropertyName("dorp")] public string RemotePortText { get; set; } = "";

        [JsonIgnore] public int RemotePort => int.TryParse(RemotePortText, out var port) ? port : 0;

        // 节点地址和端口 拼 frpc 配置要用
        [JsonPropertyName("ip")] public string NodeAddress { get; set; } = "";
        [JsonPropertyName("server_port")] public int NodePort { get; set; }

        // 每个隧道一个的节点密钥 frpc 的 token 字段
        [JsonPropertyName("node_token")] public string NodeToken { get; set; } = "";

        // 外网连接地址 界面直接显示这个
        [JsonIgnore] public string PublicAddress => $"{NodeAddress}:{RemotePortText}";
    }

    public sealed class NodeInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("area")] public string Area { get; set; } = "";
        [JsonPropertyName("nodegroup")] public string Group { get; set; } = "";
        [JsonPropertyName("notes")] public string Notes { get; set; } = "";
        [JsonPropertyName("china")] public string China { get; set; } = "";

        // 节点说明里会写禁止用途 甚至有封号警告 建隧道前得让用户看见
        [JsonIgnore] public bool IsFreeTier => Group.Equals("user", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class FrpcRelease
    {
        public string Url { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long Size { get; init; }
    }

    public static async Task<(List<TunnelInfo>? Tunnels, string? Error)> ListTunnelsAsync(string token,
        CancellationToken cancellationToken = default)
    {
        var (data, error) = await _GetAsync<List<TunnelInfo>>("/tunnel", token, cancellationToken)
            .ConfigureAwait(false);
        return (data, error);
    }

    public static async Task<(List<NodeInfo>? Nodes, string? Error)> ListNodesAsync(string token,
        CancellationToken cancellationToken = default)
    {
        var (data, error) = await _GetAsync<List<NodeInfo>>("/node", token, cancellationToken).ConfigureAwait(false);
        return (data, error);
    }

    // 账号信息 里面的 usertoken 是 frpc 配置要用的那把钥匙 登录后顺手取回来
    public static async Task<(UserInfo? User, string? Error)> GetUserInfoAsync(string token,
        CancellationToken cancellationToken = default)
        => await _GetAsync<UserInfo>("/userinfo", token, cancellationToken).ConfigureAwait(false);

    public sealed class UserInfo
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";

        // 面板里显示的那串 给 frpc 的 user 字段用
        [JsonPropertyName("usertoken")] public string UserToken { get; set; } = "";

        [JsonPropertyName("realname")] public string RealName { get; set; } = "";

        [JsonIgnore] public bool IsRealNameVerified => !string.IsNullOrWhiteSpace(RealName) && RealName != "0";
    }

    // 建隧道 名字和本地端口由调用方给 节点不指定就挑第一个
    public static async Task<(bool Ok, string? Error)> CreateTunnelAsync(string token, string name, string node,
        int localPort, int remotePort, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            tunnelname = name,
            node,
            localip = "127.0.0.1",
            porttype = "tcp",
            localport = localPort,
            encryption = false,
            compression = false,
            extraparams = "",
            remoteport = remotePort
        };

        return await _PostAsync("/create_tunnel", token, payload, cancellationToken).ConfigureAwait(false);
    }

    // 免费节点常见的外网端口区间 实测湖南邵阳电信可用 别的节点不一定吃这一段
    private const int PortRangeStart = 30000;
    private const int PortRangeEnd = 50000;

    // 外网端口必须自己指定 传 0 服务端直接拒 而且每个节点允许的范围不一样 好端口还常被别人占着
    // 只能拿随机端口去试 试的过程本身就是建隧道 撞上能用的就成了
    public static async Task<(bool Ok, string Name, int Port, string? Error)> CreateTunnelAutoAsync(string token,
        string baseName, string node, int localPort, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 12;

        var random = new Random();
        var name = baseName;
        var rangeMisses = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var remotePort = random.Next(PortRangeStart, PortRangeEnd + 1);
            progress?.Report($"正在挑选可用端口 {attempt}/{maxAttempts}");

            var (ok, error) = await CreateTunnelAsync(token, name, node, localPort, remotePort, cancellationToken)
                .ConfigureAwait(false);
            if (ok) return (true, name, remotePort, null);

            // 名字撞了就加个序号 用户看不到这个名字 不用问他
            if (error is not null && error.Contains("名称"))
            {
                name = $"{baseName}{attempt + 1}";
                continue;
            }

            // 连着几次都说超出范围 说明这个节点不吃这一段 再多试也是白费
            if (error is not null && error.Contains("范围"))
            {
                if (++rangeMisses >= 4)
                    return (false, name, 0, $"{node} 不允许 {PortRangeStart} 到 {PortRangeEnd} 的端口 换一个节点试试");
                continue;
            }

            rangeMisses = 0;
        }

        return (false, name, 0, $"{node} 上没有挑到可用端口 换一个节点再试");
    }

    public static async Task<(bool Ok, string? Error)> DeleteTunnelAsync(string token, int tunnelId,
        CancellationToken cancellationToken = default)
    {
        var (_, error) = await _GetAsync<JsonElement>($"/delete_tunnel?tunnelid={tunnelId}", token,
            cancellationToken).ConfigureAwait(false);
        // 删除接口返回的 data 是 null 只要 code 是 200 就算成功
        return (error is null, error);
    }

    // 取官方 frpc 的下载地址和哈希 每次现取 换版本不用改代码
    public static async Task<(FrpcRelease? Release, string? Error)> GetFrpcReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpRequest.Create(FrpcInfoUrl)
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 2,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, $"获取 frpc 信息失败（HTTP {(int)response.StatusCode}）");

            var body = await response.AsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<FrpcInfoResponse>(body, JsonCompat.SerializerOptions);
            // 官方接口里 64 位 Windows 写作 x86_64 amd64 和 x64 都得认 三个写法都见过
            var entry = parsed?.Data?.Downloads?.FirstOrDefault(download =>
                download.Os == "windows" && download.Arch is "x86_64" or "x64" or "amd64");

            if (entry?.Link is null || entry.Hash is null)
                return (null, "官方没有提供 Windows x64 的 frpc");

            return (new FrpcRelease { Url = entry.Link, Sha256 = entry.Hash, Size = entry.Size }, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, $"获取 frpc 信息失败（{ex.Message}）");
        }
    }

    private static async Task<(T? Data, string? Error)> _GetAsync<T>(string endpoint, string token,
        CancellationToken cancellationToken)
        => await _SendAsync<T>(endpoint, token, null, cancellationToken).ConfigureAwait(false);

    private static async Task<(bool Ok, string? Error)> _PostAsync(string endpoint, string token, object payload,
        CancellationToken cancellationToken)
    {
        var (_, error) = await _SendAsync<JsonElement>(endpoint, token, payload, cancellationToken)
            .ConfigureAwait(false);
        return (error is null, error);
    }

    private static async Task<(T? Data, string? Error)> _SendAsync<T>(string endpoint, string token, object? payload,
        CancellationToken cancellationToken, bool refreshed = false)
    {
        try
        {
            var url = endpoint.StartsWith('/') ? BaseUrl + endpoint : $"{BaseUrl}/{endpoint}";
            var request = payload is null
                ? HttpRequest.Create(url)
                : HttpRequest.CreatePost(url).WithJsonContent(payload);

            request.WithHeader("authorization", $"Bearer {token}");
            request.WithHeader("Accept", "application/json");

            using var response = await request
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 1,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            var body = await response.AsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonCompat.SerializerOptions);

            if (parsed is null) return (default, "服务端返回了无法解析的内容");

            // 说登录失效就换一把新令牌重试一次 本地看着没过期也可能被服务端提前作废
            if (parsed.Code == 401 && !refreshed)
            {
                var fresh = await ChmlFrpAuth.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
                if (fresh is not null)
                    return await _SendAsync<T>(endpoint, fresh, payload, cancellationToken, true).ConfigureAwait(false);
            }

            if (parsed.Code != 200)
                return (default, string.IsNullOrWhiteSpace(parsed.Message) ? $"请求失败（{parsed.Code}）" : parsed.Message);

            return (parsed.Data, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (default, $"请求失败（{ex.Message}）");
        }
    }

    #region 报文结构

    private sealed class ApiResponse<T>
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class FrpcInfoResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("data")] public FrpcInfoData? Data { get; set; }
    }

    private sealed class FrpcInfoData
    {
        [JsonPropertyName("downloads")] public List<FrpcDownload>? Downloads { get; set; }
    }

    private sealed class FrpcDownload
    {
        [JsonPropertyName("os")] public string? Os { get; set; }
        [JsonPropertyName("arch")] public string? Arch { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    #endregion
}
