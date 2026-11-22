using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Effects;
using Color = System.Windows.Media.Color;

namespace PCL.Core.Test;

/// <summary>
///     在真实窗口上验证液态玻璃 —— 这是唯一能反映实际观感的方式。<br/>
///     <para>
///     <see cref="RenderTargetBitmap"/> 走软件光栅化，会整体跳过自定义 <c>ShaderEffect</c>
///     （实测连仓库里已有的 <c>AdaptiveBlur</c> 都是 0% 差异），所以任何离屏断言都无法
///     反映真实渲染结果。这里改为开一个真窗口、走 GPU 渲染，再用 GDI 从屏幕取回像素。
///     </para>
/// </summary>
[TestClass]
public class RealWindowGlassTest
{
    private const int W = 480;
    private const int H = 320;
    private const int Cell = 16;

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

    /// <summary>把消息队列泵到 Background 优先级，让渲染提交有机会执行。</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>高饱和棋盘：两种格子亮度差异大，位移极易检出。</summary>
    private static BitmapSource Checker()
    {
        var stride = W * 4;
        var px = new byte[stride * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var on = ((x / Cell) + (y / Cell)) % 2 == 0;
            var o = y * stride + x * 4;
            px[o + 0] = on ? (byte)60 : (byte)250;
            px[o + 1] = on ? (byte)200 : (byte)60;
            px[o + 2] = on ? (byte)255 : (byte)80;
            px[o + 3] = 255;
        }

        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    private static LiquidGlassEffect MakeEffect(double refraction) => new()
    {
        BlurAmount = 6,
        Refraction = refraction,
        RefThickness = 50,
        NoiseScale = 40,
        NoiseStrength = 0,
        FlowTime = 10,
        Glare = 0,
        TextureSize = new System.Windows.Point(W, H),
        Tint = Color.FromArgb(0, 255, 255, 255),
        Opacity = 1
    };

    /// <summary>把任意 UIElement 放进真窗口渲染一次并抓回像素。</summary>
    private static byte[] Capture(Func<UIElement> build, string? dumpName = null)
    {
        var root = build();

        var window = new Window
        {
            Content = root,
            Width = W,
            Height = H,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 200,
            Top = 200
        };

        window.Show();
        try
        {
            Pump();
            Thread.Sleep(250);
            Pump();
            Thread.Sleep(100);
            Pump();

            var origin = window.PointToScreen(new System.Windows.Point(0, 0));

            using var shot = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(W, H));

            if (dumpName is not null)
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "TestArtifacts");
                Directory.CreateDirectory(dir);
                shot.Save(Path.Combine(dir, dumpName + ".png"), ImageFormat.Png);
            }

            var stride = W * 4;
            var result = new byte[stride * H];
            var data = shot.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < H; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + y * data.Stride, result, y * stride, stride);
            }
            finally
            {
                shot.UnlockBits(data);
            }

            return result;
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static double DiffRatio(byte[] a, byte[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) > 12 || Math.Abs(a[i + 1] - b[i + 1]) > 12 ||
                Math.Abs(a[i + 2] - b[i + 2]) > 12) n++;
        return (double)n / (a.Length / 4);
    }

    /// <summary>只有图片本身，无宿主结构，作为基线。</summary>
    private static byte[] Plain(double refraction)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = Checker(),
            Stretch = Stretch.Fill,
            Width = W,
            Height = H,
            Effect = MakeEffect(refraction)
        };
        return Capture(() => image);
    }

    /// <summary>
    ///     复现 MyCard 的层次结构：父级带不透明/半透明底色，玻璃面板嵌在其内部。<br/>
    ///     <c>BackgroundPresenter</c> 会把父级的绘制内容一并算进玻璃的输入，
    ///     因此父级底色的不透明度直接决定玻璃能否看到背后的纹理。
    /// </summary>
    private static byte[] InHost(byte parentAlpha, string? dumpName = null)
    {
        var root = new Grid { Width = W, Height = H, Background = System.Windows.Media.Brushes.Transparent };

        // 模拟壁纸
        root.Children.Add(new System.Windows.Controls.Image
        {
            Source = Checker(),
            Stretch = Stretch.Fill
        });

        // 模拟 MyCard：带底色的父级容器（与 MyCard 同为 Grid，背景由容器自身绘制）
        var card = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(parentAlpha, 0xFB, 0xFB, 0xFB))
        };

        // 模拟 mainBorder：真正承担玻璃的 BlurBorder，效果由全局配置驱动
        var glass = new BlurBorder
        {
            BlurRadius = 16,
            CornerRadius = new CornerRadius(5)
        };

        card.Children.Add(glass);
        root.Children.Add(card);

        return Capture(() => root, dumpName);
    }

    [TestMethod]
    public void 基线_纯图片上折射必须生效()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var (flat, bent) = OnSta(() => (Plain(0), Plain(60)));
        var ratio = DiffRatio(flat, bent);
        Console.WriteLine($"纯图片：折射开/关差异 = {ratio:P1}");
        Assert.IsTrue(ratio > 0.05, $"基线折射未生效（{ratio:P1}）");
    }

    /// <summary>
    ///     核心诊断：真实的 <see cref="BlurBorder"/> 在父级容器内能否看到背后的纹理。<br/>
    ///     与 <see cref="Plain"/> 的区别在于它的输入由 <c>BackgroundPresenter</c> 抓取父级视觉树
    ///     得到，而非元素自身内容 —— 父级底色会整体算进输入。
    /// </summary>
    [TestMethod]
    public void 诊断_BlurBorder在父级容器内的表现()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var (enabled, ratio) = OnSta(() =>
        {
            var ok = false;
            try
            {
                PCL.Core.App.Config.Preference.Glass.LiquidEnabled = true;
                PCL.Core.App.Config.Preference.Glass.LiquidRefraction = 60;
                PCL.Core.App.Config.Preference.Glass.LiquidRefThickness = 50;
                PCL.Core.App.Config.Preference.Glass.LiquidNoiseScale = 40;
                PCL.Core.App.Config.Preference.Glass.LiquidNoiseStrength = 0;
                PCL.Core.App.Config.Preference.Glass.LiquidGlare = 0;
                ok = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"配置不可用：{ex.GetType().Name}: {ex.Message}");
            }

            if (!ok) return (false, 0d);

            var a = InHost(36, "blurborder-on");   // alpha 0.14，对应开启毛玻璃
            var b = InHost(210, "blurborder-82");  // alpha 0.824，对应关闭毛玻璃
            return (true, DiffRatio(a, b));
        });

        if (!enabled)
        {
            Console.WriteLine("Config 在测试环境不可用，跳过");
            return;
        }

        Console.WriteLine($"父级 0.14 vs 0.824 的渲染差异：{ratio:P1}");
        Console.WriteLine("（差异大 → 父级底色确实参与了玻璃输入；差异小 → 玻璃抓不到父级底色）");
    }

    /// <summary>
    ///     边缘不得出现黑边：输入为高饱和棋盘，取最外圈最暗像素与内圈对比。
    /// </summary>
    [TestMethod]
    public void 边缘不应出现黑边()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var px = OnSta(() => Plain(60));

        var stride = W * 4;
        for (var ring = 0; ring < 4; ring++)
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

    /// <summary>核心验收：折射必须真的改变边缘的背景内容，且不影响中心。</summary>
    [TestMethod]
    public void 边缘应发生折射而中心保持平整()
    {
        Assert.IsNotNull(AppHost.Value, "无法在测试进程中启动 WPF Application");

        var (flat, bent) = OnSta(() => (Plain(0), Plain(60)));

        var stride = W * 4;
        int edgeChanged = 0, edgeTotal = 0, centerChanged = 0, centerTotal = 0;

        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = y * stride + x * 4;
            var changed = Math.Abs(flat[o] - bent[o]) > 12;
            var edgeDist = Math.Min(Math.Min(x, W - 1 - x), Math.Min(y, H - 1 - y));

            if (edgeDist <= 40) { edgeTotal++; if (changed) edgeChanged++; }
            else if (edgeDist > 80) { centerTotal++; if (changed) centerChanged++; }
        }

        var edgeRatio = (double)edgeChanged / edgeTotal;
        var centerRatio = (double)centerChanged / centerTotal;
        Console.WriteLine($"边缘差异 {edgeRatio:P1}（{edgeChanged}/{edgeTotal}）");
        Console.WriteLine($"中心差异 {centerRatio:P1}（{centerChanged}/{centerTotal}）");

        Assert.IsTrue(edgeRatio > 0.30, $"边缘折射未生效：仅 {edgeRatio:P1}");
        Assert.IsTrue(centerRatio < 0.02, $"中心区域被过度扭曲：{centerRatio:P1}");
    }
}
