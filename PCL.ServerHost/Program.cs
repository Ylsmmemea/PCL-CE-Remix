using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.ServerHost;

// 服务器实例的常驻宿主 每个实例一个进程 独立于启动器运行
// 启动器关掉之后服务端继续跑 崩溃由看门狗拉起 启动器重开再接管显示
// 用法 PCL.ServerHost.exe --instance <实例目录> [--java <java.exe>]
internal static class Program
{
    // 与启动器之间的协议版本 host.json 和管道响应都会带上 对不上启动器会提示重启实例
    internal const int ProtocolVersion = 1;

    private static async Task<int> Main(string[] args)
    {
        var instanceDirectory = _ArgumentValue(args, "--instance");
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return _Fail(null, "缺少 --instance 参数");

        instanceDirectory = Path.GetFullPath(instanceDirectory);
        if (!Directory.Exists(instanceDirectory))
            return _Fail(null, $"实例目录不存在 {instanceDirectory}");

        var meta = InstanceMeta.Load(instanceDirectory);
        if (meta is null)
            return _Fail(instanceDirectory, "实例元数据读取失败 请重新创建实例");

        // 启动器解析好的 Java 优先 元数据里存的那份是上次用的 可能已经失效
        var javaPath = _ArgumentValue(args, "--java");
        if (string.IsNullOrWhiteSpace(javaPath)) javaPath = meta.JavaPath;
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            return _Fail(instanceDirectory, "Java 路径无效 请在启动器里重新选择 Java");

        var identifier = HostNaming.IdentifierOf(instanceDirectory);

        // 同一实例只允许一个宿主 重复拉起说明状态乱了 让调用方去读现成的 host.json
        using var singleInstance = new Mutex(true, HostNaming.MutexName(identifier), out var createdNew);
        if (!createdNew)
            return _Fail(null, "该实例已有宿主在运行");

        var supervisor = new ServerSupervisor(instanceDirectory, meta, javaPath, identifier);

        // 宿主没窗口也没控制台 崩了外面只看到服务端失联 异常一定要落进控制台日志
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            supervisor.LogHost($"宿主发生未处理异常 {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            supervisor.LogHost($"宿主后台任务异常 {eventArgs.Exception}");
            eventArgs.SetObserved();
        };

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            supervisor.RequestStop();
        };

        using var control = new ControlServer(HostNaming.PipeName(identifier), supervisor);
        control.Start();

        var exitCode = await supervisor.RunAsync().ConfigureAwait(false);
        // 服务端都停了 隧道留着也没意义
        supervisor.StopTunnel();
        return exitCode;
    }

    private static string? _ArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

        return null;
    }

    // 启动失败时宿主还没有日志设施 尽力写一下 stderr 没有控制台写不出去也无所谓
    // 实例目录已经知道的话把原因写进 host.json 启动器才显示得出来
    private static int _Fail(string? instanceDirectory, string message)
    {
        if (instanceDirectory is not null) HostInfoFile.WriteFailure(instanceDirectory, message);

        try
        {
            Console.Error.WriteLine($"[PCL.ServerHost] {message}");
        }
        catch (Exception)
        {
            // 句柄无效时抛异常 忽略
        }

        return 2;
    }
}
