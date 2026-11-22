using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCL.Core.UI.Effects;

// ReSharper disable UnusedMember.Local, UnusedParameter.Local

/// <summary>
///     液态玻璃效果。<br/>
///     在模糊的基础上，沿边缘对背景做置换采样产生折射，形成类似水滴/厚玻璃的质感；
///     中心区域保持相对清晰，扭曲集中在边缘。<br/>
///     着色器源码见 <c>UI/Assets/Shaders/LiquidGlass.hlsl</c>，
///     用同目录 <c>ShaderCompiler</c> 工具编译成 <c>LiquidGlass.ps</c>。
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private const string PixelShaderUri =
        "pack://application:,,,/PCL.Core;component/UI/Assets/Shaders/LiquidGlass.ps";

    private static readonly object ShaderLock = new();
    private static PixelShader? _cachedShader;

    static LiquidGlassEffect()
    {
        _EnsureShaderInitialized();
    }

    public LiquidGlassEffect()
    {
        PixelShader = _cachedShader;

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BlurAmountProperty);
        UpdateShaderValue(RefractionProperty);
        UpdateShaderValue(RefThicknessProperty);
        UpdateShaderValue(GlareProperty);
        UpdateShaderValue(TextureSizeProperty);
        UpdateShaderValue(TintProperty);
        UpdateShaderValue(OpacityProperty);
        UpdateShaderValue(CornerRadiusProperty);
    }

    #region 属性

    /// <summary>
    ///     输入背景。
    /// </summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>
    ///     基础模糊半径（像素）。
    /// </summary>
    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    /// <summary>
    ///     折射强度，控制背景在边缘处被位移的程度。
    /// </summary>
    public double Refraction
    {
        get => (double)GetValue(RefractionProperty);
        set => SetValue(RefractionProperty, value);
    }

    /// <summary>
    ///     边缘折射厚度。越大则扭曲向中心延伸得越多。
    /// </summary>
    public double RefThickness
    {
        get => (double)GetValue(RefThicknessProperty);
        set => SetValue(RefThicknessProperty, value);
    }

    /// <summary>
    ///     边缘高光强度。
    /// </summary>
    public double Glare
    {
        get => (double)GetValue(GlareProperty);
        set => SetValue(GlareProperty, value);
    }

    /// <summary>
    ///     输入纹理尺寸，用于计算纹素大小。
    /// </summary>
    public Point TextureSize
    {
        get => (Point)GetValue(TextureSizeProperty);
        set => SetValue(TextureSizeProperty, value);
    }

    /// <summary>
    ///     色调叠加。<c>rgb</c> 为颜色，<c>a</c> 为混合量。
    /// </summary>
    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>
    ///     整体不透明度。
    /// </summary>
    public double Opacity
    {
        get => (double)GetValue(OpacityProperty);
        set => SetValue(OpacityProperty, value);
    }

    /// <summary>
    ///     面板圆角半径（像素）。用于圆角矩形 SDF，使折射带贴合控件真实轮廓。
    /// </summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    #endregion

    #region 依赖属性

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty BlurAmountProperty =
        DependencyProperty.Register(nameof(BlurAmount), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(8.0, PixelShaderConstantCallback(0)));

    // 以下默认值均为「像素」单位，与 LiquidGlass.hlsl 的参数定义保持一致。
    // 早期版本这里用的是 UV 比例单位（如 0.03），若被直接实例化而不显式赋值，
    // 会得到几乎为零的折射。正常路径由 BlurBorder._CreateLiquidGlassEffect() 赋值。
    public static readonly DependencyProperty RefractionProperty =
        DependencyProperty.Register(nameof(Refraction), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(30.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty RefThicknessProperty =
        DependencyProperty.Register(nameof(RefThickness), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(50.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty GlareProperty =
        DependencyProperty.Register(nameof(Glare), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.35, PixelShaderConstantCallback(3)));

    // 寄存器编号必须与 LiquidGlass.hlsl 的声明严格一致。
    // Point 类型只占一个常量寄存器（填在 .x/.y），因此 Tint 从 C5 开始而不是 C6。
    public static readonly DependencyProperty TextureSizeProperty =
        DependencyProperty.Register(nameof(TextureSize), typeof(Point), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point(1920, 1080), PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(Colors.White, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty OpacityProperty =
        DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(6)));

    // C7 起为后续扩展位：PS 3.0 允许 c0~c223，WPF 的 PixelShaderConstantCallback(N)
    // 直接映射到 cN，因此新增参数不会与 C4 的「float2 占一个寄存器」约束冲突。
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(5.0, PixelShaderConstantCallback(7)));

    #endregion

    #region Shader 初始化与克隆

    /// <summary>
    ///     构造着色器资源 URI。<br/>
    ///     <c>pack://</c> 方案由 WPF 在 <see cref="System.Windows.Application"/> 构造时注册，
    ///     在此之前解析 pack URI 会抛 <see cref="UriFormatException"/>。<br/>
    ///     一旦首次访问发生在注册之前，静态构造会永久停留在「无着色器」状态，
    ///     因此这里主动触发一次 pack 方案注册后重试。
    /// </summary>
    private static Uri _CreateShaderUri()
    {
        try
        {
            return new Uri(PixelShaderUri, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            // 访问 PackUriHelper 的任意静态成员都会触发其静态构造并注册 pack 方案
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            return new Uri(PixelShaderUri, UriKind.Absolute);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _EnsureShaderInitialized()
    {
        if (_cachedShader is not null) return;

        lock (ShaderLock)
        {
            if (_cachedShader is not null) return;

            try
            {
                _cachedShader = new PixelShader { UriSource = _CreateShaderUri() };
            }
            catch
            {
                // 着色器缺失时退化为空着色器，由调用方回退到普通模糊
                _cachedShader = new PixelShader();
            }
        }
    }

    /// <summary>
    ///     着色器是否成功加载。未加载时调用方应回退到普通模糊。
    /// </summary>
    public static bool IsShaderAvailable => _cachedShader?.UriSource is not null;

    protected override Freezable CreateInstanceCore() => new LiquidGlassEffect();

    protected override void CloneCore(Freezable sourceFreezable)
    {
        if (sourceFreezable is LiquidGlassEffect source)
        {
            BlurAmount = source.BlurAmount;
            Refraction = source.Refraction;
            RefThickness = source.RefThickness;
            Glare = source.Glare;
            TextureSize = source.TextureSize;
            Tint = source.Tint;
            Opacity = source.Opacity;
            CornerRadius = source.CornerRadius;
        }

        base.CloneCore(sourceFreezable);
    }

    protected override void CloneCurrentValueCore(Freezable sourceFreezable)
    {
        CloneCore(sourceFreezable);
        base.CloneCurrentValueCore(sourceFreezable);
    }

    protected override void GetAsFrozenCore(Freezable sourceFreezable)
    {
        CloneCore(sourceFreezable);
        base.GetAsFrozenCore(sourceFreezable);
    }

    protected override void GetCurrentValueAsFrozenCore(Freezable sourceFreezable)
    {
        CloneCore(sourceFreezable);
        base.GetCurrentValueAsFrozenCore(sourceFreezable);
    }

    #endregion
}
