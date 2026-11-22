using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Server.Network;

public enum DirectConnectVerdict
{
    // 有公网 IPv6 朋友直接填地址就能进
    PublicIPv6,

    // 路由器上开好了端口映射 外网地址也是公网地址
    UpnpMapped,

    // 映射成功但外网地址还是内网地址 说明上面还有一层 NAT
    DoubleNat,

    // 局域网里找不到 UPnP 网关设备
    UpnpUnavailable,

    // 找到网关但映射或者查外网地址失败
    UpnpFailed
}

public sealed class DirectConnectResult
{
    public DirectConnectVerdict Verdict { get; init; }

    // 公网 IPv6 地址 没有就是空
    public string PublicIPv6 { get; init; } = "";

    // UPnP 拿到的外网 IPv4 以及映射的端口
    public string PublicIPv4 { get; init; } = "";
    public int MappedPort { get; init; }

    public bool CanDirectConnect => Verdict is DirectConnectVerdict.PublicIPv6 or DirectConnectVerdict.UpnpMapped;

    // 给界面显示的外网连接地址 不能直连时是空
    public string Address => Verdict switch
    {
        DirectConnectVerdict.PublicIPv6 => $"[{PublicIPv6}]:{MappedPort}",
        DirectConnectVerdict.UpnpMapped => $"{PublicIPv4}:{MappedPort}",
        _ => ""
    };
}

// 直连可用性探测 决定界面推荐直连还是走穿透
// 纯本地操作 不依赖任何第三方服务
public static class DirectConnectProbe
{
    public static async Task<DirectConnectResult> ProbeAsync(int localPort, CancellationToken token = default)
    {
        var ipv6 = FindPublicIPv6();
        if (ipv6 is not null)
            return new DirectConnectResult
            {
                Verdict = DirectConnectVerdict.PublicIPv6,
                PublicIPv6 = ipv6,
                MappedPort = localPort
            };

        var gateways = await UpnpClient.DiscoverAsync(token).ConfigureAwait(false);
        if (gateways.Count == 0)
            return new DirectConnectResult { Verdict = DirectConnectVerdict.UpnpUnavailable, MappedPort = localPort };

        // 只认真网关那台设备的回答 别的设备映射了外网也进不来
        var gateway = gateways.FirstOrDefault(entry => entry.IsDefaultGateway) ?? gateways[0];

        var externalIp = await UpnpClient.GetExternalIpAsync(gateway, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(externalIp))
            return new DirectConnectResult { Verdict = DirectConnectVerdict.UpnpFailed, MappedPort = localPort };

        // 外网地址本身就是内网地址 上面至少还有一层 NAT 映射再成功也没用
        if (!UpnpClient.IsPublicAddress(externalIp))
            return new DirectConnectResult
            {
                Verdict = DirectConnectVerdict.DoubleNat,
                PublicIPv4 = externalIp,
                MappedPort = localPort
            };

        var localAddress = UpnpClient.LocalAddressFor(gateway.HostAddress);
        if (localAddress is null)
            return new DirectConnectResult
            {
                Verdict = DirectConnectVerdict.UpnpFailed,
                PublicIPv4 = externalIp,
                MappedPort = localPort
            };

        var mapped = await UpnpClient.AddPortMappingAsync(gateway, localPort, localPort, localAddress,
            "PCL Server", token).ConfigureAwait(false);

        return new DirectConnectResult
        {
            Verdict = mapped ? DirectConnectVerdict.UpnpMapped : DirectConnectVerdict.UpnpFailed,
            PublicIPv4 = externalIp,
            MappedPort = localPort
        };
    }

    // 关服时把之前建的映射删掉 路由器上留一堆死映射不好
    public static async Task RemoveMappingAsync(int port, CancellationToken token = default)
    {
        var gateways = await UpnpClient.DiscoverAsync(token).ConfigureAwait(false);
        var gateway = gateways.FirstOrDefault(entry => entry.IsDefaultGateway) ?? gateways.FirstOrDefault();
        if (gateway is null) return;

        await UpnpClient.DeletePortMappingAsync(gateway, port, token).ConfigureAwait(false);
    }

    // 找全局 unicast 的 IPv6 地址
    // 临时地址隐私扩展会定期换 优先用稳定的那个 换了地址朋友就进不来了
    public static string? FindPublicIPv6()
    {
        var candidates = new List<(string Address, int Score)>();

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up) continue;
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (network.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            var properties = network.GetIPProperties();
            // 这条链路得有 IPv6 网关 否则地址是真的但包出不去
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address is not null && gateway.Address.AddressFamily == AddressFamily.InterNetworkV6);

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                if (unicast.Address.IsIPv6LinkLocal || unicast.Address.IsIPv6SiteLocal) continue;
                if (unicast.Address.IsIPv6Multicast) continue;

                // 2000::/3 才是全球单播
                var first = unicast.Address.GetAddressBytes()[0];
                if (first is < 0x20 or > 0x3f) continue;

                var score = 0;
                if (hasGateway) score += 2;
                if (unicast.SuffixOrigin != SuffixOrigin.Random) score += 1;

                candidates.Add((unicast.Address.ToString(), score));
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Address)
            .FirstOrDefault();
    }
}
