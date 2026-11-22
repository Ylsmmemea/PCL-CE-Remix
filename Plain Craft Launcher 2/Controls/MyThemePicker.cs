using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;

namespace PCL;

/// <summary>
///     配色选择器。以色块网格的形式列出全部可选配色，点击即可切换当前明暗模式下的配色。
/// </summary>
public class MyThemePicker : WrapPanel
{
    /// <summary>
    ///     色块尺寸。
    /// </summary>
    private const double SwatchHeight = 32d;

    /// <summary>
    ///     可选配色列表。非愚人节时不提供 HmclBlue。
    /// </summary>
    private static readonly ColorTheme[] Themes = Basics.IsAprilFool
        ? [ColorTheme.SkyBlue, ColorTheme.CatBlue, ColorTheme.DeathBlue, ColorTheme.Emerald,
           ColorTheme.Violet, ColorTheme.Rose, ColorTheme.Amber, ColorTheme.Teal,
           ColorTheme.Slate, ColorTheme.Custom, ColorTheme.HmclBlue]
        : [ColorTheme.SkyBlue, ColorTheme.CatBlue, ColorTheme.DeathBlue, ColorTheme.Emerald,
           ColorTheme.Violet, ColorTheme.Rose, ColorTheme.Amber, ColorTheme.Teal,
           ColorTheme.Slate, ColorTheme.Custom];

    private readonly List<Border> _swatches = [];

    public MyThemePicker()
    {
        Orientation = Orientation.Horizontal;
        Loaded += (_, _) => Refresh();
        // 亮暗模式或配色变化后重建色块，保证选中态与预览色正确
        ThemeService.ColorModeChanged += (_, _) => Dispatcher.Invoke(Refresh);
        ThemeService.ColorThemeChanged += _ => Dispatcher.Invoke(Refresh);
        // 自定义颜色变化时更新「自定义」色块
        Config.Preference.Theme.CustomThemeColorConfig.Observe(
            new PCL.Core.App.Configuration.ConfigObserver(
                PCL.Core.App.Configuration.ConfigEvent.Changed, _ => Dispatcher.Invoke(Refresh)));
    }

    /// <summary>
    ///     重建色块并同步选中状态。
    /// </summary>
    public void Refresh()
    {
        Children.Clear();
        _swatches.Clear();
        var selected = Config.Preference.Theme;
        var current = ThemeService.IsDarkMode ? selected.DarkColor : selected.LightColor;

        foreach (var theme in Themes)
        {
            var color = ThemeService.GetThemePreviewColor(theme, ThemeService.IsDarkMode);
            var isSelected = theme == current;

            var label = new TextBlock
            {
                Text = Lang.Text("Setup.Ui.Theme.Color." + ThemeKeyOf(theme)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5d,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // 依据色块亮度选择可读的文字颜色
            label.Foreground = _ContrastBrush(color);

            var swatch = new Border
            {
                Width = 86d,
                Height = SwatchHeight,
                Margin = new Thickness(0d, 0d, 8d, 8d),
                CornerRadius = new CornerRadius(5d),
                Background = new SolidColorBrush(color),
                // 边框厚度固定，避免切换选中态时改变尺寸而触发布局重算（会波及毛玻璃层导致闪烁）
                BorderThickness = new Thickness(2d),
                // 选中态用白色描边。此前用 ColorBrush1（主题里最暗的一档），
                // 在深色配色下几乎就是黑框，压在饱和度高的色块上很突兀。
                BorderBrush = isSelected
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                Child = label,
                Cursor = Cursors.Hand,
                ToolTip = Lang.Text("Setup.Ui.Theme.Color." + ThemeKeyOf(theme))
            };
            swatch.MouseLeftButtonUp += (_, _) => _Select(theme);

            _swatches.Add(swatch);
            Children.Add(swatch);
        }
    }

    /// <summary>
    ///     配色枚举 → 本地化 key 后缀。部分枚举名与历史文案 key 不一致。
    /// </summary>
    private static string ThemeKeyOf(ColorTheme theme) => theme switch
    {
        ColorTheme.DeathBlue => "CrashBlue",
        ColorTheme.HmclBlue => "Hmcl",
        _ => theme.ToString()
    };

    /// <summary>
    ///     依据背景色亮度返回可读的文字画刷。
    /// </summary>
    private static Brush _ContrastBrush(Color background)
    {
        // 采用 sRGB 相对亮度的简化加权
        var luminance = (0.299d * background.R + 0.587d * background.G + 0.114d * background.B) / 255d;
        return luminance > 0.62d
            ? new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            : new SolidColorBrush(Colors.White);
    }

    private void _Select(ColorTheme theme)
    {
        // 写入当前明暗模式对应的配置项
        if (ThemeService.IsDarkMode)
            Config.Preference.Theme.DarkColor = theme;
        else
            Config.Preference.Theme.LightColor = theme;

        ThemeManager.ThemeRefresh();
        Refresh();
        // 通知设置页刷新自定义配色面板的可见性
        ModMain.frmSetupUI?.RefreshCustomThemePanel();
    }
}
