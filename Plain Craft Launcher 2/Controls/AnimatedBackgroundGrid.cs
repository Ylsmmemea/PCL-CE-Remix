using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL;

public class AnimatedBackgroundGrid : Grid
{
    public static readonly DependencyProperty BackgroundBrushProperty = DependencyProperty.Register("BackgroundBrush",
        typeof(SolidColorBrush), typeof(AnimatedBackgroundGrid),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), _BackgroundBrushChanged));

    private readonly DependencyProperty _animatableBrushProperty;

    public readonly int uuid = ModBase.GetUuid();

    public AnimatedBackgroundGrid(DependencyProperty brushDp)
    {
        _animatableBrushProperty = brushDp;
        Loaded += (_, _) => Init();
    }

    public AnimatedBackgroundGrid() : this(BackgroundProperty)
    {
    }

    protected virtual FrameworkElement AnimatableElement => this;

    protected virtual SolidColorBrush AnimatableBrush
    {
        get => (SolidColorBrush)Background;
        set => Background = value;
    }

    protected bool IsAnimating
    {
        get => field;
        private set => field = value;
    }

    public SolidColorBrush BackgroundBrush
    {
        get => (SolidColorBrush)GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    private static void _BackgroundBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (AnimatedBackgroundGrid)d;
        var brush = (SolidColorBrush)e.NewValue;

        // 未加载或不可见时直接赋值，无动画
        if (!(grid.IsLoaded && grid.IsVisible))
        {
            grid.AnimatableBrush = brush;
            return;
        }

        // 已有过渡动画在进行时不叠加新动画：
        // 否则多个异步回调会在各自的 300ms 后争相写入 AnimatableBrush，导致颜色来回跳变。
        if (grid.IsAnimating)
        {
            grid._pendingBrush = brush;
            return;
        }

        grid._pendingBrush = null;
        grid.IsAnimating = true;
        grid.Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaColor(grid.AnimatableElement, grid._animatableBrushProperty,
                        new ModBase.MyColor(brush) - grid.AnimatableBrush, 300)
                }, "MyCard Theme " + grid.uuid);
            await Task.Delay(300);
            // 动画期间若又有新值，以最新值为准
            grid.AnimatableBrush = grid._pendingBrush ?? brush;
            grid._pendingBrush = null;
            grid.IsAnimating = false;
        }));
    }

    /// <summary>
    ///     过渡动画期间收到的最新画刷，动画结束后应用。
    /// </summary>
    private SolidColorBrush? _pendingBrush;

    private void Init()
    {
        AnimatableBrush = BackgroundBrush;
    }
}
