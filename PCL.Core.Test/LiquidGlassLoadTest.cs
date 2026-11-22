using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

// 验证液态玻璃着色器的加载链路
//
// 加载失败会被 catch 静默吞掉 界面上只表现为液态玻璃开关没反应 跟普通毛玻璃一模一样 极难排查
// 所以把这条路径显式钉住
//
// 必须在真实的 Application 环境下测 pack 方案是 WPF 启动应用时才注册的
// 在那之前解析 pack URI 会抛异常 而静态构造只跑一次 一旦赶在注册之前就永久退化了
[TestClass]
public class LiquidGlassLoadTest
{
    private const string AssemblyName = "PCL.Core";
    private const string ShaderPath = "UI/Assets/Shaders/LiquidGlass.ps";
    private const string PixelShaderUri =
        "pack://application:,,,/" + AssemblyName + ";component/" + ShaderPath;

    private static readonly Lazy<Dispatcher?> AppHost = new(() =>
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
                app.Shutdown();
            }
            catch
            {
                ready.Set();
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(15));
        return dispatcher;
    });

    private static T OnSta<T>(Func<T> func)
    {
        var dispatcher = AppHost.Value;
        if (dispatcher is null)
            throw new AssertFailedException("无法在测试进程中启动 WPF Application");

        return dispatcher.Invoke(func);
    }

    [TestMethod]
    public void ShaderResourceShouldBePackaged()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var uri = new Uri(PixelShaderUri, UriKind.Absolute);
        var stream = OnSta(() => Application.GetResourceStream(uri));

        Assert.IsNotNull(stream, $"着色器资源未被打包：{PixelShaderUri}");
        try
        {
            Console.WriteLine($"着色器资源大小 = {stream.Stream.Length} 字节");
            Assert.IsTrue(stream.Stream.Length > 100, "着色器资源内容为空");
        }
        finally
        {
            stream.Stream.Dispose();
        }
    }

    // 最关键的一条 IsShaderAvailable 为 false 时 BlurBorder 会静默退回普通模糊
    // 液态玻璃那套折射逻辑一点都不执行
    [TestMethod]
    public void ShaderShouldActuallyLoadInsteadOfSilentlyFallingBack()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var available = OnSta(() => UI.Effects.LiquidGlassEffect.IsShaderAvailable);
        Console.WriteLine($"LiquidGlassEffect.IsShaderAvailable = {available}");

        Assert.IsTrue(available,
            "液态玻璃着色器未能加载 —— 折射不会生效，卡片会退化成普通毛玻璃");
    }

    // 直接 new 一个 PixelShader 看编译产物本身是不是有效 排除空壳和坏二进制
    [TestMethod]
    public void CompiledOutputShouldBeValidPixelShader()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var loaded = OnSta(() =>
        {
            try
            {
                var shader = new PixelShader { UriSource = new Uri(PixelShaderUri, UriKind.Absolute) };
                return shader.UriSource is not null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        });

        Assert.IsTrue(loaded, "LiquidGlass.ps 无法被 WPF 加载");
    }
}
