using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PCL.Core.UI.Effects;

// 该部分源码来自或修改于 https://github.com/OrgEleCho/EleCho.WpfSuite
// 项目: EleCho.WpfSuite
// 作者: EleCho
// 协议: MIT License

namespace PCL.Core.UI.Controls;

// ReSharper disable All

public class BlurBorder : Border
{

    private const double DoubleEpsilon = 2.2204460492503131e-016;

    private static bool _IsZero(double value) => Math.Abs(value) < 10.0 * DoubleEpsilon;

    private readonly Stack<UIElement> _panelStack = new();

    /// <summary>
    /// A geometry to clip the content of this border correctly
    /// </summary>
    public Geometry? ContentClip
    {
        get { return (Geometry)GetValue(ContentClipProperty); }
        set { SetValue(ContentClipProperty, value); }
    }

    /// <summary>
    /// Gets or sets the maximum depth of the visual tree to render.
    /// </summary>
    public int MaxDepth
    {
        get { return (int)GetValue(MaxDepthProperty); }
        set { SetValue(MaxDepthProperty, value); }
    }

    /// <summary>
    /// Gets or sets the radius of the blur effect applied to the background.
    /// </summary>
    public double BlurRadius
    {
        get { return (double)GetValue(BlurRadiusProperty); }
        set { SetValue(BlurRadiusProperty, value); }
    }

    /// <summary>
    /// Gets or sets the type of kernel used for the blur effect.
    /// </summary>
    public KernelType BlurKernelType
    {
        get { return (KernelType)GetValue(BlurKernelTypeProperty); }
        set { SetValue(BlurKernelTypeProperty, value); }
    }

    /// <summary>
    /// Gets or sets the rendering bias for the blur effect, which can affect performance and quality.
    /// </summary>
    public RenderingBias BlurRenderingBias
    {
        get { return (RenderingBias)GetValue(BlurRenderingBiasProperty); }
        set { SetValue(BlurRenderingBiasProperty, value); }
    }

    /// <summary>
    /// Gets or sets the sampling rate for blur effect (0.1-1.0).
    /// Lower values significantly improve performance: 0.3 = 70% performance boost.
    /// Default is 0.7 for balanced quality and performance.
    /// </summary>
    public double BlurSamplingRate
    {
        get { return (double)GetValue(BlurSamplingRateProperty); }
        set { SetValue(BlurSamplingRateProperty, Math.Max(0.1, Math.Min(1.0, value))); }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        SetValue(ContentClipPropertyKey, CalculateContentClip(this));

        return base.ArrangeOverride(finalSize);
    }

    /// <inheritdoc/>
    protected override Geometry? GetLayoutClip(Size layoutSlotSize)
    {
        if (!ClipToBounds)
        {
            return null;
        }

        return CalculateLayoutClip(layoutSlotSize, BorderThickness, CornerRadius);
    }

    /// <inheritdoc/>
    protected override void OnVisualParentChanged(DependencyObject oldParentObject)
    {
        if (oldParentObject is UIElement oldParent)
        {
            oldParent.LayoutUpdated -= ParentLayoutUpdated;
        }

        if (Parent is UIElement newParent)
        {
            newParent.LayoutUpdated += ParentLayoutUpdated;
        }
    }

    private void ParentLayoutUpdated(object? sender, EventArgs e)
    {
        // 布局更新非常频繁 滚动 悬停动画 卡片高度变化都会触发
        // 真正要不要重采交给 RequestRender 里的几何比对
        RequestRender();
    }

    private bool _renderPending;
    private bool _forceNextRender;

    // 上次渲染时相对窗口的位置和尺寸 用来跳过没意义的重复渲染
    private Rect _lastRenderBounds = Rect.Empty;

    // 上一次采集成没成功 false 表示祖先不可见或者正在布局 画面上什么都没有
    private bool _lastCaptureOk = true;

    // 采集连续失败了几次 免得在异常状态下无限重绘
    private int _captureRetry;

    // 每重试一次就是一次完整的背景采集 代价不低 所以上限取小值
    // 布局时序问题一般一两帧内自己就好了 超过 3 次说明本来就没内容可采
    private const int MaxCaptureRetry = 3;

    // 活着的毛玻璃实例 背景内容变了要一起失效 用弱引用 免得挡住控件回收
    private static readonly List<WeakReference<BlurBorder>> _instances = [];

    private static readonly object _instancesLock = new();

    public BlurBorder()
    {
        lock (_instancesLock)
        {
            // 顺手清掉已经回收的实例
            _instances.RemoveAll(reference => !reference.TryGetTarget(out _));
            _instances.Add(new WeakReference<BlurBorder>(this));
        }
    }

    // 让所有毛玻璃层失效并重新采背景 换壁纸 换主题配色这种几何没变但背后内容变了的情况用
    // invalidateEffect 传 true 会顺带清掉效果缓存 液态玻璃参数变化时要传
    public static void InvalidateAllBackgrounds(bool invalidateEffect = false)
    {
        List<BlurBorder> alive;
        lock (_instancesLock)
        {
            alive = [];
            foreach (var reference in _instances)
            {
                if (reference.TryGetTarget(out var target))
                    alive.Add(target);
            }
        }

        foreach (var border in alive)
        {
            try
            {
                if (invalidateEffect)
                    border.InvalidateEffect();
                else
                    border.RequestForceRender();
            }
            catch
            {
                // 一个实例失效不能连累其他实例
            }
        }
    }

    // 请求重绘模糊背景
    // 重绘要遍历并重新采样父级视觉树 开销不小 所以关掉毛玻璃直接跳过 同一帧内的多次请求合并
    // 位置和尺寸都没变也跳过 布局更新的次数远多于真正的视觉变化
    private void RequestRender()
    {
        if (_renderPending || BlurRadius <= 0d)
            return;

        _renderPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _renderPending = false;

            if (BlurRadius <= 0d || !IsVisible)
            {
                // 不可见就不渲染 丢掉几何记录 重新可见时必然重绘一次
                _lastRenderBounds = Rect.Empty;
                return;
            }

            var bounds = _CurrentBounds();
            if (!_forceNextRender && bounds == _lastRenderBounds)
                return;

            _forceNextRender = false;
            _RenderNow(bounds);
        }));
    }

    // 采集一次 按结果决定要不要记录几何
    // 不能无条件记录 采集可能因为祖先正在布局或者祖先不可见而静默失败 那时候画面上什么都没有
    // 要是照常把当前几何记成已渲染 之后每次 RequestRender 都会因为几何没变而跳过
    // 这层玻璃就永久空白 直到位置或尺寸碰巧变一次 用户看到的就是液态玻璃时有时无 动一下窗口才出来
    private void _RenderNow(Rect bounds)
    {
        BackgroundPresenter.ForceRender(this);

        if (_lastCaptureOk)
        {
            _captureRetry = 0;
            _lastRenderBounds = bounds;
            return;
        }

        if (_captureRetry == 1)
        {
            PCL.Core.Logging.LogWrapper.Debug("Glass",
                $"[玻璃诊断] 采集失败 {GetType().Name} " +
                $"{(string.IsNullOrEmpty(Name) ? "(未命名)" : Name)} " +
                $"尺寸={RenderSize.Width:F0}x{RenderSize.Height:F0} " +
                $"层级={AncestorDepth(this)} 上限={MaxDepth}");
        }

        if (++_captureRetry > MaxCaptureRetry)
        {
            // 一直失败说明不是布局时序的问题 别重试了 免得空转
            _captureRetry = 0;
            _lastRenderBounds = bounds;
            return;
        }

        // 保留旧几何并强制下一次请求过几何检查 等于下一帧再试一次
        _forceNextRender = true;
        RequestRender();
    }

    // 数一下这个元素到视觉树根有多少层祖先
    // 背景采集向上遍历的上限是 MaxDepth 超了就采不到壁纸那一层 玻璃整块透明 所以要跟上限一起看
    internal static int AncestorDepth(Visual visual)
    {
        var depth = 0;
        for (var parent = VisualTreeHelper.GetParent(visual); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
            depth++;

        return depth;
    }

    // 强制重绘 不走几何比对和合并
    // 模糊参数变化 背后内容变化这种靠几何判断不出来的情况用它
    private void RequestForceRender()
    {
        _forceNextRender = true;

        // 不直接走 RequestRender 它碰到已有待处理请求或者半径为 0 就直接返回了
        // 几何永不变化的元素比如导航栏和卡片会错过这次参数变化 之后不再重绘
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            if (!_forceNextRender)
                return;

            _forceNextRender = false;
            if (BlurRadius <= 0d || !IsVisible)
            {
                _lastRenderBounds = Rect.Empty;
                return;
            }

            _RenderNow(_CurrentBounds());
        }));
    }

    // 取当前相对窗口的位置和尺寸
    private Rect _CurrentBounds()
    {
        var pos = new Point(0d, 0d);
        var window = Window.GetWindow(this);
        if (window is not null && IsVisible)
        {
            try
            {
                pos = TranslatePoint(default, window);
            }
            catch
            {
                // 视觉树还没就绪 忽略 退化成只比尺寸
            }
        }

        return new Rect(pos, RenderSize);
    }

    private static Geometry? CalculateContentClip(Border border)
    {
        var borderThickness = border.BorderThickness;
        var cornerRadius = border.CornerRadius;
        var renderSize = border.RenderSize;

        var contentWidth = renderSize.Width - borderThickness.Left - borderThickness.Right;
        var contentHeight = renderSize.Height - borderThickness.Top - borderThickness.Bottom;

        if (contentWidth > 0 && contentHeight > 0)
        {
            var rect = new Rect(0, 0, contentWidth, contentHeight);
            var radii = new Radii(cornerRadius, borderThickness, false);

            var contentGeometry = new StreamGeometry();
            using StreamGeometryContext ctx = contentGeometry.Open();
            GenerateGeometry(ctx, rect, radii);

            contentGeometry.Freeze();
            return contentGeometry;
        }
        else
        {
            return null;
        }
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        // 防止无意义渲染
        if (BlurRadius == 0
            || Opacity == 0
            || Visibility is Visibility.Collapsed or Visibility.Hidden)
        {
            base.OnRender(dc);
            return;
        }

        // 尺寸是 0 就别建效果了 着色器靠 TextureSize 把像素换算成 UV
        // 拿 0 去构建会被钳成 1 参数退化成没意义的 而且这次采集本来就是空的
        // 侧边栏就是这样 宽度由子元素事后赋值 首次渲染时还是 0
        //
        // 这里不能调 RequestRender 本方法返回前还没记录几何 请求立刻又会触发渲染
        // 而尺寸还是 0 于是自旋 丢掉几何记录就行 尺寸变化本身会让元素重新渲染
        // 到时候 RequestRender 的几何比对必然放行
        if (RenderSize.Width < 1d || RenderSize.Height < 1d)
        {
            _lastRenderBounds = Rect.Empty;
            base.OnRender(dc);
            return;
        }

        var effect = _GetOrCreateEffect();

        DrawingVisual drawingVisual = new DrawingVisual()
        {
            Clip = new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)),
            Effect = effect
        };

        using (DrawingContext visualContext = drawingVisual.RenderOpen())
        {
            // 把采集内容裁到控件矩形再交给效果
            //
            // 只设 DrawingVisual.Clip 不够 Clip 是 Visual 级的属性
            // 而 WPF 合成时先应用 Effect 后应用 Clip 着色器拿到的是没裁过的内容纹理
            // 实测卡片控件 684x341 输入纹理却是 880x940 把左侧栏和整页内容都带上了
            //
            // 着色器算边缘距离时隐含假设 uv 的 0 到 1 就是控件范围
            // 纹理一大折射带就落到纹理自己的边缘而不是控件边缘 效果出现在莫名其妙的位置或者干脆看不见
            // 在绘制上下文里裁 内容边界才等于控件矩形 这个假设才成立
            visualContext.PushClip(
                new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)));
            _lastCaptureOk = BackgroundPresenter.DrawBackground(visualContext, this, _panelStack, MaxDepth, false);
            visualContext.Pop();
        }

        if (drawingVisual.Drawing is not null)
        {
            var layoutClip = CalculateLayoutClip(RenderSize, BorderThickness, CornerRadius);
            if (layoutClip is not null)
            {
                dc.PushClip(layoutClip);
            }

            BackgroundPresenter.DrawVisual(dc, drawingVisual, default);

            if (layoutClip is not null)
            {
                dc.Pop();
            }
        }

        base.OnRender(dc);
    }

    // 缓存的效果实例 OnRender 会被反复调 每次都新建的话临时对象太多
    // 而且液态玻璃实例会被重复注册到动画器上 只在参数或模式变化时重建
    private Effect? _cachedEffect;

    // 缓存对应的参数指纹 用来判断要不要重建
    private (double Radius, double Sampling, bool Liquid) _cachedEffectKey = (-1d, -1d, false);

    // 让效果缓存失效 下次渲染重建 改完液态玻璃参数要调一下
    public void InvalidateEffect()
    {
        _cachedEffectKey = (-1d, -1d, false);
        RequestForceRender();
    }

    // 取效果实例 该重建就重建
    private Effect _GetOrCreateEffect()
    {
        var key = (BlurRadius, BlurSamplingRate, PCL.Core.App.Config.Preference.Glass.LiquidEnabled);
        if (_cachedEffect is not null && key == _cachedEffectKey)
        {
            // 缓存键里只有参数没有尺寸 而着色器靠 TextureSize 把像素换算成 UV
            // 沿用旧尺寸的话折射范围和边缘厚度会整体错位 卡片展开折叠 窗口缩放之后
            // 表现为整块面板扭曲异常 颜色发暗 所以每次取用都同步一次
            _SyncEffectGeometry(_cachedEffect);
            return _cachedEffect;
        }

        _cachedEffect = CreateOptimizedBlurEffect();
        _cachedEffectKey = key;
        _SyncEffectGeometry(_cachedEffect);

        // 只在重建时打一条 正好覆盖切换液态玻璃开关的那一瞬间 重建完的类型和参数对不对一眼能看出来
        DiagnoseGlass(this, _cachedEffect);

        return _cachedEffect;
    }

    // 把玻璃控件的运行时状态打进日志 效果没生效时用来分辨是着色器的问题还是宿主的问题
    // 只在效果重建的时候输出
    private static void DiagnoseGlass(BlurBorder border, Effect effect)
    {
        try
        {
            var kind = effect switch
            {
                LiquidGlassEffect => "LiquidGlass",
                BlurEffect => "BlurEffect(内置)",
                _ => effect.GetType().Name
            };

            var extra = effect is LiquidGlassEffect lg
                ? $" 折射={lg.Refraction:F0} 厚度={lg.RefThickness:F0} " +
                  $"纹理={lg.TextureSize.X:F0}x{lg.TextureSize.Y:F0} 圆角={lg.CornerRadius:F1} " +
                  $"模糊={lg.BlurAmount:F2} 灰层={lg.ScrimAlpha:F2}"
                : "";

            var name = string.IsNullOrEmpty(border.Name) ? "(未命名)" : border.Name;
            PCL.Core.Logging.LogWrapper.Debug("Glass",
                $"[玻璃诊断] {border.GetType().Name} {name} 尺寸={border.RenderSize.Width:F0}x{border.RenderSize.Height:F0} " +
                $"层级={AncestorDepth(border)}/{border.MaxDepth} " +
                $"模糊半径={border.BlurRadius:F1} 效果={kind}" +
                $" 液态开关={PCL.Core.App.Config.Preference.Glass.LiquidEnabled}{extra}");
        }
        catch
        {
            // 诊断挂了不能影响渲染
        }
    }

    // 同步液态玻璃要用的元素几何 纹理尺寸和圆角半径
    // 这俩都不在缓存键里 而着色器靠它们把像素换算成 UV 和构造圆角矩形 SDF 沿用旧值会整体错位
    // 值没变就不写 免得白白触发依赖属性通知
    private void _SyncEffectGeometry(Effect effect)
    {
        if (effect is not LiquidGlassEffect liquid) return;

        var size = new Point(
            Math.Max(RenderSize.Width, 1d),
            Math.Max(RenderSize.Height, 1d));
        if (liquid.TextureSize != size)
            liquid.TextureSize = size;

        // 面板统一使用相同圆角；BlurBorder 继承自 Border，直接取左上角即可。
        var radius = Math.Max(CornerRadius.TopLeft, 0d);
        if (Math.Abs(liquid.CornerRadius - radius) > 0.01d)
            liquid.CornerRadius = radius;
    }

    /// <summary>
    /// 创建用于模糊背景的效果实例
    /// </summary>
    private Effect CreateOptimizedBlurEffect()
    {
        // 液态玻璃 折射加边缘高光 着色器加载不上就退回普通模糊
        if (PCL.Core.App.Config.Preference.Glass.LiquidEnabled
            && LiquidGlassEffect.IsShaderAvailable)
            return _CreateLiquidGlassEffect();

        // 根据模糊半径和采样率智能选择算法
        if (BlurRadius <= 2.0 || BlurSamplingRate >= 0.95)
        {
            // 小半径或高采样率：使用原生 BlurEffect 获得最佳质量
            return new BlurEffect
            {
                Radius = BlurRadius,
                KernelType = BlurKernelType,
                RenderingBias = BlurRenderingBias
            };
        }
        else if (BlurRadius >= 50.0 && BlurSamplingRate <= 0.3)
        {
            // 大半径低采样率：使用极速优化版本
            var ultraFastBlur = OptimizedBlurFactory.CreateRealTimePreview(BlurRadius);
            ultraFastBlur.SamplingRate = Math.Max(0.1, BlurSamplingRate);
            ultraFastBlur.RenderingBias = RenderingBias.Performance;
            return ultraFastBlur.GetEffectInstance();
        }
        else
        {
            // 中等情况：使用我们的自适应优化算法
            var adaptiveBlur = OptimizedBlurFactory.CreateAdaptive(BlurRadius);
            adaptiveBlur.SamplingRate = BlurSamplingRate;
            adaptiveBlur.RenderingBias = BlurRenderingBias;
            adaptiveBlur.KernelType = BlurKernelType;
            return adaptiveBlur.GetEffectInstance();
        }
    }

    // 按配置拼一个液态玻璃效果
    private LiquidGlassEffect _CreateLiquidGlassEffect()
    {
        var glass = PCL.Core.App.Config.Preference.Glass;
        var effect = new LiquidGlassEffect
        {
            // 磨砂强度走独立滑块 0 就是完全通透
            // 它跟毛玻璃强度不是一回事 后者按普通磨砂玻璃标定 大约是 BlurRadius 的 0.6 倍 13px 上下
            // 套到液态玻璃上会把细节整体抹平 折射和高光全被淹掉 退化成普通毛玻璃
            // 滑块拉满也只到弱磨砂 材质感来自折射和边缘高光 模糊只是让主体看起来像一层介质
            BlurAmount = Math.Clamp(glass.LiquidBlur, 0, 100) / 100d * MaxLiquidBlurPx,
            // 折射强度按折射带宽的比例解释 着色器把滑块 0 到 150 映射到 0 到 0.62 倍带宽
            // 位移上限受采样映射单调性约束 超了边缘会出现镜像重影
            // 想要更明显就调边缘折射厚度 继续拉折射强度没有用
            Refraction = glass.LiquidRefraction,
            RefThickness = Math.Max(glass.LiquidRefThickness, 1),
            Glare = glass.LiquidGlare / 100d,
            TextureSize = new Point(
                Math.Max(RenderSize.Width, 1d),
                Math.Max(RenderSize.Height, 1d)),
            // 不叠色调 保持背景原色
            Tint = Color.FromArgb(0, 255, 255, 255),
            Opacity = 1d,
            // 磨砂较强时在玻璃主体下垫一层灰 把背景的明暗差拉平
            ScrimAlpha = LiquidScrimAlpha(glass.LiquidBlur)
        };

        return effect;
    }

    /// <summary>
    /// The key needed set a read-only property
    /// </summary>
    private static readonly DependencyPropertyKey ContentClipPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(ContentClip), typeof(Geometry), typeof(BlurBorder), new FrameworkPropertyMetadata(default(Geometry)));

    /// <summary>
    /// The DependencyProperty for the ContentClip property. <br/>
    /// Flags: None <br/>
    /// Default value: null
    /// </summary>
    public static readonly DependencyProperty ContentClipProperty =
        ContentClipPropertyKey.DependencyProperty;

    // 背景采集向上遍历祖先的层数上限
    // 上层库默认 16 对普通背景够用 但玻璃得采到壁纸那一层
    // 实测卡片已经在第 15 层 页面嵌套加 PanMainRight 加 Border 加 PanMain 加 PanForm 只剩一层余量
    // 页面再深一点就采不到壁纸 玻璃会整块渲染成透明 所以放宽到 64
    // 遍历本身只是 GetParent 开销可以忽略 多画几层祖先内容本来就是玻璃该显示的东西
    public static readonly DependencyProperty MaxDepthProperty =
        BackgroundPresenter.MaxDepthProperty.AddOwner(typeof(BlurBorder),
            new FrameworkPropertyMetadata(MaxAncestorDepth));

    internal const int MaxAncestorDepth = 64;

    // 液态玻璃磨砂滑块满量程对应的模糊半径 像素 仍然是弱磨砂的量级
    // 材质感主要来自折射和边缘高光 模糊只是让主体看起来像一层介质
    // 着色器中心处还要乘 0.6 见 radius 的 lerp 所以满量程实际半径约 8.4px
    private const double MaxLiquidBlurPx = 14d;

    // 灰层从磨砂强度 75% 开始介入 再低就完全不生效
    // 轻度磨砂要的就是通透 蒙一层灰会把折射和边缘高光一起压掉 玻璃读成一块灰塑料
    private const double ScrimStartPercent = 75d;

    private const double MaxScrimAlpha = 0.40d;

    // 按磨砂强度换算灰层不透明度 起点以下恒为 0 之后线性升到满量程
    internal static double LiquidScrimAlpha(int liquidBlur)
    {
        var t = (Math.Clamp(liquidBlur, 0, 100) - ScrimStartPercent) / (100d - ScrimStartPercent);
        return Math.Clamp(t, 0d, 1d) * MaxScrimAlpha;
    }

    /// <summary>
    /// The radius of the blur effect applied to the background.
    /// </summary>
    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(BlurBorder), new FrameworkPropertyMetadata(16.0, propertyChangedCallback: OnRenderPropertyChanged));

    /// <summary>
    /// The type of kernel used for the blur effect.
    /// </summary>
    public static readonly DependencyProperty BlurKernelTypeProperty =
        DependencyProperty.Register(nameof(BlurKernelType), typeof(KernelType), typeof(BlurBorder), new FrameworkPropertyMetadata(KernelType.Gaussian, propertyChangedCallback: OnRenderPropertyChanged));

    /// <summary>
    /// The rendering bias for the blur effect, which can affect performance and quality.
    /// </summary>
    public static readonly DependencyProperty BlurRenderingBiasProperty =
        DependencyProperty.Register(nameof(BlurRenderingBias), typeof(RenderingBias), typeof(BlurBorder), new FrameworkPropertyMetadata(RenderingBias.Performance, propertyChangedCallback: OnRenderPropertyChanged));

    /// <summary>
    /// The sampling rate for blur effect, controlling performance vs quality trade-off.
    /// </summary>
    public static readonly DependencyProperty BlurSamplingRateProperty =
        DependencyProperty.Register(nameof(BlurSamplingRate), typeof(double), typeof(BlurBorder), new FrameworkPropertyMetadata(0.9, propertyChangedCallback: OnRenderPropertyChanged));

    private static void OnRenderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 模糊参数变化无法通过几何比对判断，需强制重绘
        if (d is BlurBorder border)
        {
            border.RequestForceRender();
        }
        else if (d is UIElement element)
        {
            BackgroundPresenter.ForceRender(element);
        }
    }

    /// <summary>
    ///     Generates a StreamGeometry.
    /// </summary>
    /// <param name="ctx">An already opened StreamGeometryContext.</param>
    /// <param name="rect">Rectangle for geometry conversion.</param>
    /// <param name="radii">Corner radii.</param>
    /// <returns>Result geometry.</returns>
    internal static void GenerateGeometry(StreamGeometryContext ctx, Rect rect, Radii radii)
    {
        //
        //  compute the coordinates of the key points
        //

        Point topLeft = new Point(radii.LeftTop, 0);
        Point topRight = new Point(rect.Width - radii.RightTop, 0);
        Point rightTop = new Point(rect.Width, radii.TopRight);
        Point rightBottom = new Point(rect.Width, rect.Height - radii.BottomRight);
        Point bottomRight = new Point(rect.Width - radii.RightBottom, rect.Height);
        Point bottomLeft = new Point(radii.LeftBottom, rect.Height);
        Point leftBottom = new Point(0, rect.Height - radii.BottomLeft);
        Point leftTop = new Point(0, radii.TopLeft);
        
        //
        //  check key points for overlap and resolve by partitioning radii according to
        //  the percentage of each one.  
        //

        //  top edge is handled here
        if (topLeft.X > topRight.X)
        {
            double v = (radii.LeftTop) / (radii.LeftTop + radii.RightTop) * rect.Width;
            topLeft.X = v;
            topRight.X = v;
        }

        //  right edge
        if (rightTop.Y > rightBottom.Y)
        {
            double v = (radii.TopRight) / (radii.TopRight + radii.BottomRight) * rect.Height;
            rightTop.Y = v;
            rightBottom.Y = v;
        }

        //  bottom edge
        if (bottomRight.X < bottomLeft.X)
        {
            double v = (radii.LeftBottom) / (radii.LeftBottom + radii.RightBottom) * rect.Width;
            bottomRight.X = v;
            bottomLeft.X = v;
        }

        // left edge
        if (leftBottom.Y < leftTop.Y)
        {
            double v = (radii.TopLeft) / (radii.TopLeft + radii.BottomLeft) * rect.Height;
            leftBottom.Y = v;
            leftTop.Y = v;
        }

        //
        //  add on offsets
        //

        Vector offset = new Vector(rect.TopLeft.X, rect.TopLeft.Y);
        topLeft += offset;
        topRight += offset;
        rightTop += offset;
        rightBottom += offset;
        bottomRight += offset;
        bottomLeft += offset;
        leftBottom += offset;
        leftTop += offset;

        //
        //  create the border geometry
        //
        ctx.BeginFigure(topLeft, true /* is filled */, true /* is closed */);

        // Top line
        ctx.LineTo(topRight, true /* is stroked */, false /* is smooth join */);

        // Upper-right corner
        double radiusX = rect.TopRight.X - topRight.X;
        double radiusY = rightTop.Y - rect.TopRight.Y;
        if (!_IsZero(radiusX)
            || !_IsZero(radiusY))
        {
            ctx.ArcTo(rightTop, new Size(radiusX, radiusY), 0, false, SweepDirection.Clockwise, true, false);
        }

        // Right line
        ctx.LineTo(rightBottom, true /* is stroked */, false /* is smooth join */);

        // Lower-right corner
        radiusX = rect.BottomRight.X - bottomRight.X;
        radiusY = rect.BottomRight.Y - rightBottom.Y;
        if (!_IsZero(radiusX)
            || !_IsZero(radiusY))
        {
            ctx.ArcTo(bottomRight, new Size(radiusX, radiusY), 0, false, SweepDirection.Clockwise, true, false);
        }

        // Bottom line
        ctx.LineTo(bottomLeft, true /* is stroked */, false /* is smooth join */);

        // Lower-left corner
        radiusX = bottomLeft.X - rect.BottomLeft.X;
        radiusY = rect.BottomLeft.Y - leftBottom.Y;
        if (!_IsZero(radiusX)
            || !_IsZero(radiusY))
        {
            ctx.ArcTo(leftBottom, new Size(radiusX, radiusY), 0, false, SweepDirection.Clockwise, true, false);
        }

        // Left line
        ctx.LineTo(leftTop, true /* is stroked */, false /* is smooth join */);

        // Upper-left corner
        radiusX = topLeft.X - rect.TopLeft.X;
        radiusY = leftTop.Y - rect.TopLeft.Y;
        if (!_IsZero(radiusX)
            || !_IsZero(radiusY))
        {
            ctx.ArcTo(topLeft, new Size(radiusX, radiusY), 0, false, SweepDirection.Clockwise, true, false);
        }
    }

    internal static Geometry? CalculateLayoutClip(Size layoutSlotSize, Thickness borderThickness, CornerRadius cornerRadius)
    {
        if (layoutSlotSize.Width <= 0 ||
            layoutSlotSize.Height <= 0)
        {
            return new RectangleGeometry(new Rect(0, 0, 0, 0));
        }

        var rect = new Rect(0, 0, layoutSlotSize.Width, layoutSlotSize.Height);
        var radii = new Radii(cornerRadius, borderThickness, true);

        var layoutGeometry = new StreamGeometry();
        using StreamGeometryContext ctx = layoutGeometry.Open();
        GenerateGeometry(ctx, rect, radii);

        layoutGeometry.Freeze();
        return layoutGeometry;
    }

    internal struct Radii
    {
        internal Radii(CornerRadius radii, Thickness borders, bool outer)
        {
            double left = 0.5 * borders.Left;
            double top = 0.5 * borders.Top;
            double right = 0.5 * borders.Right;
            double bottom = 0.5 * borders.Bottom;

            if (outer)
            {
                if (_IsZero(radii.TopLeft))
                {
                    LeftTop = TopLeft = 0.0;
                }
                else
                {
                    LeftTop = radii.TopLeft + left;
                    TopLeft = radii.TopLeft + top;
                }
                if (_IsZero(radii.TopRight))
                {
                    TopRight = RightTop = 0.0;
                }
                else
                {
                    TopRight = radii.TopRight + top;
                    RightTop = radii.TopRight + right;
                }
                if (_IsZero(radii.BottomRight))
                {
                    RightBottom = BottomRight = 0.0;
                }
                else
                {
                    RightBottom = radii.BottomRight + right;
                    BottomRight = radii.BottomRight + bottom;
                }
                if (_IsZero(radii.BottomLeft))
                {
                    BottomLeft = LeftBottom = 0.0;
                }
                else
                {
                    BottomLeft = radii.BottomLeft + bottom;
                    LeftBottom = radii.BottomLeft + left;
                }
            }
            else
            {
                LeftTop = Math.Max(0.0, radii.TopLeft - left);
                TopLeft = Math.Max(0.0, radii.TopLeft - top);
                TopRight = Math.Max(0.0, radii.TopRight - top);
                RightTop = Math.Max(0.0, radii.TopRight - right);
                RightBottom = Math.Max(0.0, radii.BottomRight - right);
                BottomRight = Math.Max(0.0, radii.BottomRight - bottom);
                BottomLeft = Math.Max(0.0, radii.BottomLeft - bottom);
                LeftBottom = Math.Max(0.0, radii.BottomLeft - left);
            }
        }

        internal double LeftTop;
        internal double TopLeft;
        internal double TopRight;
        internal double RightTop;
        internal double RightBottom;
        internal double BottomRight;
        internal double BottomLeft;
        internal double LeftBottom;
    }
}
