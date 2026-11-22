using PCL.Core.App.Configuration;

namespace PCL.Core.App;

/// <summary>
/// 全局配置类。
/// </summary>
// ReSharper disable InconsistentNaming
public static partial class Config
{
    /// <summary>
    /// 系统配置。
    /// </summary>
    [ConfigGroup("System")] partial class SystemConfigGroup
    {
        // /// <summary>
        // /// 系统缓存目录。
        // /// </summary>
        // [ConfigItem<string>("SystemSystemCache", "")] public partial string CacheDirectory { get; set; }

        /// <summary>
        /// 禁用硬件加速。
        /// </summary>
        [ConfigItem<bool>("SystemDisableHardwareAcceleration", false)] public partial bool DisableHardwareAcceleration { get; set; }

        /// <summary>
        /// 遥测。
        /// </summary>
        [ConfigItem<bool>("SystemTelemetry", false)] public partial bool Telemetry { get; set; }

        /// <summary>
        /// 实时日志最大行数。
        /// </summary>
        [ConfigItem<int>("SystemMaxLog", 13)] public partial int MaxGameLog { get; set; }

        /// <summary>
        /// 动画帧率上限。
        /// </summary>
        [ConfigItem<int>("UiAniFPS", 59)] public partial int AnimationFpsLimit { get; set; }

        [ConfigItem<string>("Profile", "{\"lastUsed\":-1,\"profiles\":[]}", ConfigSource.SharedEncrypt)] public partial string Profiles { get; set; }

    }

    /// <summary>
    /// 网络配置。
    /// </summary>
    [ConfigGroup("Network")] partial class NetworkConfigGroup
    {
        [ConfigItem<bool>("SystemNetEnableDoH", true)] public partial bool EnableDoH { get; set; }

        [ConfigGroup("HttpProxy")] partial class HttpProxyConfigGroup
        {
            [ConfigItem<string>("SystemHttpProxy", "", ConfigSource.SharedEncrypt)] public partial string CustomAddress { get; set; }
            [ConfigItem<int>("SystemHttpProxyType", 1)] public partial int Type { get; set; }
            [ConfigItem<string>("SystemHttpProxyCustomUsername", "")] public partial string CustomUsername { get; set; }
            [ConfigItem<string>("SystemHttpProxyCustomPassword", "")] public partial string CustomPassword { get; set; }
        }
    }

    /// <summary>
    /// 调试配置
    /// </summary>
    [ConfigGroup("Debug")] partial class DebugConfigGroup
    {
        [ConfigItem<bool>("SystemDebugMode", false)] public partial bool Enabled { get; set; }
        [ConfigItem<int>("SystemDebugAnim", 9)] public partial int AnimationSpeed { get; set; }
        [ConfigItem<bool>("SystemDebugDelay", false)] public partial bool AddRandomDelay { get; set; }
        [ConfigItem<bool>("SystemDebugSkipCopy", false)] public partial bool DontCopy { get; set; }
        [ConfigItem<bool>("SystemDebugAllowRestrictedFeature", false)] public partial bool AllowRestrictedFeature { get; set; }
    }

    /// <summary>
    /// 下载配置。
    /// </summary>
    [ConfigGroup("Download")] partial class DownloadConfigGroup
    {
        [ConfigItem<int>("ToolDownloadThread", 63)] public partial int ThreadLimit { get; set; }
        [ConfigItem<int>("ToolDownloadSpeed", 42)] public partial int SpeedLimit { get; set; }
        [ConfigItem<int>("ToolDownloadSource", 1)] public partial int FileSource { get; set; }
        [ConfigItem<int>("ToolDownloadVersion", 1)] public partial int VersionListSource { get; set; }
        [ConfigItem<bool>("ToolDownloadAutoSelectVersion", true)] public partial bool AutoSelectInstance { get; set; }
        [ConfigItem<bool>("ToolFixAuthlib", true)] public partial bool FixAuthLib { get; set; }

        /// <summary>
        /// 第三方资源配置。
        /// </summary>
        [ConfigGroup("Comp")] partial class CompConfigGroup
        {
            [ConfigItem<int>("ToolDownloadTranslate", 0)] public partial int NameFormatV1 { get; set; }
            [ConfigItem<int>("ToolDownloadTranslateV2", 1)] public partial int NameFormatV2 { get; set; }
            [ConfigItem<bool>("ToolDownloadIgnoreQuilt", false)] public partial bool IgnoreQuilt { get; set; }

            /// <summary>
            ///     合并同款模组。<br/>
            ///     开启时，「全部来源」下同一模组在 CurseForge 与 Modrinth 上只列一条，
            ///     另一来源挂在条目下方（可点击进入各自详情页），便于比较哪边更新更及时。<br/>
            ///     关闭则回到旧行为：判定为同款时直接丢弃另一端的结果。
            /// </summary>
            [ConfigItem<bool>("ToolDownloadCompMergeSource", true)] public partial bool MergeSameSource { get; set; }
            [ConfigItem<bool>("ToolDownloadAutoInstallDependencies", true)] public partial bool AutoInstallDependencies { get; set; }
            [ConfigItem<bool>("ToolDownloadClipboard", false)] public partial bool ReadClipboard { get; set; }
            [ConfigItem<int>("ToolDownloadMod", 1)] public partial int CompSourceSolution { get; set; }
            [ConfigItem<int>("ToolModLocalNameStyle", 0)] public partial int UiCompNameSolution { get; set; }
            [ConfigItem<int>("ToolDownloadQuickBehavior", 0)] public partial int QuickDownloadBehavior { get; set; }
        }
    }

    /// <summary>
    /// 工具配置。
    /// </summary>
    [ConfigGroup("Tool")] partial class ToolConfigGroup
    {
        [ConfigItem<bool>("ToolHelpChinese", true)] public partial bool AutoChangeLanguage { get; set; }
        // [ConfigItem<int>("ToolUpdateAlpha", 0, ConfigSource.SharedEncrypt)] public partial int Alpha { get; set; }
        [ConfigItem<bool>("ToolUpdateRelease", false)] public partial bool ReleaseNotification { get; set; }
        [ConfigItem<bool>("ToolUpdateSnapshot", false)] public partial bool SnapshotNotification { get; set; }
    }

    /// <summary>
    /// 更新配置。
    /// </summary>
    [ConfigGroup("Update")] partial class UpdateConfigGroup
    {
        /// <summary>
        /// 自动更新行为。
        /// </summary>
        [ConfigItem<LauncherAutoUpdateBehavior>("SystemSystemUpdate", LauncherAutoUpdateBehavior.DownloadAndAnnounce, ConfigSource.Local)] public partial LauncherAutoUpdateBehavior UpdateMode { get; set; }

        /// <summary>
        /// 更新分支。
        /// </summary>
        [ConfigItem<UpdateChannel>("SystemUpdateChannel", UpdateChannel.Release, ConfigSource.Local)] public partial UpdateChannel UpdateChannel { get; set; }
            
        /// <summary>
        /// Mirror 酱 CDK。
        /// </summary>
        [ConfigItem<string>("SystemMirrorChyanKey", "", ConfigSource.SharedEncrypt)] public partial string MirrorChyanKey { get; set; }
    }

    /// <summary>
    /// 联机大厅配置。
    /// </summary>
    [ConfigGroup("Link")] partial class LinkConfigGroup
    {
        /// <summary>
        /// 大厅用户名。
        /// </summary>
        [ConfigItem<string>("LinkUsername", "")] public partial string Username { get; set; }

        /// <summary>
        /// 中继方式。
        /// </summary>
        [ConfigItem<LinkRelayBehavior>("LinkRelayType", LinkRelayBehavior.Default)] public partial LinkRelayBehavior RelayType { get; set; }

        /// <summary>
        /// 中继服务器类型 (社区/自有)。
        /// </summary>
        [ConfigItem<int>("LinkServerType", 1)] public partial int ServerType { get; set; }

        /// <summary>
        /// 延迟优先模式。
        /// </summary>
        [ConfigItem<bool>("LinkLatencyFirstMode", true)] public partial bool UseLatencyFirstMode { get; set; }

        /// <summary>
        /// 自定义中继服务器。
        /// </summary>
        [ConfigItem<string>("LinkRelayServer", "")] public partial string CustomRelayServer { get; set; }

        /// <summary>
        /// 传输协议优先策略。
        /// </summary>
        [ConfigItem<LinkProtocolPreference>("LinkProtocolPreference", LinkProtocolPreference.Tcp)] public partial LinkProtocolPreference ProtocolPreference { get; set; }

        /// <summary>
        /// 尝试使用端口猜测打通对称性 NAT。
        /// </summary>
        [ConfigItem<bool>("LinkTryPunchSym", true)] public partial bool TryPunchSym { get; set; }

        /// <summary>
        /// 启用 IPv6。
        /// </summary>
        [ConfigItem<bool>("LinkEnableIPv6", true)] public partial bool EnableIPv6 { get; set; }
        
        /// <summary>
        /// 在日志中输出 Cli 信息以用于调试。
        /// </summary>
        [ConfigItem<bool>("LinkEnableCliOutput", false)] public partial bool EnableCliOutput { get; set; }
    }

    /// <summary>
    /// 服务器配置
    /// </summary>
    [ConfigGroup("Server")] partial class ServerConfigGroup
    {
        /// <summary>
        /// 新建服务器实例的默认内存 MB
        /// </summary>
        [ConfigItem<int>("ServerDefaultMemory", 2048)] public partial int DefaultMemory { get; set; }

        /// <summary>
        /// 上次选中的服务器实例目录名
        /// </summary>
        [ConfigItem<string>("ServerSelectedName", "")] public partial string SelectedName { get; set; }

        /// <summary>
        /// 穿透用的 frps 地址 服务商节点域名或自建服务器
        /// </summary>
        [ConfigItem<string>("ServerTunnelAddress", "")] public partial string TunnelAddress { get; set; }

        /// <summary>
        /// frps 端口
        /// </summary>
        [ConfigItem<int>("ServerTunnelPort", 7000)] public partial int TunnelPort { get; set; }

        /// <summary>
        /// 隧道鉴权 token 服务商控制台里取
        /// </summary>
        [ConfigItem<string>("ServerTunnelToken", "", ConfigSource.SharedEncrypt)]
        public partial string TunnelToken { get; set; }

        /// <summary>
        /// 外网暴露的端口 服务商分配的或者自建 frps 允许的
        /// </summary>
        [ConfigItem<int>("ServerTunnelRemotePort", 0)] public partial int TunnelRemotePort { get; set; }

        /// <summary>
        /// 隧道服务商 0 手动填参数 1 ChmlFrp
        /// </summary>
        [ConfigItem<int>("ServerTunnelProvider", 0)] public partial int TunnelProvider { get; set; }

        /// <summary>
        /// ChmlFrp 的登录凭据 存的是 OAuth 令牌串 不是面板那串 token
        /// </summary>
        [ConfigItem<string>("ServerTunnelChmlFrpToken", "", ConfigSource.SharedEncrypt)]
        public partial string TunnelChmlFrpToken { get; set; }

        /// <summary>
        /// ChmlFrp 面板个人资料里的用户 token frpc 的 user 字段要用 接口不再返回只能用户自己粘
        /// </summary>
        [ConfigItem<string>("ServerTunnelChmlFrpUser", "", ConfigSource.SharedEncrypt)]
        public partial string TunnelChmlFrpUser { get; set; }

        /// <summary>
        /// ChmlFrp 选中的隧道 id 每个实例一条
        /// </summary>
        [ConfigItem<string>("ServerTunnelChmlFrpTunnel", "")] public partial string TunnelChmlFrpTunnel { get; set; }

        /// <summary>
        /// 樱花内网穿透的访问密钥 面板用户中心里生成
        /// </summary>
        [ConfigItem<string>("ServerTunnelSakuraKey", "", ConfigSource.SharedEncrypt)]
        public partial string TunnelSakuraKey { get; set; }

        /// <summary>
        /// 樱花选中的隧道 id
        /// </summary>
        [ConfigItem<string>("ServerTunnelSakuraTunnel", "")] public partial string TunnelSakuraTunnel { get; set; }
    }

    /// <summary>
    /// AI 配置
    /// </summary>
    [ConfigGroup("Ai")] partial class AiConfigGroup
    {
        /// <summary>
        /// 接口基址 例如 https://api.openai.com/v1 具体路径由代码拼接 这里不要带
        /// </summary>
        [ConfigItem<string>("AiApiUrl", "")] public partial string ApiUrl { get; set; }

        /// <summary>
        /// 接口密钥 作为 Bearer Token 发送
        /// </summary>
        [ConfigItem<string>("AiApiKey", "", ConfigSource.SharedEncrypt)] public partial string ApiKey { get; set; }

        /// <summary>
        /// 模型名 例如 gpt-4o-mini 可以用设置页的获取模型列表拉取后填入
        /// </summary>
        [ConfigItem<string>("AiModel", "")] public partial string Model { get; set; }

        /// <summary>
        /// 认证方式 0 用 API Key 1 用 ChatGPT 订阅
        /// </summary>
        [ConfigItem<int>("AiAuthMode", 0)] public partial int AuthMode { get; set; }

        /// <summary>
        /// 订阅模式用的模型名 跟 API Key 模式的分开存 两边能选的模型根本不是一套
        /// </summary>
        [ConfigItem<string>("AiChatGptModel", "")] public partial string ChatGptModel { get; set; }

        /// <summary>
        /// 本机标识 首次登录前生成一次之后固定 OpenAI 要求每台机器一个且不含用户信息
        /// </summary>
        [ConfigItem<string>("AiHostId", "")] public partial string HostId { get; set; }

        /// <summary>
        /// ChatGPT 订阅的凭据 JSON 含 refresh token 所以加密存
        /// </summary>
        [ConfigItem<string>("AiChatGptCredentials", "", ConfigSource.SharedEncrypt)]
        public partial string ChatGptCredentials { get; set; }
    }

    /// <summary>
    /// 个性化配置。
    /// </summary>
    [ConfigGroup("Preference")] partial class PreferenceConfigGroup
    {
        /// <summary>
        /// 启动时显示 Logo。
        /// </summary>
        [ConfigItem<bool>("UiLauncherLogo", true, ConfigSource.Local)] public partial bool ShowStartupLogo { get; set; }

        /// <summary>
        /// 锁定窗口大小。
        /// </summary>
        [ConfigItem<bool>("UiLockWindowSize", false)] public partial bool LockWindowSize { get; set; }

        /// <summary>
        /// 在启动游戏时显示你知道吗。
        /// </summary>
        [ConfigItem<bool>("UiShowLaunchingHint", true, ConfigSource.Local)] public partial bool ShowLaunchingHint { get; set; }

        /// <summary>
        /// 标题内容类型。
        /// </summary>
        [ConfigItem<LauncherTitleType>("UiLogoType", LauncherTitleType.Default, ConfigSource.Local)] public partial LauncherTitleType WindowTitleType { get; set; }

        /// <summary>
        /// 窗口标题文本。
        /// </summary>
        [ConfigItem<string>("UiLogoText", "", ConfigSource.Local)] public partial string WindowTitleCustomText { get; set; }

        /// <summary>
        /// 导航栏居左。
        /// </summary>
        [ConfigItem<bool>("UiLogoLeft", false, ConfigSource.Local)] public partial bool TopBarLeftAlign { get; set; }

        /// <summary>
        /// 全局字体。
        /// </summary>
        [ConfigItem<string>("UiFont", "", ConfigSource.Local)] public partial string Font { get; set; }

        /// <summary>
        /// MOTD 字体。
        /// </summary>
        [ConfigItem<string>("UiMotdFont", "", ConfigSource.Local)] public partial string MotdFont { get; set; }

        /// <summary>
        /// 详细实例分类。
        /// </summary>
        [ConfigItem<bool>("DetailedInstanceClassification", false, ConfigSource.Local)] public partial bool DetailedInstanceClassification {  get; set; }

        /// <summary>
        /// 本地化配置。
        /// </summary>
        [ConfigGroup("Localization", ConfigSource.Local)] partial class LocalizationConfigGroup
        {
            /// <summary>
            /// UI 语言。auto 表示跟随系统语言。
            /// </summary>
            [ConfigItem<string>("UiLanguage", "auto")] public partial string Language { get; set; }

            /// <summary>
            /// UI 展示格式所使用的区域性。auto 表示跟随系统区域格式。
            /// </summary>
            [ConfigItem<string>("UiFormatCulture", "auto")] public partial string FormatCulture { get; set; }

            /// <summary>
            /// 区域覆盖。auto 表示自动判断。
            /// </summary>
            [ConfigItem<string>("UiRegion", "auto")] public partial string Region { get; set; }
        }

        /// <summary>
        /// 界面主题配置。
        /// </summary>
        [ConfigGroup("Theme")] partial class ThemeConfigGroup
        {
            /// <summary>
            /// 配色主题模式。
            /// </summary>
            [ConfigItem<ColorMode>("UiDarkMode", ColorMode.System)] public partial ColorMode ColorMode { get; set; }

            /// <summary>
            /// 暗色配色主题。
            /// </summary>
            [ConfigItem<ColorTheme>("UiDarkColor", ColorTheme.CatBlue)] public partial ColorTheme DarkColor { get; set; }

            /// <summary>
            /// 亮色配色主题。
            /// </summary>
            [ConfigItem<ColorTheme>("UiLightColor", ColorTheme.CatBlue)] public partial ColorTheme LightColor { get; set; }

            /// <summary>
            /// 自定义配色的基础颜色（十六进制）。
            /// </summary>
            [ConfigItem<string>("UiCustomThemeColor", "#1370f3", ConfigSource.Local)] public partial string CustomThemeColor { get; set; }

            /// <summary>
            /// 正文文字颜色。<br/>
            /// 亮色主题下正文默认取配色里最暗的一档（L1），配上毛玻璃面板背后偏暗的壁纸时
            /// 对比度很低。这里允许把正文整体提亮，供壁纸较暗的用户使用。<br/>
            /// 0 = 跟随主题，1 = 浅灰，2 = 白。
            /// </summary>
            [ConfigItem<int>("UiTextTone", 0, ConfigSource.Local)] public partial int TextTone { get; set; }

            /// <summary>
            /// 自定义配色：色相 (H)。与自定义颜色同步，供设置页滑块展示。
            /// </summary>
            [ConfigItem<int>("UiCustomThemeHue", 215, ConfigSource.Local)] public partial int CustomThemeHue { get; set; }

            /// <summary>
            /// 自定义配色：饱和度 (S)。
            /// </summary>
            [ConfigItem<int>("UiCustomThemeSat", 90, ConfigSource.Local)] public partial int CustomThemeSat { get; set; }

            /// <summary>
            /// 自定义配色：明度 (L)。
            /// </summary>
            [ConfigItem<int>("UiCustomThemeLight", 51, ConfigSource.Local)] public partial int CustomThemeLight { get; set; }

            /// <summary>
            /// 窗口透明度。
            /// </summary>
            [ConfigItem<int>("UiLauncherTransparent", 600, ConfigSource.Local)] public partial int WindowOpacity { get; set; }

            /// <summary>
            /// 自定义主题：色相 (H)。
            /// </summary>
            [ConfigItem<int>("UiLauncherHue", 180, ConfigSource.Local)] public partial int WindowHue { get; set; }

            /// <summary>
            /// 自定义主题：饱和度 (S)。
            /// </summary>
            [ConfigItem<int>("UiLauncherSat", 80, ConfigSource.Local)] public partial int WindowSat { get; set; }

            /// <summary>
            /// 自定义主题：明度 (L)。
            /// </summary>
            [ConfigItem<int>("UiLauncherLight", 20, ConfigSource.Local)] public partial int WindowLight { get; set; }

            /// <summary>
            /// 自定义主题：色相渐变。
            /// </summary>
            [ConfigItem<int>("UiLauncherDelta", 90, ConfigSource.Local)] public partial int WindowDelta { get; set; }

            /// <summary>
            /// 传说中的主题选择，但是没卵用。
            /// </summary>
            [ConfigItem<int>("UiLauncherTheme", 0, ConfigSource.Local)] public partial int ThemeSelected { get; set; }
        }

        /// <summary>
        /// 背景内容。
        /// </summary>
        [ConfigGroup("Background")] partial class BackgroundConfigGroup
        {
            /// <summary>
            /// 背景来源类型（0 文件夹轮播，1 必应每日一图，2 指定图片文件）。
            /// </summary>
            [ConfigItem<int>("UiBackgroundSource", 0, ConfigSource.Local)] public partial int SourceType { get; set; }

            /// <summary>
            /// 指定的背景图片文件路径。
            /// </summary>
            [ConfigItem<string>("UiBackgroundImagePath", "", ConfigSource.Local)] public partial string LocalImagePath { get; set; }

            /// <summary>
            /// 上次使用的必应壁纸文件名。用于避免每次启动都显示同一张。
            /// </summary>
            [ConfigItem<string>("UiBackgroundBingLast", "", ConfigSource.Local)] public partial string BingLastImage { get; set; }

            /// <summary>
            /// 彩色底部填充。
            /// </summary>
            [ConfigItem<bool>("UiBackgroundColorful", true, ConfigSource.Local)] public partial bool BackgroundColorful { get; set; }

            /// <summary>
            /// 透明度。
            /// </summary>
            [ConfigItem<int>("UiBackgroundOpacity", 1000, ConfigSource.Local)] public partial int WallpaperOpacity { get; set; }

            /// <summary>
            /// 旋转。
            /// </summary>
            [ConfigItem<int>("UiBackgroundCarousel", 1000, ConfigSource.Local)] public partial int WallpaperCarousel { get; set; }

            /// <summary>
            /// 模糊遮罩。
            /// </summary>
            [ConfigItem<int>("UiBackgroundBlur", 0, ConfigSource.Local)] public partial int WallpaperBlurRadius { get; set; }

            /// <summary>
            /// 内容裁剪模式。
            /// </summary>
            [ConfigItem<int>("UiBackgroundSuit", 0, ConfigSource.Local)] public partial int WallpaperSuitMode { get; set; }

            /// <summary>
            /// 视频自动暂停。
            /// </summary>
            [ConfigItem<bool>("UiAutoPauseVideo", true, ConfigSource.Local)] public partial bool AutoPauseVideo { get; set; }
        }

        /// <summary>
        /// 高级材质。
        /// </summary>
        [ConfigGroup("Blur")] partial class BlurConfigGroup
        {
            /// <summary>
            /// 是否启用。
            /// </summary>
            [ConfigItem<bool>("UiBlur", false, ConfigSource.Local)] public partial bool IsEnabled { get; set; }

            /// <summary>
            /// 模糊半径。
            /// </summary>
            [ConfigItem<int>("UiBlurValue", 16, ConfigSource.Local)] public partial int Radius { get; set; }

            /// <summary>
            /// 采样率。
            /// </summary>
            [ConfigItem<int>("UiBlurSamplingRate", 70, ConfigSource.Local)] public partial int SamplingRate { get; set; }

            /// <summary>
            /// 模糊方法。
            /// </summary>
            [ConfigItem<int>("UiBlurType", 0, ConfigSource.Local)] public partial int KernelType { get; set; }
        }

        /// <summary>
        /// 毛玻璃。
        /// </summary>
        [ConfigGroup("Glass")] partial class GlassConfigGroup
        {
            /// <summary>
            /// 毛玻璃模糊强度。0 表示关闭。<br/>
            /// 作用于顶部导航栏与左侧菜单。
            /// </summary>
            [ConfigItem<int>("UiGlassValue", 0, ConfigSource.Local)] public partial int Radius { get; set; }

            /// <summary>
            /// 是否同时为卡片启用毛玻璃。<br/>
            /// 卡片数量较多，开启后每张卡片都需要单独抓取并模糊背景，性能开销显著，故默认关闭。
            /// </summary>
            [ConfigItem<bool>("UiGlassCard", false, ConfigSource.Local)] public partial bool CardEnabled { get; set; }

            /// <summary>
            /// 是否启用液态玻璃（折射 + 流动扭曲）。<br/>
            /// 该效果需要逐帧更新参数，开销明显高于普通毛玻璃，故默认关闭。
            /// </summary>
            [ConfigItem<bool>("UiGlassLiquid", false, ConfigSource.Local)] public partial bool LiquidEnabled { get; set; }

            /// <summary>
            /// 液态玻璃：折射强度。<br/>
            /// 着色器把 0~150 线性映射到「折射带宽的比例」0~0.55，滑块全程有效。<br/>
            /// 位移峰值有物理上限（超过会让采样映射反向、边缘出现镜像重影），
            /// 因此想要更强的效果应同时调高「边缘折射厚度」。
            /// </summary>
            [ConfigItem<int>("UiGlassLiquidRefraction", 120, ConfigSource.Local)] public partial int LiquidRefraction { get; set; }

            /// <summary>
            /// 液态玻璃：边缘折射厚度。
            /// </summary>
            [ConfigItem<int>("UiGlassLiquidThickness", 50, ConfigSource.Local)] public partial int LiquidRefThickness { get; set; }

            /// <summary>
            /// 液态玻璃：磨砂强度。<br/>
            /// 0 为完全通透（只保留折射，背景细节原样透出），满量程是一层薄磨砂。
            /// 着色器把 0~100 线性映射到模糊半径 0~8 像素（<c>BlurBorder.MaxLiquidBlurPx</c>）。
            /// </summary>
            [ConfigItem<int>("UiGlassLiquidBlur", 30, ConfigSource.Local)] public partial int LiquidBlur { get; set; }

            /// <summary>
            /// 液态玻璃：边缘高光强度。
            /// </summary>
            [ConfigItem<int>("UiGlassLiquidGlare", 50, ConfigSource.Local)] public partial int LiquidGlare { get; set; }
        }

        /// <summary>
        /// 自定义主页。
        /// </summary>
        [ConfigGroup("Homepage")] partial class HomepageConfigGroup
        {
            /// <summary>
            /// 主页来源类型。
            /// </summary>
            [ConfigItem<int>("UiCustomType", 0, ConfigSource.Local)] public partial int Type { get; set; }

            /// <summary>
            /// 预设选项。
            /// </summary>
            [ConfigItem<int>("UiCustomPreset", 0, ConfigSource.Local)] public partial int SelectedPreset { get; set; }

            /// <summary>
            /// 自定义 URL。
            /// </summary>
            [ConfigItem<string>("UiCustomNet", "", ConfigSource.Local)] public partial string CustomUrl { get; set; }
        }

        /// <summary>
        /// 背景音乐。
        /// </summary>
        [ConfigGroup("Music")] partial class MusicConfigGroup
        {
            /// <summary>
            /// 音量。
            /// </summary>
            [ConfigItem<int>("UiMusicVolume", 500, ConfigSource.Local)] public partial int Volume { get; set; }

            /// <summary>
            /// 启动游戏后自动暂停。
            /// </summary>
            [ConfigItem<bool>("UiMusicStop", false, ConfigSource.Local)] public partial bool StopInGame { get; set; }

            /// <summary>
            /// 启动游戏后自动开始播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicStart", false, ConfigSource.Local)] public partial bool StartInGame { get; set; }

            /// <summary>
            /// 自动开始播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicAuto", true, ConfigSource.Local)] public partial bool StartOnStartup { get; set; }

            /// <summary>
            /// 随机播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicRandom", true, ConfigSource.Local)] public partial bool ShufflePlayback { get; set; }

            /// <summary>
            /// 启用 SMTC。
            /// </summary>
            [ConfigItem<bool>("UiMusicSMTC", true, ConfigSource.Local)] public partial bool EnableSMTC { get; set; }
        }

        /// <summary>
        /// 功能隐藏。
        /// </summary>
        [ConfigGroup("Hide")]
        partial class HideConfigGroup
        {
            // 主页面
            [ConfigItem<bool>("UiHiddenPageDownload", false, ConfigSource.Local)] public partial bool PageDownload { get; set; }
            [ConfigItem<bool>("UiHiddenPageSetup", false, ConfigSource.Local)] public partial bool PageSetup { get; set; }
            [ConfigItem<bool>("UiHiddenPageTools", false, ConfigSource.Local)] public partial bool PageTools { get; set; }

            // 子页面 设置
            [ConfigItem<bool>("UiHiddenSetupLaunch", false, ConfigSource.Local)] public partial bool SetupLaunch { get; set; }
            [ConfigItem<bool>("UiHiddenSetupUi", false, ConfigSource.Local)] public partial bool SetupUi { get; set; }
            [ConfigItem<bool>("UiHiddenSetupLauncherLanguage", false, ConfigSource.Local)] public partial bool SetupLauncherLanguage { get; set; }
            [ConfigItem<bool>("UiHiddenSetupLauncherMisc", false, ConfigSource.Local)] public partial bool SetupLauncherMisc { get; set; }
            [ConfigItem<bool>("UiHiddenSetupGameManage", false, ConfigSource.Local)] public partial bool SetupGameManage { get; set; }
            [ConfigItem<bool>("UiHiddenSetupJava", false, ConfigSource.Local)] public partial bool SetupJava { get; set; }
            [ConfigItem<bool>("UiHiddenSetupUpdate", false, ConfigSource.Local)] public partial bool SetupUpdate { get; set; }
            [ConfigItem<bool>("UiHiddenSetupGameLink", false, ConfigSource.Local)] public partial bool SetupGameLink { get; set; } // 新增
            [ConfigItem<bool>("UiHiddenSetupAbout", false, ConfigSource.Local)] public partial bool SetupAbout { get; set; } // 修正名称
            [ConfigItem<bool>("UiHiddenSetupFeedback", false, ConfigSource.Local)] public partial bool SetupFeedback { get; set; } // 修正名称
            [ConfigItem<bool>("UiHiddenSetupLog", false, ConfigSource.Local)] public partial bool SetupLog { get; set; } // 修正名称

            // 子页面 工具
            [ConfigItem<bool>("UiHiddenToolsGameLink", false, ConfigSource.Local)] public partial bool ToolsGameLink { get; set; } // 新增
            [ConfigItem<bool>("UiHiddenToolsTest", false, ConfigSource.Local)] public partial bool ToolsTest { get; set; } // 新增

            // 子页面 实例设置
            [ConfigItem<bool>("UiHiddenVersionEdit", false, ConfigSource.Local)] public partial bool InstanceEdit { get; set; }
            [ConfigItem<bool>("UiHiddenVersionExport", false, ConfigSource.Local)] public partial bool InstanceExport { get; set; }
            [ConfigItem<bool>("UiHiddenVersionSave", false, ConfigSource.Local)] public partial bool InstanceSave { get; set; }
            [ConfigItem<bool>("UiHiddenVersionScreenshot", false, ConfigSource.Local)] public partial bool InstanceScreenshot { get; set; }
            [ConfigItem<bool>("UiHiddenVersionMod", false, ConfigSource.Local)] public partial bool InstanceMod { get; set; }
            [ConfigItem<bool>("UiHiddenVersionResourcePack", false, ConfigSource.Local)] public partial bool InstanceResourcePack { get; set; }
            [ConfigItem<bool>("UiHiddenVersionShader", false, ConfigSource.Local)] public partial bool InstanceShader { get; set; }
            [ConfigItem<bool>("UiHiddenVersionSchematic", false, ConfigSource.Local)] public partial bool InstanceSchematic { get; set; }
            [ConfigItem<bool>("UiHiddenVersionServer", false, ConfigSource.Local)] public partial bool InstanceServer { get; set; }

            // 特定功能
            [ConfigItem<bool>("UiHiddenFunctionSelect", false, ConfigSource.Local)] public partial bool FunctionSelect { get; set; }
            [ConfigItem<bool>("UiHiddenFunctionModUpdate", false, ConfigSource.Local)] public partial bool FunctionModUpdate { get; set; }
            [ConfigItem<bool>("UiHiddenFunctionHidden", false, ConfigSource.Local)] public partial bool FunctionHidden { get; set; }
        }
    }

    /// <summary>
    /// 启动配置。
    /// </summary>
    [ConfigGroup("Launch")] partial class LaunchConfigGroup
    {
        /// <summary>
        /// 内存分配模式。
        /// </summary>
        [ConfigItem<int>("LaunchRamType", 0, ConfigSource.Local)] public partial int MemoryAllocationMode { get; set; }

        /// <summary>
        /// 自定义内存分配大小。
        /// </summary>
        [ConfigItem<int>("LaunchRamCustom", 15, ConfigSource.Local)] public partial int CustomMemorySize { get; set; }

        /// <summary>
        /// 自定义初始堆大小（-Xms）的滑块刻度值，0 表示不主动设置。仅在手动分配模式下生效，且实际值不会超过最大堆。
        /// </summary>
        [ConfigItem<int>("LaunchRamCustomInitial", 0, ConfigSource.Local)] public partial int CustomInitialMemorySize { get; set; }

        /// <summary>
        /// 是否固定堆大小：启用后额外追加 -Xms 并使其等于 -Xmx，隐式禁用内存归还以降低延迟抖动、利于 ZGC。见 #3282。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceLockMemory", false, ConfigSource.Local)] public partial bool LockMemory { get; set; }

        /// <summary>
        /// 优先 IP 协议栈。
        /// </summary>
        [ConfigItem<JvmPreferredIpStack>("LaunchPreferredIpStack", JvmPreferredIpStack.Default)] public partial JvmPreferredIpStack PreferredIpStack { get; set; }

        /// <summary>
        /// 附加 JVM 参数。
        /// </summary>
        [ConfigItem<string>("LaunchAdvanceJvm", "-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy -XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=true -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True -Dlog4j2.formatMsgNoLookups=true", ConfigSource.Local)] public partial string JvmArgs { get; set; }

        /// <summary>
        /// 附加游戏参数。
        /// </summary>
        [ConfigItem<string>("LaunchAdvanceGame", "", ConfigSource.Local)] public partial string GameArgs { get; set; }

        /// <summary>
        /// 预启动指令。
        /// </summary>
        [ConfigItem<string>("LaunchAdvanceRun", "", ConfigSource.Local)] public partial string PreLaunchCommand { get; set; }

        /// <summary>
        /// 是否等待预启动指令完成。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceRunWait", true, ConfigSource.Local)] public partial bool PreLaunchCommandWait { get; set; }

        /// <summary>
        /// 禁用 Java Launch Wrapper。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceDisableJLW", true, ConfigSource.Local)] public partial bool DisableJlw { get; set; }

        /// <summary>
        /// 禁用 LegacyFix
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceDisableLF", false, ConfigSource.Local)] public partial bool DisableLF { get; set; }

        /// <summary>
        /// 强制使用高性能显卡。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceGraphicCard", true)] public partial bool SetGpuPreference { get; set; }

        /// <summary>
        /// 使用 java 而不是 javaw。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceNoJavaw", false)] public partial bool NoJavaw { get; set; }

        /// <summary>
        /// 禁用 LWJGL Unsafe Agent。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceDisableLwjglUnsafeAgent", false)] public partial bool DisableLwjglUnsafeAgent { get; set; }

        /// <summary>
        /// 禁用自动崩溃分析。
        /// </summary>
        [ConfigItem<bool>("LaunchAdvanceDisableCrashAnalysis", false, ConfigSource.Local)] public partial bool DisableCrashAnalysis { get; set; }

        /// <summary>
        /// 渲染器。
        /// </summary>
        [ConfigItem<int>("LaunchAdvanceRenderer", 0 ,ConfigSource.Local)] public partial int Renderer { get; set; }

        /// <summary>
        /// 游戏窗口标题。
        /// </summary>
        [ConfigItem<string>("LaunchArgumentTitle", "", ConfigSource.Local)] public partial string Title { get; set; }

        /// <summary>
        /// 自定义左下角版本信息。
        /// </summary>
        [ConfigItem<string>("LaunchArgumentInfo", "PCLCE", ConfigSource.Local)] public partial string TypeInfo { get; set; }

        /// <summary>
        /// 选择的默认 Java 实例。
        /// </summary>
        [ConfigItem<string>("LaunchArgumentJavaSelect", "")] public partial string SelectedJava { get; set; }

        /// <summary>
        /// 版本隔离 V2。
        /// </summary>
        [ConfigItem<int>("LaunchArgumentIndieV2", 4, ConfigSource.Local)] public partial int IndieSolutionV2 { get; set; }

        /// <summary>
        /// 游戏启动后启动器可见性。
        /// </summary>
        [ConfigItem<LauncherVisibility>("LaunchArgumentVisible", LauncherVisibility.DoNothing)] public partial LauncherVisibility LauncherVisibility { get; set; }

        /// <summary>
        /// 游戏进程优先级。
        /// </summary>
        [ConfigItem<GameProcessPriority>("LaunchArgumentPriority", GameProcessPriority.Normal)] public partial GameProcessPriority ProcessPriority { get; set; }

        /// <summary>
        /// 游戏窗口宽度。
        /// </summary>
        [ConfigItem<int>("LaunchArgumentWindowWidth", 854, ConfigSource.Local)] public partial int GameWindowWidth { get; set; }

        /// <summary>
        /// 游戏窗口高度。
        /// </summary>
        [ConfigItem<int>("LaunchArgumentWindowHeight", 480, ConfigSource.Local)] public partial int GameWindowHeight { get; set; }

        /// <summary>
        /// 游戏窗口模式 (正常/最大化/全屏)。
        /// </summary>
        [ConfigItem<GameWindowSizeMode>("LaunchArgumentWindowType", GameWindowSizeMode.Default, ConfigSource.Local)] public partial GameWindowSizeMode GameWindowMode { get; set; }

        /// <summary>
        /// 正版登录方式。
        /// </summary>
        [ConfigItem<int>("LoginMsAuthType", 1)] public partial int LoginMsAuthType { get; set; }
    }

    /// <summary>
    /// 实例独立配置。<br/>
    /// 懒得写注释了，自己理解吧。
    /// </summary>
    [ConfigGroup("Instance", ConfigSource.GameInstance)] partial class InstanceConfigGroup
    {
        [ConfigItem<string>("VersionAdvanceJvm", "")] public partial ArgConfig<string> JvmArgs { get; }
        [ConfigItem<string>("VersionAdvanceGame", "")] public partial ArgConfig<string> GameArgs { get; }
        [ConfigItem<int>("VersionAdvanceRenderer", 0)] public partial ArgConfig<int> Renderer { get; }
        [ConfigItem<int>("VersionAdvanceAssets", 0)] public partial ArgConfig<int> AssetVerifySolutionV1 { get; }
        [ConfigItem<bool>("VersionAdvanceAssetsV2", false)] public partial ArgConfig<bool> DisableAssetVerifyV2 { get; }
        [ConfigItem<bool>("VersionAdvanceJava", false)] public partial ArgConfig<bool> IgnoreJavaCompatibility { get; }
        [ConfigItem<bool>("VersionAdvanceDisableJlw", false)] public partial ArgConfig<bool> DisableJlwObsolete { get; }
        [ConfigItem<string>("VersionAdvanceRun", "")] public partial ArgConfig<string> PreLaunchCommand { get; }
        [ConfigItem<string>("VersionAdvanceClasspathHead", "")] public partial ArgConfig<string> ClasspathHead { get; }
        [ConfigItem<bool>("VersionAdvanceRunWait", true)] public partial ArgConfig<bool> PreLaunchCommandWait { get; }
        [ConfigItem<bool>("VersionAdvanceDisableJLW", false)] public partial ArgConfig<bool> DisableJlw { get; }
        [ConfigItem<bool>("VersionAdvanceDisableLwjglUnsafeAgent", false)] public partial ArgConfig<bool> DisableLwjglUnsafeAgent { get; }
        [ConfigItem<bool>("VersionAdvanceUseProxyV2", false)] public partial ArgConfig<bool> UseProxy { get; }
        [ConfigItem<bool>("VersionAdvanceDisableLF", false)] public partial ArgConfig<bool> DisableLF { get; }
        [ConfigItem<bool>("VersionUseDebugLog4j2Config", false)] public partial ArgConfig<bool> UseDebugLof4j2Config { get; }
        [ConfigItem<int>("VersionRamType", 2)] public partial ArgConfig<int> MemorySolution { get; }
        [ConfigItem<int>("VersionRamCustom", 15)] public partial ArgConfig<int> CustomMemorySize { get; }
        [ConfigItem<int>("VersionRamCustomInitial", 0)] public partial ArgConfig<int> CustomInitialMemorySize { get; }
        [ConfigItem<string>("VersionArgumentTitle", "")] public partial ArgConfig<string> Title { get; }
        [ConfigItem<bool>("VersionArgumentTitleEmpty", false)] public partial ArgConfig<bool> UseGlobalTitle { get; }
        [ConfigItem<string>("VersionArgumentInfo", "")] public partial ArgConfig<string> TypeInfo { get; }
        [ConfigItem<int>("VersionArgumentIndie", -1)] public partial ArgConfig<int> IndieV1 { get; }
        [ConfigItem<bool>("VersionArgumentIndieV2", false)] public partial ArgConfig<bool> IndieV2 { get; }
        [ConfigItem<string>("VersionArgumentJavaSelect", "使用全局设置")] public partial ArgConfig<string> SelectedJava { get; }
        [ConfigItem<string>("VersionServerEnter", "")] public partial ArgConfig<string> ServerToEnter { get; }
    }

    /// <summary>
    /// 实例独立配置的认证部分
    /// </summary>
    [ConfigGroup("InstanceAuth", ConfigSource.GameInstance)] partial class InstanceAuthConfigGroup
    {
        [ConfigItem<int>("VersionServerLoginRequire", 0)] public partial ArgConfig<int> LoginRequirementSolution { get; }
        [ConfigItem<string>("VersionServerAuthRegister", "")] public partial ArgConfig<string> AuthRegisterAddress { get; }
        [ConfigItem<string>("VersionServerAuthName", "")] public partial ArgConfig<string> AuthServerDisplayName { get; }
        [ConfigItem<string>("VersionServerAuthServer", "")] public partial ArgConfig<string> AuthServerAddress { get; }
        [ConfigItem<bool>("VersionServerLoginLock", false)] public partial ArgConfig<bool> AuthLocked { get; }
    }
}
