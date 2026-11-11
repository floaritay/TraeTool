using System.Text;
using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>
/// Anthropic Messages 协议（/v1/messages）：请求解析与响应/SSE 构造。
/// 与 <see cref="ChatProtocol"/> 共用内部 <see cref="ChatRequest"/> 结构，
/// 由转发层在两者之间转换，供 Claude 系客户端直连。
/// </summary>
public static class AnthropicProtocol
{
    private static string Js(object payload) => GatewayJson.Serialize(payload);

    /// <summary>解析 /v1/messages 请求体；非法输入抛 <see cref="ChatRequestException"/>。</summary>
    public static ChatRequest ParseRequest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ChatRequestException("invalid_request_error", "body 必须是 JSON 对象", 400);

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : null;
        if (string.IsNullOrWhiteSpace(model))
            throw new ChatRequestException("invalid_request_error", "缺少 model 字段", 400);

        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array ||
            msgs.GetArrayLength() == 0)
            throw new ChatRequestException("invalid_request_error", "messages 必须是非空数组", 400);

        var list = new List<ChatMessage>();

        // Anthropic 的 system 是顶层字段，置于消息之前
        if (root.TryGetProperty("system", out var sys))
        {
            var sysText = FlattenContent(sys);
            if (sysText.Length > 0) list.Add(new ChatMessage("system", sysText));
        }

        foreach (var item in msgs.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : "user";
            var content = item.TryGetProperty("content", out var c) ? FlattenContent(c) : "";
            if (content.Length == 0 && !item.TryGetProperty("content", out _)) continue;
            list.Add(new ChatMessage(role, content));
        }
        if (list.Count == 0)
            throw new ChatRequestException("invalid_request_error", "messages 里没有可用内容", 400);

        int? maxTokens = root.TryGetProperty("max_tokens", out var mt) && mt.ValueKind == JsonValueKind.Number
            ? mt.GetInt32() : null;
        bool stream = root.TryGetProperty("stream", out var s) && ChatProtocol.StreamRequested(s);

        return new ChatRequest(model!, list, stream, null, maxTokens);
    }

    /// <summary>
    /// content 可能是字符串或块数组；块类型 text/thinking 取文本，
    /// 其余（tool_use / tool_result / image 等）序列化成 JSON 文本，保证模型看得见上下文。
    /// </summary>
    private static string FlattenContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : "";

            if (type is "text" or "thinking")
            {
                if (block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    parts.Add(tx.GetString() ?? "");
            }
            else if (type == "image")
            {
                parts.Add("[image]");
            }
            else
            {
                parts.Add("[" + type + "] " + Js(block));
            }
        }
        return string.Join("\n", parts);
    }

    /// <summary>非流式响应体。</summary>
    public static string RenderResponse(ChatCompletion completion, string requestId, string model)
        => Js(new
        {
            id = requestId,
            type = "message",
            role = "assistant",
            model,
            content = new[] { new { type = "text", text = completion.Content } },
            stop_reason = MapStopReason(completion.FinishReason),
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = completion.PromptTokens ?? 0,
                output_tokens = completion.CompletionTokens ?? 0,
            },
        });

    /// <summary>流式帧序列（message_start → content_block → message_delta → message_stop）。</summary>
    public static IEnumerable<string> RenderFrames(ChatCompletion completion, string requestId, string model)
    {
        var message = new
        {
            id = requestId,
            type = "message",
            role = "assistant",
            model,
            content = Array.Empty<object>(),
            stop_reason = (string?)null,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 0, output_tokens = 0 },
        };
        yield return Frame("message_start", Js(new { type = "message_start", message }));

        yield return Frame("content_block_start", Js(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "text", text = "" },
        }));

        yield return Frame("content_block_delta", Js(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "text_delta", text = completion.Content },
        }));

        yield return Frame("content_block_stop", Js(new { type = "content_block_stop", index = 0 }));

        yield return Frame("message_delta", Js(new
        {
            type = "message_delta",
            delta = new { stop_reason = MapStopReason(completion.FinishReason), stop_sequence = (string?)null },
            usage = new { output_tokens = 0 },
        }));

        yield return Frame("message_stop", Js(new { type = "message_stop" }));
    }

    /// <summary>流式起始帧：message_start（content 为空，正文由后续块承载）。</summary>
    public static string RenderStreamStart(string requestId, string model)
        => Frame("message_start", Js(new
        {
            type = "message_start",
            message = new
            {
                id = requestId,
                type = "message",
                role = "assistant",
                model,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 },
            },
        }));

    /// <summary>文本块开始（一次流只用一个文本块）。</summary>
    public static string RenderBlockStart(int index = 0)
        => Frame("content_block_start", Js(new
        {
            type = "content_block_start",
            index,
            content_block = new { type = "text", text = "" },
        }));

    /// <summary>文本增量。</summary>
    public static string RenderTextDelta(string text, int index = 0)
        => Frame("content_block_delta", Js(new
        {
            type = "content_block_delta",
            index,
            delta = new { type = "text_delta", text },
        }));

    /// <summary>文本块结束。</summary>
    public static string RenderBlockStop(int index = 0)
        => Frame("content_block_stop", Js(new
        {
            type = "content_block_stop",
            index,
        }));

    /// <summary>收尾：stop_reason + 真实用量（上游没给时记 0）。</summary>
    public static string RenderMessageDelta(string finishReason, int? promptTokens = null,
        int? outputTokens = null)
        => Frame("message_delta", Js(new
        {
            type = "message_delta",
            delta = new { stop_reason = MapStopReason(finishReason), stop_sequence = (string?)null },
            usage = new
            {
                input_tokens = promptTokens ?? 0,
                output_tokens = outputTokens ?? 0,
            },
        }));

    /// <summary>流结束标记。</summary>
    public static string RenderMessageStop()
        => Frame("message_stop", Js(new { type = "message_stop" }));

    /// <summary>流内错误帧（已开始的块由调用方先行关闭）。</summary>
    public static string RenderErrorFrame(ChatUpstreamError error) => Frame("error", RenderError(error));

    /// <summary>Anthropic 形状的错误体（error.type 已归一化到 Anthropic 词表）。</summary>
    public static string RenderError(ChatUpstreamError error)
        => Js(new
        {
            type = "error",
            error = new { type = MapErrorType(error), message = error.Message },
        });

    /// <summary>
    /// 内部错误类型 → Anthropic 错误类型（invalid_request_error / authentication_error /
    /// permission_error / not_found_error / rate_limit_error / api_error）。
    /// 配额与限流在 Anthropic 侧同为 429，统一给 rate_limit_error。
    /// </summary>
    public static string MapErrorType(ChatUpstreamError error) => error.Type switch
    {
        "invalid_request_error" => "invalid_request_error",
        "auth_error" => "authentication_error",
        "permission_denied" => "permission_error",
        "not_found_error" => "not_found_error",
        "rate_limit_error" or "insufficient_quota" => "rate_limit_error",
        _ => error.Status >= 500 ? "api_error" : "invalid_request_error",
    };

    private static string Frame(string evt, string json)
        => "event: " + evt + "\n" + "data: " + json + "\n\n";

    private static string MapStopReason(string finish) => (finish ?? "").ToLowerInvariant() switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        "content_filter" => "refusal",
        _ => "end_turn",
    };
}
