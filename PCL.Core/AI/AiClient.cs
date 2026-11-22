using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.AI.ChatGpt;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL.Core.AI;

public sealed record AiChatMessage(string Role, string Content);

// OpenAI 兼容接口的调用入口 配置从 Config.Ai 读 出错一律抛 InvalidOperationException
public static class AiClient
{
    private const string TestMessage = "Hi";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // 对话要一次生成一大段 20 秒不够
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(2);

    // 基址去空格去尾斜杠再拼路径 拼出来的必须是个 http 或 https 的绝对地址
    private static string _BuildUrl(string path)
    {
        var baseUrl = (Config.Ai.ApiUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoUrl"));

        if (!Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.InvalidUrl"));

        return uri.AbsoluteUri;
    }

    private static string _RequireKey()
    {
        var key = (Config.Ai.ApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoKey"));

        return key;
    }

    // 优先掏服务端的 error.message 那是 401 之类的真正原因 掏不到就退回状态码加原文
    private static string _DescribeError(System.Net.Http.HttpResponseMessage response, string body)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ErrorResponse>(body, JsonCompat.SerializerOptions)?.Error?.Message;
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
            // 不是 JSON 就走下面的兜底
        }

        var detail = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 200) detail = detail[..200] + "…";
        return string.IsNullOrEmpty(detail)
            ? Lang.Text("Setup.Ai.Error.Http", (int)response.StatusCode)
            : Lang.Text("Setup.Ai.Error.HttpWithMessage", (int)response.StatusCode, detail);
    }

    private static CancellationTokenSource _LinkTimeout(CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Timeout);
        return cts;
    }

    // 发一句 Hi 过去 返回模型回的内容
    public static Task<string> TestAsync(CancellationToken token = default)
    {
        // 订阅模式走 Responses API 顺带把登录状态也验了
        if (Config.Ai.AuthMode == 1)
        {
            return ChatGptClient.ChatAsync([new AiChatMessage("user", TestMessage)], AiPrompts.CrashAnalysis,
                _ => { }, token);
        }

        return _ChatCompletionsTestAsync(token);
    }

    private static async Task<string> _ChatCompletionsTestAsync(CancellationToken token = default)
    {
        var model = (Config.Ai.Model ?? "").Trim();
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoModel"));

        var payload = new ChatRequest
        {
            Model = model,
            Messages = [new ChatMessage { Role = "user", Content = TestMessage }]
        };

        using var cts = _LinkTimeout(token);
        // 连通性测试不重试 默认那三次重试要白等好几秒 失败就让用户马上看到原因
        using var response = await HttpRequest.CreatePost(_BuildUrl("/chat/completions"))
            .WithBearerToken(_RequireKey())
            .WithJsonContent(payload)
            .SendAsync(retryTimes: 0, cancellationToken: cts.Token)
            .ConfigureAwait(false);

        var body = await response.AsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidOperationException(_DescribeError(response, body));

        var parsed = JsonSerializer.Deserialize<ChatResponse>(body, JsonCompat.SerializerOptions);
        return parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    // 流式对话 按设置里选的方式分派
    public static Task<string> ChatAsync(
        IReadOnlyList<AiChatMessage> history,
        string systemPrompt,
        Action<string> onDelta,
        CancellationToken token = default)
    {
        if (Config.Ai.AuthMode == 1)
            return ChatGptClient.ChatAsync(history, systemPrompt, onDelta, token);

        return _ChatCompletionsChatAsync(history, systemPrompt, onDelta, token);
    }

    private static async Task<string> _ChatCompletionsChatAsync(
        IReadOnlyList<AiChatMessage> history,
        string systemPrompt,
        Action<string> onDelta,
        CancellationToken token = default)
    {
        var model = (Config.Ai.Model ?? "").Trim();
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoModel"));

        var messages = new List<ChatMessage> { new() { Role = "system", Content = systemPrompt } };
        messages.AddRange(history.Select(item => new ChatMessage { Role = item.Role, Content = item.Content }));

        var payload = new ChatRequest { Model = model, Messages = messages.ToArray(), Stream = true };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(ChatTimeout);

        // 流式必须用 ResponseHeadersRead 否则会等整个响应体读完才返回
        using var response = await HttpRequest.CreatePost(_BuildUrl("/chat/completions"))
            .WithBearerToken(_RequireKey())
            .WithJsonContent(payload)
            .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseHeadersRead, retryTimes: 0,
                cancellationToken: cts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            var errorBody = await response.AsStringAsync(cts.Token).ConfigureAwait(false);
            throw new InvalidOperationException(_DescribeError(response, errorBody));
        }

        var result = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            string? delta;
            try
            {
                delta = JsonSerializer.Deserialize<ChatChunk>(data, JsonCompat.SerializerOptions)
                    ?.Choices?.FirstOrDefault()?.Delta?.Content;
            }
            catch (JsonException)
            {
                // 有的实现会往里插心跳或注释行 解不出来就跳过
                continue;
            }

            if (string.IsNullOrEmpty(delta)) continue;
            result.Append(delta);
            onDelta(delta);
        }

        return result.ToString();
    }

    // 拉上游支持的模型名 走 OpenAI 的 /models
    public static Task<List<string>> ListModelsAsync(CancellationToken token = default)
    {
        if (Config.Ai.AuthMode == 1)
            return ChatGptClient.ListModelsAsync(token);

        return _ChatCompletionsListModelsAsync(token);
    }

    private static async Task<List<string>> _ChatCompletionsListModelsAsync(CancellationToken token = default)
    {
        using var cts = _LinkTimeout(token);
        using var response = await HttpRequest.Create(_BuildUrl("/models"))
            .WithBearerToken(_RequireKey())
            .SendAsync(retryTimes: 0, cancellationToken: cts.Token)
            .ConfigureAwait(false);

        var body = await response.AsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidOperationException(_DescribeError(response, body));

        var parsed = JsonSerializer.Deserialize<ModelListResponse>(body, JsonCompat.SerializerOptions);
        return parsed?.Data?
                   .Select(entry => entry.Id)
                   .Where(id => !string.IsNullOrWhiteSpace(id))
                   .Select(id => id!)
                   .ToList()
               ?? [];
    }

    #region 报文结构

    private sealed record ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("content")] public string? Content { get; init; }
    }

    private sealed record ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; init; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    private sealed record ChatChunk
    {
        [JsonPropertyName("choices")] public ChunkChoice[]? Choices { get; init; }

        public sealed record ChunkChoice
        {
            [JsonPropertyName("delta")] public ChatMessage? Delta { get; init; }
        }
    }

    private sealed record ChatResponse
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; init; }

        public sealed record Choice
        {
            [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
        }
    }

    private sealed record ModelListResponse
    {
        [JsonPropertyName("data")] public ModelEntry[]? Data { get; init; }

        public sealed record ModelEntry
        {
            [JsonPropertyName("id")] public string? Id { get; init; }
        }
    }

    private sealed record ErrorResponse
    {
        [JsonPropertyName("error")] public ErrorBody? Error { get; init; }

        public sealed record ErrorBody
        {
            [JsonPropertyName("message")] public string? Message { get; init; }
        }
    }

    #endregion
}
