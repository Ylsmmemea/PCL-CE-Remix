using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.AI;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls.SvgIcon;

namespace PCL;

// 挂在对话气泡上的附件 界面上只显示名字和大小 内容照样随请求发出去
// 因为除了 OpenAI 系 大多数服务商根本没有文件上传接口
public sealed record AiAttachment(string Name, string Content)
{
    public long Size => System.Text.Encoding.UTF8.GetByteCount(Content);
}

public partial class AiChatPanel
{
    private const string AnimationKey = "AiChatPanel";
    private const int PanelWidth = 340;

    // 附件一次最多先显示这么多 剩下的折在一行灰字后面
    private const int CollapsedAttachmentCount = 3;

    private readonly List<AiChatMessage> _history = [];
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _streamStarted;

    public AiChatPanel()
    {
        InitializeComponent();
        // 内层宽度只有这一个来源 别再往 XAML 上写 Width
        PanPanelContent.Width = PanelWidth;
    }

    // 开始一轮新的分析 上一轮的对话直接丢掉
    public void Start(string title, IReadOnlyList<AiAttachment> attachments, string prompt)
    {
        _cts?.Cancel();
        LabTitle.Text = title;
        _history.Clear();
        PanAiMessages.Children.Clear();
        _SlideIn();

        _AppendAttachments(attachments);
        _AppendBubble(true, prompt);
        _history.Add(new AiChatMessage("user", _ComposeFirstMessage(attachments, prompt)));
        _RunRequest();
    }

    public void Close()
    {
        _cts?.Cancel();
        _SlideOut();
    }

    #region 显示与隐藏

    // 动的是自身宽度 由 Grid 的第二列撑开 第一列的内容跟着变窄
    // 注意 AaDouble 的 value 是增量不是目标值
    private void _SlideIn()
    {
        Visibility = Visibility.Visible;
        Width = 0;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaDouble(this, WidthProperty, PanelWidth, 260, 0,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
        }, AnimationKey);
    }

    private void _SlideOut()
    {
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaDouble(this, WidthProperty, -Width, 220, 0,
                new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => Visibility = Visibility.Collapsed, 250, true)
        }, AnimationKey);
    }

    private void BtnAiPanelClose_Click(object sender, EventArgs e)
    {
        Close();
    }

    #endregion

    #region 对话

    // 附件在界面上是附件 发出去时仍得把正文并进消息里
    private static string _ComposeFirstMessage(IReadOnlyList<AiAttachment> attachments, string prompt)
    {
        var text = new System.Text.StringBuilder(prompt);
        foreach (var attachment in attachments)
        {
            text.AppendLine();
            text.AppendLine();
            text.AppendLine($"--- {attachment.Name} ---");
            text.Append(attachment.Content);
        }

        return text.ToString();
    }

    private void _Submit()
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        var text = TextAiPanelInput.Text.Trim();
        TextAiPanelInput.Clear();
        _Send(text);
    }

    private void _Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _busy) return;

        _AppendBubble(true, text);
        _history.Add(new AiChatMessage("user", text));
        _RunRequest();
    }

    private void _RunRequest()
    {
        _SetBusy(true);

        var bubble = _AppendBubble(false, Lang.Text("Setup.AiPanel.Thinking"));
        _streamStarted = false;
        var snapshot = _history.ToList();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            try
            {
                var reply = await AiClient
                    .ChatAsync(snapshot, AiPrompts.CrashAnalysis, delta => _AppendDelta(bubble, delta), token)
                    .ConfigureAwait(false);

                // 与增量更新用同一个优先级 不然会插队跑到还没执行的增量前面去 内容会重复一遍
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    // 上游不按流式回的话前面一段都没收到 这里一次性贴上去
                    if (!_streamStarted) bubble.Set(reply);
                    if (string.IsNullOrWhiteSpace(bubble.Raw))
                        bubble.Set(Lang.Text("Setup.Ai.Test.EmptyReply"));

                    _history.Add(new AiChatMessage("assistant", reply));
                    _SetBusy(false);
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    // 已经吐出来的部分留着 也塞进历史 免得下一轮答非所问
                    if (_streamStarted && !string.IsNullOrWhiteSpace(bubble.Raw))
                    {
                        bubble.Set(bubble.Raw);
                        _history.Add(new AiChatMessage("assistant", bubble.Raw));
                    }
                    else
                    {
                        PanAiMessages.Children.Remove(bubble.Box);
                    }

                    _SetBusy(false);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[AI] 崩溃分析请求失败");
                var message = ex.Message;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    bubble.SetError(message);
                    _SetBusy(false);
                });
            }
        });
    }

    // 这个是在后台线程上被调的 状态位要当场改掉 别放进 UI 回调里
    private void _AppendDelta(Bubble bubble, string delta)
    {
        var first = !_streamStarted;
        _streamStarted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (first) bubble.Set("");
            bubble.Append(delta);
            _ScrollToEnd();
        });
    }

    private void _SetBusy(bool busy)
    {
        _busy = busy;
        BtnAiPanelSend.Text = busy ? Lang.Text("Setup.AiPanel.Stop") : Lang.Text("Setup.AiPanel.Send");
        TextAiPanelInput.IsEnabled = !busy;
        if (!busy) TextAiPanelInput.Focus();
    }

    private void BtnAiPanelSend_Click(object sender, MouseButtonEventArgs e)
    {
        _Submit();
    }

    private void TextAiPanelInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        _Submit();
    }

    #endregion

    #region 气泡

    // 助手气泡走 Markdown 用户气泡保持纯文本 用户可能贴进来一堆带符号的原文 渲染了反而变味
    private sealed class Bubble(Border box, TextBlock? plain, Markdig.Wpf.MarkdownViewer? markdown)
    {
        // 每来一小段就重新解析一遍 markdown 太浪费 流式期间按时间节流
        private const int RenderIntervalMs = 120;

        private string _raw = "";
        private int _lastRender = Environment.TickCount;

        public Border Box { get; } = box;

        public string Raw => _raw;

        public void Set(string text)
        {
            _raw = text;
            _Render();
        }

        public void Append(string delta)
        {
            _raw += delta;
            if (Environment.TickCount - _lastRender < RenderIntervalMs) return;
            _Render();
        }

        public void SetError(string message)
        {
            _raw = message;
            _Render();
            if (markdown is not null)
                markdown.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
        }

        private void _Render()
        {
            _lastRender = Environment.TickCount;
            if (plain is not null) plain.Text = _raw;
            else if (markdown is not null) markdown.Markdown = _raw;
        }
    }

    private Bubble _AppendBubble(bool isUser, string text)
    {
        var box = new Border
        {
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(isUser ? 40 : 0, 0, isUser ? 0 : 40, 9),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch
        };
        box.SetResourceReference(Border.BackgroundProperty, isUser ? "ColorBrush5" : "ColorBrush7");

        Bubble bubble;
        if (isUser)
        {
            var label = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            box.MaxWidth = 296;
            box.Child = label;
            bubble = new Bubble(box, label, null);
        }
        else
        {
            // 助手气泡撑满可用宽度 段落和代码块才有地方换行 靠右对齐的窄气泡会把 markdown 挤变形
            var viewer = new Markdig.Wpf.MarkdownViewer
            {
                Markdown = text,
                VerticalAlignment = VerticalAlignment.Top
            };
            viewer.CommandBindings.Add(
                new CommandBinding(Markdig.Wpf.Commands.Hyperlink, _OpenHyperlink));
            box.Child = viewer;
            bubble = new Bubble(box, null, viewer);
        }

        PanAiMessages.Children.Add(box);
        _ScrollToEnd();
        return bubble;
    }

    // 只放行 http(s) 日志里什么字符串都有 别让它们变成能点的东西
    private static void _OpenHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        var url = e.Parameter?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ModBase.Log("[AI] 已忽略非 http(s) 的链接：" + url, ModBase.LogLevel.Developer);
            return;
        }

        ModBase.OpenWebsite(url);
    }

    // 附件多的时候一屏全是文件名 先只铺前几个 剩下的折起来
    private void _AppendAttachments(IReadOnlyList<AiAttachment> attachments)
    {
        var shown = Math.Min(CollapsedAttachmentCount, attachments.Count);
        for (var i = 0; i < shown; i++)
            PanAiMessages.Children.Add(_CreateAttachmentChip(attachments[i]));

        if (attachments.Count <= shown) return;

        var rest = attachments.Skip(shown).ToList();
        var tip = new TextBlock
        {
            Text = Lang.Text("Setup.AiPanel.ExpandAttachments", rest.Count),
            FontSize = 12,
            Margin = new Thickness(0, 2, 6, 9),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        tip.MouseLeftButtonUp += (_, _) =>
        {
            var index = PanAiMessages.Children.IndexOf(tip);
            PanAiMessages.Children.Remove(tip);
            foreach (var attachment in rest)
                PanAiMessages.Children.Insert(index++, _CreateAttachmentChip(attachment));
            _ScrollToEnd();
        };

        PanAiMessages.Children.Add(tip);
        _ScrollToEnd();
    }

    private Border _CreateAttachmentChip(AiAttachment attachment)
    {
        var icon = new SvgIcon
        {
            Icon = "lucide/file-text",
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        icon.SetResourceReference(SvgIcon.IconBrushProperty, "ColorBrush2");

        // 分三列 名字占剩余宽度并允许截断 大小固定在右边
        // 不然长文件名会把大小整个顶出去
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(icon);

        var name = new TextBlock
        {
            Text = attachment.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        line.Children.Add(name);

        var size = new TextBlock
        {
            Text = _FormatSize(attachment.Size),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            Opacity = 0.7
        };
        Grid.SetColumn(size, 2);
        line.Children.Add(size);

        var box = new Border
        {
            Child = line,
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(40, 0, 0, 9),
            HorizontalAlignment = HorizontalAlignment.Right,
            // 不给上限的话超长名字会把整条顶出面板 星号列也就失去意义了
            MaxWidth = PanelWidth - 40
        };
        box.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
        box.SetResourceReference(Border.BorderBrushProperty, "ColorBrush5");
        return box;
    }

    private static string _FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("F1") + " KB";
        return (bytes / 1024d / 1024d).ToString("F1") + " MB";
    }

    private void _ScrollToEnd()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => PanAiScroll.ScrollToEnd());
    }

    #endregion
}
