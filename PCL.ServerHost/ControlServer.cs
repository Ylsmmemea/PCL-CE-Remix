using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.ServerHost;

// 命名管道控制口 启动器连上来发一条指令拿一条 JSON 回复就断开
// 指令 status / stop / restart / command <文本> / ping
internal sealed class ControlServer : IDisposable
{
    // 单条指令的处理上限 客户端连上不发东西不能把控制口占死
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly ServerSupervisor _supervisor;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    internal ControlServer(string pipeName, ServerSupervisor supervisor)
    {
        _pipeName = pipeName;
        _supervisor = supervisor;
    }

    private void _Log(string text) => _supervisor.LogHost($"[控制口] {text}");

    internal void Start() => _loop = Task.Run(() => _AcceptLoopAsync(_cts.Token));

    // 串行处理连接 一条指令几毫秒就完事 不需要并发
    private async Task _AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                _Log($"正在监听 {_pipeName}");
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                _Log("客户端已连接");
                await _HandleAsync(server).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 管道异常不能让宿主退出 隔一会重建监听
                _Log($"管道异常 {ex.GetType().Name} {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    private async Task _HandleAsync(NamedPipeServerStream server)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);

        string? request;
        try
        {
            request = await _ReadLineAsync(server, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _Log("请求读取超时");
            return;
        }

        if (string.IsNullOrWhiteSpace(request)) return;

        _Log($"收到指令 {request.Trim()}");

        var response = _Dispatch(request.Trim());
        await _WriteLineAsync(server, response, timeout.Token).ConfigureAwait(false);
    }

    // 管道上不能用 StreamReader StreamWriter 那一套 StreamWriter 的 AutoFlush 会走
    // PipeStream.Flush 实测直接卡死 这里按换行自己切字节
    private static async Task<string?> _ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), token).ConfigureAwait(false);
            if (read == 0) break;

            used += read;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, used);
            if (newline >= 0) return Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r');
        }

        return used == 0 ? null : Encoding.UTF8.GetString(buffer, 0, used);
    }

    // PipeStream 的写入本身就是直通管道的 不缓冲 所以写完不用 Flush
    private static async Task _WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    private string _Dispatch(string request)
    {
        var space = request.IndexOf(' ');
        var command = space < 0 ? request : request[..space];
        var argument = space < 0 ? "" : request[(space + 1)..].Trim();

        switch (command.ToLowerInvariant())
        {
            case "ping":
                return "{\"ok\":true}";

            case "status":
                return $"{{\"ok\":true,\"status\":{_supervisor.StatusJson()}}}";

            case "stop":
                _supervisor.RequestStop();
                return "{\"ok\":true}";

            case "kill":
                _supervisor.RequestKill();
                return "{\"ok\":true}";

            case "restart":
                _supervisor.RequestRestart();
                return "{\"ok\":true}";

            case "command":
                if (string.IsNullOrWhiteSpace(argument)) return _Error("命令为空");
                return _supervisor.SendCommand(argument) ? "{\"ok\":true}" : _Error("服务端未在运行");

            case "tunnel-start":
                return _supervisor.StartTunnel()
                    ? "{\"ok\":true}"
                    : _Error("隧道配置或 frpc 还没准备好");

            case "tunnel-stop":
                _supervisor.StopTunnel();
                return "{\"ok\":true}";

            default:
                return _Error($"未知指令 {command}");
        }
    }

    private static string _Error(string message)
        => JsonSerializer.Serialize(new ControlResponse { Ok = false, Error = message });

    private sealed class ControlResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 退出路径上不等了
        }

        _cts.Dispose();
    }
}
