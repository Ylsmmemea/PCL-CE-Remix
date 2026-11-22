using System;
using System.Windows;
using System.Windows.Media;

namespace PCL.Core.UI.Controls;

/// <summary>
///     毛玻璃面板。<br/>
///     与 <see cref="BlurBorder"/> 的区别是模糊强度自动跟随全局的毛玻璃强度设置，
///     并在关闭时隐藏自身，供界面中所有需要玻璃质感的面板统一使用。
/// </summary>
public class GlassBorder : BlurBorder
{
    /// <summary>
    ///     玻璃表面的填充色。开启毛玻璃时使用半透明的玻璃色，否则回退到指定的常规色。
    /// </summary>
    public static readonly DependencyProperty GlassBackgroundProperty = DependencyProperty.Register(
        nameof(GlassBackground), typeof(Brush), typeof(GlassBorder),
        new PropertyMetadata(null, _GlassBackgroundChanged));

    /// <summary>
    ///     关闭毛玻璃时使用的填充色。
    /// </summary>
    public static readonly DependencyProperty FallbackBackgroundProperty = DependencyProperty.Register(
        nameof(FallbackBackground), typeof(Brush), typeof(GlassBorder),
        new PropertyMetadata(null, _GlassBackgroundChanged));

    public Brush? GlassBackground
    {
        get => (Brush?)GetValue(GlassBackgroundProperty);
        set => SetValue(GlassBackgroundProperty, value);
    }

    public Brush? FallbackBackground
    {
        get => (Brush?)GetValue(FallbackBackgroundProperty);
        set => SetValue(FallbackBackgroundProperty, value);
    }

    /// <summary>
    ///     全局毛玻璃强度（由 <c>UiGlassValue</c> 广播）。0 表示关闭。
    /// </summary>
    public static readonly DependencyProperty GlassRadiusProperty = DependencyProperty.Register(
        nameof(GlassRadius), typeof(double), typeof(GlassBorder),
        new PropertyMetadata(0d, _GlassRadiusChanged));

    public double GlassRadius
    {
        get => (double)GetValue(GlassRadiusProperty);
        set => SetValue(GlassRadiusProperty, value);
    }

    private static void _GlassRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var border = (GlassBorder)d;
        border.BlurRadius = (double)e.NewValue;
        border._RefreshBackground();
    }

    private static void _GlassBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlassBorder)d)._RefreshBackground();

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _RefreshBackground();
    }

    private void _RefreshBackground()
    {
        var useGlass = GlassRadius > 0d && GlassBackground is not null;
        Background = useGlass ? GlassBackground : FallbackBackground;
    }
}
