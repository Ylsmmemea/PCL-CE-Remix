using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;

namespace PCL;

// 配色选择器 把可选配色摊成一片色块 点哪个切哪个 切的是当前明暗模式下的配色
public class MyThemePicker : WrapPanel
{
    private const double SwatchHeight = 32d;

    // 可选配色 非愚人节不给 HmclBlue
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
        // 亮暗模式或配色变了就重建色块 不然选中态和预览色都是旧的
        ThemeService.ColorModeChanged += (_, _) => Dispatcher.Invoke(Refresh);
        ThemeService.ColorThemeChanged += _ => Dispatcher.Invoke(Refresh);
        // 自定义颜色变了更新那一块
        Config.Preference.Theme.CustomThemeColorConfig.Observe(
            new PCL.Core.App.Configuration.ConfigObserver(
                PCL.Core.App.Configuration.ConfigEvent.Changed, _ => Dispatcher.Invoke(Refresh)));
    }

    // 重建色块并同步选中状态
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
            label.Foreground = _ContrastBrush(color);

            var swatch = new Border
            {
                Width = 86d,
                Height = SwatchHeight,
                Margin = new Thickness(0d, 0d, 8d, 8d),
                CornerRadius = new CornerRadius(5d),
                Background = new SolidColorBrush(color),
                // 边框厚度固定 切换选中态时尺寸不能变 一变就触发布局重算 布局一动毛玻璃层跟着闪
                BorderThickness = new Thickness(2d),
                // 选中态用白描边 以前用的是 ColorBrush1 主题里最暗那一档
                // 深色配色下几乎就是黑框 压在饱和度高的色块上很突兀
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

    // 配色枚举到本地化 key 后缀 有几个枚举名跟历史文案的 key 对不上
    private static string ThemeKeyOf(ColorTheme theme) => theme switch
    {
        ColorTheme.DeathBlue => "CrashBlue",
        ColorTheme.HmclBlue => "Hmcl",
        _ => theme.ToString()
    };

    // 按底色亮度挑一个能看清的文字颜色
    private static Brush _ContrastBrush(Color background)
    {
        // sRGB 相对亮度的简化加权
        var luminance = (0.299d * background.R + 0.587d * background.G + 0.114d * background.B) / 255d;
        return luminance > 0.62d
            ? new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            : new SolidColorBrush(Colors.White);
    }

    private void _Select(ColorTheme theme)
    {
        // 写当前明暗模式对应的那一项
        if (ThemeService.IsDarkMode)
            Config.Preference.Theme.DarkColor = theme;
        else
            Config.Preference.Theme.LightColor = theme;

        ThemeManager.ThemeRefresh();
        Refresh();
        // 让设置页里自定义配色的面板重新判断要不要显示
        ModMain.frmSetupUI?.RefreshCustomThemePanel();
    }
}
