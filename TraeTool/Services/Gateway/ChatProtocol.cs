using System.Text.Json;
using System.Text.Json.Nodes;

namespace TraeTool.Services.Gateway;

/// <summary>解析后的对话请求。</summary>
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool Stream,
    double? Temperature,
    int? MaxTokens)
{
    /// <summary>取最后一条用户消息（上游适配器用作 prompt）。</summary>
    public string LastUserMessage =>
        Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
}

public sealed record ChatMessage(string Role, string Content);

/// <summary>上游返回：内容、结束原因、模型名。</summary>
public sealed record ChatCompletion(string Content, string FinishReason, string Model);

/// <summary>上游错误（转发层据此回 OpenAI 形状的错误）。</summary>
public sealed record ChatUpstreamError(string Message, string Type, int Status);

/// <summary>
/// OpenAI 兼容协议的解析与序列化。所有输入输出均为 JSON 字符串，
/// 便于与真实 OpenAI 客户端互测，也便于单元测试直接断言。
/// </summary>
public static class ChatProtocol
{
    /// <summary>
    /// 输出序列化选项：非 ASCII 保持原样（日志与客户端易读）；
    /// 字符串内的双引号仍由 JSON 规则转义，不影响解析。
    /// </summary>
    private static readonly JsonSerializerOptions Out = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Write(object payload) => JsonSerializer.Serialize(payload, Out);
    /// <summary>解析 /v1/chat/completions 请求体；非法输入抛 <see cref="ChatRequestException"/>。</summary>
    public static ChatRequest ParseRequest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ChatRequestException("invalid_request_error", "body 必须是 JSON 对象", 400);

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : null;
        if (string.IsNullOrWhiteSpace(model))
            throw new ChatRequestException("invalid_request_error", "缺少 model 字段", 400);

        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array ||
            msgs.GetArrayLength() == 0)
            throw new ChatRequestException("invalid_request_error", "messages 必须是非空数组", 400);

        var list = new List<ChatMessage>();
        foreach (var item in msgs.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : "user";
            var content = item.TryGetProperty("content", out var c) switch
            {
                true when c.ValueKind == JsonValueKind.String => c.GetString() ?? "",
                true when c.ValueKind == JsonValueKind.Array => FlattenContent(c),
                _ => "",
            };
            list.Add(new ChatMessage(role, content));
        }

        double? temperature = root.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble() : null;
        int? maxTokens = root.TryGetProperty("max_tokens", out var mt) && mt.ValueKind == JsonValueKind.Number
            ? mt.GetInt32() : null;
        bool stream = root.TryGetProperty("stream", out var s) &&
                      (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.String);

        return new ChatRequest(model!, list, stream, temperature, maxTokens);
    }

    /// <summary>content 为数组时（OpenAI 多模态/分段写法）按 text 段拼接。</summary>
    private static string FlattenContent(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var seg in array.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.String) parts.Add(seg.GetString() ?? "");
            else if (seg.ValueKind == JsonValueKind.Object &&
                     seg.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                parts.Add(tx.GetString() ?? "");
        }
        return string.Join("", parts);
    }

    /// <summary>非流式响应体。</summary>
    public static string RenderCompletion(ChatCompletion completion, string requestId, long unixSeconds)
        => Write(new
        {
            id = requestId,
            @object = "chat.completion",
            created = unixSeconds,
            model = completion.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = completion.Content },
                    finish_reason = completion.FinishReason,
                },
            },
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
        });

    /// <summary>流式分段：chunk 的 SSE 文本；done=true 时发送结束分段。</summary>
    public static string RenderChunk(ChatCompletion completion, string requestId, long unixSeconds,
        bool done, bool startChunk)
    {
        object payload = new
        {
            id = requestId,
            @object = "chat.completion.chunk",
            created = unixSeconds,
            model = completion.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = startChunk
                        ? (object)new { role = "assistant" }
                        : done ? (object)new { } : (object)new { content = completion.Content },
                    finish_reason = done ? completion.FinishReason : null,
                },
            },
        };
        return "data: " + JsonSerializer.Serialize(payload) + "\n\n";
    }

    /// <summary>流式结束标记。</summary>
    public const string DoneMarker = "data: [DONE]\n\n";

    /// <summary>流式起始分段（delta 只带 role）。</summary>
    public static string RenderStreamStart(string requestId, string model, long unixSeconds)
        => "data: " + Write(new
        {
            id = requestId,
            @object = "chat.completion.chunk",
            created = unixSeconds,
            model,
            choices = new[]
            {
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null },
            },
        }) + "\n\n";

    /// <summary>
    /// 流式内容分段：只输出非空字段（正文 / 思考 / 工具调用），
    /// 思考以 <c>reasoning_content</c> 外发（DeepSeek 系客户端的通行字段）。
    /// </summary>
    public static string RenderStreamDelta(string requestId, string model, long unixSeconds,
        string? content = null, string? reasoningContent = null, string? toolCallsJson = null)
    {
        var delta = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(content)) delta["content"] = content;
        if (!string.IsNullOrEmpty(reasoningContent)) delta["reasoning_content"] = reasoningContent;
        if (NormalizeToolCalls(toolCallsJson) is { } tools) delta["tool_calls"] = tools;

        return "data: " + Write(new
        {
            id = requestId,
            @object = "chat.completion.chunk",
            created = unixSeconds,
            model,
            choices = new[]
            {
                new { index = 0, delta, finish_reason = (string?)null },
            },
        }) + "\n\n";
    }

    /// <summary>流式结束分段：finish_reason + 真实用量（上游没给时记 0）。</summary>
    public static string RenderStreamStop(string requestId, string model, long unixSeconds,
        string finishReason, int? promptTokens = null, int? completionTokens = null)
        => "data: " + Write(new
        {
            id = requestId,
            @object = "chat.completion.chunk",
            created = unixSeconds,
            model,
            choices = new[]
            {
                new { index = 0, delta = new { }, finish_reason = finishReason },
            },
            usage = new
            {
                prompt_tokens = promptTokens ?? 0,
                completion_tokens = completionTokens ?? 0,
                total_tokens = (promptTokens ?? 0) + (completionTokens ?? 0),
            },
        }) + "\n\n";

    /// <summary>流内错误分段（其后由调用方补 <see cref="DoneMarker"/>）。</summary>
    public static string RenderStreamError(ChatUpstreamError error)
        => "data: " + RenderError(error) + "\n\n";

    /// <summary>
    /// 把上游的 tool_calls 片段规整成 OpenAI delta 形状：补 index / type，
    /// function 统一成 { name, arguments }（arguments 非字符串时序列化原样）。
    /// </summary>
    private static JsonArray? NormalizeToolCalls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonArray array || array.Count == 0) return null;

        var result = new JsonArray();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject call) continue;
            var function = call["function"] as JsonObject;
            result.Add(new JsonObject
            {
                ["index"] = Number(call["index"]) ?? i,
                ["id"] = Text(call["id"]) is { Length: > 0 } id ? id : "call_" + i,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Text(function?["name"]) is { Length: > 0 } name ? name : Text(call["name"]),
                    ["arguments"] = Arguments(function?["arguments"] ?? call["arguments"]),
                },
            });
        }
        return result.Count > 0 ? result : null;

        static string Text(JsonNode? value)
            => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        static int? Number(JsonNode? value)
            => value is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

        static string Arguments(JsonNode? value) => value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => value.ToJsonString(),
        };
    }

    /// <summary>/v1/models 响应体。</summary>
    public static string RenderModels(IEnumerable<string> models, long unixSeconds)
        => Write(new
        {
            @object = "list",
            data = models.Select(id => new
            {
                id,
                @object = "model",
                created = unixSeconds,
                owned_by = "traetool",
            }).ToList(),
        });

    /// <summary>OpenAI 形状的错误体（type 已归一化到 OpenAI 词表）。</summary>
    public static string RenderError(ChatUpstreamError error)
        => Write(new
        {
            error = new { message = error.Message, type = MapErrorType(error.Type), code = (int?)null },
        });

    /// <summary>内部错误类型 → OpenAI 错误类型；未知类型归到 api_error。</summary>
    public static string MapErrorType(string? type) => type switch
    {
        "invalid_request_error" => "invalid_request_error",
        "auth_error" => "authentication_error",
        "permission_denied" => "permission_error",
        "not_found_error" => "not_found_error",
        "rate_limit_error" => "rate_limit_error",
        "insufficient_quota" => "insufficient_quota",
        _ => "api_error", // upstream_error / api_error / 未登记类型
    };
}

/// <summary>请求体不合法（映射为 4xx + OpenAI 错误体）。</summary>
public sealed class ChatRequestException(string type, string message, int status)
    : Exception(message)
{
    public string Type { get; } = type;
    public int Status { get; } = status;
}
