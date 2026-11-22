using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using PCL.Core.IO.Net;

namespace PCL;

/// <summary>
///     模组下载源的延迟测量与排序。<br/>
///     <para>
///     用途：模组资源在官方源与镜像（mcimirror）之间二选一时，按实测延迟决定先后顺序，
///     而不是固定「官方优先」或「镜像优先」—— 不同网络环境下哪个更快差别很大。
///     </para>
///     <para>
///     <b>为什么用 HTTP 而不是 ICMP：</b>官方 CDN 普遍不响应 ICMP，而且 ping 只测网络层，
///     不含 DNS 解析、TLS 握手与代理开销，测出来的快慢和实际下载体验对不上。
///     这里对目标 URL 发 HEAD 请求，只计到响应头为止（TTFB），失败再退回 GET 取首字节。
///     任何状态码都算有效样本 —— 403/405 说明链路是通的，往返时间同样可比。
///     </para>
/// </summary>
public static class ModSourceSpeedTest
{
    /// <summary>单次测量超时。</summary>
    private const int TimeoutMs = 2000;

    /// <summary>测量结果的缓存时长。网络状况变化比版本更新频繁得多，取 30 分钟。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>按主机名缓存的延迟（毫秒）。</summary>
    private static readonly ConcurrentDictionary<string, (int Ms, DateTime At)> Cache = new();

    /// <summary>正在测量中的主机，避免同一主机被反复并发测量。</summary>
    private static readonly ConcurrentDictionary<string, Task> Pending = new();

    /// <summary>
    ///     取 URL 的主机名作为缓存键；解析不出来时返回空串。
    /// </summary>
    private static string _HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    ///     测量并缓存指定 URL 的延迟。已有新鲜缓存时直接返回。<br/>
    ///     失败返回 <c>null</c>，表示这条链路当前不可用或探测不到。
    /// </summary>
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
            _ = response.StatusCode; // 拿到响应头即算成功，状态码本身不参与判断
        }
        catch
        {
            // HEAD 被拒时退回 GET，只取响应头就断开
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

    /// <summary>
    ///     按实测延迟给两个候选排序，快的在前。<br/>
    ///     任一侧没有可用数据时回退为「官方在前」—— 与「平衡」档的既有行为一致，
    ///     保证测速尚未完成时不会改变原有表现。
    /// </summary>
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

    /// <summary>
    ///     后台预热常用下载源的延迟，供随后进入的模组列表直接使用。<br/>
    ///     只测量、不阻塞；已经测过的主机会命中缓存。
    /// </summary>
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
