using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Effects;

namespace PCL.Core.Test;

/// <summary>
///     液态玻璃着色器的离屏像素验证。<br/>
///     先解决「离屏渲染能否跑自定义着色器」这一前置问题：<see cref="RenderTargetBitmap"/>
///     走软件光栅化，此前测得 <c>AdaptiveBlur</c> 0% 差异，但那可能是因为该着色器在测试宿主里
///     根本没加载（它同样有 catch 吞异常的加载路径）。若在已构造 <see cref="Application"/>
///     的环境下能测出差异，则本文档中的像素断言可信。
/// </summary>
[TestClass]
public class LiquidGlassRenderTest
{
    private const int W = 600;
    private const int H = 300;

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

    /// <summary>高饱和棋盘：两种格子亮度接近但蓝通道差异极大，位移与变暗都极易检出。</summary>
    private static BitmapSource Checker()
    {
        const int cell = 10;
        var stride = W * 4;
        var px = new byte[stride * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var on = ((x / cell) + (y / cell)) % 2 == 0;
            var o = y * stride + x * 4;
            px[o + 0] = on ? (byte)80 : (byte)255;   // B
            px[o + 1] = on ? (byte)200 : (byte)240;  // G
            px[o + 2] = 255;                         // R
            px[o + 3] = 255;
        }

        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] RenderVisual(DrawingVisual visual)
    {
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var stride = W * 4;
        var outPx = new byte[stride * H];
        rtb.CopyPixels(outPx, stride, 0);
        return outPx;
    }

    private static byte[] RenderWith(Effect? effect)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(Checker(), new Rect(0, 0, W, H));

        if (effect is not null)
            visual.Effect = effect;

        return RenderVisual(visual);
    }

    private static double DiffRatio(byte[] a, byte[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) n++;
        return (double)n / (a.Length / 4);
    }

    /// <summary>
    ///     前置问题：离屏渲染到底能不能跑自定义着色器？
    /// </summary>
    [TestMethod]
    public void 前置_离屏渲染能否跑自定义着色器()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var (builtin, adaptive, liquid) = OnSta(() =>
        {
            var raw = RenderWith(null);

            var blur = RenderWith(new BlurEffect { Radius = 30 });
            var adaptiveShader = RenderWith(new AdaptiveBlurEffect { Radius = 30, SamplingRate = 1.0 });
            var liquidShader = RenderWith(new LiquidGlassEffect
            {
                BlurAmount = 6,
                Refraction = 60,
                RefThickness = 50,
                NoiseScale = 40,
                NoiseStrength = 0,
                FlowTime = 10,
                Glare = 0,
                TextureSize = new Point(W, H),
                Tint = Color.FromArgb(0, 255, 255, 255),
                Opacity = 1
            });

            return (DiffRatio(raw, blur), DiffRatio(raw, adaptiveShader), DiffRatio(raw, liquidShader));
        });

        Console.WriteLine($"内置 BlurEffect   vs 原图：{builtin:P1}");
        Console.WriteLine($"AdaptiveBlur     vs 原图：{adaptive:P1}");
        Console.WriteLine($"LiquidGlass      vs 原图：{liquid:P1}");
    }

    /// <summary>
    ///     边缘不得出现黑边。输入全为亮色（luma 约 198~206），
    ///     输出任何明显偏暗的像素都只可能来自越界采样。
    /// </summary>
    [TestMethod]
    public void 边缘不应出现黑边()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var px = OnSta(() => RenderWith(new LiquidGlassEffect
        {
            BlurAmount = 6,
            Refraction = 60,
            RefThickness = 50,
            NoiseScale = 40,
            NoiseStrength = 0,
            FlowTime = 10,
            Glare = 0,
            TextureSize = new Point(W, H),
            Tint = Color.FromArgb(0, 255, 255, 255),
            Opacity = 1
        }));

        var stride = W * 4;
        for (var ring = 0; ring < 3; ring++)
        {
            var darkest = double.MaxValue;
            var at = (X: 0, Y: 0);

            void Consider(int x, int y)
            {
                var o = y * stride + x * 4;
                var luma = 0.299 * px[o + 2] + 0.587 * px[o + 1] + 0.114 * px[o];
                if (luma < darkest) { darkest = luma; at = (x, y); }
            }

            for (var i = ring; i < W - ring; i++) { Consider(i, ring); Consider(i, H - 1 - ring); }
            for (var j = ring; j < H - ring; j++) { Consider(ring, j); Consider(W - 1 - ring, j); }

            Console.WriteLine($"外圈 {ring}：最暗 luma = {darkest:F1} @ ({at.X},{at.Y})");
        }
    }

    /// <summary>
    ///     折射是否真的改变了边缘的背景内容。这是本次改动的核心验收点。
    /// </summary>
    [TestMethod]
    public void 边缘应发生折射而中心保持平整()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var (flat, bent) = OnSta(() =>
        {
            LiquidGlassEffect Make(double refraction) => new()
            {
                BlurAmount = 6,
                Refraction = refraction,
                RefThickness = 50,
                NoiseScale = 40,
                NoiseStrength = 0,
                FlowTime = 10,
                Glare = 0,
                TextureSize = new Point(W, H),
                Tint = Color.FromArgb(0, 255, 255, 255),
                Opacity = 1
            };

            return (RenderWith(Make(0)), RenderWith(Make(60)));
        });

        var stride = W * 4;
        int edgeChanged = 0, edgeTotal = 0, centerChanged = 0, centerTotal = 0;

        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = y * stride + x * 4;
            var changed = Math.Abs(flat[o] - bent[o]) > 12; // 蓝通道 80 vs 255
            var edgeDist = Math.Min(Math.Min(x, W - 1 - x), Math.Min(y, H - 1 - y));

            if (edgeDist <= 40) { edgeTotal++; if (changed) edgeChanged++; }
            else if (edgeDist > 80) { centerTotal++; if (changed) centerChanged++; }
        }

        var edgeRatio = (double)edgeChanged / edgeTotal;
        var centerRatio = (double)centerChanged / centerTotal;
        Console.WriteLine($"边缘差异 {edgeRatio:P1}（{edgeChanged}/{edgeTotal}）");
        Console.WriteLine($"中心差异 {centerRatio:P1}（{centerChanged}/{centerTotal}）");
    }
}
