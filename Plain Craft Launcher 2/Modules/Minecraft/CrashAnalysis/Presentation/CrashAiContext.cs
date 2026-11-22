using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.App.Localization;

namespace PCL;

// 打包好的一份待发内容 界面上按附件显示 正文发出去之前先把用户名抹掉
internal sealed record CrashAiPayload(string FileName, string Content);

// 把崩溃现场拆成几份文件交给 AI 而不是糊成一大段
internal static partial class CrashAiContext
{
    // 每个文件各自的上限 崩溃原因基本都在尾部
    private const int CrashReportLimit = 40 * 1024;
    private const int GameLogLimit = 20 * 1024;
    private const int OtherLimit = 16 * 1024;

    private const string TailMarker = "……（更早的内容已省略）\n";

    public static List<CrashAiPayload> Build(CrashAnalysisContext context, IEnumerable<string>? extraFiles)
    {
        var result = new List<CrashAiPayload>();

        _AppendLauncherConclusion(result, context);

        // 直接用导出报告那一套 用户能导出的文件 AI 都能看到 不漏环境信息和模组列表
        foreach (var (fileName, content) in CrashReportExporter.BuildFiles(context, extraFiles))
        {
            if (string.IsNullOrWhiteSpace(content)) continue;
            result.Add(new CrashAiPayload(fileName, _Tail(content, _LimitOf(fileName))));
        }

        // 一个文件都没攒出来时退回整包日志 免得空着手去问 AI
        if (result.Count <= 1 && !string.IsNullOrWhiteSpace(context.LogAll))
            result.Add(new CrashAiPayload(Lang.Text("Setup.AiPanel.AttachmentName"), _Tail(context.LogAll, GameLogLimit)));

        return result;
    }

    private static int _LimitOf(string fileName)
    {
        if (fileName.Contains("crash", StringComparison.OrdinalIgnoreCase)) return CrashReportLimit;
        if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return GameLogLimit;
        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return GameLogLimit;
        return OtherLimit;
    }

    // 规则引擎已经得出「原因 + 建议」了 体量小又准 单独当一份文件给 AI 当线索
    private static void _AppendLauncherConclusion(ICollection<CrashAiPayload> target, CrashAnalysisContext context)
    {
        try
        {
            var text = new StringBuilder();
            if (context.Instance is { } instance)
                text.AppendLine($"实例：{instance.Name}");

            // 传 true 是为了去掉「查看错误报告」那类给用户看的尾巴 对 AI 没意义
            var conclusion = new CrashResultFormatter().Format(context, true).Text;
            if (string.IsNullOrWhiteSpace(conclusion)) return;

            if (text.Length > 0) text.AppendLine();
            text.AppendLine("启动器初步分析：");
            text.Append(conclusion);

            target.Add(new CrashAiPayload(Lang.Text("Setup.AiPanel.LauncherAnalysis"), Mask(text.ToString())));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[AI] 读取启动器分析结论失败");
        }
    }

    // 只留尾部 前面补一行说明 免得模型以为日志本来就只这么长
    private static string _Tail(string text, int limit)
    {
        var masked = Mask(text);
        return masked.Length <= limit ? masked : TailMarker + masked[^limit..];
    }

    // 用户目录里的用户名跟崩溃原因基本无关 没必要发给第三方
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var masked = WindowsUserDirectory().Replace(text, "$1<user>");
        return PosixUserDirectory().Replace(masked, "$1<user>");
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserDirectory();

    [GeneratedRegex(@"(/(?:home|Users)/)[^/\s""']+")]
    private static partial Regex PosixUserDirectory();
}
