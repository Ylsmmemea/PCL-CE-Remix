using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
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

// 直接测真实的 BlurBorder 宿主路径
//
// 之前的测试都是把效果套在普通 Image 上 绕过了 BackgroundPresenter
// 而卡片和导航栏走的是另一条路 抓父级视觉树当着色器输入 观感差异很可能就出在这
//
// BlurBorder 的效果由全局 Config 驱动 而 Config 在测试宿主里初始化不了
// 所以这里用反射直接塞效果实例 在真控件上验证
[TestClass]
public class BlurBorderProbeTest
{
    private static readonly string[] PhotoCandidates =
    [
        @"D:\Code\_lg_refs\LiqiudGlassHLSL\imgui-example\examples\example_win32_directx11\pic.jpg",
        @"D:\Code\_lg_refs\WPF-Liquid-Glass-Effect\docs\Screenshot.png"
    ];

    private static BitmapSource? _photo;

    private static BitmapSource? Photo()
    {
        if (_photo is not null) return _photo;
        foreach (var p in PhotoCandidates)
        {
            if (!File.Exists(p)) continue;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(p);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _photo = bmp;
            return _photo;
        }
        return null;
    }

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

    // 把液态玻璃塞进 BlurBorder 的缓存字段 绕开 Config
    // _GetOrCreateEffect 在缓存键匹配时直接返回缓存实例 所以键也得一起写进去 效果才会真的被用上
    private static void InjectLiquidGlass(BlurBorder border, int refraction, int thickness)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BlurBorder);

        var effect = new LiquidGlassEffect
        {
            // 跟应用里一致 液态玻璃的模糊量级比普通毛玻璃低得多
            BlurAmount = 1.8,
            Refraction = refraction,
            RefThickness = thickness,
            Glare = 0.5,
            Tint = Color.FromArgb(0, 255, 255, 255),
            Opacity = 1
        };

        // 键是 BlurRadius BlurSamplingRate LiquidEnabled 必须跟控件当前值一致
        var key = (border.BlurRadius, border.BlurSamplingRate, false);
        type.GetField("_cachedEffect", flags)!.SetValue(border, effect);
        type.GetField("_cachedEffectKey", flags)!.SetValue(border, key);
    }

    private static byte[] Capture(int w, int h, Action<BlurBorder>? configure, string? dump = null)
    {
        return OnSta(() =>
        {
            var root = new Grid { Width = w, Height = h };

            root.Children.Add(new System.Windows.Controls.Image
            {
                Source = Photo() ?? throw new AssertFailedException("找不到测试照片"),
                Stretch = Stretch.UniformToFill
            });

            // 被测层跟卡片同构 外层 Grid 带半透明底色 里面的 BlurBorder 负责玻璃
            var card = new Grid
            {
                Width = w * 0.72,
                Height = h * 0.72,
                Background = new SolidColorBrush(Color.FromArgb(36, 0xFB, 0xFB, 0xFB))
            };

            var glass = new BlurBorder
            {
                BlurRadius = 21,
                CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false
            };
            configure?.Invoke(glass);
            card.Children.Add(glass);
            root.Children.Add(card);

            var window = new Window
            {
                Content = root,
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
            try
            {
                Pump();
                Thread.Sleep(250);
                Pump();

                // 等画面稳下来再抓 连续两帧差异够小就算稳了
                byte[]? prev = null;
                byte[] cur = [];
                for (var i = 0; i < 6; i++)
                {
                    cur = Grab(window, w, h);
                    if (prev is not null && Diff(prev, cur) < 0.002) break;
                    prev = cur;
                    Thread.Sleep(140);
                    Pump();
                }

                if (dump is not null) SavePng(cur, w, h, dump);
                return cur;
            }
            finally
            {
                window.Close();
                Pump();
            }
        });
    }

    private static byte[] Grab(Window window, int w, int h)
    {
        var origin = window.PointToScreen(new System.Windows.Point(0, 0));
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

    // 在真实 BlurBorder 上跑液态玻璃 导几张对照图出来 顺便量一下卡片内部有没有发生折射
    [TestMethod]
    public void LiquidGlassOnRealBlurBorder()
    {
        const int w = 720, h = 480;

        var plain = Capture(w, h, null, "card-无玻璃");
        var glassOff = Capture(w, h, b => InjectLiquidGlass(b, 0, 50), "card-折射0");
        var glassOn = Capture(w, h, b => InjectLiquidGlass(b, 150, 90), "card-折射150");

        Console.WriteLine($"折射0   vs 无玻璃 = {Diff(plain, glassOff):P1}");
        Console.WriteLine($"折射150 vs 无玻璃 = {Diff(plain, glassOn):P1}");
        Console.WriteLine($"折射150 vs 折射0  = {Diff(glassOff, glassOn):P1}");

        // 只算卡片内部避开外框 要是差值等于 0 说明玻璃压根没作用到卡片上
        var stride = w * 4;
        double Inner(byte[] a, byte[] b)
        {
            double s = 0;
            var n = 0;
            var x0 = (int)(w * 0.2);
            var x1 = (int)(w * 0.8);
            var y0 = (int)(h * 0.2);
            var y1 = (int)(h * 0.8);
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var o = y * stride + x * 4;
                s += Math.Abs(a[o + 2] - b[o + 2]);
                n++;
            }
            return n == 0 ? 0 : s / n;
        }

        Console.WriteLine($"卡片内部：折射150 vs 折射0 = {Inner(glassOff, glassOn):F2}（应显著大于 0）");
    }
}
