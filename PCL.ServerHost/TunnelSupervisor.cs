using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.ServerHost;

// frpc 隧道的托管 由宿主看着它跑 所以关掉启动器隧道也不会断
// 配置和二进制都由启动器准备好放实例目录的 tunnel 下 宿主只负责拉起来和看门狗
internal sealed class TunnelSupervisor
{
    // frpc 掉线重连是常态 崩溃阈值给宽一点 熔断太早隧道就永久断了
    private const int MaxConsecutiveCrashes = 10;

    private static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    // 稳定跑过这么久就把崩溃计数清零
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromMinutes(2);

    private readonly ConsoleLog _log;
    private readonly Action _onStateChanged;
    private readonly string _executablePath;
    private readonly string _tomlConfigPath;
    private readonly string _iniConfigPath;
    private readonly object _sync = new();

    // 启动时按存在哪个配置文件定下来
    private string _configPath = "";

    private Process? _process;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _crashes;
    private DateTimeOffset _startedAt;
    private string _state = "stopped";
    private string _lastError = "";

    // onStateChanged 让宿主把隧道状态落进 host.json 不然启动器重开看不出来隧道在跑
    internal TunnelSupervisor(string instanceDirectory, ConsoleLog log, Action onStateChanged)
    {
        _log = log;
        _onStateChanged = onStateChanged;
        var directory = Path.Combine(instanceDirectory, "tunnel");
        _executablePath = Path.Combine(directory, "frpc.exe");
        _tomlConfigPath = Path.Combine(directory, "frpc.toml");
        _iniConfigPath = Path.Combine(directory, "frpc.ini");
    }

    internal string State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    internal string LastError
    {
        get
        {
            lock (_sync) return _lastError;
        }
    }

    // 返回 false 表示配置或者二进制还没准备好
    internal bool Start()
    {
        if (!File.Exists(_executablePath))
        {
            _SetError("缺少 frpc 可执行文件");
            return false;
        }

        // frpc 按扩展名决定怎么解析配置 手动模式是 toml ChmlFrp 是 ini
        // 启动器写配置时会把另一份删掉 所以这里看哪个存在就用哪个
        var configPath = File.Exists(_iniConfigPath) ? _iniConfigPath
            : File.Exists(_tomlConfigPath) ? _tomlConfigPath
            : null;
        if (configPath is null)
        {
            _SetError("缺少隧道配置");
            return false;
        }

        lock (_sync)
        {
            if (_loop is not null) return true;

            _configPath = configPath;
            _crashes = 0;
            _lastError = "";
            _state = "starting";
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => _RunLoopAsync(_loopCts.Token));
        }

        _log.WriteHost($"隧道已启动托管 配置 {Path.GetFileName(configPath)}");
        _onStateChanged();
        return true;
    }

    internal void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            cts = _loopCts;
            loop = _loop;
            _loopCts = null;
            _loop = null;
        }

        cts?.Cancel();
        _KillProcess();

        if (loop is not null)
        {
            try
            {
                loop.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // 退出路径上不等了
            }
        }

        cts?.Dispose();
        _SetState("stopped");
    }

    private async Task _RunLoopAsync(CancellationToken token)
    {
        var failed = false;
        while (!token.IsCancellationRequested)
        {
            var exitCode = await _RunOnceAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) break;

            // 跑够久说明只是偶发断了 重新计数
            var ranLong = DateTimeOffset.Now - _startedAt > StableRunThreshold;
            _crashes = ranLong ? 1 : _crashes + 1;

            if (_crashes >= MaxConsecutiveCrashes)
            {
                _SetError($"隧道连续断开 {_crashes} 次，已停止重试");
                _log.WriteHost($"隧道连续断开 {_crashes} 次，不再重试");
                failed = true;
                break;
            }

            _SetState("retrying");
            var seconds = Math.Min(FirstRestartDelay.TotalSeconds * _crashes, MaxRestartDelay.TotalSeconds);
            _log.WriteHost($"隧道断开，{(int)seconds} 秒后重连（第 {_crashes} 次）");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 循环退出必须把 _loop 清掉 不然下次 tunnel-start 会被当成已在运行直接返回
        // 熔断的情况保留 failed 状态 界面才看得到原因
        lock (_sync)
        {
            _loop = null;
            if (!failed) _state = "stopped";
        }

        _onStateChanged();
    }

    private async Task<int> _RunOnceAsync(CancellationToken token)
    {
        var startInfo = new ProcessStartInfo(_executablePath, $"-c \"{_configPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(_configPath)!,
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
            _SetError($"隧道进程启动失败 {ex.Message}");
            return -1;
        }

        lock (_sync)
        {
            _process = process;
            _startedAt = DateTimeOffset.Now;
            _state = "running";
        }

        _onStateChanged();

        var pumps = new[]
        {
            _PumpAsync(process.StandardOutput, token),
            _PumpAsync(process.StandardError, token)
        };

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        await Task.WhenAll(pumps).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        process.Dispose();
        lock (_sync) _process = null;

        if (!token.IsCancellationRequested)
            _log.WriteHost($"隧道进程退出 退出码 {exitCode}");

        return exitCode;
    }

    // frpc 的输出转进控制台日志 好让用户在面板上看到隧道状态
    private async Task _PumpAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _log.WriteTunnel(line);
                _RememberProblem(line);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log.WriteTunnel($"隧道输出读取结束 {ex.GetType().Name}");
        }
    }

    // frpc 出错时会把原因打在日志里 留一条给界面显示
    private void _RememberProblem(string line)
    {
        if (line.Contains("[W]", StringComparison.Ordinal) || line.Contains("[E]", StringComparison.Ordinal))
            lock (_sync) _lastError = _Trim(line);
    }

    private static string _Trim(string line)
    {
        // frpc 的日志带 ANSI 颜色码 显示前去掉
        var cleaned = System.Text.RegularExpressions.Regex.Replace(line, "\u001b\\[[0-9;]*m", "").Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private void _KillProcess()
    {
        Process? process;
        lock (_sync) process = _process;
        if (process is null) return;

        try
        {
            process.Kill(true);
        }
        catch (Exception ex)
        {
            _log.WriteTunnel($"结束隧道进程失败 {ex.Message}");
        }
    }

    private void _SetState(string state)
    {
        lock (_sync) _state = state;
        _onStateChanged();
    }

    private void _SetError(string message)
    {
        lock (_sync)
        {
            _state = "failed";
            _lastError = message;
        }

        _log.WriteHost(message);
        _onStateChanged();
    }
}
