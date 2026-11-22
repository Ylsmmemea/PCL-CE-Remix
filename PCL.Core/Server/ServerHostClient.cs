using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL.Core.Server;

// 宿主写的状态文件 .pclhost.json 字段与 PCL.ServerHost 的 HostInfo 一一对应
public sealed class ServerHostInfo
{
    public const string FileName = ".pclhost.json";

    [JsonPropertyName("protocol")] public int Protocol { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("server_pid")] public int ServerPid { get; set; }
    [JsonPropertyName("pipe")] public string Pipe { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("crashes")] public int Crashes { get; set; }
    [JsonPropertyName("last_error")] public string LastError { get; set; } = "";
    [JsonPropertyName("tunnel_state")] public string TunnelState { get; set; } = "stopped";
    [JsonPropertyName("tunnel_error")] public string TunnelError { get; set; } = "";
    [JsonPropertyName("host_started_at")] public DateTimeOffset HostStartedAt { get; set; }
    [JsonPropertyName("server_started_at")] public DateTimeOffset? ServerStartedAt { get; set; }
    [JsonPropertyName("ready_at")] public DateTimeOffset? ReadyAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }

    // 宿主还活着并且托管着服务端 停止和熔断之后宿主会自己退出
    [JsonIgnore]
    public bool IsHostActive => State is "starting" or "running" or "stopping" or "restarting";

    // 服务端是不是已经在收玩家了 用来区分启动中和运行中
    [JsonIgnore] public bool IsServerReady => State == "running" && ReadyAt is not null;
}

// 与 PCL.ServerHost 的控制管道通信 命名规则和协议版本必须跟宿主那边保持一致
public static class ServerHostClient
{
    public const int ProtocolVersion = 1;

    // 宿主 exe 相对启动器目录的位置 随发布一起产出
    public static string HostExecutablePath => Path.Combine(Basics.ExecutableDirectory, "PCL", "ServerHost", "PCL.ServerHost.exe");

    public static string PipeNameOf(ServerInstance instance)
    {
        var normalized = instance.DirectoryPath.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "PCLServerHost_" + Convert.ToHexString(hash)[..16];
    }

    public static ServerHostInfo? ReadInfo(ServerInstance instance)
        => ReadInfoFromDirectory(instance.DirectoryPath);

    public static ServerHostInfo? ReadInfoFromDirectory(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, ServerHostInfo.FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ServerHostInfo>(File.ReadAllText(path), JsonCompat.SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 宿主正在换名写入时可能读到半截 下一轮再读
            return null;
        }
    }

    // pid 会被系统回收 所以顺带核一下进程名 免得把别人的进程当成宿主
    // 名字只做前缀匹配 更新器替换 exe 时会把运行中的宿主改名 比如 PCL.ServerHost.old
    // 严格相等会把那种情况判成没在运行 界面上就一直是已停止
    private const string HostProcessName = "PCL.ServerHost";

    public static bool IsHostAlive(ServerHostInfo? info)
    {
        if (info is null || info.Pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            return process.ProcessName.StartsWith(HostProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // 发一条指令拿一行响应 连不上或者超时返回 null
    // 管道上按裸字节收发 用 StreamWriter 的话它的 AutoFlush 会卡在 PipeStream.Flush
    public static async Task<string?> SendAsync(string pipeName, string command, CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(command + "\n");
            await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);

            var buffer = new byte[65536];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(used), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;

                used += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, used) >= 0) break;
            }

            return used == 0 ? null : Encoding.UTF8.GetString(buffer, 0, used).Trim();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
