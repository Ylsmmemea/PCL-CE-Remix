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
        // 布局更新极为频繁（滚动、悬停动画、卡片高度变化等都会触发），
        // 因此先比对自身相对窗口的位置与尺寸，未发生变化时无需重新采样背景。
        RequestRender();
    }

    private bool _renderPending;
    private bool _forceNextRender;

    /// <summary>
    ///     上次渲染时的位置（相对窗口）与尺寸，用于跳过无意义的重复渲染。
    /// </summary>
    private Rect _lastRenderBounds = Rect.Empty;

    /// <summary>
    ///     上一次采集是否有效。<c>false</c> 表示祖先不可见或正在布局，画面上什么都没有。
    /// </summary>
    private bool _lastCaptureOk = true;

    /// <summary>采集失败后的连续重试次数，用于避免在异常状态下无限重绘。</summary>
    private int _captureRetry;

    // 每次重试都是一次完整的背景采集，代价不低，因此上限取小值：
    // 布局时序问题通常在 1~2 帧内自解，超过 3 次说明本来就没有可采的内容。
    private const int MaxCaptureRetry = 3;

    /// <summary>
    ///     存活中的毛玻璃实例，用于在背景内容变化时统一失效。<br/>
    ///     使用弱引用，避免阻止控件被回收。
    /// </summary>
    private static readonly List<WeakReference<BlurBorder>> _instances = [];

    private static readonly object _instancesLock = new();

    public BlurBorder()
    {
        lock (_instancesLock)
        {
            // 顺带清理已回收的实例
            _instances.RemoveAll(reference => !reference.TryGetTarget(out _));
            _instances.Add(new WeakReference<BlurBorder>(this));
        }
    }

    /// <summary>
    ///     使所有毛玻璃层失效并重新采样背景。<br/>
    ///     用于壁纸切换、主题配色变化等「几何未变但背后内容已变」的情形。
    /// </summary>
    /// <param name="invalidateEffect">
    ///     是否同时使效果缓存失效。液态玻璃参数变化时需传 <c>true</c> 以重新读取配置。
    /// </param>
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
                // 单个实例失效不应影响其余实例
            }
        }
    }

    /// <summary>
    ///     请求重绘模糊背景。<br/>
    ///     重绘需要遍历并重新采样父级视觉树，开销较大，因此：
    ///     1. 关闭毛玻璃时直接跳过；
    ///     2. 合并同一帧内的多次请求；
    ///     3. 位置与尺寸均未变化时跳过（布局更新次数远多于实际的视觉变化）。
    /// </summary>
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
                // 不可见时不渲染，并丢弃几何记录，以便重新可见时必定重绘一次
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

    /// <summary>
    ///     执行一次采集，并按结果决定是否记录几何。<br/>
    ///     <para>
    ///     <b>为什么不能无条件记录：</b>采集可能因「祖先正在布局 / 祖先不可见」而静默失败，
    ///     此时画面里什么都没有。若照常把当前几何记为已渲染，之后每次 <see cref="RequestRender"/>
    ///     都会因「几何未变」而跳过 —— 这一层玻璃就永久空白，直到它的位置或尺寸碰巧改变为止。
    ///     用户看到的现象就是「液态玻璃时有时无，动一下窗口才出来」。
    ///     </para>
    /// </summary>
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
            // 持续失败说明不是布局时序问题，放弃重试以免空转
            _captureRetry = 0;
            _lastRenderBounds = bounds;
            return;
        }

        // 保留旧几何并强制下一次请求通过几何检查，等价于「下一帧重试一次」
        _forceNextRender = true;
        RequestRender();
    }

    /// <summary>
    ///     统计该元素到视觉树根的祖先层数。<br/>
    ///     背景采集向上遍历的上限是 <see cref="MaxDepth"/>，超限就采不到壁纸所在的层级，
    ///     玻璃会渲染成完全透明 —— 因此这个值需要与上限一起观察。
    /// </summary>
    internal static int AncestorDepth(Visual visual)
    {
        var depth = 0;
        for (var parent = VisualTreeHelper.GetParent(visual); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
            depth++;

        return depth;
    }

    /// <summary>
    ///     请求强制重绘，忽略几何比对与合并逻辑。<br/>
    ///     用于模糊参数变化、背后内容变化等无法通过几何判断的情形。
    /// </summary>
    private void RequestForceRender()
    {
        _forceNextRender = true;

        // 不经过 RequestRender：后者在已有待处理请求或半径为 0 时会直接返回，
        // 导致「几何永不变化」的元素（如导航栏、卡片）错过本次参数变化而不再重绘。
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

    /// <summary>
    ///     获取当前相对窗口的位置与尺寸。
    /// </summary>
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
                // 视觉树尚未就绪时忽略，退化为仅比对尺寸
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

        // 尺寸为 0 时不要建立效果：着色器以 TextureSize 把像素换算成 UV，
        // 拿 0（会被钳成 1）去构建会得到退化参数，而且这次采集注定是空的。
        // 侧边栏就是这种情况 —— 它的宽度由子元素事后赋值，首次渲染时宽度还是 0。
        //
        // 这里不能调 RequestRender：本方法返回前没有记录几何，请求会立刻再次触发渲染，
        // 而尺寸仍是 0，于是自旋。丢弃几何记录即可 —— 尺寸变化本身会让元素重新渲染，
        // 届时 RequestRender 的几何比对必然放行。
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
            // 把采集内容裁到控件矩形，再交给效果。
            //
            // 只设 DrawingVisual.Clip 不够：Clip 是 Visual 级属性，而 WPF 合成时先应用
            // Effect、后应用 Clip，于是着色器拿到的是**未裁剪**的内容纹理。
            // 实测卡片控件 684x341，输入纹理却是 880x940（含左侧栏与整页内容）。
            //
            // 而着色器用 p = (uv - 0.5) * TextureSize 计算边缘距离，隐含假设
            // 「uv 的 [0,1] 就是控件范围」。纹理一旦更大，折射带就落到纹理自身的边缘，
            // 而不是控件的边缘 —— 表现为效果出现在莫名其妙的位置或干脆看不见。
            // 在绘制上下文里裁剪，内容边界即等于控件矩形，这个假设才成立。
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

    /// <summary>
    ///     缓存的效果实例。<br/>
    ///     <see cref="OnRender"/> 会被反复调用，若每次都新建 <see cref="Effect"/> 会产生大量临时对象，
    ///     并且液态玻璃实例还会被重复注册到动画器上。仅在参数或模式变化时重建。
    /// </summary>
    private Effect? _cachedEffect;

    /// <summary>
    ///     缓存对应的参数指纹，用于判断是否需要重建。
    /// </summary>
    private (double Radius, double Sampling, bool Liquid) _cachedEffectKey = (-1d, -1d, false);

    /// <summary>
    ///     使效果缓存失效，下次渲染时重建。<br/>
    ///     用于液态玻璃参数变化等需要重新读取配置的情形。
    /// </summary>
    public void InvalidateEffect()
    {
        _cachedEffectKey = (-1d, -1d, false);
        RequestForceRender();
    }

    /// <summary>
    ///     获取效果实例，必要时重建。
    /// </summary>
    private Effect _GetOrCreateEffect()
    {
        var key = (BlurRadius, BlurSamplingRate, PCL.Core.App.Config.Preference.Glass.LiquidEnabled);
        if (_cachedEffect is not null && key == _cachedEffectKey)
        {
            // 缓存键只反映参数，尺寸不在其中。而着色器用 TextureSize 把像素单位换算成 UV，
            // 若沿用旧尺寸，折射范围、边缘厚度会整体错位 —— 卡片展开/折叠、
            // 窗口缩放后表现为整块面板扭曲异常、颜色发暗。因此每次取用都同步一次。
            _SyncEffectGeometry(_cachedEffect);
            return _cachedEffect;
        }

        _cachedEffect = CreateOptimizedBlurEffect();
        _cachedEffectKey = key;
        _SyncEffectGeometry(_cachedEffect);

        // 每次重建效果时记录一次：正好覆盖「切换实验性开关 / 液态玻璃开关」这类
        // 配置变化，可以直接看出重建后的效果类型与参数是否符合预期。
        DiagnoseGlass(this, _cachedEffect);

        return _cachedEffect;
    }

    /// <summary>
    ///     把玻璃控件的运行时状态写入日志，用于定位「效果没有生效」到底是
    ///     着色器问题还是宿主（配置 / 尺寸 / 参数）问题。<br/>
    ///     仅在效果重建时输出，正好覆盖配置切换的瞬间。
    /// </summary>
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
                  $"纹理={lg.TextureSize.X:F0}x{lg.TextureSize.Y:F0} 圆角={lg.CornerRadius:F1} 模糊={lg.BlurAmount:F2}"
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
            // 诊断失败不应影响渲染
        }
    }

    /// <summary>
    ///     同步液态玻璃依赖的元素几何：纹理尺寸与圆角半径。<br/>
    ///     二者都不在效果缓存的键里，但着色器用它们把像素换算成 UV / 构造圆角矩形 SDF；
    ///     沿用旧值会让折射范围、边缘厚度、圆角位置整体错位。<br/>
    ///     值未变化时不写入，避免无谓的依赖属性通知。
    /// </summary>
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
        // 液态玻璃：折射 + 边缘高光。着色器不可用时回退到普通模糊。
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

    /// <summary>
    ///     创建液态玻璃效果，并按配置设置各参数。
    /// </summary>
    private LiquidGlassEffect _CreateLiquidGlassEffect()
    {
        var glass = PCL.Core.App.Config.Preference.Glass;
        var effect = new LiquidGlassEffect
        {
            // 磨砂强度由独立滑块给出，0 为完全通透。它与「毛玻璃强度」是两回事：
            // 后者是为普通磨砂玻璃标定的（BlurRadius × 0.6 ≈ 13px），
            // 套在液态玻璃上会把细节整体抹平，折射与高光全被淹没，退化成普通毛玻璃。
            // 即使滑块拉满也只到弱磨砂 —— 液态玻璃的材质感来自折射与边缘高光，
            // 模糊只是让主体看起来像一层介质，而不是把内容糊掉。
            BlurAmount = Math.Clamp(glass.LiquidBlur, 0, 100) / 100d * MaxLiquidBlurPx,
            // 折射强度按「折射带宽的比例」解释：着色器把滑块 0~150 映射到 0~0.62×带宽。
            // 位移上限受采样映射单调性约束（超出会在边缘产生镜像重影），
            // 因此想要更强的效果应调高「边缘折射厚度」，而不是继续拉折射强度。
            Refraction = glass.LiquidRefraction,
            RefThickness = Math.Max(glass.LiquidRefThickness, 1),
            Glare = glass.LiquidGlare / 100d,
            TextureSize = new Point(
                Math.Max(RenderSize.Width, 1d),
                Math.Max(RenderSize.Height, 1d)),
            // 不叠加色调，保持背景原有颜色
            Tint = Color.FromArgb(0, 255, 255, 255),
            Opacity = 1d
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

    /// <summary>
    ///     背景采集向上遍历祖先的层数上限。<br/>
    ///     <para>
    ///     上层库的默认值是 16，对普通背景采样够用，但玻璃必须采到壁纸所在的层级：
    ///     实测卡片已经处在第 15 层（页面内部嵌套 + PanMainRight + Border + PanMain + PanForm），
    ///     只剩一层余量，页面再深一点就采不到壁纸，玻璃会渲染成完全透明。
    ///     </para>
    ///     <para>
    ///     这里放宽到 64：遍历本身只是 GetParent，代价可忽略；真正的开销在于多画的几层
    ///     祖先内容，而那恰恰是玻璃应该呈现的东西。
    ///     </para>
    /// </summary>
    public static readonly DependencyProperty MaxDepthProperty =
        BackgroundPresenter.MaxDepthProperty.AddOwner(typeof(BlurBorder),
            new FrameworkPropertyMetadata(MaxAncestorDepth));

    internal const int MaxAncestorDepth = 64;

    /// <summary>
    ///     液态玻璃磨砂滑块满量程对应的模糊半径（像素）。<br/>
    ///     仍是「弱磨砂」量级 —— 液态玻璃的材质感主要来自折射与边缘高光，
    ///     模糊只是让主体看起来像一层介质。着色器中心处取 0.6 倍（见 radius 的 lerp），
    ///     因此满量程的实际半径约为 8.4px。
    /// </summary>
    private const double MaxLiquidBlurPx = 14d;

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
