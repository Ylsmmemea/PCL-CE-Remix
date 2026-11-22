using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

/// <summary>
///     验证液态玻璃着色器的加载链路。<br/>
///     <para>
///     着色器加载失败时会被 <c>catch</c> 静默吞掉，界面上的表现仅仅是「液态玻璃开关没反应、
///     和普通毛玻璃一模一样」—— 极难排查。这里把加载路径显式钉住。
///     </para>
///     <para>
///     必须在真实 <see cref="Application"/> 环境下验证：<c>pack://</c> 方案由 WPF 在应用启动时
///     注册，在此之前解析 pack URI 会抛异常，而静态构造只执行一次，一旦赶在注册之前就永久退化。
///     </para>
/// </summary>
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
    public void 着色器资源必须已打包()
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

    /// <summary>
    ///     这是最关键的一条：<c>IsShaderAvailable</c> 为 <c>false</c> 时，
    ///     <c>BlurBorder.CreateOptimizedBlurEffect()</c> 会静默回退到普通模糊，
    ///     液态玻璃的全部折射逻辑都不会执行。
    /// </summary>
    [TestMethod]
    public void 着色器必须真的加载成功而不是静默退化()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var available = OnSta(() => UI.Effects.LiquidGlassEffect.IsShaderAvailable);
        Console.WriteLine($"LiquidGlassEffect.IsShaderAvailable = {available}");

        Assert.IsTrue(available,
            "液态玻璃着色器未能加载 —— 折射不会生效，卡片会退化成普通毛玻璃");
    }

    /// <summary>
    ///     直接构造 <see cref="PixelShader"/> 验证编译产物本身有效（而非空壳或损坏的二进制）。
    /// </summary>
    [TestMethod]
    public void 编译产物必须是有效的像素着色器()
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
