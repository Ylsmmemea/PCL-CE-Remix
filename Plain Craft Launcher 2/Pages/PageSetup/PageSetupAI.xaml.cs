using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.AI;
using PCL.Core.AI.ChatGpt;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupAI
{
    public PageSetupAI()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    // 从配置回填输入框 XAML 上的事件只负责写 不回读的话界面和实际存的对不上
    private void Reload()
    {
        TextAiApiUrl.Text = Config.Ai.ApiUrl;
        TextAiApiKey.Text = Config.Ai.ApiKey;
        PassAiApiKey.Password = Config.Ai.ApiKey;
        TextAiModel.Text = Config.Ai.Model;
        TextAiChatGptModel.Text = Config.Ai.ChatGptModel;

        ModAnimation.AniControlEnabled += 1;
        try
        {
            var chatGpt = Config.Ai.AuthMode == 1;
            RadioAiModeApiKey.SetChecked(!chatGpt, false);
            RadioAiModeChatGpt.SetChecked(chatGpt, false);
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }

        _ApplyMode();
    }

    // 两种认证方式区块互斥 只显示当前选的那个
    private void _ApplyMode()
    {
        var chatGpt = Config.Ai.AuthMode == 1;
        PanAiApiKey.Visibility = chatGpt ? Visibility.Collapsed : Visibility.Visible;
        PanAiChatGpt.Visibility = chatGpt ? Visibility.Visible : Visibility.Collapsed;
        // 登录按钮挪到了使用方式行 不再随区块显隐 得单独跟模式走
        BtnAiChatGpt.Visibility = chatGpt ? Visibility.Visible : Visibility.Collapsed;
        if (chatGpt) _RefreshChatGptState();
    }

    // 登录状态和按钮文案都跟着凭据走
    private void _RefreshChatGptState()
    {
        var credentials = ChatGptAuth.Current;
        var signedIn = credentials is { CanRefresh: true };

        LabAiChatGptState.Text = signedIn
            ? Lang.Text("Setup.Ai.ChatGpt.SignedInAs", credentials!.Email)
            : $"{Lang.Text("Setup.Ai.ChatGpt.NotSignedIn")}。{Lang.Text("Setup.Ai.ChatGpt.Hint")}";
        BtnAiChatGpt.Text = Lang.Text(signedIn ? "Setup.Ai.ChatGpt.SignOut" : "Setup.Ai.ChatGpt.SignIn");
    }

    private void RadioBoxChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        var sender = (MyRadioBox)senderRaw;
        var got = sender.Tag?.ToString()?.Split("/") ?? Array.Empty<string>();
        if (got.Length < 2) return;

        ConfigService.TrySetValue(got[0], int.Parse(got[1]));
        _ApplyMode();
    }

    // 密钥框当前该读哪个 打码时是 PasswordBox 明文时是 TextBox
    private string _KeyText => PassAiApiKey.Visibility == Visibility.Visible
        ? PassAiApiKey.Password
        : TextAiApiKey.Text;

    // 把输入框的值落进配置 保存和测试前都调 免得用户填完直接点测试却没生效
    // 两种模式各存各的 订阅能用的模型和 API 那边根本不是一套
    private void _ApplyInput()
    {
        if (Config.Ai.AuthMode == 1)
        {
            Config.Ai.ChatGptModel = TextAiChatGptModel.Text.Trim();
            return;
        }

        Config.Ai.ApiUrl = TextAiApiUrl.Text.Trim();
        Config.Ai.ApiKey = _KeyText.Trim();
        Config.Ai.Model = TextAiModel.Text.Trim();
    }

    private void _SetBusy(bool busy)
    {
        BtnAiSave.IsEnabled = !busy;
        BtnAiTest.IsEnabled = !busy;
        BtnAiFetchModels.IsEnabled = !busy;
        BtnAiChatGptFetchModels.IsEnabled = !busy;
        BtnAiToggleKey.IsEnabled = !busy;
        BtnAiChatGpt.IsEnabled = !busy;
        BtnAiTest.Text = busy ? Lang.Text("Setup.Ai.Testing") : Lang.Text("Setup.Ai.Test");
    }

    // 状态与报错走这一行 模型回复另有独立的框
    private void _ShowStatus(string text, bool isError)
    {
        LabAiResult.Text = text;
        LabAiResult.Visibility = Visibility.Visible;
        LabAiResult.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ColorBrushRedLight" : "ColorBrush2");
    }

    private void _ShowReply(string text)
    {
        LabAiReply.Text = text;
        PanAiReply.Visibility = Visibility.Visible;
    }

    // 点一下在明文和打码之间切 切之前把值抄到另一个控件上 免得切完内容没了
    private void BtnAiToggleKey_Click(object sender, EventArgs e)
    {
        var toMask = TextAiApiKey.Visibility == Visibility.Visible;
        if (toMask)
        {
            PassAiApiKey.Password = TextAiApiKey.Text;
            PassAiApiKey.Visibility = Visibility.Visible;
            TextAiApiKey.Visibility = Visibility.Collapsed;
        }
        else
        {
            TextAiApiKey.Text = PassAiApiKey.Password;
            TextAiApiKey.Visibility = Visibility.Visible;
            PassAiApiKey.Visibility = Visibility.Collapsed;
        }

        // 图标和提示写的都是点下去会做什么 不是当前状态
        BtnAiToggleKey.SvgIcon = toMask ? "lucide/eye" : "lucide/eye-off";
        BtnAiToggleKey.ToolTip = Lang.Text(toMask ? "Setup.Ai.ApiKey.Show" : "Setup.Ai.ApiKey.Hide");
    }

    private void BtnAiSave_Click(object sender, MouseButtonEventArgs e)
    {
        _ApplyInput();
        HintService.Hint(Lang.Text("Setup.Ai.Saved"), HintType.Success);
    }

    // 一个按钮两用 已登录就是退出 没登录就是走一遍授权
    private void BtnAiChatGpt_Click(object sender, MouseButtonEventArgs e)
    {
        if (ChatGptAuth.Current is { CanRefresh: true })
        {
            ChatGptAuth.SignOut();
            _RefreshChatGptState();
            HintService.Hint(Lang.Text("Setup.Ai.ChatGpt.SignOut"), HintType.Success);
            return;
        }

        _SetBusy(true);
        BtnAiChatGpt.Text = Lang.Text("Setup.Ai.ChatGpt.SigningIn");

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var credentials = ChatGptAuth.SignInAsync().GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    _SetBusy(false);
                    _RefreshChatGptState();
                    // 上一次失败留下的红字得清掉 不然登录成功了下面还挂着旧报错
                    LabAiResult.Visibility = Visibility.Collapsed;
                    HintService.Hint(Lang.Text("Setup.Ai.ChatGpt.SignedInAs", credentials.Email), HintType.Success);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[AI] ChatGPT 登录失败");
                var message = ex.Message;
                ModBase.RunInUi(() =>
                {
                    _SetBusy(false);
                    _RefreshChatGptState();
                    _ShowStatus(message, true);
                    HintService.Hint(Lang.Text("Setup.Ai.Error.SignInFailed"), HintType.Error);
                });
            }
        });
    }

    private void BtnAiTest_Click(object sender, MouseButtonEventArgs e)
    {
        _ApplyInput();
        _SetBusy(true);
        PanAiReply.Visibility = Visibility.Collapsed;
        _ShowStatus(Lang.Text("Setup.Ai.Testing"), false);

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var reply = AiClient.TestAsync().GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    LabAiResult.Visibility = Visibility.Collapsed;
                    _ShowReply(string.IsNullOrWhiteSpace(reply) ? Lang.Text("Setup.Ai.Test.EmptyReply") : reply);
                    HintService.Hint(Lang.Text("Setup.Ai.Test.Success"), HintType.Success);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[AI] 连通性测试失败");
                var message = ex.Message;
                ModBase.RunInUi(() =>
                {
                    _ShowStatus(message, true);
                    HintService.Hint(Lang.Text("Setup.Ai.Test.Failed"), HintType.Error);
                });
            }
            finally
            {
                ModBase.RunInUi(() => _SetBusy(false));
            }
        });
    }

    private void BtnAiFetchModels_Click(object sender, MouseButtonEventArgs e)
    {
        _ApplyInput();
        _SetBusy(true);

        ModBase.RunInNewThread(() =>
        {
            List<string> models = [];
            string? error = null;
            try
            {
                models = AiClient.ListModelsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[AI] 获取模型列表失败");
                error = ex.Message;
            }

            ModBase.RunInUi(() =>
            {
                _SetBusy(false);

                if (error is not null)
                {
                    _ShowStatus(error, true);
                    HintService.Hint(Lang.Text("Setup.Ai.FetchModels.Failed"), HintType.Error);
                    return;
                }

                if (models.Count == 0)
                {
                    HintService.Hint(Lang.Text("Setup.Ai.FetchModels.Empty"), HintType.Error);
                    return;
                }

                var options = new List<IMyRadio>();
                foreach (var id in models)
                    options.Add(new MyRadioBox { Text = id });

                // 弹窗是模态的 必须在 UI 线程上调
                var index = ModMain.MyMsgBoxSelect(options, Lang.Text("Setup.Ai.SelectModel.Title"),
                    button2: Lang.Text("Common.Action.Cancel"));
                if (index is null)
                    return;

                // 两个模式各有一个模型框 要写进当前显示的那个
                var picked = models[index.Value];
                if (Config.Ai.AuthMode == 1) TextAiChatGptModel.Text = picked;
                else TextAiModel.Text = picked;

                _ApplyInput();
                HintService.Hint(Lang.Text("Setup.Ai.Model.Selected", models[index.Value]), HintType.Success);
            });
        });
    }
}
