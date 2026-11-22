using System;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 配色模式更改事件。
/// </summary>
/// <param name="isDarkMode">当前是否为深色模式</param>
/// <param name="theme">当前配色主题</param>
public delegate void ColorModeChangedEvent(bool isDarkMode, ColorTheme theme);

/// <summary>
/// 配色主题更改事件。
/// </summary>
/// <param name="theme">当前配色主题</param>
public delegate void ColorThemeChangedEvent(ColorTheme theme);

[LifecycleScope("theme", "主题", false)]
[LifecycleService(LifecycleState.WindowCreating)]
public sealed partial class ThemeService
{
    [AnyConfigItem<ToneProfileConfig>("UiToneProfiles", ConfigSource.Local)]
    public static partial ToneProfileConfig ToneProfiles { get; set; }

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorModeConfigChanged => new(
        scope: Config.Preference.Theme.ColorModeConfig,
        trigger: ConfigEvent.Update,
        handler: _ => RefreshColorMode()
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorThemeConfigChanged => new(
        // 把 TextToneConfig 也挂进来 它的效果同样落在 ApplyColorResources 里 整套配色算完之后才覆盖正文档位
        // 走这条刷新路径就行 下面那两个非当前模式就忽略的判断拦不到它
        scope:
        [
            Config.Preference.Theme.DarkColorConfig, Config.Preference.Theme.LightColorConfig,
            Config.Preference.Theme.TextToneConfig
        ],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            // ignore no change or non-current color theme change
            if (e.OldValue == e.Value) return;
            if (IsDarkMode) { if (e.Item == Config.Preference.Theme.LightColorConfig) return; }
            else { if (e.Item == Config.Preference.Theme.DarkColorConfig) return; }
            // trigger color refresh
            if (Lifecycle.CurrentState > LifecycleState.Loading)
            {
                Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
                {
                    ApplyColorResources();
                    ColorThemeChanged?.Invoke(CurrentTheme);
                    _AprilFoolLogic();
                });
            }
        }
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnCustomThemeColorChanged => new(
        scope: [Config.Preference.Theme.CustomThemeColorConfig],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            // ignore no change or non-custom theme
            if (e.OldValue == e.Value) return;
            if (CurrentTheme != ColorTheme.Custom) return;
            if (Lifecycle.CurrentState <= LifecycleState.Loading) return;
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
            {
                ApplyColorResources();
                ColorThemeChanged?.Invoke(CurrentTheme);
            });
        }
    );

    [LifecycleStart]
    private static void _Start()
    {
        IsDarkMode = _IsDarkMode();
        _LogStatus();
        _RefreshAll();
    }

    private static void _LogStatus()
    {
        Context.Debug($"当前状态: {(IsDarkMode ? "Dark" : "Light")}, {CurrentTheme}");
    }

    private static bool _IsDarkMode() => Config.Preference.Theme.ColorMode switch
    {
        ColorMode.Light => false,
        ColorMode.Dark => true,
        ColorMode.System => SystemThemeHelper.IsSystemInDarkMode(),
        _ => false
    };

    /// <summary>
    /// 当前是否为深色主题。
    /// </summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>
    /// 配色模式更改事件。
    /// </summary>
    public static event ColorModeChangedEvent? ColorModeChanged;

    /// <summary>
    /// 配色主题更改事件。
    /// </summary>
    public static event ColorThemeChangedEvent? ColorThemeChanged;

    private static void _RefreshAll()
    {
        ApplyGrayResources();
        ApplyColorResources();
        ColorModeChanged?.Invoke(IsDarkMode, CurrentTheme);
        _AprilFoolLogic();
    }

    private static void _AprilFoolLogic()
    {
        // for HMCL theme on April Fools' Day
        if (Basics.IsAprilFool) Config.Preference.WindowTitleTypeConfig.TriggerEvent(ConfigEvent.Changed, null);
        else if (CurrentTheme == ColorTheme.HmclBlue) CurrentTheme = ColorTheme.CatBlue;
    }

    /// <summary>
    /// 刷新配色模式，若检测到当前配色模式有实际更改，则会触发主题刷新。
    /// </summary>
    public static void RefreshColorMode()
    {
        var isDarkMode = _IsDarkMode();
        if (IsDarkMode == isDarkMode) return;
        Context.Info("正在更改配色模式");
        IsDarkMode = isDarkMode;
        _LogStatus();
        if (Lifecycle.CurrentState > LifecycleState.Loading)
        {
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(_RefreshAll);
        }
    }

    /// <summary>
    /// 当前使用的色彩属性。
    /// </summary>
    public static ToneProfile CurrentTone => IsDarkMode ? ToneProfiles.Dark : ToneProfiles.Light;

    /// <summary>
    /// 当前使用的主题色。
    /// </summary>
    public static ColorTheme CurrentTheme
    {
        get
        {
            var theme = Config.Preference.Theme;
            return IsDarkMode ? theme.DarkColor : theme.LightColor;
        }
        set
        {
            var theme = Config.Preference.Theme;
            var config = IsDarkMode ? theme.DarkColorConfig : theme.LightColorConfig;
            config.SetValue(value);
        }
    }

    /// <summary>
    /// 获取当前色彩主题对应的各种参数。
    /// </summary>
    public static (int Hue, double LightAdjust, double ChromaAdjust) GetCurrentThemeArgs()
        => GetThemeArgsOf(CurrentTheme);

    /// <summary>
    /// 获取指定配色主题对应的参数。
    /// </summary>
    public static (int Hue, double LightAdjust, double ChromaAdjust) GetThemeArgsOf(ColorTheme theme)
    {
        return theme switch
        {
            ColorTheme.SkyBlue => (235, 0.36, 0.2),
            ColorTheme.CatBlue => (255, 0, -0.2),
            ColorTheme.DeathBlue => (268, -0.05, -0.1),
            ColorTheme.HmclBlue => (275, -0.03, -0.35),
            ColorTheme.Emerald => (155, 0, -0.15),
            ColorTheme.Violet => (300, 0, -0.1),
            ColorTheme.Rose => (15, 0, -0.15),
            ColorTheme.Amber => (75, 0.05, -0.1),
            ColorTheme.Teal => (190, 0, -0.2),
            ColorTheme.Slate => (260, -0.1, -0.7),
            ColorTheme.Custom => _GetCustomThemeArgs(),
            _ => _GetFallbackThemeArgs(theme)
        };
    }

    /// <summary>
    /// 获取指定配色主题在对应明暗模式下的代表性颜色（色阶 3，即导航栏与强调色所用的级别），用于在设置界面中预览。
    /// </summary>
    public static System.Windows.Media.Color GetThemePreviewColor(ColorTheme theme, bool isDarkMode)
    {
        if (theme == ColorTheme.Custom)
        {
            // 自定义配色直接使用用户指定的颜色，便于直观预览
            try
            {
                return NColor.FromHex(Config.Preference.Theme.CustomThemeColor);
            }
            catch
            {
                return LabColor.FromRgb(19, 112, 243).ToWpfColor();
            }
        }

        var tone = isDarkMode ? ToneProfiles.Dark : ToneProfiles.Light;
        var (hue, lightAdj, chromaAdj) = GetThemeArgsOf(theme);
        return LabColor.FromLch(
            _AdjustLinear(tone.L3, lightAdj),
            _AdjustLinear(tone.C3, chromaAdj),
            hue).ToWpfColor();
    }

    /// <summary>
    /// 获取自定义配色对应的参数。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetCustomThemeArgs()
    {
        try
        {
            var color = NColor.FromHex(Config.Preference.Theme.CustomThemeColor);
            return _ReverseFromColor(color, CurrentTone);
        }
        catch (Exception ex)
        {
            Context.Warn("解析自定义配色失败，已回退到默认配色", ex);
            return (255, 0, -0.2);
        }
    }

    /// <summary>
    /// 对未能识别的配色主题提供安全回退，避免旧配置或枚举变更导致崩溃。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetFallbackThemeArgs(ColorTheme theme)
    {
        Context.Warn($"未知的配色主题：{theme}，已回退到 CatBlue");
        return (255, 0, -0.2);
    }

    /// <summary>
    /// 从用户指定的颜色反推出配色参数，将该颜色视为色阶 3（强调色）的期望值。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _ReverseFromColor(NColor color, ToneProfile tone)
    {
        var lch = LabColor.FromWpfColor(color);
        var (l, c, h) = lch.ToOklch();
        var hue = ((h % 360d) + 360d) % 360d;
        return (
            (int)Math.Round(hue),
            _InvertAdjustLinear(tone.L3, l),
            _InvertAdjustLinear(tone.C3, c)
        );
    }

    /// <summary>
    /// <see cref="_AdjustLinear"/> 的逆运算。
    /// </summary>
    private static double _InvertAdjustLinear(double baseline, double target)
    {
        if (Math.Abs(target - baseline) < 1e-6) return 0d;
        double adjustment;
        if (target > baseline)
        {
            if (baseline >= 1d) return 0d; // 基准已是纯白，无法再调亮
            adjustment = (target - baseline) / (1d - baseline);
        }
        else
        {
            if (baseline <= 0d) return 0d; // 基准已是纯黑，无法再调暗
            adjustment = (target - baseline) / baseline;
        }
        return Math.Clamp(adjustment, -1d, 1d);
    }

    private static double _AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        // 确保输入在合理范围内
        value = Math.Clamp(value, 0.0, 1.0);
        adjustment = Math.Clamp(adjustment, -1.0, 1.0);
        // 非对称线性插值
        return adjustment switch
        {
            > 0 => value + (1.0 - value) * adjustment,
            _ => value + value * adjustment
        };
    }

    private static CatColorResource[] _CalculateGrays(ToneProfile tone) => [
        LabColor.FromLch(tone.L1).ToCatColor("Gray1"),
        LabColor.FromLch(tone.L2).ToCatColor("Gray2"),
        LabColor.FromLch(tone.L3).ToCatColor("Gray3"),
        LabColor.FromLch(tone.L4).ToCatColor("Gray4"),
        LabColor.FromLch(tone.L5).ToCatColor("Gray5"),
        LabColor.FromLch(tone.L6).ToCatColor("Gray6"),
        LabColor.FromLch(tone.L7).ToCatColor("Gray7"),
        LabColor.FromLch(tone.L8).ToCatColor("Gray8"),
        LabColor.FromLch(tone.LWhite, alpha:tone.AHalfWhite).ToCatColor("HalfWhite", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ASemiWhite).ToCatColor("SemiWhite", false),
        LabColor.FromLch(tone.LWhite).ToCatColor("White", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ATransparent).ToCatColor("Transparent", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.ABackground).ToCatColor("TransparentBackground", false),
        LabColor.FromLch(tone.LBackground).ToCatColor("Background", false),
        // 毛玻璃表面：低不透明度，以便透出背后被模糊的内容
        LabColor.FromLch(tone.LBackground, alpha:tone.AGlass).ToCatColor("GlassBackground", false),
        // 弹窗与通知的玻璃表面：不透明度较高以保证文字可读
        LabColor.FromLch(tone.LBackground, alpha:0.72).ToCatColor("MsgBoxGlass", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.AToolTip).ToCatColor("ToolTip", false),
        LabColor.FromLch(tone.L7, 0.25, 30, tone.AHalfTransparent).ToCatColor("RedBack", false),
        LabColor.FromLch(tone.LForeground).ToCatColor("Memory", false),
    ];

    private static CatColorResource[] _CalculateColors(ToneProfile tone, (int hue, double lightAdj, double chromaAdj) args) => [
        LabColor.FromLch(_AdjustLinear(tone.L1, args.lightAdj * 0.1), _AdjustLinear(tone.C1, args.chromaAdj * 0.25), args.hue).ToCatColor("1"),
        LabColor.FromLch(_AdjustLinear(tone.L2, args.lightAdj), _AdjustLinear(tone.C2, args.chromaAdj), args.hue).ToCatColor("2"),
        LabColor.FromLch(_AdjustLinear(tone.L3, args.lightAdj), _AdjustLinear(tone.C3, args.chromaAdj), args.hue).ToCatColor("3"),
        LabColor.FromLch(_AdjustLinear(tone.L4, args.lightAdj), _AdjustLinear(tone.C4, args.chromaAdj), args.hue).ToCatColor("4"),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("5"),
        LabColor.FromLch(_AdjustLinear(tone.L6, args.lightAdj), _AdjustLinear(tone.C6, args.chromaAdj), args.hue).ToCatColor("6"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue).ToCatColor("7"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue).ToCatColor("8"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue, tone.ASemiTransparent).ToCatColor("SemiTransparent", false),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("Bg0"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue, tone.ASemiWhite).ToCatColor("Bg1"),
    ];

    private static CatColorResource[] LightGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Light); set; } = null!;

    private static CatColorResource[] DarkGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Dark); set; } = null!;

    /// <summary>
    /// 清除灰度配色的计算缓存。
    /// </summary>
    public static void InvalidateGrayCache()
    {
        LightGrayCache = null!;
        DarkGrayCache = null!;
    }

    /// <summary>
    /// 应用灰度配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyGrayResources()
    {
        var cache = IsDarkMode ? DarkGrayCache : LightGrayCache;
        foreach (var c in cache) c.Apply();
    }

    /// <summary>
    /// 应用彩色配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyColorResources()
    {
        var colors = _CalculateColors(CurrentTone, GetCurrentThemeArgs());
        foreach (var c in colors) c.Apply();
        _ApplyTextToneOverride();
    }

    // 按 UiTextTone 提亮正文颜色
    // 正文用的是配色里最暗那一档 ColorBrush1 次级文字用 ColorBrush2
    // 亮色主题下这两档接近黑色 碰上毛玻璃面板后面偏暗的壁纸对比度就很低
    // 所以在整套配色算完之后把它们换成配色自带的浅档位
    // 只覆盖这两档 按钮填充 边框 面板底色用的都是 3 以上的档位 不会把整个界面翻成深色
    private static void _ApplyTextToneOverride()
    {
        var level = PCL.Core.App.Config.Preference.Theme.TextTone;
        if (level <= 0) return;

        var res = Lifecycle.CurrentApplication.Resources;
        var tone = CurrentTone;
        var args = GetCurrentThemeArgs();

        // 取配色里偏亮的档位，并沿用当前色相，避免文字成为与配色无关的纯灰
        var source = level >= 2 ? tone.L8 : tone.L7;
        var (a, r, g, b) = LabColor.FromLch(
            _AdjustLinear(source, args.LightAdjust),
            _AdjustLinear(tone.C7, args.ChromaAdjust * 0.5),
            args.Hue).ScRgb;
        var text = System.Windows.Media.Color.FromScRgb(a, r, g, b);

        // 灰度档一并覆盖：相当一部分控件直接用 ColorBrushGray* 当正文色，
        // 尤其是列表项里的简介、版本、下载量、更新时间这些次级文字（Gray3 / Gray4）。
        // 只覆盖 Gray1/Gray2 的话，模组列表的简介在偏暗壁纸上依然几乎看不见。
        var (ga, gr, gg, gb) = LabColor.FromLch(source).ScRgb;
        var gray = System.Windows.Media.Color.FromScRgb(ga, gr, gg, gb);

        // 次级文字保持同一色相、只降不透明度，这样正文与副文本之间的层次不会被抹平
        static System.Windows.Media.Color Fade(System.Windows.Media.Color c, double factor)
            => System.Windows.Media.Color.FromScRgb((float)(c.ScA * factor), c.ScR, c.ScG, c.ScB);

        foreach (var (key, color) in new[]
                 {
                     ("ColorBrush1", text), ("ColorBrush2", text),
                     ("ColorBrushGray1", gray), ("ColorBrushGray2", gray),
                     ("ColorBrushGray3", Fade(gray, 0.85d)),
                     ("ColorBrushGray4", Fade(gray, 0.72d))
                 })
        {
            if (res[key] is System.Windows.Media.SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else res[key] = new System.Windows.Media.SolidColorBrush(color);
        }
    }
}
