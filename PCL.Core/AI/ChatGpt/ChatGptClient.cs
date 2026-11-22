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
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL.Core.AI.ChatGpt;

// 用 ChatGPT 订阅额度跑推理 走 Responses API
// 跟 API Key 那条路的区别 端点不同 流式事件不同 而且每次必须 store false 加 stream true
public static class ChatGptClient
{
    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<string> ChatAsync(
        IReadOnlyList<AiChatMessage> history,
        string systemPrompt,
        Action<string> onDelta,
        CancellationToken token = default)
    {
        var model = (Config.Ai.ChatGptModel ?? "").Trim();
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException(Lang.Text("Setup.Ai.Error.NoModel"));

        var accessToken = await ChatGptAuth.GetAccessTokenAsync(token).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Timeout);

        var payload = new ResponseRequest
        {
            Model = model,
            Instructions = systemPrompt,
            Input = history.Select(message => new ResponseMessage
            {
                Role = message.Role,
                Content = message.Content
            }).ToArray()
        };

        using var response = await HttpRequest.CreatePost(ResponsesEndpoint)
            .WithBearerToken(accessToken)
            .WithJsonContent(payload)
            .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseHeadersRead, retryTimes: 0,
                cancellationToken: cts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            var body = await response.AsStringAsync(cts.Token).ConfigureAwait(false);
            throw new InvalidOperationException(_DescribeError(response, body));
        }

        return await _ReadStreamAsync(response, onDelta, cts.Token).ConfigureAwait(false);
    }

    public static async Task<List<string>> ListModelsAsync(CancellationToken token = default)
    {
        var accessToken = await ChatGptAuth.GetAccessTokenAsync(token).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Timeout);

        using var response = await HttpRequest.Create(ModelsEndpoint)
            .WithBearerToken(accessToken)
            .SendAsync(retryTimes: 0, cancellationToken: cts.Token)
            .ConfigureAwait(false);

        var body = await response.AsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidOperationException(_DescribeError(response, body));

        var parsed = JsonSerializer.Deserialize<ModelListResponse>(body, JsonCompat.SerializerOptions);
        return parsed?.Models?
                   .Where(entry => entry.Visibility == "list")
                   .Select(entry => entry.Slug)
                   .Where(slug => !string.IsNullOrWhiteSpace(slug))
                   .Select(slug => slug!)
                   .ToList()
               ?? [];
    }

    // 事件按 data 里的 type 分派 只有收到 response.completed 才算成功
    private static async Task<string> _ReadStreamAsync(
        HttpResponseMessage response,
        Action<string> onDelta,
        CancellationToken token)
    {
        var text = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;

            ResponseEvent? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ResponseEvent>(json, JsonCompat.SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (parsed?.Type)
            {
                case "response.output_text.delta" when !string.IsNullOrEmpty(parsed.Delta):
                    text.Append(parsed.Delta);
                    onDelta(parsed.Delta);
                    break;

                case "response.completed":
                    return text.ToString();

                case "response.failed":
                    throw new InvalidOperationException(_DescribeFailure(parsed));

                case "response.incomplete":
                    throw new InvalidOperationException(_DescribeIncomplete(parsed));
            }
        }

        // 流断在半路 已经收到的部分留着 由调用方决定怎么处理
        return text.ToString();
    }

    private static string _DescribeFailure(ResponseEvent parsed)
    {
        var code = parsed.Response?.Error?.Code;
        return code switch
        {
            "subscription_sharing_usage_limit_exceeded" => Lang.Text("Setup.Ai.Error.UsageLimitExceeded"),
            "subscription_sharing_usage_unavailable" => Lang.Text("Setup.Ai.Error.UsageUnavailable"),
            _ => parsed.Response?.Error?.Message ?? Lang.Text("Setup.Ai.Test.Failed")
        };
    }

    private static string _DescribeIncomplete(ResponseEvent parsed)
    {
        var reason = parsed.Response?.IncompleteDetails?.Reason;
        return string.IsNullOrWhiteSpace(reason)
            ? Lang.Text("Setup.Ai.Test.Failed")
            : $"{Lang.Text("Setup.Ai.Test.Failed")}：{reason}";
    }

    private static string _DescribeError(HttpResponseMessage response, string body)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ErrorResponse>(body, JsonCompat.SerializerOptions)
                ?.Error?.Message;
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

    #region 报文结构

    private sealed record ResponseRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("instructions")] public string Instructions { get; init; } = "";
        [JsonPropertyName("input")] public ResponseMessage[] Input { get; init; } = [];

        // 这两项是这条通道的硬要求 少了会被拒
        [JsonPropertyName("store")] public bool Store => false;
        [JsonPropertyName("stream")] public bool Stream => true;
    }

    private sealed record ResponseMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("content")] public string Content { get; init; } = "";
    }

    private sealed record ResponseEvent
    {
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("delta")] public string? Delta { get; init; }
        [JsonPropertyName("response")] public ResponseSnapshot? Response { get; init; }

        public sealed record ResponseSnapshot
        {
            [JsonPropertyName("error")] public ResponseFailure? Error { get; init; }
            [JsonPropertyName("incomplete_details")] public IncompleteReason? IncompleteDetails { get; init; }

            public sealed record ResponseFailure
            {
                [JsonPropertyName("code")] public string? Code { get; init; }
                [JsonPropertyName("message")] public string? Message { get; init; }
            }

            public sealed record IncompleteReason
            {
                [JsonPropertyName("reason")] public string? Reason { get; init; }
            }
        }
    }

    private sealed record ModelListResponse
    {
        [JsonPropertyName("models")] public ModelEntry[]? Models { get; init; }

        public sealed record ModelEntry
        {
            [JsonPropertyName("slug")] public string? Slug { get; init; }
            [JsonPropertyName("visibility")] public string? Visibility { get; init; }
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
