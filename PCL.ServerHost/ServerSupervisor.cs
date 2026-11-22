using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.ServerHost;

// 实例元数据 只取宿主需要的字段 完整定义在 PCL.Core 的 ServerInstance
internal sealed class InstanceMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("type")] public string ServerType { get; set; } = "vanilla";
    [JsonPropertyName("java_path")] public string JavaPath { get; set; } = "";
    [JsonPropertyName("memory_mb")] public int MemoryMb { get; set; } = 2048;
    [JsonPropertyName("port")] public int Port { get; set; } = 25565;
    [JsonPropertyName("eula_accepted")] public bool EulaAccepted { get; set; }

    [JsonIgnore] internal string JarFileName => ServerType == "paper" ? "server.jar" : $"{Version}-server.jar";

    internal static InstanceMeta? Load(string directory)
    {
        var path = Path.Combine(directory, ".pclmeta.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<InstanceMeta>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal enum HostState
{
    Starting,
    Running,
    Stopping,
    Restarting,
    Stopped,
    Faulted
}

// 启动阶段就失败时也得往 host.json 里留一笔
// 那时候还没建起 supervisor 启动器只看得到进程没了 原因写进去界面才显示得出来
internal static class HostInfoFile
{
    internal static void WriteFailure(string instanceDirectory, string message)
    {
        try
        {
            var info = new HostInfo
            {
                Pid = 0,
                State = "faulted",
                LastError = message,
                UpdatedAt = DateTimeOffset.Now
            };
            var path = Path.Combine(instanceDirectory, HostInfo.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(info, ServerSupervisor.JsonOptions));
        }
        catch (Exception)
        {
            // 写不进去只能算了 启动器那边会显示成没起来
        }
    }
}

// 写进实例目录的 .pclhost.json 启动器靠它找到宿主并显示状态
internal sealed class HostInfo
{
    internal const string FileName = ".pclhost.json";

    [JsonPropertyName("protocol")] public int Protocol { get; set; } = Program.ProtocolVersion;
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("server_pid")] public int ServerPid { get; set; }
    [JsonPropertyName("pipe")] public string Pipe { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "starting";
    [JsonPropertyName("crashes")] public int Crashes { get; set; }
    [JsonPropertyName("last_error")] public string LastError { get; set; } = "";
    [JsonPropertyName("tunnel_state")] public string TunnelState { get; set; } = "stopped";
    [JsonPropertyName("tunnel_error")] public string TunnelError { get; set; } = "";
    [JsonPropertyName("host_started_at")] public DateTimeOffset HostStartedAt { get; set; }
    [JsonPropertyName("server_started_at")] public DateTimeOffset? ServerStartedAt { get; set; }
    [JsonPropertyName("ready_at")] public DateTimeOffset? ReadyAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

// 服务端进程的生命周期与看门狗 24 小时场景下它必须比启动器活得久
internal sealed class ServerSupervisor
{
    // 连续崩溃到这么多次就熔断 不再重启 免得陷入无限重启
    private const int MaxConsecutiveCrashes = 3;

    // 服务端稳定运行超过这个时长就把崩溃计数清零 偶发一次不算崩溃循环
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(60);

    // 没等到就绪标志也要把状态从启动中放出来 免得界面一直卡在启动中
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private const int StopTimeoutSeconds = 30;
    private const int RecentLineCapacity = 60;

    private static readonly string[] CrashMarkers =
    [
        "Encountered an unexpected exception",
        "A fatal error has been detected",
        "Exception in thread",
        "java.lang.OutOfMemoryError",
        "Failed to start the minecraft server",
        "crash report"
    ];

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly InstanceMeta _meta;
    private readonly string _javaPath;
    private readonly ConsoleLog _log;
    private readonly TunnelSupervisor _tunnel;
    private readonly HostInfo _info;
    private readonly string _infoPath;
    private readonly object _sync = new();
    private readonly Queue<string> _recentLines = new();

    // 每轮服务端一个 用来叫醒等待退出的循环
    private TaskCompletionSource _actionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private CancellationTokenSource? _restartDelay;
    private CancellationTokenSource? _readyWatch;
    private int _crashes;
    private bool _stopRequested;
    private bool _restartRequested;
    private bool _killRequested;
    private bool _ready;

    internal ServerSupervisor(string directory, InstanceMeta meta, string javaPath, string identifier)
    {
        _directory = directory;
        _meta = meta;
        _javaPath = javaPath;
        _log = new ConsoleLog(Path.Combine(directory, "logs"));
        _tunnel = new TunnelSupervisor(directory, _log, _PersistTunnelState);
        _infoPath = Path.Combine(directory, ".pclhost.json");
        _info = new HostInfo
        {
            Pid = Environment.ProcessId,
            Pipe = HostNaming.PipeName(identifier),
            HostStartedAt = DateTimeOffset.Now
        };
    }

    // 返回值直接当宿主进程的退出码 0 正常停止 1 熔断 4 前置检查没过
    internal async Task<int> RunAsync()
    {
        _log.WriteHost($"宿主启动 实例 {_meta.Name} 协议版本 {Program.ProtocolVersion} 进程 {Environment.ProcessId}");

        var problem = _PreflightProblem();
        if (problem is not null)
        {
            _log.WriteHost($"无法启动 {problem}");
            _info.LastError = problem;
            _SetState(HostState.Faulted);
            return problem.StartsWith("需要", StringComparison.Ordinal) ? 4 : 1;
        }

        while (true)
        {
            if (_stopRequested)
            {
                _log.WriteHost("已按请求停止");
                _SetState(HostState.Stopped);
                return 0;
            }

            var exitCode = await _RunOnceAsync().ConfigureAwait(false);

            if (_stopRequested)
            {
                _log.WriteHost("已按请求停止");
                _SetState(HostState.Stopped);
                return 0;
            }

            // 重启请求不算崩溃 直接进入下一轮
            if (_restartRequested)
            {
                _restartRequested = false;
                continue;
            }

            // 退出码零说明服务端自己正常关了 在控制台里敲过 stop 就是这条路径
            if (exitCode == 0)
            {
                _log.WriteHost("服务端正常退出");
                _SetState(HostState.Stopped);
                return 0;
            }

            // 稳定跑过一段时间的实例 崩一次不该往崩溃循环上算
            var ranLong = _info.ServerStartedAt is { } started
                          && DateTimeOffset.Now - started > StableRunThreshold;
            _crashes = ranLong ? 1 : _crashes + 1;

            var reason = _DescribeCrash(exitCode);
            _log.WriteHost($"服务端异常退出 退出码 {exitCode} 原因 {reason}");
            _info.LastError = reason;

            if (_crashes >= MaxConsecutiveCrashes)
            {
                _log.WriteHost($"连续崩溃 {_crashes} 次 已停止自动重启 请检查日志");
                _SetState(HostState.Faulted);
                return 1;
            }

            _SetState(HostState.Restarting);
            var delay = _RestartDelay();
            _log.WriteHost($"{(int)delay.TotalSeconds} 秒后重启 已连续崩溃 {_crashes}/{MaxConsecutiveCrashes} 次");

            _restartDelay = new CancellationTokenSource();
            try
            {
                await Task.Delay(delay, _restartDelay.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 等待期间被停止或者手动重启打断 回到循环开头重新判断
            }
            finally
            {
                _restartDelay?.Dispose();
                _restartDelay = null;
            }
        }
    }

    private TimeSpan _RestartDelay()
    {
        var seconds = FirstRestartDelay.TotalSeconds * Math.Pow(2, Math.Max(_crashes - 1, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRestartDelay.TotalSeconds));
    }

    // 起一次服务端并等它退出 返回退出码
    private async Task<int> _RunOnceAsync()
    {
        lock (_sync)
        {
            _recentLines.Clear();
            _actionSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ready = false;
        }

        _SetState(HostState.Starting);

        var memory = Math.Max(_meta.MemoryMb, 512);
        var arguments = "-XX:+UseG1GC -XX:+ExitOnOutOfMemoryError " +
                        $"-Xmx{memory}M -Xms{Math.Min(memory, 512)}M " +
                        "-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 " +
                        $"-jar \"{_meta.JarFileName}\" nogui";

        var startInfo = new ProcessStartInfo(_javaPath, arguments)
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _log.WriteHost($"无法启动 Java 进程 {ex.Message}");
            _info.LastError = ex.Message;
            _SetState(HostState.Faulted);
            return -1;
        }

        _process = process;
        _info.ServerPid = process.Id;
        _info.ServerStartedAt = DateTimeOffset.Now;
        _SetState(HostState.Starting);
        _log.WriteHost($"服务端已启动 Java 进程 {process.Id} 内存 {memory}M 端口 {_meta.Port}");

        _readyWatch = new CancellationTokenSource();
        _ = _MarkReadyOnTimeoutAsync(_readyWatch.Token);

        var pumps = new[]
        {
            _PumpAsync(process.StandardOutput),
            _PumpAsync(process.StandardError)
        };
        var exitCode = await _WaitForExitAsync(process).ConfigureAwait(false);

        _readyWatch.Cancel();
        _readyWatch.Dispose();
        _readyWatch = null;

        await Task.WhenAll(pumps).ConfigureAwait(false);

        process.Dispose();
        _process = null;
        _info.ServerPid = 0;
        return exitCode;
    }

    // 等待退出 期间收到停止或者重启请求就发 stop 并开始计时 超时强杀
    private async Task<int> _WaitForExitAsync(Process process)
    {
        Task signal;
        lock (_sync) signal = _actionSignal.Task;

        var exitTask = process.WaitForExitAsync();
        await Task.WhenAny(exitTask, signal).ConfigureAwait(false);

        if (!exitTask.IsCompleted)
        {
            // 强制停止那条路已经在杀进程了 不用再发 stop
            if (!_killRequested) _SendStop();
            var timeout = Task.Delay(TimeSpan.FromSeconds(StopTimeoutSeconds));
            if (await Task.WhenAny(exitTask, timeout).ConfigureAwait(false) == timeout)
            {
                _log.WriteHost($"服务端未在 {StopTimeoutSeconds} 秒内退出 强制结束");
                try
                {
                    process.Kill(true);
                }
                catch (Exception ex)
                {
                    _log.WriteHost($"强制结束失败 {ex.Message}");
                }

                await exitTask.ConfigureAwait(false);
            }
        }

        return process.ExitCode;
    }

    private void _SendStop()
    {
        var process = _process;
        if (process is null) return;

        try
        {
            // 走标准输入让服务端自己保存世界再退出
            process.StandardInput.WriteLine("stop");
            process.StandardInput.Flush();
            _SetState(HostState.Stopping);
        }
        catch (Exception ex)
        {
            _log.WriteHost($"发送 stop 失败 {ex.Message}");
        }
    }

    // 读 java 的输出 写控制台日志并盯着就绪和崩溃的关键字
    private async Task _PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _log.WriteServerLine(line);
                _Remember(line);

                if (!_ready && line.Contains("Done (", StringComparison.Ordinal))
                {
                    _ready = true;
                    _info.ReadyAt = DateTimeOffset.Now;
                    _SetState(HostState.Running);
                    _log.WriteHost("服务端已就绪 可以进服了");
                }
            }
        }
        catch (Exception ex)
        {
            // 进程结束时流会断 不算错误
            _log.WriteHost($"输出读取结束 {ex.GetType().Name}");
        }
    }

    // 有些服务端不打印就绪标志 或者输出被改过 到点就当它起来了
    private async Task _MarkReadyOnTimeoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ReadyTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_ready || _stopRequested || _restartRequested) return;

        _ready = true;
        _info.ReadyAt = DateTimeOffset.Now;
        _SetState(HostState.Running);
        _log.WriteHost("未检测到就绪标志 按超时放行");
    }

    private void _Remember(string line)
    {
        lock (_sync)
        {
            _recentLines.Enqueue(line);
            while (_recentLines.Count > RecentLineCapacity) _recentLines.Dequeue();
        }
    }

    // 从最近的输出里挑一条最能说明问题的 优先崩溃标记
    // 没有标记多半是进程被外部干掉的 那时候最后一行还是正常日志 不能把它当成错误原因
    private string _DescribeCrash(int exitCode)
    {
        string[] recent;
        lock (_sync) recent = _recentLines.ToArray();

        for (var i = recent.Length - 1; i >= 0; i--)
            if (CrashMarkers.Any(marker => recent[i].Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return _Trim(recent[i]);

        var last = recent.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return last is null ? $"退出码 {exitCode}" : $"退出码 {exitCode} 最后输出 {_Trim(last)}";
    }

    private static string _Trim(string line)
    {
        var text = line.Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    // 起服务端之前能查的都查掉 这几条不过的话看门狗只会白转三圈
    private string? _PreflightProblem()
    {
        if (!File.Exists(Path.Combine(_directory, _meta.JarFileName)))
            return "找不到服务端文件 请在启动器里重新安装";

        if (!_meta.EulaAccepted)
            return "需要先同意 Minecraft 最终用户许可协议";

        return null;
    }

    private void _SetState(HostState state)
    {
        lock (_sync)
        {
            _info.State = state switch
            {
                HostState.Starting => "starting",
                HostState.Running => "running",
                HostState.Stopping => "stopping",
                HostState.Restarting => "restarting",
                HostState.Stopped => "stopped",
                HostState.Faulted => "faulted",
                _ => "unknown"
            };
            _info.Crashes = _crashes;
            _info.TunnelState = _tunnel.State;
            _info.TunnelError = _tunnel.LastError;
            _info.UpdatedAt = DateTimeOffset.Now;
            _WriteInfoLocked();
        }
    }

    // 隧道状态变化时把 host.json 重写一遍 启动器就是靠它显示隧道状态的
    private void _PersistTunnelState()
    {
        lock (_sync)
        {
            _info.TunnelState = _tunnel.State;
            _info.TunnelError = _tunnel.LastError;
            _info.UpdatedAt = DateTimeOffset.Now;
            _WriteInfoLocked();
        }
    }

    // 调用方必须已经持有 _sync
    private void _WriteInfoLocked()
    {
        try
        {
            // 先写临时文件再换名 免得启动器读到写了一半的 json
            var temp = _infoPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_info, JsonOptions));
            File.Move(temp, _infoPath, true);
        }
        catch (Exception ex)
        {
            _log.WriteHost($"写 host.json 失败 {ex.Message}");
        }
    }

    #region 控制接口

    internal void LogHost(string text) => _log.WriteHost(text);

    internal bool StartTunnel() => _tunnel.Start();

    internal void StopTunnel() => _tunnel.Stop();

    internal string StatusJson()
    {
        lock (_sync) return JsonSerializer.Serialize(_info, JsonOptions);
    }

    // 停止服务端并且让宿主一起退出
    internal void RequestStop()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            signal = _actionSignal;
        }

        _restartDelay?.Cancel();
        signal.TrySetResult();

        if (_process is not null) _SendStop();
    }

    // 强制停止 直接结束进程树 不给服务端保存世界的时间
    // 走的是和停止同一条退出路径 所以不会触发看门狗重启
    internal void RequestKill()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            _killRequested = true;
            signal = _actionSignal;
        }

        _restartDelay?.Cancel();
        signal.TrySetResult();
        _KillProcess();
    }

    private void _KillProcess()
    {
        var process = _process;
        if (process is null) return;

        _log.WriteHost("收到强制停止请求 直接结束服务端进程");
        _SetState(HostState.Stopping);
        try
        {
            process.Kill(true);
        }
        catch (Exception ex)
        {
            _log.WriteHost($"强制结束失败 {ex.Message}");
        }
    }

    // 重启服务端 宿主留着
    internal void RequestRestart()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_stopRequested) return;
            _restartRequested = true;
            signal = _actionSignal;
        }

        // 退避等待期间请求重启 直接跳过等待
        var delay = _restartDelay;
        if (delay is not null)
        {
            delay.Cancel();
            return;
        }

        if (_process is not null)
        {
            signal.TrySetResult();
            _SendStop();
        }
    }

    internal bool SendCommand(string command)
    {
        var process = _process;
        if (process is null) return false;

        try
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _log.WriteHost($"发送命令失败 {ex.Message}");
            return false;
        }
    }

    #endregion
}

// 控制台日志 java 的输出和宿主事件写在一起 启动器增量读这个文件显示控制台
// 不用服务端自己的 logs/latest.log 是因为宿主事件 比如自动重启 也得让用户看见
internal sealed class ConsoleLog
{
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly string _path;
    private readonly string _backupPath;
    private StreamWriter? _writer;

    internal ConsoleLog(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _path = Path.Combine(logDirectory, "console.log");
        _backupPath = Path.Combine(logDirectory, "console.1.log");
    }

    internal void WriteServerLine(string line) => _Write(line);

    internal void WriteHost(string text) => _Write($"[{DateTime.Now:HH:mm:ss}] [主机] {text}");

    internal void WriteTunnel(string text) => _Write($"[{DateTime.Now:HH:mm:ss}] [隧道] {text}");

    private readonly object _sync = new();

    private void _Write(string line)
    {
        lock (_sync)
        {
            try
            {
                _EnsureOpen();
                if (_writer!.BaseStream.Length > MaxBytes) _Rotate();
                _writer.WriteLine(line);
                // 启动器按行增量读 每行都刷出去
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 磁盘写不进去不能拖垮服务端托管
            }
        }
    }

    private void _EnsureOpen()
    {
        if (_writer is not null) return;
        _writer = new StreamWriter(
            new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
    }

    private void _Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        File.Move(_path, _backupPath, true);
    }
}
