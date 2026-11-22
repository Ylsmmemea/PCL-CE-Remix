using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PCL.Core.Server.Network;

// 极简 UPnP IGD 客户端 只做网关发现外网地址查询和端口映射
// 用到的 SOAP action 就三个 自己写比引一个停维护的库可控
public static class UpnpClient
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    // 局域网里的 SOAP 请求绝不能走系统代理 单独开一个不吃代理的客户端
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = RequestTimeout
    })
    {
        Timeout = RequestTimeout
    };

    public sealed record Gateway(string HostAddress, string ControlUrl, string ServiceType)
    {
        // 网关那台设备自己也是内网设备时映射没意义 拿它的地址跟默认网关对一下
        public bool IsDefaultGateway { get; init; }
    }

    // SSDP 搜索网关设备 返回它们各自的 WAN 连接控制地址
    // 同一个设备可能回好几条 按控制地址去重
    public static async Task<List<Gateway>> DiscoverAsync(CancellationToken token = default)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Gateway>();

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var search = "M-SEARCH * HTTP/1.1\r\n" +
                         "HOST: 239.255.255.250:1900\r\n" +
                         "MAN: \"ssdp:discover\"\r\n" +
                         "MX: 2\r\n" +
                         "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            var payload = Encoding.ASCII.GetBytes(search);
            await udp.SendAsync(payload, SsdpEndpoint, token).ConfigureAwait(false);

            // ReceiveAsync 不认 ReceiveTimeout 那个属性只对同步 API 生效 只能靠取消令牌
            using var searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            searchTimeout.CancelAfter(SearchTimeout);
            while (!searchTimeout.IsCancellationRequested)
            {
                var response = await udp.ReceiveAsync(searchTimeout.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(response.Buffer);
                foreach (var line in text.Split("\r\n"))
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        locations.Add(line[9..].Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // 收满超时时间就停
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 组播发不出去基本就是没有可用的局域网 按找不到处理
        }

        var defaultGateways = _DefaultGatewayAddresses();
        foreach (var location in locations)
        {
            var host = new Uri(location).Host;
            var isDefault = defaultGateways.Contains(host);

            var (controlUrl, serviceType) = await _FindWanServiceAsync(location, token).ConfigureAwait(false);
            if (controlUrl is null || serviceType is null) continue;

            result.Add(new Gateway(host, controlUrl, serviceType) { IsDefaultGateway = isDefault });
        }

        // 默认网关排前面 别的设备映射了也没用
        return result.OrderByDescending(gateway => gateway.IsDefaultGateway).ToList();
    }

    public static async Task<string?> GetExternalIpAsync(Gateway gateway, CancellationToken token = default)
    {
        var reply = await _SoapAsync(gateway, "GetExternalIPAddress", "", token).ConfigureAwait(false);
        if (reply is null) return null;

        try
        {
            return XDocument.Parse(reply)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "NewExternalIPAddress")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // 端口映射 租期给 0 表示长期有效 正常关服时我们会主动删
    public static async Task<bool> AddPortMappingAsync(Gateway gateway, int externalPort, int internalPort,
        string internalClient, string description, CancellationToken token = default)
    {
        var body = "<NewRemoteHost></NewRemoteHost>" +
                   $"<NewExternalPort>{externalPort}</NewExternalPort>" +
                   "<NewProtocol>TCP</NewProtocol>" +
                   $"<NewInternalPort>{internalPort}</NewInternalPort>" +
                   $"<NewInternalClient>{internalClient}</NewInternalClient>" +
                   "<NewEnabled>1</NewEnabled>" +
                   $"<NewPortMappingDescription>{_XmlEscape(description)}</NewPortMappingDescription>" +
                   "<NewLeaseDuration>0</NewLeaseDuration>";
        return await _SoapAsync(gateway, "AddPortMapping", body, token).ConfigureAwait(false) is not null;
    }

    public static async Task<bool> DeletePortMappingAsync(Gateway gateway, int externalPort,
        CancellationToken token = default)
    {
        var body = "<NewRemoteHost></NewRemoteHost>" +
                   $"<NewExternalPort>{externalPort}</NewExternalPort>" +
                   "<NewProtocol>TCP</NewProtocol>";
        return await _SoapAsync(gateway, "DeletePortMapping", body, token).ConfigureAwait(false) is not null;
    }

    // 判断是不是真的公网地址 映射拿到内网地址说明上面还有一层 NAT 直连没戏
    public static bool IsPublicAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (!IPAddress.TryParse(address, out var parsed)) return false;

        if (IPAddress.IsLoopback(parsed)) return false;
        if (parsed.IsIPv6LinkLocal || parsed.IsIPv6SiteLocal) return false;

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = parsed.GetAddressBytes();
            // 10/8 172.16/12 192.168/16 是常规内网 100.64/10 是运营商级 NAT
            if (bytes[0] == 10) return false;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return false;
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] == 127) return false;
        }

        return true;
    }

    // 取本机在某个网关所在网段上的地址 端口映射要把内网地址报给路由器
    public static string? LocalAddressFor(string gatewayAddress)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(gatewayAddress, 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static HashSet<string> _DefaultGatewayAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var gateway in network.GetIPProperties().GatewayAddresses)
                if (gateway.Address is not null)
                    result.Add(gateway.Address.ToString());
        }

        return result;
    }

    // 描述文档里找出 WAN 连接服务的控制地址 服务可能藏在下级设备里 所以整棵树搜
    private static async Task<(string? ControlUrl, string? ServiceType)> _FindWanServiceAsync(string location,
        CancellationToken token)
    {
        try
        {
            var xml = await Http.GetStringAsync(location, token).ConfigureAwait(false);
            var document = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            foreach (var service in document.Descendants(ns + "service"))
            {
                var serviceType = service.Element(ns + "serviceType")?.Value ?? "";
                if (!serviceType.Contains("WANIPConnection") && !serviceType.Contains("WANPPPConnection"))
                    continue;

                var control = service.Element(ns + "controlURL")?.Value;
                if (string.IsNullOrWhiteSpace(control)) continue;

                return (new Uri(new Uri(location), control).ToString(), serviceType);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or System.Xml.XmlException or UriFormatException)
        {
            // 有的设备描述文档读不出来 直接跳过
        }

        return (null, null);
    }

    private static async Task<string?> _SoapAsync(Gateway gateway, string action, string innerXml,
        CancellationToken token)
    {
        var envelope = "<?xml version=\"1.0\"?>" +
                       "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                       "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                       $"<u:{action} xmlns:u=\"{gateway.ServiceType}\">{innerXml}</u:{action}>" +
                       "</s:Body></s:Envelope>";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUrl);
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
            request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");

            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            // 失败时设备会回一个带 errorCode 的 SOAP Fault
            if (!response.IsSuccessStatusCode || body.Contains("<errorCode>", StringComparison.Ordinal))
                return null;

            return body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string _XmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
