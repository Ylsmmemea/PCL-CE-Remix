using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Utils;

namespace PCL.Core.AI.ChatGpt;

// ChatGPT 订阅登录后存下来的凭据 字段名跟官方文档给的存储示例对齐
// 整串以加密方式写进配置 refresh token 会轮换 刷新时要整体替换
public sealed record ChatGptCredentials
{
    [JsonPropertyName("email")] public string Email { get; init; } = "";

    [JsonPropertyName("issuer")] public string Issuer { get; init; } = "";

    [JsonPropertyName("subject")] public string Subject { get; init; } = "";

    [JsonPropertyName("client_id")] public string ClientId { get; init; } = "";

    [JsonPropertyName("ext_agent_host_id")] public string HostId { get; init; } = "";

    [JsonPropertyName("id_token")] public string IdToken { get; init; } = "";

    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";

    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";

    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("scopes")] public string[] Scopes { get; init; } = [];

    [JsonPropertyName("saved_at")] public DateTimeOffset SavedAt { get; init; }

    // 留 60 秒余量 免得卡在过期那一瞬间
    [JsonIgnore] public bool IsExpired => DateTimeOffset.UtcNow.AddSeconds(60) >= ExpiresAt;

    [JsonIgnore] public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

    public static ChatGptCredentials? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<ChatGptCredentials>(json, JsonCompat.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonCompat.SerializerOptions);
}
