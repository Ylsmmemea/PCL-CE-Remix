using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Server;
using PCL.Core.Server.Network;

namespace PCL;

// 服务端托管的启动器侧门面
// 真正托管服务端的是 PCL.ServerHost 独立进程 这里只负责拉起它 转发指令 跟着读控制台日志
// 启动器关掉不影响宿主 重开之后靠 host.json 和管道接管回来
public static class ServerRuntime
{
    // 控制台日志的轮询间隔 宿主那边每行都刷盘 够实时了
    private static readonly TimeSpan TailInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(2);

    // 各实例最近一次看到的状态 变了才通知界面刷新
    private static readonly Dictionary<string, string> _lastStates = [];
    private static readonly object _sync = new();

    private static CancellationTokenSource? _watchCts;
    private static long _tailPosition;

    // 服务端有新输出时触发 用于控制台显示
    public static event Action<ServerInstance, string>? OutputReceived;

    // 运行状态变化时触发 用于刷新按钮和状态
    public static event Action? StateChanged;

    public static bool IsRunning(ServerInstance instance)
    {
        var info = ServerHostClient.ReadInfo(instance);
        return info is not null && info.IsHostActive && ServerHostClient.IsHostAlive(info);
    }

    // 界面拿它显示详细状态和上次失败的原因
    public static ServerHostInfo? GetInfo(ServerInstance instance) => ServerHostClient.ReadInfo(instance);

    public static void Start(ServerInstance instance)
    {
        if (IsRunning(instance)) return;

        if (!instance.IsInstalled)
        {
            HintService.Hint(Lang.Text("Server.Launch.NotInstalled"), HintType.Error);
            return;
        }

        if (!instance.EulaAccepted)
        {
            HintService.Hint(Lang.Text("Server.Launch.NeedEula"), HintType.Error);
            return;
        }

        if (!File.Exists(ServerHostClient.HostExecutablePath))
        {
            HintService.Hint(Lang.Text("Server.Launch.NoHost"), HintType.Error);
            return;
        }

        ModBase.RunInNewThread(() =>
        {
            try
            {
                JavaEntry? java;
                lock (ModJava.javaLock)
                    java = ModJava.JavaSelect(Lang.Text("Server.Launch.NoJava"), _GetMinJavaVersion(instance.Version));

                if (java is null)
                {
                    ModBase.RunInUi(() => HintService.Hint(Lang.Text("Server.Launch.NoJava"), HintType.Error));
                    return;
                }

                var startInfo = new ProcessStartInfo(ServerHostClient.HostExecutablePath)
                {
                    WorkingDirectory = instance.DirectoryPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                    // 这里绝不能重定向标准输出 启动器一退出管道就断 宿主会被拖死
                };
                startInfo.ArgumentList.Add("--instance");
                startInfo.ArgumentList.Add(instance.DirectoryPath);
                startInfo.ArgumentList.Add("--java");
                startInfo.ArgumentList.Add(java.Installation.JavaExePath);

                // 拉起来就行 后面全走管道 句柄不用留
                using (Process.Start(startInfo))
                {
                }

                ModBase.Log($"[Server] 已拉起宿主 {instance.Name} Java {java.Installation.JavaExePath}");
                ModBase.RunInUi(() =>
                {
                    OutputReceived?.Invoke(instance, Lang.Text("Server.Console.HostStarted"));
                    StateChanged?.Invoke();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] 拉起宿主失败");
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Server.Launch.Failed"), HintType.Error));
            }
        });
    }

    public static void Stop(ServerInstance instance)
        => _Send(instance, "stop");

    // 强制停止 不给服务端保存世界的时间 直接结束进程
    public static void ForceStop(ServerInstance instance)
        => _Send(instance, "kill");

    public static void Restart(ServerInstance instance)
        => _Send(instance, "restart");

    // 命令走管道转给宿主 由宿主写进服务端标准输入
    public static void SendCommand(ServerInstance instance, string command)
        => _Send(instance, "command " + command);

    // 准备好 frpc 和配置再让宿主把隧道拉起来 返回 null 表示成功 否则是给用户看的错误
    public static async Task<string?> StartTunnelAsync(ServerInstance instance, IProgress<string>? progress = null)
    {
        var provider = (TunnelProvider)Config.Server.TunnelProvider;
        string? problem;

        if (provider == TunnelProvider.SakuraFrp)
        {
            var accessKey = Config.Server.TunnelSakuraKey.Trim();
            if (accessKey.Length == 0) return Lang.Text("Server.Network.Sakura.NoKey");

            if (!int.TryParse(Config.Server.TunnelSakuraTunnel, out var sakuraTunnelId))
                return Lang.Text("Server.Network.Sakura.NoTunnel");

            progress?.Report(Lang.Text("Server.Network.Sakura.LoadingConfig"));
            var (content, configError) = await SakuraFrpApi.GetConfigAsync(accessKey, sakuraTunnelId)
                .ConfigureAwait(false);
            if (content is null) return configError;

            problem = await ServerTunnel.EnsureFrpcAsync(instance, provider, progress).ConfigureAwait(false);
            if (problem is not null) return problem;

            ServerTunnel.WriteSakuraFrpConfig(instance, content);
        }
        else if (provider == TunnelProvider.ChmlFrp)
        {
            // 认证走 OAuth 拿 access token 面板那串 usertoken 已经不能当接口凭证了
            var (accessToken, authError) = await ChmlFrpAuth.GetAccessTokenAsync().ConfigureAwait(false);
            if (accessToken is null) return authError;

            var tunnelId = Config.Server.TunnelChmlFrpTunnel;
            if (tunnelId.Length == 0) return Lang.Text("Server.Network.ChmlFrp.NoTunnel");

            progress?.Report(Lang.Text("Server.Network.ChmlFrp.Loading"));
            var (tunnels, listError) = await ChmlFrpApi.ListTunnelsAsync(accessToken).ConfigureAwait(false);
            if (tunnels is null) return listError;

            var tunnel = tunnels.FirstOrDefault(entry => entry.Id.ToString() == tunnelId);
            if (tunnel is null) return Lang.Text("Server.Network.ChmlFrp.NoTunnel");

            problem = await ServerTunnel.EnsureFrpcAsync(instance, provider, progress).ConfigureAwait(false);
            if (problem is not null) return problem;

            // frpc 的 user 字段是面板个人资料里的用户 token token 字段是隧道的节点密钥
            // 接口已经不返回用户 token 了 只能让用户从面板复制一次
            var frpcUser = Config.Server.TunnelChmlFrpUser.Trim();
            if (frpcUser.Length == 0) return Lang.Text("Server.Network.ChmlFrp.NeedUserToken");

            ServerTunnel.WriteChmlFrpConfig(instance, frpcUser, tunnel);
        }
        else
        {
            var settings = new TunnelSettings
            {
                ServerAddress = Config.Server.TunnelAddress.Trim(),
                ServerPort = Math.Clamp(Config.Server.TunnelPort, 1, 65535),
                Token = Config.Server.TunnelToken.Trim(),
                RemotePort = Config.Server.TunnelRemotePort,
                Name = $"pcl-{instance.Name}"
            };

            if (settings.ServerAddress.Length == 0) return Lang.Text("Server.Tunnel.NeedAddress");
            if (settings.RemotePort is <= 0 or > 65535) return Lang.Text("Server.Tunnel.NeedRemotePort");

            problem = await ServerTunnel.EnsureFrpcAsync(instance, provider, progress).ConfigureAwait(false);
            if (problem is not null) return problem;

            ServerTunnel.WriteManualConfig(instance, settings);
        }

        var response = await ServerHostClient
            .SendAsync(ServerHostClient.PipeNameOf(instance), "tunnel-start").ConfigureAwait(false);

        return response is null || response.Contains("\"ok\":false", StringComparison.Ordinal)
            ? Lang.Text("Server.Tunnel.StartFailed")
            : null;
    }

    public static void StopTunnel(ServerInstance instance)
        => _Send(instance, "tunnel-stop");

    private static void _Send(ServerInstance instance, string command)
    {
        var pipeName = ServerHostClient.PipeNameOf(instance);
        _ = Task.Run(async () =>
        {
            var response = await ServerHostClient.SendAsync(pipeName, command).ConfigureAwait(false);
            if (response is null)
                ModBase.Log($"[Server] 指令 {command} 没有送达 {instance.Name}");
        });
    }

    // 界面切到某个实例时跟着它的控制台日志 传 null 停下
    // 只看新增内容 历史部分由界面自己读一次
    public static void Follow(ServerInstance? instance)
    {
        lock (_sync)
        {
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;

            if (instance is null) return;

            // 从现在的位置开始跟 免得把界面已经显示过的又推一遍
            _tailPosition = _CurrentLength(instance.ConsoleLogPath);

            var cts = new CancellationTokenSource();
            _watchCts = cts;
            _ = _TailLoopAsync(instance, cts.Token);
            _ = _StateLoopAsync(cts.Token);
        }
    }

    private static long _CurrentLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // 跟着读控制台日志 宿主每写一行都会刷盘 这里按字节偏移取新增的部分
    private static async Task _TailLoopAsync(ServerInstance instance, CancellationToken token)
    {
        var path = instance.ConsoleLogPath;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TailInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!File.Exists(path)) continue;

                var length = new FileInfo(path).Length;
                // 日志轮转过就从头来过
                if (length < _tailPosition) _tailPosition = 0;
                if (length <= _tailPosition) continue;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(_tailPosition, SeekOrigin.Begin);

                var buffer = new byte[length - _tailPosition];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) continue;

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                // 只处理到最后一个换行 宿主正在写的半行留到下一轮
                var lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0) continue;

                var complete = text[..lastNewline];
                _tailPosition += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);

                foreach (var line in complete.Split('\n'))
                    OutputReceived?.Invoke(instance, line.TrimEnd('\r'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 宿主正在写 下一轮再来
            }
        }
    }

    // 盯着各实例的宿主状态 变化了才通知界面
    private static async Task _StateLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StateInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_StatesChanged()) ModBase.RunInUi(() => StateChanged?.Invoke());
        }
    }

    private static bool _StatesChanged()
    {
        var root = ServerInstance.RootDirectory;
        if (!Directory.Exists(root)) return false;

        var changed = false;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(directory, ServerHostInfo.FileName);
            var state = "none";

            if (File.Exists(path))
            {
                var info = ServerHostClient.ReadInfoFromDirectory(directory);
                // 宿主没了就按停止算 哪怕文件里还写着运行中
                state = info is null ? "none" : ServerHostClient.IsHostAlive(info) ? info.State : "stopped";
            }

            if (_lastStates.TryGetValue(directory, out var last) && last == state) continue;
            _lastStates[directory] = state;
            changed = true;
        }

        return changed;
    }

    // 服务端要求的 Java 下限 与客户端同一套规则
    private static Version _GetMinJavaVersion(string mcVersion)
    {
        if (!Version.TryParse(mcVersion, out var version)) return new Version(1, 8, 0, 0);
        if (version >= new Version(1, 20, 5)) return new Version(21, 0, 0, 0);
        if (version >= new Version(1, 18)) return new Version(17, 0, 0, 0);
        if (version >= new Version(1, 17)) return new Version(16, 0, 0, 0);
        return new Version(1, 8, 0, 0);
    }
}
