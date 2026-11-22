using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Localization;
using PCL.Core.Server;
using PCL.Core.Server.Network;

namespace PCL;

// 单服务器管理界面的分页 由左栏菜单切换
public enum ServerSection
{
    Home,
    Console,
    Network,
    Files
}

public partial class PageServerRight
{
    // 控制台只留最近的输出 多了会拖慢渲染
    private const int MaxConsoleLines = 500;

    private ServerInstance? _instance;
    private readonly Queue<string> _consoleLines = new();

    // 首页那几个数字要一直走 只在详情页里跑
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public PageServerRight()
    {
        InitializeComponent();
        ServerRuntime.OutputReceived += _OnOutput;
        ServerRuntime.StateChanged += _RefreshState;
        _statsTimer.Tick += (_, _) => _RefreshStats();
    }

    // 首页的信息卡 联机地址 在线时间 内存占用
    private void _RefreshStats()
    {
        if (_instance is null) return;

        var info = ServerRuntime.GetInfo(_instance);
        var alive = ServerHostClient.IsHostAlive(info);

        var tunnelAddress = _TunnelAddressOf();
        if (alive && info!.TunnelState == "running" && tunnelAddress.Length > 0)
        {
            LabHomeAddress.Text = tunnelAddress;
            LabHomeAddressHint.Text = Lang.Text("Server.Home.Address.Ready");
        }
        else
        {
            LabHomeAddress.Text = $"127.0.0.1:{_instance.Port}";
            LabHomeAddressHint.Text = Lang.Text("Server.Home.Address.Local");
        }

        if (alive && info?.ServerStartedAt is { } started)
        {
            var span = DateTimeOffset.Now - started;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            LabHomeUptime.Text = span.TotalHours >= 1
                ? Lang.Text("Server.Home.Uptime.Hour", (int)span.TotalHours, span.Minutes)
                : Lang.Text("Server.Home.Uptime.Minute", span.Minutes, span.Seconds);
            LabHomeUptimeHint.Text = Lang.Text("Server.Home.Uptime.Since", started.ToString("HH:mm:ss"));
        }
        else
        {
            LabHomeUptime.Text = Lang.Text("Server.Wall.Stopped");
            LabHomeUptimeHint.Text = "";
        }

        var used = _ServerMemoryBytes(info, alive);
        LabHomeMemory.Text = used > 0 ? _FormatSize(used) : "—";
        LabHomeMemoryHint.Text = Lang.Text("Server.Home.Memory.Limit", _instance.MemoryMb);
    }

    // 当前服务商对应的外网地址 隧道没在跑的时候不算数
    private string _TunnelAddressOf()
    {
        var address = (TunnelProvider)Config.Server.TunnelProvider switch
        {
            TunnelProvider.ChmlFrp => _chmlFrpAddress,
            TunnelProvider.SakuraFrp => _sakuraAddress,
            _ => Config.Server.TunnelAddress.Length > 0
                ? $"{Config.Server.TunnelAddress}:{Config.Server.TunnelRemotePort}"
                : ""
        };

        // 重开启动器还没登录穿透账号时上面是空的 但隧道可能正在跑 直接读宿主用的配置文件
        return address.Length > 0 ? address : _TunnelAddressFromConfig();
    }

    private string _TunnelAddressFromConfig()
    {
        if (_instance is null) return "";

        foreach (var name in new[] { "frpc.ini", "frpc.toml" })
        {
            var path = Path.Combine(ServerTunnel.DirectoryOf(_instance), name);
            if (!File.Exists(path)) continue;

            try
            {
                string? host = null;
                string? port = null;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    var index = line.IndexOf('=');
                    if (index <= 0) continue;

                    var key = line[..index].Trim().ToLowerInvariant();
                    var value = line[(index + 1)..].Trim().Trim('"');
                    if (key is "server_addr" or "serveraddr") host = value;
                    else if (key is "remote_port" or "remoteport") port = value;
                }

                if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port)) return $"{host}:{port}";
            }
            catch (IOException)
            {
                // 读不出来就当不知道
            }
        }

        return "";
    }

    // 服务端进程的物理内存 宿主没在跑或者进程已经没了就是 0
    private static long _ServerMemoryBytes(ServerHostInfo? info, bool alive)
    {
        if (!alive || info is null || info.ServerPid <= 0) return 0;

        try
        {
            using var process = Process.GetProcessById(info.ServerPid);
            return process.WorkingSet64;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static string _FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB"
    };

    // 从卡片墙点进某台服务器
    public void Enter(ServerInstance instance)
    {
        PanWall.Visibility = Visibility.Collapsed;
        PanDetail.Visibility = Visibility.Visible;
        ShowInstance(instance);
        ShowSection(ServerSection.Home);
        ModMain.frmServerLeft?.ShowMenu(instance);
        _statsTimer.Start();
    }

    // 实例增删后重建卡片墙
    public void RefreshWall() => PanWall.Refresh();

    // 回卡片墙
    public void ExitToList()
    {
        _statsTimer.Stop();
        PanFiles.Close();
        PanDetail.Visibility = Visibility.Collapsed;
        PanWall.Visibility = Visibility.Visible;
        ServerRuntime.Follow(null);
        _instance = null;
        ModMain.frmServerLeft?.ShowList();
        PanWall.Refresh();
    }

    // 左栏菜单切分页
    public void ShowSection(ServerSection section)
    {
        PanHome.Visibility = section == ServerSection.Home ? Visibility.Visible : Visibility.Collapsed;
        PanConsole.Visibility = section == ServerSection.Console ? Visibility.Visible : Visibility.Collapsed;
        PanNetwork.Visibility = section == ServerSection.Network ? Visibility.Visible : Visibility.Collapsed;
        PanFiles.Visibility = section == ServerSection.Files ? Visibility.Visible : Visibility.Collapsed;

        if (section == ServerSection.Files)
        {
            if (_instance is not null) PanFiles.Show(_instance);
        }
        else
        {
            PanFiles.Close();
        }

        if (section == ServerSection.Home) _RefreshStats();
    }

    // 切换到某个实例
    public void ShowInstance(ServerInstance? instance)
    {
        // 状态刷新会反复调进来 还是同一个实例就别动控制台 不然每次都被清掉重读
        var sameInstance = instance is not null && _instance is not null
                           && instance.DirectoryPath == _instance.DirectoryPath;

        _instance = instance;

        if (sameInstance)
        {
            _RefreshState();
            return;
        }

        // 切走就把日志跟随停掉 不然后台还在给不显示的页面推输出
        ServerRuntime.Follow(null);
        if (instance is null) return;

        LabName.Text = instance.Name;
        LabAddress.Text = Lang.Text("Server.Panel.Address", instance.Version, instance.Port);
        PanEula.Visibility = instance.EulaAccepted ? Visibility.Collapsed : Visibility.Visible;
        CheckOnlineMode.SetChecked(ServerInstallLib.ReadOnlineMode(instance), false);

        _consoleLines.Clear();
        LabConsole.Text = "";
        TextCommand.Text = "";
        _LoadTunnelSettings();
        LabProbe.Text = "";
        LabTunnel.Text = "";
        _LoadTailLog(instance);
        // 先读历史再跟上 顺序反了会把历史推两遍
        ServerRuntime.Follow(instance);
        _RefreshState();
    }

    // 把上次运行留下的日志尾部读进来 切过来时不至于一片空白
    // 读的是宿主写的控制台日志 比服务端自己的 latest.log 多了重启这类宿主事件
    private void _LoadTailLog(ServerInstance instance)
    {
        try
        {
            if (!File.Exists(instance.ConsoleLogPath)) return;

            // 宿主正拿着这个文件写 File.ReadLines 的共享模式会撞上 必须自己开 ReadWrite
            using var stream = new FileStream(instance.ConsoleLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                while (lines.Count > 200) lines.Dequeue();
            }

            foreach (var line in lines.TakeLast(80)) _AppendLine(line);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Server] 读取控制台日志失败");
        }
    }

    private void _OnOutput(ServerInstance instance, string line)
    {
        if (_instance is null || instance.DirectoryPath != _instance.DirectoryPath) return;
        ModBase.RunInUi(() => _AppendLine(line));
    }

    private void _AppendLine(string line)
    {
        _consoleLines.Enqueue(line);
        while (_consoleLines.Count > MaxConsoleLines) _consoleLines.Dequeue();

        LabConsole.Text = string.Join(Environment.NewLine, _consoleLines);
        // 布局跑完之后再滚 跟 AI 对话面板同一个写法
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PanConsoleScroll.ScrollToEnd));
    }

    private void _RefreshState()
    {
        if (_instance is null) return;
        ModBase.RunInUi(() =>
        {
            if (_instance is null) return;

            var info = ServerRuntime.GetInfo(_instance);
            var alive = ServerHostClient.IsHostAlive(info);

            var statusKey = "Server.Status.Stopped";
            if (alive)
                statusKey = info!.State switch
                {
                    "starting" => "Server.Status.Starting",
                    "restarting" => "Server.Status.Restarting",
                    "stopping" => "Server.Status.Stopping",
                    _ => "Server.Status.Running"
                };

            LabStatus.Text = Lang.Text(statusKey);

            // 停止态只留启动 运行态才给重启和两种停止
            BtnStart.Visibility = alive ? Visibility.Collapsed : Visibility.Visible;
            var runningVisibility = alive ? Visibility.Visible : Visibility.Collapsed;
            BtnRestart.Visibility = runningVisibility;
            BtnStop.Visibility = runningVisibility;
            BtnForceStop.Visibility = runningVisibility;

            // 熔断退出之后把原因挂出来 不然界面上只看到"已停止"
            var problem = !alive && info is { State: "faulted", LastError.Length: > 0 } ? info.LastError : "";
            LabProblem.Text = problem;
            LabProblem.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;

            // 隧道状态 手动模式的外网地址是 frps 地址加远端端口 ChmlFrp 用隧道自己的地址
            // 宿主没在跑的时候不能显示隧道状态 host.json 里留着的是上次的残留
            var provider = (TunnelProvider)Config.Server.TunnelProvider;
            var address = provider switch
            {
                TunnelProvider.ChmlFrp => _chmlFrpAddress.Length > 0 ? _chmlFrpAddress : "—",
                TunnelProvider.SakuraFrp => _sakuraAddress.Length > 0 ? _sakuraAddress : "—",
                _ => $"{Config.Server.TunnelAddress}:{Config.Server.TunnelRemotePort}"
            };
            LabTunnel.Text = !alive ? "" : info!.TunnelState switch
            {
                "running" => Lang.Text("Server.Network.TunnelRunning", address),
                "starting" => Lang.Text("Server.Network.TunnelStarting"),
                "retrying" => Lang.Text("Server.Network.TunnelRetrying"),
                "failed" => info.TunnelError,
                _ => LabTunnel.Text
            };

            // 状态一变首页那几个数字也跟着变 不用等下一次计时
            _RefreshStats();
        });
    }

    private void BtnStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerRuntime.Start(_instance);
    }

    private void BtnRestart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerRuntime.Restart(_instance);
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerRuntime.Stop(_instance);
    }

    private void BtnForceStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerRuntime.ForceStop(_instance);
    }

    private void BtnProbe_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        var instance = _instance;
        BtnProbe.IsEnabled = false;
        LabProbe.Text = Lang.Text("Server.Network.Probing");

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var result = DirectConnectProbe.ProbeAsync(instance.Port).GetAwaiter().GetResult();
                ModBase.RunInUi(() => LabProbe.Text = _DescribeVerdict(result));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] 直连检测失败");
                ModBase.RunInUi(() => LabProbe.Text = Lang.Text("Server.Network.ProbeFailed"));
            }
            finally
            {
                ModBase.RunInUi(() => BtnProbe.IsEnabled = true);
            }
        });
    }

    private static string _DescribeVerdict(DirectConnectResult result) => result.Verdict switch
    {
        DirectConnectVerdict.PublicIPv6 => Lang.Text("Server.Network.Verdict.IPv6", result.Address),
        DirectConnectVerdict.UpnpMapped => Lang.Text("Server.Network.Verdict.Upnp", result.Address),
        DirectConnectVerdict.DoubleNat => Lang.Text("Server.Network.Verdict.DoubleNat", result.PublicIPv4),
        DirectConnectVerdict.UpnpUnavailable => Lang.Text("Server.Network.Verdict.NoUpnp"),
        _ => Lang.Text("Server.Network.Verdict.UpnpFailed")
    };

    private void BtnTunnelStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        var instance = _instance;
        _ApplyTunnelInput();
        BtnTunnelStart.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.TunnelStarting");

        ModBase.RunInNewThread(() =>
        {
            var progress = new Progress<string>(text => ModBase.RunInUi(() => LabTunnel.Text = text));
            string? problem;
            try
            {
                problem = ServerRuntime.StartTunnelAsync(instance, progress).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] 建立隧道失败");
                problem = Lang.Text("Server.Tunnel.StartFailed");
            }

            ModBase.RunInUi(() =>
            {
                BtnTunnelStart.IsEnabled = true;
                LabTunnel.Text = problem ?? Lang.Text("Server.Network.TunnelStarted");
                _RefreshState();
            });
        });
    }

    private void BtnTunnelStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerRuntime.StopTunnel(_instance);
        LabTunnel.Text = Lang.Text("Server.Network.TunnelStopped");
    }

    // 隧道参数跟着实例走太细了 这里就全局一份 切实例时回填
    private void _LoadTunnelSettings()
    {
        TextTunnelAddress.Text = Config.Server.TunnelAddress;
        TextTunnelPort.Text = Config.Server.TunnelPort.ToString();
        TextTunnelToken.Text = Config.Server.TunnelToken;
        TextTunnelRemotePort.Text =
            Config.Server.TunnelRemotePort > 0 ? Config.Server.TunnelRemotePort.ToString() : "";
        TextSakuraKey.Text = Config.Server.TunnelSakuraKey;
        TextChmlFrpUser.Text = Config.Server.TunnelChmlFrpUser;

        ModAnimation.AniControlEnabled += 1;
        try
        {
            var provider = (TunnelProvider)Config.Server.TunnelProvider;
            RadioTunnelManual.SetChecked(provider == TunnelProvider.Manual, false);
            RadioTunnelChmlFrp.SetChecked(provider == TunnelProvider.ChmlFrp, false);
            RadioTunnelSakura.SetChecked(provider == TunnelProvider.SakuraFrp, false);
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }

        _ApplyTunnelProvider();
    }

    private void _ApplyTunnelInput()
    {
        Config.Server.TunnelAddress = TextTunnelAddress.Text.Trim();
        Config.Server.TunnelPort = int.TryParse(TextTunnelPort.Text.Trim(), out var port) ? port : 7000;
        Config.Server.TunnelToken = TextTunnelToken.Text.Trim();
        Config.Server.TunnelRemotePort =
            int.TryParse(TextTunnelRemotePort.Text.Trim(), out var remote) ? remote : 0;
        Config.Server.TunnelSakuraKey = TextSakuraKey.Text.Trim();
        Config.Server.TunnelChmlFrpUser = TextChmlFrpUser.Text.Trim();
    }

    // 三套参数结构完全不同 只显示当前这套
    private void _ApplyTunnelProvider()
    {
        var provider = (TunnelProvider)Config.Server.TunnelProvider;
        PanTunnelManual.Visibility = provider == TunnelProvider.Manual ? Visibility.Visible : Visibility.Collapsed;
        PanTunnelChmlFrp.Visibility = provider == TunnelProvider.ChmlFrp ? Visibility.Visible : Visibility.Collapsed;
        PanTunnelSakura.Visibility = provider == TunnelProvider.SakuraFrp ? Visibility.Visible : Visibility.Collapsed;

        // ChmlFrp 是登录制 没登录就把登录按钮亮出来
        var signedIn = ChmlFrpAuth.IsSignedIn;
        LabChmlFrpAccount.Text = signedIn
            ? Lang.Text("Server.Network.ChmlFrp.SignedInAs", ChmlFrpAuth.SignedInUser)
            : Lang.Text("Server.Network.ChmlFrp.NotSignedIn");
        BtnChmlFrpLogin.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        BtnChmlFrpLogout.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        if (!signedIn) ComboChmlFrpTunnel.Items.Clear();

        // 切过去的时候把隧道拉一次 省得用户还要点一下
        if (provider == TunnelProvider.ChmlFrp && signedIn && ComboChmlFrpTunnel.Items.Count == 0 && !_chmlFrpLoading)
            _ = _LoadChmlFrpTunnelsAsync();
        if (provider == TunnelProvider.SakuraFrp && ComboSakuraTunnel.Items.Count == 0 && !_sakuraLoading &&
            Config.Server.TunnelSakuraKey.Trim().Length > 0)
            _ = _LoadSakuraTunnelsAsync();
    }

    // 登录走浏览器授权 点了会开浏览器 授权完自己回来
    private void BtnChmlFrpLogin_Click(object sender, MouseButtonEventArgs e)
    {
        BtnChmlFrpLogin.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Loading");

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var (code, startError) = ChmlFrpAuth.StartAsync().GetAwaiter().GetResult();
                if (code is null)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = startError ?? Lang.Text("Server.Network.ChmlFrp.LoginFailed"));
                    return;
                }

                ModBase.RunInUi(() =>
                {
                    LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.EnterCode", code.UserCode);
                    // 授权页带着用户码 打开就能直接确认
                    try
                    {
                        Process.Start(new ProcessStartInfo(code.VerifyUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, "[Server] 打开 ChmlFrp 授权页失败");
                    }
                });

                var (done, waitError) = ChmlFrpAuth.WaitAsync(code, CancellationToken.None).GetAwaiter().GetResult();
                if (!done)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = waitError ?? Lang.Text("Server.Network.ChmlFrp.LoginFailed"));
                    return;
                }

                // 取一次用户名 界面上要显示登录的是哪个账号
                var access = ChmlFrpAuth.GetAccessTokenAsync().GetAwaiter().GetResult();
                if (access.Token is not null)
                {
                    var (user, _) = ChmlFrpApi.GetUserInfoAsync(access.Token).GetAwaiter().GetResult();
                    if (user is not null) ChmlFrpAuth.SaveUsername(user.Username);
                }

                // 拉列表要碰控件 只能在 UI 线程上发起 这里是后台线程 直接调会抛跨线程异常
                ModBase.RunInUi(() =>
                {
                    _ApplyTunnelProvider();
                    LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.SignedInAs",
                        ChmlFrpAuth.SignedInUser);
                    _ = _LoadChmlFrpTunnelsAsync();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] ChmlFrp 登录失败");
                ModBase.RunInUi(() => LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.LoginFailed"));
            }
            finally
            {
                ModBase.RunInUi(() => BtnChmlFrpLogin.IsEnabled = true);
            }
        });
    }

    private void BtnChmlFrpLogout_Click(object sender, MouseButtonEventArgs e)
    {
        ChmlFrpAuth.Clear();
        ComboChmlFrpTunnel.Items.Clear();
        _ApplyTunnelProvider();
        LabTunnel.Text = "";
    }

    private bool _sakuraLoading;

    private async Task _LoadSakuraTunnelsAsync()
    {
        var key = TextSakuraKey.Text.Trim();
        if (key.Length == 0)
        {
            LabTunnel.Text = Lang.Text("Server.Network.Sakura.NoKey");
            return;
        }

        _sakuraLoading = true;
        BtnSakuraRefresh.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Loading");

        try
        {
            var (tunnels, error) = await SakuraFrpApi.ListTunnelsAsync(key).ConfigureAwait(true);
            if (tunnels is null)
            {
                LabTunnel.Text = error ?? Lang.Text("Server.Network.ChmlFrp.LoadFailed");
                return;
            }

            var tcpTunnels = tunnels.Where(tunnel => tunnel.Type == "tcp").ToList();
            ComboSakuraTunnel.Items.Clear();
            foreach (var tunnel in tcpTunnels)
                ComboSakuraTunnel.Items.Add(new MyComboBoxItem
                {
                    Content = tunnel.Remote.Length > 0 ? $"{tunnel.Name}（{tunnel.Remote}）" : tunnel.Display,
                    Tag = tunnel.Id.ToString()
                });

            if (tcpTunnels.Count == 0)
            {
                LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Empty");
                return;
            }

            var saved = Config.Server.TunnelSakuraTunnel;
            var index = tcpTunnels.FindIndex(tunnel => tunnel.Id.ToString() == saved);
            ComboSakuraTunnel.SelectedIndex = index >= 0 ? index : 0;
            var selected = tcpTunnels[Math.Max(index, 0)];
            Config.Server.TunnelSakuraTunnel = selected.Id.ToString();
            _sakuraAddress = selected.Remote;
            LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Loaded", tcpTunnels.Count);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Server] 获取樱花隧道列表失败");
            LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.LoadFailed");
        }
        finally
        {
            _sakuraLoading = false;
            BtnSakuraRefresh.IsEnabled = true;
        }
    }

    private string _sakuraAddress = "";

    private void SakuraTunnelChange(object senderRaw, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        if (ComboSakuraTunnel.SelectedItem is not MyComboBoxItem item) return;

        Config.Server.TunnelSakuraTunnel = item.Tag?.ToString() ?? "";
    }

    private void BtnSakuraRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        _ApplyTunnelInput();
        _ = _LoadSakuraTunnelsAsync();
    }

    // 樱花建隧道选节点用弹窗 节点列表是公开的
    private void BtnSakuraCreate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        _ApplyTunnelInput();
        var key = Config.Server.TunnelSakuraKey;
        if (key.Length == 0)
        {
            LabTunnel.Text = Lang.Text("Server.Network.Sakura.NoKey");
            return;
        }

        BtnSakuraCreate.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Creating");

        var instance = _instance;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var (nodes, nodeError) = SakuraFrpApi.ListNodesAsync(key).GetAwaiter().GetResult();
                if (nodes is null || nodes.Count == 0)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = nodeError ?? Lang.Text("Server.Network.ChmlFrp.CreateFailed"));
                    return;
                }

                // 控件只能在 UI 线程上建 而且必须等用户选完再往下走
                // RunInUi 是异步投递的 这里要用会阻塞的 RunInUiWait 拿返回值
                int? picked = null;
                ModBase.RunInUiWait(() =>
                {
                    var options = new List<IMyRadio>();
                    foreach (var node in nodes)
                        options.Add(new MyRadioBox { Text = node.Name });

                    picked = ModMain.MyMsgBoxSelect(options,
                        Lang.Text("Server.Network.ChmlFrp.SelectNode"),
                        button2: Lang.Text("Common.Action.Cancel"));
                });
                if (picked is null)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = "");
                    return;
                }

                var (ok, error) = SakuraFrpApi.CreateTunnelAsync(key,
                    _TunnelName(instance), nodes[picked.Value].Id, instance.Port).GetAwaiter().GetResult();

                ModBase.Log($"[Server] 创建樱花隧道{(ok ? "成功" : $"失败：{error}")}");
                // 拉列表要碰控件 只能在 UI 线程上发起
                ModBase.RunInUi(() =>
                {
                    LabTunnel.Text = ok
                        ? Lang.Text("Server.Network.ChmlFrp.Created")
                        : error ?? Lang.Text("Server.Network.ChmlFrp.CreateFailed");
                    if (ok) _ = _LoadSakuraTunnelsAsync();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] 创建樱花隧道失败");
                ModBase.RunInUi(() => LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.CreateFailed"));
            }
            finally
            {
                ModBase.RunInUi(() => BtnSakuraCreate.IsEnabled = true);
            }
        });
    }

    private void TunnelProviderChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        var sender = (MyRadioBox)senderRaw;
        var got = sender.Tag?.ToString()?.Split("/") ?? Array.Empty<string>();
        if (got.Length < 2) return;

        ConfigService.TrySetValue(got[0], int.Parse(got[1]));
        _ApplyTunnelProvider();
    }

    private void ChmlFrpTunnelChange(object senderRaw, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        if (ComboChmlFrpTunnel.SelectedItem is not MyComboBoxItem item) return;

        Config.Server.TunnelChmlFrpTunnel = item.Tag?.ToString() ?? "";
    }

    // 选中的隧道外网地址 状态栏要显示它
    private string _chmlFrpAddress = "";

    private bool _chmlFrpLoading;

    // 拉当前账号下的隧道列表 认证要 OAuth 拿到的 access token
    private async Task _LoadChmlFrpTunnelsAsync()
    {
        var (token, authError) = await ChmlFrpAuth.GetAccessTokenAsync().ConfigureAwait(true);
        if (token is null)
        {
            LabTunnel.Text = authError ?? Lang.Text("Server.Network.ChmlFrp.NotSignedIn");
            return;
        }

        _chmlFrpLoading = true;
        BtnChmlFrpRefresh.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Loading");

        try
        {
            var (tunnels, error) = await ChmlFrpApi.ListTunnelsAsync(token).ConfigureAwait(true);
            if (tunnels is null)
            {
                LabTunnel.Text = error ?? Lang.Text("Server.Network.ChmlFrp.LoadFailed");
                return;
            }

            // 接口的 type 大小写不保证 只认小写会让列表看着永远是空的 这里忽略大小写
            // 顺带记一行原始内容 列表空的时候能看出是没隧道还是被过滤掉了
            ModBase.Log($"[Server] ChmlFrp 隧道列表 {tunnels.Count} 条：" +
                        string.Join("，", tunnels.Select(t => $"{t.Id}/{t.Name}/{t.Type}")));
            var tcpTunnels = tunnels
                .Where(tunnel => string.Equals(tunnel.Type, "tcp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            ComboChmlFrpTunnel.Items.Clear();
            foreach (var tunnel in tcpTunnels)
                ComboChmlFrpTunnel.Items.Add(new MyComboBoxItem
                {
                    Content = $"{tunnel.Name}（{tunnel.PublicAddress}）",
                    Tag = tunnel.Id.ToString()
                });

            if (tcpTunnels.Count == 0)
            {
                LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Empty");
                return;
            }

            // 选回上次那条 找不到就用第一条
            var saved = Config.Server.TunnelChmlFrpTunnel;
            var index = tcpTunnels.FindIndex(tunnel => tunnel.Id.ToString() == saved);
            ComboChmlFrpTunnel.SelectedIndex = index >= 0 ? index : 0;
            var selected = tcpTunnels[Math.Max(index, 0)];
            Config.Server.TunnelChmlFrpTunnel = selected.Id.ToString();
            _chmlFrpAddress = selected.PublicAddress;
            LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Loaded", tcpTunnels.Count);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Server] 获取 ChmlFrp 隧道列表失败");
            LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.LoadFailed");
        }
        finally
        {
            _chmlFrpLoading = false;
            BtnChmlFrpRefresh.IsEnabled = true;
        }
    }

    private void BtnChmlFrpRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        _ApplyTunnelInput();
        _ = _LoadChmlFrpTunnelsAsync();
    }

    // 拿本机服务端的端口建一条新隧道 名字带实例名好认
    private void BtnChmlFrpCreate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        _ApplyTunnelInput();

        BtnChmlFrpCreate.IsEnabled = false;
        LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.Creating");

        var instance = _instance;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                // 建隧道要 OAuth 的 access token 配置里那串是登录凭据得先换出来
                var (token, authError) = ChmlFrpAuth.GetAccessTokenAsync().GetAwaiter().GetResult();
                if (token is null)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = authError ?? Lang.Text("Server.Network.ChmlFrp.NoToken"));
                    return;
                }

                var (nodes, nodeError) = ChmlFrpApi.ListNodesAsync(token).GetAwaiter().GetResult();
                if (nodes is null || nodes.Count == 0)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = nodeError ?? Lang.Text("Server.Network.ChmlFrp.CreateFailed"));
                    return;
                }

                // 节点必须让用户自己挑 有的节点明令禁止游戏联机 还有的写了违规就封号
                // 排序只把更可能合适的往前放 不做过滤 免得用户想用的节点看不到
                var ordered = nodes
                    .OrderByDescending(node => node.China.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(node => node.IsFreeTier)
                    .ToList();

                // 控件只能在 UI 线程上建 而且必须阻塞等用户选完 异步投递的话拿不到返回值
                int? picked = null;
                ModBase.RunInUiWait(() =>
                {
                    var options = new List<IMyRadio>();
                    foreach (var node in ordered)
                        options.Add(new MyRadioBox
                        {
                            Text = node.Notes.Length > 0 ? $"{node.Name}（{node.Notes}）" : node.Name
                        });

                    picked = ModMain.MyMsgBoxSelect(options,
                        Lang.Text("Server.Network.ChmlFrp.SelectNode"),
                        button2: Lang.Text("Common.Action.Cancel"));
                });
                if (picked is null)
                {
                    ModBase.RunInUi(() => LabTunnel.Text = "");
                    return;
                }

                var selectedNode = ordered[picked.Value];
                // 端口要一个个试 过程报到状态行上 不然十几秒没动静看着像卡死
                var progress = new Progress<string>(text => ModBase.RunInUi(() => LabTunnel.Text = text));
                var (ok, name, port, error) = ChmlFrpApi.CreateTunnelAutoAsync(token,
                        _TunnelName(instance), selectedNode.Name, instance.Port, progress)
                    .GetAwaiter().GetResult();

                // 失败原因光显示在界面上 用户报问题时没法回看 落一行日志
                ModBase.Log($"[Server] 创建 ChmlFrp 隧道{(ok ? $"成功 {name} 外网端口 {port}" : $"失败：{error}")}");
                // 拉列表要碰控件 只能在 UI 线程上发起
                ModBase.RunInUi(() =>
                {
                    LabTunnel.Text = ok
                        ? Lang.Text("Server.Network.ChmlFrp.Created")
                        : error ?? Lang.Text("Server.Network.ChmlFrp.CreateFailed");
                    if (ok) _ = _LoadChmlFrpTunnelsAsync();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Server] 创建 ChmlFrp 隧道失败");
                ModBase.RunInUi(() => LabTunnel.Text = Lang.Text("Server.Network.ChmlFrp.CreateFailed"));
            }
            finally
            {
                ModBase.RunInUi(() => BtnChmlFrpCreate.IsEnabled = true);
            }
        });
    }

    // 服务商的隧道名只收字母数字下划线 实例名可能是中文 拼一个合法的
    private static string _TunnelName(ServerInstance instance)
    {
        var cleaned = new string(instance.Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        return cleaned.Length > 0 ? $"pcl_{cleaned}" : $"pcl_server_{instance.Port}";
    }

    // 正版验证只有服务端启动时才读 运行中改了提醒一句 免得用户以为没保存上
    private void OnlineModeChange(object sender, bool user)
    {
        if (!user || _instance is null) return;

        var enabled = CheckOnlineMode.Checked == true;
        if (!ServerInstallLib.WriteOnlineMode(_instance, enabled))
        {
            HintService.Hint(Lang.Text("Server.Panel.OnlineMode.Failed"), HintType.Error);
            CheckOnlineMode.SetChecked(!enabled, false);
            return;
        }

        if (ServerRuntime.IsRunning(_instance))
            HintService.Hint(Lang.Text("Server.Panel.OnlineMode.Restart"), HintType.Info);
    }

    private void BtnEula_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;
        ServerInstallLib.AcceptEula(_instance);
        PanEula.Visibility = Visibility.Collapsed;
        HintService.Hint(Lang.Text("Server.Panel.EulaAccepted"), HintType.Success);
    }

    private void BtnSend_Click(object sender, MouseButtonEventArgs e) => _SendCommand();

    private void _SendCommand()
    {
        if (_instance is null) return;
        var command = TextCommand.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;

        ServerRuntime.SendCommand(_instance, command);
        _AppendLine($"> {command}");
        TextCommand.Text = "";
    }
}
