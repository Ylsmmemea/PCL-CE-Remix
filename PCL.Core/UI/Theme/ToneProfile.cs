namespace PCL.Core.UI.Theme;

public record ToneProfile(
    double L1 = 0.35,
    double L2 = 0.5,
    double L3 = 0.575,
    double L4 = 0.65,
    double L5 = 0.8,
    double L6 = 0.92,
    double L7 = 0.94,
    double L8 = 0.96,
    double LWhite = 1,
    double LForeground = 0,
    double LBackground = 0.995,
    double C1 = 0.025,
    double C2 = 0.188,
    double C3 = 0.213,
    double C4 = 0.168,
    double C5 = 0.093,
    double C6 = 0.036,
    double C7 = 0.028,
    double C8 = 0.018,
    double ASemiWhite = 0.733,
    double AHalfWhite = 0.333,
    double ASemiTransparent = 0.004,
    double AHalfTransparent = 0.5,
    double ATransparent = 0,
    double ABackground = 0.824,
    double AToolTip = 0.9,
    // 毛玻璃表面的不透明度 取值低一些才透得出背后内容
    // 这层色是画在玻璃之上的 BlurBorder.OnRender 最后才调 base.OnRender 会整体抬高面板亮度
    // 液态玻璃的折射已经把背景内容搬进面板了 再压一层浅色只会冲淡它
    // 实测 0.14 时侧边栏和相邻没有玻璃的页面之间有明显亮度落差 观感像一张图被切成两块
    double AGlass = 0.08
);
