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
using PCL.Core.UI.Effects;
using Color = System.Windows.Media.Color;

namespace PCL.Core.Test;

/// <summary>
///     液态玻璃的视觉检查工具。<br/>
///     <para>
///     <b>为什么必须用真窗口：</b><see cref="RenderTargetBitmap"/> 走软件光栅化，会整体跳过
///     自定义 <c>ShaderEffect</c>（仓库里已有的 <c>AdaptiveBlur</c> 同样测得 0% 差异），
///     任何离屏断言都不反映真实渲染。
///     </para>
///     <para>
///     <b>为什么必须用真实图像：</b>合成图案（棋盘、同心环）只能验证位移量、方向对称性这类
///     可量化指标，判断不了「像不像玻璃」。而观感恰恰是本项目的主要验收标准，
///     因此默认输入改用参考实现自带的照片，输出直接落盘供目视比对。
///     </para>
///     <para>
///     <b>稳定性：</b>GDI 抓屏可能拿到尚未合成完成的帧（曾出现同一条测试先测出 -58、
///     重跑三次都是 +34 的情况）。所有采样都经过「连抓两帧一致才接受」的校验。
///     </para>
/// </summary>
[TestClass]
public class GlassVisualTest
{
    /// <summary>参考实现自带的测试照片，便于与 example.png 直接对照。</summary>
    private static readonly string[] PhotoCandidates =
    [
        @"D:\Code\_lg_refs\LiqiudGlassHLSL\imgui-example\examples\example_win32_directx11\pic.jpg",
        @"D:\Code\_lg_refs\WPF-Liquid-Glass-Effect\docs\Screenshot.png",
        @"D:\Code\_lg_refs\liquidglass\site\banner.jpg"
    ];

    private static BitmapSource? _photo;

    private static BitmapSource? Photo()
    {
        if (_photo is not null) return _photo;

        foreach (var path in PhotoCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _photo = bmp;
                Console.WriteLine($"测试图：{Path.GetFileName(path)}  {bmp.PixelWidth}x{bmp.PixelHeight}");
                return _photo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载失败 {path}：{ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    ///     高饱和棋盘。用于**量化**测量 —— 照片上有大片纯色区，位移发生在纯色区内
    ///     不会改变任何一个像素，会把真实的折射量测成 0（曾据此误判「上边缘没有折射」）。
    ///     棋盘的高频边缘让任何位移都立刻表现为像素差。
    /// </summary>
    private static BitmapSource Checker(int w, int h, int cell = 12)
    {
        // 格子数必须取偶数，图案才是左右/上下镜像对称的。
        // 若用 x/cell 直接分格，格数通常不是整数，左右两端的相位就不同 ——
        // 同一条折射在左右测得的值会差出百分之几十，看起来像「折射不对称」，
        // 实际是图案自身的偏差（判别方法：正方形图案上「左=上、右=下」成对相等即为图案伪影）。
        var nx = Math.Max(2, w / cell);
        if (nx % 2 != 0) nx++;
        var ny = Math.Max(2, h / cell);
        if (ny % 2 != 0) ny++;

        var stride = w * 4;
        var px = new byte[stride * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var on = (x * nx / w + y * ny / h) % 2 == 0;
            var o = y * stride + x * 4;
            px[o + 0] = on ? (byte)60 : (byte)250;  // B
            px[o + 1] = on ? (byte)200 : (byte)60;  // G
            px[o + 2] = on ? (byte)255 : (byte)80;  // R
            px[o + 3] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    #region 宿主

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
            catch { ready.Set(); }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(15));
        return dispatcher;
    });

    private static T OnSta<T>(Func<T> func)
    {
        var dispatcher = AppHost.Value;
        if (dispatcher is null) throw new AssertFailedException("无法启动 WPF Application");
        return dispatcher.Invoke(func);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    #endregion

    #region 效果参数

    /// <summary>
    ///     与真实应用一致的效果参数。<br/>
    ///     <paramref name="refraction"/> 对应 UiGlassLiquidRefraction 滑块（0~150），
    ///     <paramref name="thickness"/> 对应 UiGlassLiquidRefThickness（0~100），
    ///     <paramref name="blur"/> 对应毛玻璃强度滑块（0~40，实际传入 BlurAmount）。
    /// </summary>
    private static LiquidGlassEffect MakeEffect(int w, int h, int refraction, int thickness,
        double glare, double blur, double cornerRadius = 12) => new()
    {
        BlurAmount = blur,
        Refraction = refraction,
        RefThickness = thickness,
        NoiseScale = 40,
        NoiseStrength = 0,
        FlowTime = 0,
        Glare = glare,
        TextureSize = new System.Windows.Point(w, h),
        CornerRadius = cornerRadius,
        Tint = Color.FromArgb(0, 255, 255, 255),
        Opacity = 1
    };

    #endregion

    #region 抓屏

    private sealed class Shot : IDisposable
    {
        public required Window Window { get; init; }
        public required int W { get; init; }
        public required int H { get; init; }
        public void Dispose() => Window.Close();
    }

    private static Shot Show(int w, int h, Func<FrameworkElement> build)
    {
        var window = new Window
        {
            Content = build(),
            Width = w,
            Height = h,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 40,
            Top = 40
        };

        window.Show();
        Pump();
        Thread.Sleep(220);
        Pump();
        return new Shot { Window = window, W = w, H = h };
    }

    private static byte[] Grab(Shot shot)
    {
        var w = shot.W;
        var h = shot.H;
        var origin = shot.Window.PointToScreen(new System.Windows.Point(0, 0));
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(w, h));

        var stride = w * 4;
        var px = new byte[stride * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, px, y * stride, stride);
        }
        finally { bmp.UnlockBits(data); }

        return px;
    }

    private static double Diff(byte[] a, byte[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) > 2 || Math.Abs(a[i + 1] - b[i + 1]) > 2 ||
                Math.Abs(a[i + 2] - b[i + 2]) > 2) n++;
        return (double)n / (a.Length / 4);
    }

    /// <summary>
    ///     抓取一帧，并校验画面已稳定：连续两次抓取一致才接受。<br/>
    ///     直接 Sleep 后抓屏会拿到未合成完成的帧，这是此前测量出现符号反转的原因。
    /// </summary>
    private static byte[] GrabStable(Shot shot)
    {
        byte[]? prev = null;
        for (var i = 0; i < 6; i++)
        {
            var cur = Grab(shot);
            if (prev is not null && Diff(prev, cur) < 0.002) return cur;
            prev = cur;
            Thread.Sleep(140);
            Pump();
        }

        Console.WriteLine("警告：画面未在预期次数内稳定，结果可能不可靠");
        return prev!;
    }

    private static void SavePng(byte[] px, int w, int h, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "GlassArtifacts");
        Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
    }

    /// <summary>
    ///     在同一块背景上渲染一次玻璃并抓图。<paramref name="applyEffect"/> 为 false 时输出原图。<br/>
    ///     效果实例必须在渲染线程内构造：<see cref="Freezable"/> 有 Dispatcher 亲和性，
    ///     在测试线程创建后再赋给 UI 元素会抛「从错误上下文使用参数访问此 API」。
    /// </summary>
    private static byte[] Render(int w, int h, int refraction, int thickness, double glare,
        double blur, string? dump = null, bool applyEffect = true, BitmapSource? source = null)
    {
        return OnSta(() =>
        {
            var photo = source ?? Photo();
            var effect = applyEffect ? MakeEffect(w, h, refraction, thickness, glare, blur) : null;

            using var shot = Show(w, h, () =>
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = photo ?? throw new AssertFailedException("找不到测试图片"),
                    Stretch = Stretch.UniformToFill,
                    Width = w,
                    Height = h
                };
                if (effect is not null) img.Effect = effect;
                return img;
            });

            try
            {
                var px = GrabStable(shot);
                if (dump is not null) SavePng(px, w, h, dump);
                return px;
            }
            finally { shot.Dispose(); }
        });
    }

    #endregion

    /// <summary>平均亮度，用于判断高光在给定背景上是否还有可见空间。</summary>
    private static double Luma(byte[] px, int w, int h, int x0, int y0, int x1, int y1)
    {
        double s = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var o = (y * w + x) * 4;
            s += 0.299 * px[o + 2] + 0.587 * px[o + 1] + 0.114 * px[o];
            n++;
        }
        return n == 0 ? 0 : s / n;
    }

    /// <summary>
    ///     导出一组可直接目视比对的图：原图 / 折射 / 高光 / 完整。<br/>
    ///     这是判断「像不像液态玻璃」的主要手段 —— 数值指标测不出观感。
    /// </summary>
    [TestMethod]
    public void 导出目视对比图()
    {
        const int w = 640, h = 420;
        // 用户当前配置：折射 60、厚度 50、高光 50、毛玻璃强度约 22
        const int refraction = 60, thickness = 50;
        const double glare = 0.5, blur = 1;

        var flat = Render(w, h, 0, thickness, 0, blur, "A-原图", applyEffect: false);
        var noLight = Render(w, h, refraction, thickness, 0, blur, "B-仅折射");
        var lightOnly = Render(w, h, 0, thickness, glare, blur, "C-仅高光");
        var full = Render(w, h, refraction, thickness, glare, blur, "D-完整");
        var strong = Render(w, h, 150, 50, glare, blur, "E-折射150");

        Console.WriteLine($"B vs A 差异 = {Diff(flat, noLight):P1}  （折射贡献）");
        Console.WriteLine($"C vs A 差异 = {Diff(flat, lightOnly):P1}  （高光贡献）");
        Console.WriteLine($"D vs A 差异 = {Diff(flat, full):P1}  （合计）");
        Console.WriteLine($"E vs D 差异 = {Diff(full, strong):P1}  （滑块满量程的增量）");

        // 细节保留度：中心区域的相邻像素梯度均值（锐度代理）。
        // 中心的折射为 0，因此这里的下降只可能来自模糊 —— 是判断「糊到什么程度」的客观依据。
        // 仅高光这一组折射为 0，其中心锐度的下降完全归因于模糊
        Console.WriteLine($"中心锐度  原图 {Sharpness(flat, w, h):F2}  " +
                          $"纯模糊 {Sharpness(lightOnly, w, h):F2}  " +
                          $"完整 {Sharpness(full, w, h):F2}");

        Assert.IsTrue(true);
    }

    /// <summary>中心区域的相邻像素梯度均值，作为锐度代理。与原图比较即可看出细节被抹掉多少。</summary>
    private static double Sharpness(byte[] px, int w, int h)
    {
        double s = 0;
        var n = 0;
        for (var y = h / 4; y < h * 3 / 4; y++)
        for (var x = w / 4; x < w * 3 / 4; x++)
        {
            var o = (y * w + x) * 4;
            var l1 = 0.299 * px[o + 2] + 0.587 * px[o + 1] + 0.114 * px[o];
            var nxt = (y * w + x + 1) * 4;
            var l2 = 0.299 * px[nxt + 2] + 0.587 * px[nxt + 1] + 0.114 * px[nxt];
            s += Math.Abs(l2 - l1);
            n++;
        }
        return n == 0 ? 0 : s / n;
    }

    /// <summary>
    ///     高光可见性 vs 背景亮度。<br/>
    ///     加法高光在亮背景上会被 <c>saturate</c> 吃掉 —— 背景已经接近纯白时，
    ///     再亮也亮不上去，边缘高光会完全不可见。这解释了「苍白壁纸上看不到光泽」。
    /// </summary>
    [TestMethod]
    public void 高光在不同亮度背景上的可见性()
    {
        const int w = 400, h = 300;

        OnSta(() =>
        {
            foreach (var bgLuma in new[] { 0.2, 0.5, 0.8 })
            {
                var baseBmp = OnSta(() => SolidBitmap(w, h, bgLuma));

                byte[] Run(double glare) => OnSta(() =>
                {
                    using var shot = Show(w, h, () =>
                    {
                        var img = new System.Windows.Controls.Image
                        {
                            Source = baseBmp,
                            Width = w,
                            Height = h,
                            Effect = MakeEffect(w, h, 0, 50, glare, 0)
                        };
                        return img;
                    });
                    try { return GrabStable(shot); }
                    finally { shot.Dispose(); }
                });

                var off = Run(0);
                var on = Run(1.0);

                // 外圈 0-4px 的亮度增量；背景越亮，可用的增量空间越小
                var ringOff = Luma(off, w, h, 0, 0, w, 4) + Luma(off, w, h, 0, h - 4, w, h);
                var ringOn = Luma(on, w, h, 0, 0, w, 4) + Luma(on, w, h, 0, h - 4, w, h);
                var edgeGain = (ringOn - ringOff) / 2;

                var headroom = 255 - bgLuma * 255;
                Console.WriteLine($"背景亮度 {bgLuma:P0}（可增量 {headroom:F0}）：外圈亮度增量 = {edgeGain:F1}");
            }

            return 0;
        });
    }

    private static BitmapSource SolidBitmap(int w, int h, double luma)
    {
        var stride = w * 4;
        var px = new byte[stride * h];
        var v = (byte)Math.Clamp(luma * 255, 0, 255);
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>分区统计折射造成的像素变化，确认四边对称且中心不受影响。</summary>
    [TestMethod]
    public void 分区测量折射强度()
    {
        const int w = 700, h = 500;
        const int thickness = 50;

        var flat = Render(w, h, 0, thickness, 0, 2, "ref-flat", applyEffect: false);

        foreach (var r in new[] { 30, 60, 90, 120, 150 })
        {
            var bent = Render(w, h, r, thickness, 0, 2);
            var stride = w * 4;
            double edge = 0;
            var n = 0;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var ed = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
                if (ed > 40) continue;
                edge += Math.Abs(flat[y * stride + x * 4 + 2] - bent[y * stride + x * 4 + 2]);
                n++;
            }
            Console.WriteLine($"Refraction={r,3}  边缘平均差异 = {edge / n:F2}");
        }

        var full = Render(w, h, 150, thickness, 0, 2, "ref-150");
        var s = w * 4;
        double Band(int x0, int y0, int x1, int y1)
        {
            double sum = 0;
            var c = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                sum += Math.Abs(flat[y * s + x * 4 + 2] - full[y * s + x * 4 + 2]);
                c++;
            }
            return c == 0 ? 0 : sum / c;
        }

        Console.WriteLine($"左边缘 {Band(0, 50, 50, h - 50):F2}   右边缘 {Band(w - 50, 50, w, h - 50):F2}");
        Console.WriteLine($"上边缘 {Band(50, 0, w - 50, 50):F2}   下边缘 {Band(50, h - 50, w - 50, h):F2}");
        Console.WriteLine($"中心区 {Band(100, 100, w - 100, h - 100):F2}  （应接近 0）");
    }

    /// <summary>
    ///     四边对称性与尺寸适配：在棋盘上分别统计四条边与中心。<br/>
    ///     用棋盘而非照片的原因见 <see cref="Checker"/>。<br/>
    ///     尺寸取真实 UI 的典型值 —— 用户反馈过「只有部分卡片有」「侧边菜单没有」，
    ///     而这两者恰恰是极端长宽比的面板。模糊置 0，测到的差异全部来自折射。
    /// </summary>
    [TestMethod]
    public void 四边对称性与尺寸适配()
    {
        const int thickness = 50;

        var cases = new (string Name, int W, int H)[]
        {
            ("卡片 684x77", 684, 77),
            ("卡片 400x200", 400, 200),
            ("侧边栏 200x700", 200, 700),
            ("导航栏 900x48", 900, 48),
            ("方块 400x400", 400, 400)
        };

        foreach (var (name, w, h) in cases)
        {
            // 格子按面板短边取比例。用固定像素格子时该指标会饱和：位移超过一个格子后
            // 差异不再随位移增长，于是 900x48（位移 3.7px）测得 11，
            // 而 400x400（位移 30px）测得 78 —— 看起来像「细长面板效果弱」，
            // 实际只是格子相对位移的比例不同。按比例取格子后各尺寸可直接比较。
            var cell = Math.Max(2, Math.Min(w, h) / 16);
            var checker = Checker(w, h, cell);
            var flat = Render(w, h, 0, thickness, 0, 0, null, true, checker);
            var bent = Render(w, h, 120, thickness, 0, 0, null, true, checker);

            var s = w * 4;

            double Band(int x0, int y0, int x1, int y1)
            {
                double sum = 0;
                var c = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    sum += Math.Abs(flat[y * s + x * 4 + 2] - bent[y * s + x * 4 + 2]);
                    c++;
                }

                return c == 0 ? 0 : sum / c;
            }

            // 带宽不超过短边的 1/4，否则细长面板上四条边带会互相重叠
            var band = Math.Max(3, Math.Min(40, Math.Min(w, h) / 4));
            var mx = Math.Max(1, w / 2 - band);
            var my = Math.Max(1, h / 2 - band);

            Console.WriteLine($"[{name}] 带宽 {band}px  格子 {cell}px");
            Console.WriteLine($"    左 {Band(0, my, band, h - my):F2}   右 {Band(w - band, my, w, h - my):F2}" +
                              $"   上 {Band(mx, 0, w - mx, band):F2}   下 {Band(mx, h - band, w - mx, h):F2}");
            Console.WriteLine($"    中心 {Band(mx, my, w - mx, h - my):F2}  （应接近 0）");
        }
    }
}
