using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.UI;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupUI
{
    public new bool isLoaded;

    public PageSetupUI()
    {
        InitializeComponent();
        Loaded += PageSetupUI_Loaded;
        Loaded += (_, _) => HiddenRefresh();
    }

    private void PageSetupUI_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();

        ModAnimation.AniControlEnabled += 1;
        Reload(); // #4826，在每次进入页面时都刷新一下
        ModAnimation.AniControlEnabled -= 1;

        // 非重复加载部分
        if (isLoaded)
            return;
        isLoaded = true;

        SliderLoad();
    }

    public void Reload()
    {
        try
        {
            // 启动器
            SliderLauncherOpacity.Value = Config.Preference.Theme.WindowOpacity;
            CheckLauncherLogo.Checked = Config.Preference.ShowStartupLogo;
            ComboDarkMode.SelectedIndex = (int)Config.Preference.Theme.ColorMode;
            ComboTextTone.SelectedIndex = Config.Preference.Theme.TextTone;
            ThemePicker.Refresh();
            CheckShowLaunchingHint.Checked = Config.Preference.ShowLaunchingHint;
            ReloadCustomTheme();

            // 字体设置
            ComboUiFont.SelectedFontTag = Config.Preference.Font;
            ComboUiMotdFont.SelectedFontTag = Config.Preference.MotdFont;

            CheckBlur.Checked = Config.Preference.Blur.IsEnabled;
            SliderBlurValue.Value = Config.Preference.Blur.Radius;
            SliderBlurSamplingRate.Value = Config.Preference.Blur.SamplingRate;
            ComboBlurType.SelectedIndex = Config.Preference.Blur.KernelType;
            PanBlurValue.Visibility = CheckBlur.Checked == true ? Visibility.Visible : Visibility.Collapsed;
            CheckLockWindowSize.Checked = Config.Preference.LockWindowSize;

            // 毛玻璃
            SliderGlass.Value = Config.Preference.Glass.Radius;
            CheckGlassCard.Checked = Config.Preference.Glass.CardEnabled;
            CheckGlassLiquid.Checked = Config.Preference.Glass.LiquidEnabled;
            SliderGlassLiquidRefraction.Value = Config.Preference.Glass.LiquidRefraction;
            SliderGlassLiquidThickness.Value = Config.Preference.Glass.LiquidRefThickness;
            SliderGlassLiquidBlur.Value = Config.Preference.Glass.LiquidBlur;
            SliderGlassLiquidGlare.Value = Config.Preference.Glass.LiquidGlare;
            PanGlassLiquid.Visibility = CheckGlassLiquid.Checked == true ? Visibility.Visible : Visibility.Collapsed;

            // 背景图片
            var bgSource = Config.Preference.Background.SourceType;
            if (bgSource is < 0 or > 2) bgSource = 0;
            ((MyRadioBox)FindName("RadioBackgroundSource" + bgSource)).Checked = true;
            LabBackgroundLocalImage.Text = Config.Preference.Background.LocalImagePath;
            PanBackgroundLocalImage.Visibility = bgSource == 2 ? Visibility.Visible : Visibility.Collapsed;
            BtnBackgroundOpen.Visibility = bgSource == 0 ? Visibility.Visible : Visibility.Collapsed;
            SliderBackgroundOpacity.Value = Config.Preference.Background.WallpaperOpacity;
            SliderBackgroundBlur.Value = Config.Preference.Background.WallpaperBlurRadius;
            ComboBackgroundSuit.SelectedIndex = Config.Preference.Background.WallpaperSuitMode;
            CheckBackgroundColorful.Checked = Config.Preference.Background.BackgroundColorful;
            var autoPauseVideo = Config.Preference.Background.AutoPauseVideo;
            CheckAutoPauseVideo.Checked = autoPauseVideo;
            if (ModVideoBack.IsGaming)
                if (autoPauseVideo)
                    BtnBackgroundRefresh.IsEnabled = false;

            BackgroundRefresh(false, false);

            // 标题栏
            ((MyRadioBox)FindName("RadioLogoType" + (int)Config.Preference.WindowTitleType))
                .Checked = true;
            CheckLogoLeft.Visibility = RadioLogoType0.Checked ? Visibility.Visible : Visibility.Collapsed;
            PanLogoText.Visibility = RadioLogoType2.Checked ? Visibility.Visible : Visibility.Collapsed;
            PanLogoChange.Visibility = RadioLogoType3.Checked ? Visibility.Visible : Visibility.Collapsed;
            TextLogoText.Text = Config.Preference.WindowTitleCustomText;
            CheckLogoLeft.Checked = Config.Preference.TopBarLeftAlign;

            // 背景音乐
            CheckMusicRandom.Checked = Config.Preference.Music.ShufflePlayback;
            CheckMusicAuto.Checked = Config.Preference.Music.StartOnStartup;
            CheckMusicStop.Checked = Config.Preference.Music.StopInGame;
            CheckMusicStart.Checked = Config.Preference.Music.StartInGame;
            CheckMusicSMTC.Checked = Config.Preference.Music.EnableSMTC;
            SliderMusicVolume.Value = Config.Preference.Music.Volume;
            MusicRefreshUI();

            // 主页
            try
            {
                ComboCustomPreset.SelectedIndex = Config.Preference.Homepage.SelectedPreset;
            }
            catch
            {
                Config.Preference.Homepage.SelectedPresetConfig.Reset();
            }

            ((MyRadioBox)FindName("RadioCustomType" + Config.Preference.Homepage.Type)).Checked = true;
            TextCustomNet.Text = Config.Preference.Homepage.CustomUrl;
            ModSetup.UiCustomType(Config.Preference.Homepage.Type);

            // 功能隐藏
            // 获取配置组引用
            var uiHidden = Config.Preference.Hide;

            // 主页面
            CheckHiddenPageDownload.Checked = uiHidden.PageDownload;
            CheckHiddenPageSetup.Checked = uiHidden.PageSetup;
            CheckHiddenPageTools.Checked = uiHidden.PageTools;

            // 子页面 设置
            CheckHiddenSetupLaunch.Checked = uiHidden.SetupLaunch;
            CheckHiddenSetupUI.Checked = uiHidden.SetupUi;
            CheckHiddenSetupLauncherLanguage.Checked = uiHidden.SetupLauncherLanguage;
            CheckHiddenSetupGameManage.Checked = uiHidden.SetupGameManage;
            CheckHiddenSetupJava.Checked = uiHidden.SetupJava;
            CheckHiddenLauncherMisc.Checked = uiHidden.SetupLauncherMisc;
            CheckHiddenSetupUpdate.Checked = uiHidden.SetupUpdate;
            CheckHiddenSetupGameLink.Checked = uiHidden.SetupGameLink;
            CheckHiddenSetupAbout.Checked = uiHidden.SetupAbout;
            CheckHiddenSetupFeedback.Checked = uiHidden.SetupFeedback;
            CheckHiddenSetupLog.Checked = uiHidden.SetupLog;

            // 子页面 工具
            CheckHiddenToolsGameLink.Checked = uiHidden.ToolsGameLink;
            CheckHiddenToolsTest.Checked = uiHidden.ToolsTest;

            // 子页面 实例设置
            CheckHiddenVersionEdit.Checked = uiHidden.InstanceEdit;
            CheckHiddenVersionExport.Checked = uiHidden.InstanceExport;
            CheckHiddenVersionSave.Checked = uiHidden.InstanceSave;
            CheckHiddenVersionScreenshot.Checked = uiHidden.InstanceScreenshot;
            CheckHiddenVersionMod.Checked = uiHidden.InstanceMod;
            CheckHiddenVersionResourcePack.Checked = uiHidden.InstanceResourcePack;
            CheckHiddenVersionShader.Checked = uiHidden.InstanceShader;
            CheckHiddenVersionSchematic.Checked = uiHidden.InstanceSchematic;
            CheckHiddenVersionServer.Checked = uiHidden.InstanceServer;

            // 特定功能
            CheckHiddenFunctionSelect.Checked = uiHidden.FunctionSelect;
            CheckHiddenFunctionModUpdate.Checked = uiHidden.FunctionModUpdate;
            CheckHiddenFunctionHidden.Checked = uiHidden.FunctionHidden;
        }
        catch (NullReferenceException ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Ui.Error.ConfigReset"),
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.ConfigReset"));
            Reset();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Ui.Error.LoadFailed"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Ui.Error.LoadFailed"));
        }
    }

    // 初始化
    public void Reset()
    {
        try
        {
            Config.Preference.Reset();
            ModBase.Log("[Setup] 已初始化个性化设置！");
            HintService.Hint(Lang.Text("Setup.Ui.Initialized"), HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Ui.Error.InitFailed"),
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.InitFailed"));
        }

        Reload();
    }

    // 将控件改变路由到设置改变
    private void SliderChange(object senderRaw, bool user)
    {
        var sender = (MySlider)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Value);
    }

    private void ComboChange(object senderRaw, SelectionChangedEventArgs e)
    {
        var sender = (MyComboBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.SelectedIndex);
    }

    private void CheckBoxChange(object senderRaw, bool user)
    {
        var sender = (MyCheckBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Checked);
    }

    private void TextBoxChange(object senderRaw, RoutedEventArgs e)
    {
        var sender = (MyTextBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Text);
    }

    private void RadioBoxChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyRadioBox)senderRaw;
        var gotCfg = sender.Tag?.ToString()?.Split("/") ?? Array.Empty<string>();
        if (ModAnimation.AniControlEnabled == 0 && gotCfg.Length >= 2)
            SetByTag(gotCfg[0], int.Parse(gotCfg[1]));
    }

    private static void SetByTag(string tag, object value)
    {
        ConfigService.TrySetValue(tag, value);
    }

    private void ComboFontChange(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled == 0) Config.Preference.Font = ComboUiFont.SelectedFontTag;
    }

    private void ComboMotdFontChange(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled == 0) Config.Preference.MotdFont = ComboUiMotdFont.SelectedFontTag;
    }

    // 背景图片
    private void BtnUIBgOpen_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenExplorer(ModBase.exePath + @"PCL\Pictures\");
    }

    private void BtnBackgroundRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        // 必应模式本地已经缓存了好几张 直接轮换就行 不用再请求接口
        if (Config.Preference.Background.SourceType == 1 && TryShowNextBingWallpaper(true))
            return;

        BackgroundRefresh(true, true);
    }

    public void BackgroundRefreshUI(bool show, int count)
    {
        if (PanBackgroundOpacity is null)
            return;
        // 清空文件夹按钮只有文件夹轮播模式下才有意义
        var isFolderSource = Config.Preference.Background.SourceType == 0;
        if (show)
        {
            PanBackgroundOpacity.Visibility = Visibility.Visible;
            PanBackgroundBlur.Visibility = Visibility.Visible;
            PanBackgroundSuit.Visibility = Visibility.Visible;
            BtnBackgroundClear.Visibility = isFolderSource ? Visibility.Visible : Visibility.Collapsed;
            CheckAutoPauseVideo.Visibility = isFolderSource ? Visibility.Visible : Visibility.Collapsed;
            CardBackground.Title = Lang.Text("Setup.Ui.Background.TitleWithCount", count);
        }
        else
        {
            PanBackgroundOpacity.Visibility = Visibility.Collapsed;
            PanBackgroundBlur.Visibility = Visibility.Collapsed;
            PanBackgroundSuit.Visibility = Visibility.Collapsed;
            BtnBackgroundClear.Visibility = Visibility.Collapsed;
            CheckAutoPauseVideo.Visibility = Visibility.Collapsed;
            CardBackground.Title = Lang.Text("Setup.Ui.Background.TitleDefault");
        }

        CardBackground.TriggerForceResize();
    }

    private void BtnBackgroundClear_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(Lang.Text("Setup.Ui.Background.Clear.Confirm.Message"),
                Lang.Text("Common.Dialog.Warning"), button2: Lang.Text("Common.Action.Cancel"),
                isWarn: true) == 1)
        {
            ModBase.DeleteDirectory(ModBase.exePath + @"PCL\Pictures");
            BackgroundRefresh(false, true);
            HintService.Hint(Lang.Text("Setup.Ui.Background.Clear.Success"), HintType.Success);
        }
    }

    // 刷新单图来源的背景 sourceType 1 是必应每日一图 2 是指定的本地图片
    private static void BackgroundRefreshSingle(int sourceType, bool isHint, bool refresh)
    {
        if (!refresh)
        {
            if (ModMain.frmSetupUI is not null)
                ModMain.frmSetupUI.BackgroundRefreshUI(true, 1);
            return;
        }

        if (sourceType == 2)
        {
            var path = Config.Preference.Background.LocalImagePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ModMain.frmMain.ImgBack.Visibility = Visibility.Collapsed;
                ModMain.frmMain.ImgBackRefreshRowSpan();
                if (isHint)
                    HintService.Hint(Lang.Text("Setup.Ui.Background.LocalImage.Missing"), HintType.Error);
                return;
            }

            ShowImageBackground(path, isHint);
            return;
        }

        // 必应壁纸先用本地缓存立刻显示 免得等网络的时候一直空着 然后再去后台更新缓存
        var cached = ModBackground.GetCachedBingWallpapers();
        if (cached.Count > 0)
        {
            ApplyBingWallpapers(cached);
            ShowNextBingWallpaper(isHint);
        }
        else
        {
            // 一张缓存都没有才提示加载中
            HintService.Hint(Lang.Text("Setup.Ui.Background.Bing.Loading"), HintType.Info);
        }

        var hasCache = cached.Count > 0;
        var showHint = isHint;
        ModBase.RunInNewThread(() =>
        {
            var paths = ModBackground.DownloadBingWallpapers(Config.Preference.Background.BingLastImage);
            ModBase.RunInUi(() =>
            {
                if (paths.Count == 0)
                {
                    // 有缓存就静默失败 保持当前显示
                    if (showHint && !hasCache)
                        HintService.Hint(Lang.Text("Setup.Ui.Background.Bing.Failed"), HintType.Error);
                    else if (!hasCache)
                        ModBase.Log("[UI] 获取必应壁纸失败且无本地缓存");
                    return;
                }

                // 没缓存就拿下载结果立刻显示一张
                if (!hasCache)
                {
                    ApplyBingWallpapers(paths);
                    ShowNextBingWallpaper(showHint);
                }
            });
        }, "Bing Wallpaper");
    }

    // 应用必应壁纸列表 首次应用时把上次用过的那张排除掉 免得每次启动都是同一张
    private static void ApplyBingWallpapers(List<string> paths)
    {
        if (_bingWallpaperIndex >= 0)
        {
            _bingWallpapers = paths;
            return;
        }

        var last = Config.Preference.Background.BingLastImage;
        var candidates = paths
            .Where(p => !string.Equals(p, last, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _bingWallpapers = candidates.Count > 0 ? candidates : paths;
    }

    private static List<string>? _bingWallpapers;

    // 当前用的是列表里第几张 -1 表示还没用过
    private static int _bingWallpaperIndex = -1;

    // 换下一张 每次调用都换 全部轮过一遍就重新打乱
    private static void ShowNextBingWallpaper(bool isHint)
    {
        if (_bingWallpapers is null || _bingWallpapers.Count == 0)
        {
            if (isHint)
                HintService.Hint(Lang.Text("Setup.Ui.Background.Bing.Failed"), HintType.Error);
            return;
        }

        _bingWallpaperIndex += 1;
        if (_bingWallpaperIndex >= _bingWallpapers.Count)
        {
            _bingWallpapers = Shuffle(_bingWallpapers);
            _bingWallpaperIndex = 0;
        }

        // 列表可能因缓存更新而缩短，此时索引会越界
        if (_bingWallpaperIndex < 0 || _bingWallpaperIndex >= _bingWallpapers.Count)
            _bingWallpaperIndex = 0;

        ShowImageBackground(_bingWallpapers[_bingWallpaperIndex], isHint);
    }

    private static List<string> Shuffle(IReadOnlyList<string> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomUtils.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    // 当前是必应模式又有缓存的话就换下一张
    // 刷新背景内容那个按钮用它 不重新请求接口也能换图
    public static bool TryShowNextBingWallpaper(bool isHint)
    {
        if (_bingWallpapers is null || _bingWallpapers.Count == 0)
            return false;
        ShowNextBingWallpaper(isHint);
        return true;
    }

    // 把指定的图片设成背景
    private static void ShowImageBackground(string path, bool isHint)
    {
        try
        {
            ModBase.Log("[UI] 加载背景内容：" + path);
            // 记录已使用的必应壁纸，供下次启动时换一张
            if (Config.Preference.Background.SourceType == 1)
                Config.Preference.Background.BingLastImage = path;
            ModVideoBack.VideoStop();
            ModMain.frmMain.VideoBack.Source = null;
            ModMain.frmMain.ImgBack.Background = new MyBitmap(path);
            ModSetup.UiBackgroundSuit(Config.Preference.Background.WallpaperSuitMode);
            ModMain.frmMain.ImgBack.Visibility = Visibility.Visible;
            ModMain.frmMain.ImgBackRefreshRowSpan();
            // 壁纸内容已变但毛玻璃层几何未变，需显式失效以重新采样
            BlurBorder.InvalidateAllBackgrounds();
            if (isHint)
                HintService.Hint(Lang.Text("Setup.Ui.Background.Refresh.Success", ModBase.GetFileNameFromPath(path)),
                    HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[UI] 加载背景图片失败：" + path, ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
            ModMain.frmMain.ImgBack.Visibility = Visibility.Collapsed;
            ModMain.frmMain.ImgBackRefreshRowSpan();
        }
    }

    /// <summary>
    ///     刷新背景图片及设置页 UI。
    /// </summary>
    /// <param name="isHint">是否显示刷新提示。</param>
    /// <param name="refresh">是否刷新图片显示。</param>
    public static void BackgroundRefresh(bool isHint, bool refresh)
    {
        try
        {
            // 获取可用的图片文件
            Directory.CreateDirectory(ModBase.exePath + @"PCL\Pictures\");
            var pic = ModBase.EnumerateFiles(ModBase.exePath + @"PCL\Pictures\").Where(file =>
                    !(file.Extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                      file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase))).Select(file => file.FullName)
                .ToList();

            // 视频加载异常处理

            EventHandler<ExceptionRoutedEventArgs> videoHandler = (sender, e) =>
            {
                var videoEx = e.ErrorException;
                var videoAddress = ModMain.frmMain.VideoBack.Source.ToString();
                if (ModMain.frmMain.VideoBack.Source is not null)
                {
                    ModVideoBack.VideoStop();

                    if (videoEx.Message.Contains("0xC00D109B"))
                        ModBase.Log(
                            $"""
                             刷新背景内容失败，该视频文件可能并非 H.264（AVC）格式。
                             你可以尝试使用视频转码工具打开视频文件并设定目标格式为 H.264（AVC），然后转码该视频。
                             文件：{videoAddress}
                             """,
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Setup.Ui.Error.BackgroundVideoUnsupported"));
                    else
                        ModBase.Log(
                            videoEx,
                            $"刷新背景内容失败（{videoAddress}）",
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
                }
            };
            ModMain.frmMain.VideoBack.MediaFailed -= videoHandler;
            ModVideoBack.GamingStateChanged -= ModVideoBack.OnGamingStateChanged;
            ModVideoBack.ForcePlayChanged -= ModVideoBack.OnForcePlayChanged;
            ModVideoBack.GamingStateChanged += ModVideoBack.OnGamingStateChanged;
            ModVideoBack.ForcePlayChanged += ModVideoBack.OnForcePlayChanged;
            if (!Config.Preference.Background.AutoPauseVideo)
                ModVideoBack.ForcePlay = true;

            // 非文件夹来源单独处理，不参与随机轮播
            var sourceType = Config.Preference.Background.SourceType;
            if (sourceType != 0)
            {
                BackgroundRefreshSingle(sourceType, isHint, refresh);
                return;
            }

            // 加载
            if (pic.Count == 0)
            {
                if (refresh)
                {
                    if (ModMain.frmMain.ImgBack.Visibility == Visibility.Collapsed)
                    {
                        if (isHint)
                            HintService.Hint(Lang.Text("Setup.Ui.Background.NoAvailableContent"), HintType.Error);
                    }
                    else
                    {
                        ModMain.frmMain.ImgBack.Visibility = Visibility.Collapsed;
                        ModMain.frmMain.ImgBackRefreshRowSpan();
                        if (isHint)
                            HintService.Hint(Lang.Text("Setup.Ui.Background.Cleared"), HintType.Success);
                    }
                }

                if (ModMain.frmSetupUI is not null)
                    ModMain.frmSetupUI.BackgroundRefreshUI(false, 0);
            }
            else
            {
                if (refresh)
                {
                    var address = RandomUtils.PickRandom(pic);
                    try
                    {
                        ModMain.frmMain.ImgBack.Background = null;
                        ModVideoBack.VideoStop();
                        ModBase.Log("[UI] 加载背景内容：" + address);
                        ModMain.frmMain.ImgBack.Background = new MyBitmap(address);
                        _ = Config.Preference.Background.WallpaperSuitMode;
                        ModMain.frmMain.ImgBack.Visibility = Visibility.Visible;
                        ModMain.frmMain.ImgBackRefreshRowSpan();
                        // 壁纸内容已变但毛玻璃层几何未变，需显式失效以重新采样
                        BlurBorder.InvalidateAllBackgrounds();
                        if (isHint)
                                HintService.Hint(Lang.Text("Setup.Ui.Background.Refresh.Success", ModBase.GetFileNameFromPath(address)), HintType.Success,
                                false);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            ModMain.frmMain.VideoBack.MediaFailed += videoHandler;
                            ModBase.Log(ex, "[UI] 加载背景图片失败" + address);
                            if (ModBase.modeDebug)
                                HintService.Hint(Lang.Text("Setup.Ui.Background.ImageLoadFailed", address));
                            ModMain.frmMain.ImgBack.Visibility = Visibility.Visible;
                            ModMain.frmMain.VideoBack.Source = new Uri(address, UriKind.Absolute);
                            ModVideoBack.VideoPlay();
                            if (isHint)
                            HintService.Hint(Lang.Text("Setup.Ui.Background.Refresh.Success", ModBase.GetFileNameFromPath(address)), HintType.Success,
                                    false);
                        }
                        catch (Exception playEx)
                        {
                            ModBase.Log(playEx, "播放背景内容时出现未知错误：");
                        }
                    }
                }

                if (ModMain.frmSetupUI is not null)
                    ModMain.frmSetupUI.BackgroundRefreshUI(true, pic.Count);
            }
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "刷新背景内容时出现未知错误",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
        }
    }

    // 顶部栏
    private void BtnLogoChange_Click(object sender, MouseButtonEventArgs e)
    {
        var fileName = SystemDialogs.SelectFile(
            Lang.Text("Setup.Ui.ImageFile.Filter"),
            Lang.Text("Setup.Ui.ImageFile.SelectTitle"));
        if (string.IsNullOrEmpty(fileName))
            return;
        try
        {
            // 拷贝文件
            File.Delete(ModBase.exePath + @"PCL\Logo.png");
            ModBase.CopyFile(fileName, ModBase.exePath + @"PCL\Logo.png");
            // 设置当前显示
            ModMain.frmMain.ImageTitleLogo.Source = null; // 防止因为 Source 属性前后的值相同而不更新 (#5628)
            ModMain.frmMain.ImageTitleLogo.Source = ModBase.exePath + @"PCL\Logo.png";
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("参数无效"))
                ModBase.Log(
                    """
                    改变标题栏图片失败，该图片文件可能并非标准格式。
                    你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。
                    """,
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Setup.Ui.Error.TitleImageInvalidFormat"));
            else
                ModBase.Log(
                    ex,
                    "设置标题栏图片失败",
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
            ModMain.frmMain.ImageTitleLogo.Source = null;
        }
    }

    private void RadioLogoType3_Check(object sender, ModBase.RouteEventArgs e)
    {
        if (!(ModAnimation.AniControlEnabled == 0 && e.raiseByMouse))
            return;
        Refresh: ;

        // 已有图片则不再选择
        if (File.Exists(ModBase.exePath + @"PCL\Logo.png"))
        {
            try
            {
                ModMain.frmMain.ImageTitleLogo.Source = null; // 防止因为 Source 属性前后的值相同而不更新 (#5628)
                ModMain.frmMain.ImageTitleLogo.Source = ModBase.exePath + @"PCL\Logo.png";
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("参数无效"))
                    ModBase.Log(
                        """
                        调整标题栏图片失败，该图片文件可能并非标准格式。
                        你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。
                        """,
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Setup.Ui.Error.TitleImageResizeInvalidFormat"));
                else
                    ModBase.Log(
                        ex,
                        "调整标题栏图片失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
                ModMain.frmMain.ImageTitleLogo.Source = null;
                e.handled = true;
                try
                {
                    File.Delete(ModBase.exePath + @"PCL\Logo.png");
                }
                catch (Exception exx)
                {
                    ModBase.Log(
                        exx,
                        "清理错误的标题栏图片失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
                }
            }

            return;
        }

        // 没有图片则要求选择
        var fileName = SystemDialogs.SelectFile(Lang.Text("Setup.Ui.ImageFile.Filter"), Lang.Text("Setup.Ui.ImageFile.SelectTitle"));
        if (string.IsNullOrEmpty(fileName))
        {
            ModMain.frmMain.ImageTitleLogo.Source = null;
            e.handled = true;
        }
        else
        {
            try
            {
                // 拷贝文件
                File.Delete(ModBase.exePath + @"PCL\Logo.png");
                ModBase.CopyFile(fileName, ModBase.exePath + @"PCL\Logo.png");
                goto Refresh;
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "复制标题栏图片失败",
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
            }
        }
    }

    private void BtnLogoDelete_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            File.Delete(ModBase.exePath + @"PCL\Logo.png");
            RadioLogoType1.SetChecked(true, true);
            HintService.Hint(Lang.Text("Setup.Ui.Logo.Clear.Success"), HintType.Success);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "清空标题栏图片失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
        }
    }

    // 背景音乐
    private void BtnMusicOpen_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenExplorer(ModBase.exePath + @"PCL\Musics\");
    }

    private void BtnMusicRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        ModMusic.MusicRefreshPlay(true);
    }

    public void MusicRefreshUI()
    {
        if (PanBackgroundOpacity is null)
            return;
        if (ModMusic.musicAllList.Any())
        {
            PanMusicVolume.Visibility = Visibility.Visible;
            PanMusicDetail.Visibility = Visibility.Visible;
            BtnMusicClear.Visibility = Visibility.Visible;
            CardMusic.Title = Lang.Text("Setup.Ui.Music.TitleWithCount", ModBase.EnumerateFiles(ModBase.exePath + @"PCL\Musics\").Count());
        }
        else
        {
            PanMusicVolume.Visibility = Visibility.Collapsed;
            PanMusicDetail.Visibility = Visibility.Collapsed;
            BtnMusicClear.Visibility = Visibility.Collapsed;
            CardMusic.Title = Lang.Text("Setup.Ui.Music.Title");
        }

        CardMusic.TriggerForceResize();
    }

    private void BtnMusicClear_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(Lang.Text("Setup.Ui.Music.Clear.Confirm.Message"),
                Lang.Text("Common.Dialog.Warning"), button2: Lang.Text("Common.Action.Cancel"),
                isWarn: true) == 1)
            ModBase.RunInThread(() =>
            {
                HintService.Hint(Lang.Text("Setup.Ui.Music.Deleting"));
                // 停止播放音乐
                ModMusic.musicNAudio = null;
                ModMusic.musicWaitingList = new List<string>();
                ModMusic.musicAllList = new List<string>();
                Thread.Sleep(200);
                // 删除文件
                try
                {
                    ModBase.DeleteDirectory(ModBase.exePath + @"PCL\Musics");
                    // DisableSMTCSupport()
                    HintService.Hint(Lang.Text("Setup.Ui.Music.Delete.Success"), HintType.Success);
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        "删除背景音乐失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
                }

                try
                {
                    Directory.CreateDirectory(ModBase.exePath + @"PCL\Musics");
                    ModBase.RunInUi(() => ModMusic.MusicRefreshPlay(false));
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        "重建背景音乐文件夹失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
                }
            });
    }

    private void CheckMusicStart_Change(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (CheckMusicStart.Checked == true)
            CheckMusicStop.Checked = false;
    }

    private void CheckMusicStop_Change()
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (CheckMusicStop.Checked == true)
            CheckMusicStart.Checked = false;
    }

    // 主页

    private void BtnCustomRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmLaunchRight.ForceRefresh();
        HintService.Hint(Lang.Text("Setup.Ui.Homepage.Refresh.Success"), HintType.Success);
    }

    private void BtnCustomTutorial_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://docs.pclc.cc/ce/customization/xaml-format");
    }

    // 主题

    // 刷新自定义配色面板的可见性
    // 自定义颜色是亮暗模式共用的 所以哪个模式选中自定义都得显示
    public void RefreshCustomThemePanel()
    {
        var theme = Config.Preference.Theme;
        PanCustomTheme.Visibility =
            theme.DarkColor == ColorTheme.Custom || theme.LightColor == ColorTheme.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        LabThemePickerCaption.Text = Lang.Text(
            ThemeService.IsDarkMode ? "Setup.Ui.Theme.PickDarkColor" : "Setup.Ui.Theme.PickLightColor");
    }

    // 按配置刷新自定义配色那几个控件
    private void ReloadCustomTheme()
    {
        SliderCustomHue.Value = Config.Preference.Theme.CustomThemeHue;
        SliderCustomSat.Value = Config.Preference.Theme.CustomThemeSat;
        SliderCustomLight.Value = Config.Preference.Theme.CustomThemeLight;
        TextCustomThemeHex.Text = Config.Preference.Theme.CustomThemeColor;
        RefreshCustomThemePreview();
        RefreshCustomThemePanel();
    }

    // 刷新自定义配色的预览色块
    private void RefreshCustomThemePreview()
    {
        try
        {
            RectCustomThemePreview.Background = new SolidColorBrush(NColor.FromHex(Config.Preference.Theme.CustomThemeColor));
        }
        catch
        {
            // 颜色不合法就保持原样
        }
    }

    // 三个 HSL 滑块里任意一个动了 就把 Hex 和另外两个滑块一起同步过去
    private void SliderCustomTheme_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;

        // FromHsl 的饱和度和明度都是 0 到 100
        var color = NColor.FromHsl(SliderCustomHue.Value, SliderCustomSat.Value, SliderCustomLight.Value);
        var hex = color.ToHexString();
        SetByTag("UiCustomThemeHue", SliderCustomHue.Value);
        SetByTag("UiCustomThemeSat", SliderCustomSat.Value);
        SetByTag("UiCustomThemeLight", SliderCustomLight.Value);
        SetByTag("UiCustomThemeColor", hex);
        ModAnimation.AniControlEnabled += 1;
        TextCustomThemeHex.Text = hex;
        ModAnimation.AniControlEnabled -= 1;
        RectCustomThemePreview.Background = new SolidColorBrush(color);
    }
    // Hex 输入框变了 反算回 HSL 再同步滑块
    private void TextCustomThemeHex_Change(object senderRaw, RoutedEventArgs e)
    {
        var sender = (MyTextBox)senderRaw;
        if (!sender.IsValidated)
            return;

        NColor color;
        try
        {
            color = NColor.FromHex(sender.Text);
        }
        catch
        {
            return;
        }

        var (hue, sat, light) = color.ToHsl();
        var hueInt = (int)Math.Round(hue);
        var satInt = (int)Math.Round(sat);
        var lightInt = (int)Math.Round(light);
        SetByTag("UiCustomThemeColor", sender.Text);
        ModAnimation.AniControlEnabled += 1;
        SliderCustomHue.Value = hueInt;
        SliderCustomSat.Value = satInt;
        SliderCustomLight.Value = lightInt;
        ModAnimation.AniControlEnabled -= 1;
        SetByTag("UiCustomThemeHue", hueInt);
        SetByTag("UiCustomThemeSat", satInt);
        SetByTag("UiCustomThemeLight", lightInt);
        RectCustomThemePreview.Background = new SolidColorBrush(color);
    }

    // 背景来源

    private void BtnBackgroundSelect_Click(object sender, MouseButtonEventArgs e)
    {
        var fileName = SystemDialogs.SelectFile(
            Lang.Text("Setup.Ui.ImageFile.Filter"),
            Lang.Text("Setup.Ui.Background.SelectTitle"));
        if (string.IsNullOrEmpty(fileName))
            return;

        SetByTag("UiBackgroundImagePath", fileName);
        LabBackgroundLocalImage.Text = fileName;
        if (Config.Preference.Background.SourceType != 2)
            SetByTag("UiBackgroundSource", 2);
        BackgroundRefresh(true, true);
    }

    // 赞助
    // 滑动条
    private void SliderLoad()
    {
        SliderMusicVolume.getHintText = new Func<object, object>(v =>
            Lang.Number(Math.Ceiling(Convert.ToDouble(v) * 0.1d) / 100d, "P0"));
        SliderLauncherOpacity.getHintText = new Func<object, object>(v =>
            Lang.Number(Math.Round(40 + Convert.ToDouble(v) * 0.1d) / 100d, "P0"));
        SliderBackgroundOpacity.getHintText = new Func<object, object>(v =>
            Lang.Number(Math.Round(Convert.ToDouble(v) * 0.1d) / 100d, "P0"));
        SliderBackgroundBlur.getHintText = new Func<object, object>(v => Lang.Text("Setup.Ui.Slider.Pixel", Lang.Number(Convert.ToDouble(v), "N0")));
        SliderBlurValue.getHintText = new Func<object, object>(v => Lang.Text("Setup.Ui.Slider.Pixel", Lang.Number(Convert.ToDouble(v), "N0")));
        SliderBlurSamplingRate.getHintText = new Func<object, object>(v => Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderGlass.getHintText = new Func<object, object>(v =>
            Lang.Text("Setup.Ui.Slider.Pixel", Lang.Number(Convert.ToDouble(v), "N0")));
        SliderGlassLiquidRefraction.getHintText = new Func<object, object>(v =>
            Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderGlassLiquidThickness.getHintText = new Func<object, object>(v =>
            Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderGlassLiquidBlur.getHintText = new Func<object, object>(v =>
            Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderGlassLiquidGlare.getHintText = new Func<object, object>(v =>
            Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderCustomHue.getHintText = new Func<object, object>(v => Lang.Number(Convert.ToDouble(v), "N0") + "°");
        SliderCustomSat.getHintText = new Func<object, object>(v => Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
        SliderCustomLight.getHintText = new Func<object, object>(v => Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
    }

    private void CheckMusicStart_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckMusicStart_Change(sender, user);
    }

    private void CheckMusicStop_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckMusicStop_Change();
    }

    #region 功能隐藏

    /// <summary>
    ///     是否强制显示被禁用的功能。
    /// </summary>
    public static bool HiddenForceShow
    {
        get => field;
        set
        {
            field = value;
            HiddenRefresh();
        }
    }

    /// <summary>
    ///     更新功能隐藏带来的显示变化。
    /// </summary>
    public static void HiddenRefresh()
    {
        if (ModMain.frmMain.PanTitleSelect is null || !ModMain.frmMain.PanTitleSelect.IsLoaded)
            return;
        try
        {
            // 获取配置组引用以缩短代码
            var conf = Config.Preference.Hide;

            // 顶部栏：下载、设置、工具
            var isAllTitleHidden = !HiddenForceShow && conf.PageDownload && conf.PageSetup && conf.PageTools;

            if (isAllTitleHidden)
            {
                ModMain.frmMain.PanTitleSelect.Visibility = Visibility.Collapsed;
            }
            else
            {
                ModMain.frmMain.PanTitleSelect.Visibility = Visibility.Visible;
                ModMain.frmMain.BtnTitleSelect1.Visibility = !HiddenForceShow && conf.PageDownload
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmMain.BtnTitleSelect2.Visibility =
                    !HiddenForceShow && conf.PageSetup ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmMain.BtnTitleSelect3.Visibility =
                    !HiddenForceShow && conf.PageTools ? Visibility.Collapsed : Visibility.Visible;
            }

            // 功能隐藏设置卡片
            if (ModMain.frmSetupUI is not null)
            {
                ModMain.frmSetupUI.CardSwitch.Visibility = !HiddenForceShow && conf.FunctionHidden
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupUI.CardSwitch.Title = HiddenForceShow ? Lang.Text("Setup.Ui.FeatureHide.TitleTemporarilyDisabled") : Lang.Text("Setup.Ui.FeatureHide.Title");
            }

            // 设置子页面 (FrmSetupLeft)
            if (ModMain.frmSetupLeft is not null)
            {
                ModMain.frmSetupLeft.ItemLaunch.Visibility =
                    !HiddenForceShow && conf.SetupLaunch ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemUI.Visibility =
                    !HiddenForceShow && conf.SetupUi ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLauncherLanguage.Visibility = !HiddenForceShow && conf.SetupLauncherLanguage
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemGameManage.Visibility = !HiddenForceShow && conf.SetupGameManage
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLauncherMisc.Visibility = !HiddenForceShow && conf.SetupLauncherMisc
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemJava.Visibility =
                    !HiddenForceShow && conf.SetupJava ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemUpdate.Visibility =
                    !HiddenForceShow && conf.SetupUpdate ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemGameLink.Visibility = !HiddenForceShow && conf.SetupGameLink
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemAbout.Visibility =
                    !HiddenForceShow && conf.SetupAbout ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemFeedback.Visibility = !HiddenForceShow && conf.SetupFeedback
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLog.Visibility =
                    !HiddenForceShow && conf.SetupLog ? Visibility.Collapsed : Visibility.Visible;

                var categories = new[]
                {
                    (ModMain.frmSetupLeft.TextGameCategory,
                        !(conf.SetupLaunch && conf.SetupJava && conf.SetupGameManage)),
                    (ModMain.frmSetupLeft.TextToolsCategory, !conf.SetupGameLink),
                    (ModMain.frmSetupLeft.TextLauncherCategory, !(conf.SetupUi && conf.SetupLauncherLanguage && conf.SetupLauncherMisc)),
                    (ModMain.frmSetupLeft.TextAboutCategory,
                        !(conf.SetupAbout && conf.SetupUpdate && conf.SetupFeedback && conf.SetupLog))
                };

                foreach (var category in categories)
                {
                    var isVisible = category.Item2 || HiddenForceShow;
                    category.Item1.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    if (isVisible)
                        category.Item1.Opacity = 0.6d;
                }

                // 统计设置页可用项数量
                var setupCount = 0;
                if (!conf.SetupLaunch)
                    setupCount += 1;
                if (!conf.SetupUi)
                    setupCount += 1;
                if (!conf.SetupLauncherLanguage)
                    setupCount += 1;
                if (!conf.SetupGameManage)
                    setupCount += 1;
                if (!conf.SetupLauncherMisc)
                    setupCount += 1;
                if (!conf.SetupJava)
                    setupCount += 1;
                if (!conf.SetupUpdate)
                    setupCount += 1;
                if (!conf.SetupGameLink)
                    setupCount += 1;
                if (!conf.SetupAbout)
                    setupCount += 1;
                if (!conf.SetupFeedback)
                    setupCount += 1;
                if (!conf.SetupLog)
                    setupCount += 1;
                ModMain.frmSetupLeft.PanItem.Visibility =
                    setupCount < 2 && !HiddenForceShow ? Visibility.Collapsed : Visibility.Visible;
            }

            // 工具子页面 (FrmToolsLeft)
            if (ModMain.frmToolsLeft is not null)
            {
                ModMain.frmToolsLeft.ItemGameLink.Visibility = !HiddenForceShow && conf.ToolsGameLink
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmToolsLeft.ItemTest.Visibility =
                    !HiddenForceShow && conf.ToolsTest ? Visibility.Collapsed : Visibility.Visible;
                
                // 处理分类标题
                var isGameLinkVisible = (!HiddenForceShow && !conf.ToolsGameLink) || HiddenForceShow;
                ModMain.frmToolsLeft.TextGameLinkCategory.Visibility = isGameLinkVisible ? Visibility.Visible : Visibility.Collapsed;
                if (isGameLinkVisible) ModMain.frmToolsLeft.TextGameLinkCategory.Opacity = 0.6;

                var isToolsVisible = (!HiddenForceShow && !conf.ToolsTest) || HiddenForceShow;
                ModMain.frmToolsLeft.TextToolsCategory.Visibility = isToolsVisible ? Visibility.Visible : Visibility.Collapsed;
                if (isToolsVisible) ModMain.frmToolsLeft.TextToolsCategory.Opacity = 0.6;
                
                // 统计工具页可用项数量
                var toolsCount = 0;
                if (!conf.ToolsGameLink)
                    toolsCount += 1;
                if (!conf.ToolsTest)
                    toolsCount += 1;
                ModMain.frmToolsLeft.PanItem.Visibility =
                    toolsCount < 2 && !HiddenForceShow ? Visibility.Collapsed : Visibility.Visible;
            }

            // 其他入口刷新
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSelect)
                ModMain.frmSelectRight.BtnEmptyDownload_Loaded();
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.Launch)
                ModMain.frmLaunchLeft.RefreshButtonsUI();
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
                ModMain.frmInstanceModDisabled is not null)
                ModMain.frmInstanceModDisabled.BtnDownload_Loaded();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "刷新功能隐藏项目失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
        }
    }

    // ================= 设置页面协同 =================
    private void HiddenSetupMain()
    {
        var isChecked = (bool)CheckHiddenPageSetup.Checked;
        CheckHiddenSetupLaunch.Checked = isChecked;
        CheckHiddenSetupUI.Checked = isChecked;
        CheckHiddenSetupLauncherLanguage.Checked = isChecked;
        CheckHiddenSetupGameManage.Checked = isChecked;
        CheckHiddenLauncherMisc.Checked = isChecked;
        CheckHiddenSetupJava.Checked = isChecked;
        CheckHiddenSetupUpdate.Checked = isChecked;
        CheckHiddenSetupGameLink.Checked = isChecked;
        CheckHiddenSetupAbout.Checked = isChecked;
        CheckHiddenSetupFeedback.Checked = isChecked;
        CheckHiddenSetupLog.Checked = isChecked;
    }

    // ================= 设置页面协同 =================
    private void HiddenSetupMain(object sender, bool user)
    {
        if (!user)
            return; // 仅处理用户点击，防止死循环
        var isChecked = (bool)CheckHiddenPageSetup.Checked;
        CheckHiddenSetupLaunch.Checked = isChecked;
        CheckHiddenSetupUI.Checked = isChecked;
        CheckHiddenSetupLauncherLanguage.Checked = isChecked;
        CheckHiddenSetupGameManage.Checked = isChecked;
        CheckHiddenLauncherMisc.Checked = isChecked;
        CheckHiddenSetupJava.Checked = isChecked;
        CheckHiddenSetupUpdate.Checked = isChecked;
        CheckHiddenSetupGameLink.Checked = isChecked;
        CheckHiddenSetupAbout.Checked = isChecked;
        CheckHiddenSetupFeedback.Checked = isChecked;
        CheckHiddenSetupLog.Checked = isChecked;
    }

    private void HiddenSetupSub(object sender, bool user)
    {
        if (!user)
            return;
        var conf = Config.Preference.Hide;
        // 判断是否全部勾选
        var allChecked = conf.SetupLaunch && conf.SetupUi && conf.SetupLauncherLanguage && conf.SetupJava &&
                         conf.SetupUpdate && conf.SetupGameLink && conf.SetupAbout && conf.SetupFeedback &&
                         conf.SetupLog && conf.SetupLauncherMisc && conf.SetupGameManage;
        CheckHiddenPageSetup.Checked = allChecked;
    }

    // ================= 工具页面协同 =================
    private void HiddenToolsMain(object sender, bool user)
    {
        if (!user)
            return;
        var isChecked = (bool)CheckHiddenPageTools.Checked;
        CheckHiddenToolsGameLink.Checked = isChecked;
        CheckHiddenToolsTest.Checked = isChecked;
    }

    private void HiddenToolsSub(object sender, bool user)
    {
        if (!user)
            return;
        var conf = Config.Preference.Hide;
        var allChecked = conf.ToolsGameLink && conf.ToolsTest;
        CheckHiddenPageTools.Checked = allChecked;
    }

    // 警告提示
    private void HiddenHint(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled == 0 && sender is MyCheckBox checkBox && checkBox.Checked == true)
            HintService.Hint(Lang.Text("Setup.Ui.FeatureHide.TemporaryHint"));
    }

    #endregion
}
