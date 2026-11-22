using System;
using System.Windows;
using System.Windows.Media;

namespace PCL.Core.UI.Controls;

// 毛玻璃面板 模糊强度自动跟随全局设置 关掉毛玻璃时整个隐藏
// 界面里需要玻璃质感的面板统一用这个 别各自去调 BlurBorder
public class GlassBorder : BlurBorder
{
    // 玻璃面的填充色 毛玻璃开着的时候用它
    public static readonly DependencyProperty GlassBackgroundProperty = DependencyProperty.Register(
        nameof(GlassBackground), typeof(Brush), typeof(GlassBorder),
        new PropertyMetadata(null, _GlassBackgroundChanged));

    // 毛玻璃关掉之后的常规填充色
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

    // 全局毛玻璃强度 由 UiGlassValue 广播下来 0 表示关闭
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
