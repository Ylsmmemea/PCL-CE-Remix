using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.AI.ChatGpt;

// 收 OAuth 回调的本地回环服务
// 只监听 127.0.0.1 不能带 IPv6 loopback 也不能用 localhost OpenAI 那边只认这个
internal sealed class ChatGptCallbackServer : HttpServer
{
    private readonly TaskCompletionSource<IDictionary<string, string>> _callback =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatGptCallbackServer() : base([IPAddress.Loopback])
    {
    }

    // 授权请求和换 token 必须用同一个地址 端口每次都换但路径不能动
    public string RedirectUri => $"http://127.0.0.1:{Port}/auth/callback";

    public async Task<IDictionary<string, string>> WaitAsync(CancellationToken token)
        => await _callback.Task.WaitAsync(token).ConfigureAwait(false);

    protected override void Init()
    {
        Register(HttpMethod.Get, "/auth/callback", request =>
        {
            var parameters = new Dictionary<string, string>();
            var query = request.QueryString;
            foreach (var key in query.AllKeys)
                if (key is not null)
                    parameters[key] = query[key] ?? "";

            _callback.TrySetResult(parameters);

            var html = "<!doctype html><meta charset=\"utf-8\">" +
                       $"<body style=\"font-family:system-ui;padding:48px\">{Lang.Text("Setup.Ai.ChatGpt.BrowserDone")}";
            return Task.FromResult(HttpRouteResponse.Text(html, "text/html", Encoding.UTF8));
        });
    }
}
