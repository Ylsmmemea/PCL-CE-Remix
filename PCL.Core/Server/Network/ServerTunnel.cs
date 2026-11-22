using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.Server.Network;

public enum TunnelProvider
{
    // 自己填 frps 地址端口密钥 走上游官方 frpc
    Manual = 0,

    // ChmlFrp 隧道由 API 建 走他们魔改的 frpc
    ChmlFrp = 1,

    // 樱花隧道由 API 建 配置文件也由 API 生成 走上游官方 frpc
    SakuraFrp = 2
}

// 手动模式的连接参数
public sealed class TunnelSettings
{
    // frps 地址 服务商给的节点域名或者自建服务器
    public string ServerAddress { get; set; } = "";
    public int ServerPort { get; set; } = 7000;
    public string Token { get; set; } = "";

    // 外网暴露的端口 服务商分配多少填多少
    public int RemotePort { get; set; }

    // 隧道名字 服务商那边用来标识这条隧道
    public string Name { get; set; } = "pcl-server";
}

// 隧道文件的准备 配置由启动器写 进程由宿主跑
// 两种服务商的配置格式和二进制都不一样
//   手动模式 官方 frpc 认 TOML 我们下载时校验硬编码哈希
//   ChmlFrp 他们的 frpc 认 INI 且用 user token 双密钥 哈希从官方接口现取
public static class ServerTunnel
{
    // 手动模式跟本地验证过的版本对齐 换版本要重新验一遍配置格式
    private const string FrpVersion = "0.61.1";

    private static readonly string OfficialDownloadUrl =
        $"https://github.com/fatedier/frp/releases/download/v{FrpVersion}/frp_{FrpVersion}_windows_amd64.zip";

    // 官方 frpc.exe 的 SHA256 换版本时必须一起换
    private const string OfficialFrpcSha256 = "D9A8CF8BC61BD5C8D57A2A4801316A18291F76D45DC5FD8AAF425BD91B57159D";

    public static string DirectoryOf(ServerInstance instance)
        => Path.Combine(instance.DirectoryPath, "tunnel");

    public static string ExecutablePathOf(ServerInstance instance)
        => Path.Combine(DirectoryOf(instance), "frpc.exe");

    // 记录当前装的是哪家的 frpc 换服务商时要重新下载
    private static string SourceMarkerPathOf(ServerInstance instance)
        => Path.Combine(DirectoryOf(instance), "frpc.source");

    // 手动模式是 TOML ChmlFrp 是 INI frpc 按扩展名决定怎么解析 名字不能混用
    // 樱花让接口按官方 frpc 版本生成配置 拿到的也是 toml 跟手动模式共用
    public static string ConfigPathOf(ServerInstance instance, TunnelProvider provider)
        => Path.Combine(DirectoryOf(instance),
            provider == TunnelProvider.ChmlFrp ? "frpc.ini" : "frpc.toml");

    // 樱花那边配置文件由接口生成 我们原样落盘 只把另一份清掉
    public static void WriteSakuraFrpConfig(ServerInstance instance, string content)
        => _WriteConfig(instance, TunnelProvider.SakuraFrp, content);

    #region 配置

    // 手动模式的 TOML 内网服务端地址指向本机对应端口
    public static void WriteManualConfig(ServerInstance instance, TunnelSettings settings)
    {
        Directory.CreateDirectory(DirectoryOf(instance));

        var builder = new StringBuilder();
        builder.AppendLine($"serverAddr = \"{_TomlEscape(settings.ServerAddress)}\"");
        builder.AppendLine($"serverPort = {settings.ServerPort}");
        if (!string.IsNullOrEmpty(settings.Token))
            builder.AppendLine($"auth.token = \"{_TomlEscape(settings.Token)}\"");
        // 隧道断了让 frpc 自己重连 不要直接退出
        // 这两行必须在 [[proxies]] 之前 TOML 里写在它后面会被归进 proxy 表 被 frpc 的严格解码器拒掉
        builder.AppendLine("transport.heartbeatInterval = 15");
        builder.AppendLine("transport.heartbeatTimeout = 45");
        builder.AppendLine();
        builder.AppendLine("[[proxies]]");
        builder.AppendLine($"name = \"{_TomlEscape(settings.Name)}\"");
        builder.AppendLine("type = \"tcp\"");
        builder.AppendLine("localIP = \"127.0.0.1\"");
        builder.AppendLine($"localPort = {instance.Port}");
        builder.AppendLine($"remotePort = {settings.RemotePort}");

        _WriteConfig(instance, TunnelProvider.Manual, builder.ToString());
    }

    // ChmlFrp 的 INI 格式照他们官方启动器的 generate_frpc_config 拼
    // user 是用户 token token 是这条隧道的节点密钥 两个都要
    public static void WriteChmlFrpConfig(ServerInstance instance, string userToken, ChmlFrpApi.TunnelInfo tunnel)
    {
        Directory.CreateDirectory(DirectoryOf(instance));

        var builder = new StringBuilder();
        builder.AppendLine("[common]");
        builder.AppendLine($"server_addr = {tunnel.NodeAddress}");
        builder.AppendLine($"server_port = {tunnel.NodePort}");
        builder.AppendLine("log_level = info");
        builder.AppendLine("tls_enable = true");
        builder.AppendLine("tcp_mux = true");
        builder.AppendLine("pool_count = 5");
        builder.AppendLine($"user = {userToken}");
        builder.AppendLine($"token = {tunnel.NodeToken}");
        builder.AppendLine();
        builder.AppendLine($"[{_IniSectionName(tunnel.Name)}]");
        builder.AppendLine("type = tcp");
        builder.AppendLine("local_ip = 127.0.0.1");
        builder.AppendLine($"local_port = {instance.Port}");
        builder.AppendLine($"remote_port = {tunnel.RemotePort}");

        _WriteConfig(instance, TunnelProvider.ChmlFrp, builder.ToString());
    }

    // 两种格式都不带 BOM frpc 拿到 BOM 开头的文件会当成 JSON 去解析
    // 写之前把另一种配置删掉 宿主是按存在哪个来选解析方式的
    private static void _WriteConfig(ServerInstance instance, TunnelProvider provider, string content)
    {
        var other = ConfigPathOf(instance,
            provider == TunnelProvider.ChmlFrp ? TunnelProvider.Manual : TunnelProvider.ChmlFrp);
        if (File.Exists(other)) File.Delete(other);

        File.WriteAllText(ConfigPathOf(instance, provider), content, new UTF8Encoding(false));
    }

    // INI 的段名里不能带方括号和换行 隧道名是用户起的得防一手
    private static string _IniSectionName(string name)
    {
        var cleaned = name.Trim().Replace("[", "").Replace("]", "").Replace("\r", "").Replace("\n", "");
        return string.IsNullOrWhiteSpace(cleaned) ? "pcl-server" : cleaned;
    }

    #endregion

    #region 二进制

    // 确保 tunnel 目录下的 frpc 是目标服务商那一份
    // 换了服务商要重新下载 用标记文件比对 光看文件存在会一直用着上一家的
    public static async Task<string?> EnsureFrpcAsync(ServerInstance instance, TunnelProvider provider,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        // 樱花用上游官方 frpc 跟手动模式是同一份 只有 ChmlFrp 要他们魔改的
        var expected = provider == TunnelProvider.ChmlFrp ? "chmlfrp" : $"official-{FrpVersion}";
        var marker = SourceMarkerPathOf(instance);

        try
        {
            if (File.Exists(ExecutablePathOf(instance)) && File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == expected)
                return null;
        }
        catch (IOException)
        {
            // 标记读不出来就当没装 重新下
        }

        Directory.CreateDirectory(DirectoryOf(instance));

        return provider == TunnelProvider.ChmlFrp
            ? await _DownloadChmlFrpFrpcAsync(instance, expected, progress, token).ConfigureAwait(false)
            : await _DownloadOfficialFrpcAsync(instance, expected, progress, token).ConfigureAwait(false);
    }

    // 上游官方发行版 走 PCL 的网络栈 这样用户在启动器里配的代理才生效
    private static async Task<string?> _DownloadOfficialFrpcAsync(ServerInstance instance, string marker,
        IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("正在下载 frp 客户端");

        try
        {
            using var response = await HttpRequest.Create(OfficialDownloadUrl)
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 2,
                    cancellationToken: token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return _ManualDownloadHint($"HTTP {(int)response.StatusCode}", instance);

            var archive = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            progress?.Report("正在解压 frp 客户端");

            using var stream = new MemoryStream(archive);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = _FindEntry(zip, "frpc.exe") ?? throw new InvalidOperationException("压缩包里没有 frpc.exe");

            await using var source = entry.Open();
            var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, token).ConfigureAwait(false);

            return await _InstallBinaryAsync(instance, buffer.ToArray(), OfficialFrpcSha256, marker, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or InvalidDataException or IOException or InvalidOperationException)
        {
            return _ManualDownloadHint(ex.Message, instance);
        }
    }

    // ChmlFrp 的 frpc 从它的官方接口取地址和哈希 这样换版本不用改我们的代码
    private static async Task<string?> _DownloadChmlFrpFrpcAsync(ServerInstance instance, string marker,
        IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("正在获取 frp 客户端信息");

        var (release, error) = await ChmlFrpApi.GetFrpcReleaseAsync(token).ConfigureAwait(false);
        if (release is null) return error;

        progress?.Report("正在下载 frp 客户端");

        try
        {
            using var response = await HttpRequest.Create(release.Url)
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseContentRead, retryTimes: 2,
                    cancellationToken: token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return $"下载 frpc 失败（HTTP {(int)response.StatusCode}）";

            var binary = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            return await _InstallBinaryAsync(instance, binary, release.Sha256, marker, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return $"下载 frpc 失败（{ex.Message}）";
        }
    }

    // 落盘前校验哈希 拿到的是要执行的程序 不能只信传输过程
    private static async Task<string?> _InstallBinaryAsync(ServerInstance instance, byte[] binary, string expectedHash,
        string marker, CancellationToken token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(binary));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            return $"frpc 校验失败（哈希 {hash[..16]} 与预期不符），可能下载被污染，请稍后重试";

        await File.WriteAllBytesAsync(ExecutablePathOf(instance), binary, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(SourceMarkerPathOf(instance), marker, token).ConfigureAwait(false);
        return null;
    }

    private static string _ManualDownloadHint(string reason, ServerInstance instance)
        => $"下载 frp 客户端失败（{reason}），可以到 frp 官方发布页手动下载后放到 {ExecutablePathOf(instance)}";

    private static ZipArchiveEntry? _FindEntry(ZipArchive zip, string name)
    {
        foreach (var entry in zip.Entries)
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return entry;

        return null;
    }

    #endregion

    private static string _TomlEscape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
