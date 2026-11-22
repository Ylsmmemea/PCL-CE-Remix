using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using PCL.Core.IO.Net;

namespace PCL;

// 模组下载源的延迟测量 官方源和 mcimirror 之间按实测快慢决定先发哪个
public static class ModSourceSpeedTest
{
    private const int TimeoutMs = 2000;

    // 网络状况变得比版本号快多了 缓存取 30 分钟
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    // 按主机名缓存延迟
    private static readonly ConcurrentDictionary<string, (int Ms, DateTime At)> Cache = new();

    // 同一个主机正在测的时候别被并发再测一遍
    private static readonly ConcurrentDictionary<string, Task> Pending = new();

    private static string _HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    // 失败返回 null 表示这条链路当前探测不到
    public static async Task<int?> MeasureAsync(string url)
    {
        var host = _HostOf(url);
        if (string.IsNullOrEmpty(host)) return null;

        if (Cache.TryGetValue(host, out var cached) && DateTime.Now - cached.At < CacheTtl)
            return cached.Ms;

        var task = Pending.GetOrAdd(host, _ => _MeasureCoreAsync(url, host));
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            Pending.TryRemove(host, out _);
        }

        return Cache.TryGetValue(host, out var result) ? result.Ms : null;
    }

    // 不用 ICMP 官方 CDN 基本不响应 ping 而且 ping 不含 DNS 与 TLS 的开销 跟实际下载对不上
    private static async Task _MeasureCoreAsync(string url, string host)
    {
        var client = NetworkService.GetClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            _ = response.StatusCode; // 拿到响应头就算成功 状态码本身不参与判断
        }
        catch
        {
            // HEAD 被拒时退回 GET 拿了响应头就断开
            try
            {
                sw.Restart();
                using var cts = new CancellationTokenSource(TimeoutMs);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                ModBase.Log($"[Comp] 测速失败：{host}（{sw.ElapsedMilliseconds} ms）");
                return;
            }
        }

        var ms = (int)sw.ElapsedMilliseconds;
        Cache[host] = (ms, DateTime.Now);
        ModBase.Log($"[Comp] 测速 {host} = {ms} ms");
    }

    // 按实测延迟排序 快的在前 任一侧没数据就退回官方在前 跟平衡档行为一致
    public static (string First, string Second) Order(string official, string mirror)
    {
        var officialMs = _CachedLatency(official);
        var mirrorMs = _CachedLatency(mirror);
        if (officialMs is null || mirrorMs is null) return (official, mirror);
        return officialMs <= mirrorMs ? (official, mirror) : (mirror, official);
    }

    private static int? _CachedLatency(string url)
    {
        var host = _HostOf(url);
        if (string.IsNullOrEmpty(host)) return null;
        return Cache.TryGetValue(host, out var cached) && DateTime.Now - cached.At < CacheTtl
            ? cached.Ms
            : null;
    }

    // 后台预热几个常用下载源 等用户点进详情页真正开始下载时结果通常已经就绪 只测不阻塞
    public static void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            string[] targets =
            [
                "https://api.modrinth.com/",
                "https://cdn.modrinth.com/",
                "https://api.curseforge.com/",
                "https://edge.forgecdn.net/",
                "https://mod.mcimirror.top/"
            ];
            await Task.WhenAll(targets.Select(MeasureAsync)).ConfigureAwait(false);
        });
    }
}
