using System.Windows.Media;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

public class CatColorResource
{
    private const string ColorPrefix = "ColorObject";
    private const string BrushPrefix = "ColorBrush";

    /// <summary>
    /// 透明度值。
    /// </summary>
    public float Alpha { get; init; } = 255;

    /// <summary>
    /// 红色值。
    /// </summary>
    public required float Red { get; init; }

    /// <summary>
    /// 绿色值。
    /// </summary>
    public required float Green { get; init; }

    /// <summary>
    /// 蓝色值。
    /// </summary>
    public required float Blue { get; init; }

    /// <summary>
    /// 资源后缀。
    /// </summary>
    public required string Suffix { get; init; }

    /// <summary>
    /// 是否应用到 <see cref="SolidColorBrush"/> 资源。
    /// </summary>
    public bool ApplyToBrush { get; init; } = false;

    /// <summary>
    /// 是否应用到 <see cref="Color"/> 资源。
    /// </summary>
    public bool ApplyToColor { get; init; } = false;

    // 把颜色写进 WPF 资源字典 只能在 LifecycleState.Loaded 及之后的阶段调
    // 已有的画刷就地改颜色而不是换实例 换实例的话 DynamicResource 那边全要重新绑定 界面会闪
    public void Apply()
    {
        var res = Lifecycle.CurrentApplication.Resources;
        var color = Color.FromScRgb(Alpha, Red, Green, Blue);
        if (ApplyToColor) res[$"{ColorPrefix}{Suffix}"] = color;
        if (!ApplyToBrush) return;

        var key = $"{BrushPrefix}{Suffix}";
        if (res[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        res[key] = new SolidColorBrush(color);
    }
}
