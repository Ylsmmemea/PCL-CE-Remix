using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCL.Core.UI.Effects;

// ReSharper disable UnusedMember.Local, UnusedParameter.Local

// 液态玻璃 在模糊的基础上沿边缘对背景做置换采样 做出水滴那种折射
// 中心相对清晰 扭曲都集中在边缘
// 着色器源码在 UI/Assets/Shaders/LiquidGlass.hlsl 用同目录的 ShaderCompiler 编成 .ps
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
        UpdateShaderValue(ScrimAlphaProperty);
    }

    #region 属性

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    // 折射强度 控制背景在边缘处被位移多少
    public double Refraction
    {
        get => (double)GetValue(RefractionProperty);
        set => SetValue(RefractionProperty, value);
    }

    // 边缘折射厚度 越大扭曲往中心延伸得越多
    public double RefThickness
    {
        get => (double)GetValue(RefThicknessProperty);
        set => SetValue(RefThicknessProperty, value);
    }

    public double Glare
    {
        get => (double)GetValue(GlareProperty);
        set => SetValue(GlareProperty, value);
    }

    public Point TextureSize
    {
        get => (Point)GetValue(TextureSizeProperty);
        set => SetValue(TextureSizeProperty, value);
    }

    // rgb 是颜色 a 是混合量
    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double Opacity
    {
        get => (double)GetValue(OpacityProperty);
        set => SetValue(OpacityProperty, value);
    }

    // 面板圆角半径 像素 圆角矩形 SDF 靠它贴合控件真实轮廓
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    // 磨砂灰层的不透明度 0 表示不叠灰
    public double ScrimAlpha
    {
        get => (double)GetValue(ScrimAlphaProperty);
        set => SetValue(ScrimAlphaProperty, value);
    }

    #endregion

    #region 依赖属性

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty BlurAmountProperty =
        DependencyProperty.Register(nameof(BlurAmount), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(8.0, PixelShaderConstantCallback(0)));

    // 下面几个默认值都是像素单位 跟 LiquidGlass.hlsl 里的定义对齐
    // 早期这里用过 UV 比例单位 也就是 0.03 那种 谁直接 new 出来不赋值会得到几乎为零的折射
    // 正常路径走 BlurBorder._CreateLiquidGlassEffect
    public static readonly DependencyProperty RefractionProperty =
        DependencyProperty.Register(nameof(Refraction), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(30.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty RefThicknessProperty =
        DependencyProperty.Register(nameof(RefThickness), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(50.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty GlareProperty =
        DependencyProperty.Register(nameof(Glare), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.35, PixelShaderConstantCallback(3)));

    // 寄存器编号必须跟 LiquidGlass.hlsl 的声明严格对上
    // Point 只占一个常量寄存器 填在 .x 和 .y 所以 Tint 从 C5 开始而不是 C6
    public static readonly DependencyProperty TextureSizeProperty =
        DependencyProperty.Register(nameof(TextureSize), typeof(Point), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point(1920, 1080), PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(Colors.White, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty OpacityProperty =
        DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(6)));

    // C7 往后是扩展位 PS 3.0 允许 c0 到 c223 PixelShaderConstantCallback(N) 直接映射到 cN
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(5.0, PixelShaderConstantCallback(7)));

    public static readonly DependencyProperty ScrimAlphaProperty =
        DependencyProperty.Register(nameof(ScrimAlpha), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(8)));

    #endregion

    #region Shader 初始化与克隆

    // pack 方案是 Application 构造时才注册的 在那之前解析 pack URI 会抛 UriFormatException
    // 万一首次访问赶在注册之前 静态构造会永远停在没着色器的状态 所以主动触发一次注册再重试
    private static Uri _CreateShaderUri()
    {
        try
        {
            return new Uri(PixelShaderUri, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            // 碰一下 PackUriHelper 的静态成员就能触发它注册 pack 方案
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
                // 着色器没加载上就退化成空着色器 调用方会回退到普通模糊
                _cachedShader = new PixelShader();
            }
        }
    }

    // 没加载成功时调用方应该退回普通模糊
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
            ScrimAlpha = source.ScrimAlpha;
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
