using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>
/// OpenAI Responses API（/v1/responses）：请求解析与响应/SSE 构造。
/// 与 <see cref="ChatProtocol"/>、<see cref="AnthropicProtocol"/> 共用内部 <see cref="ChatRequest"/>。
///
/// 能力范围与前两者一致：文本进、文本出、支持流式。
/// <c>tools</c> / <c>tool_choice</c> / <c>store</c> / <c>previous_response_id</c> / <c>metadata</c>
/// 只接受不实现（记日志、不报错）——本网关不做工具回合，也不存会话。
/// </summary>
public static class ResponsesProtocol
{
    /// <summary>收到了也无力实现的字段：记一条日志便于排查，但不影响请求。</summary>
    private static readonly string[] UnsupportedFields =
        ["tools", "tool_choice", "previous_response_id", "include", "truncation"];

    private static string Js(object payload) => GatewayJson.Serialize(payload);

    /// <summary>解析 /v1/responses 请求体；非法输入抛 <see cref="ChatRequestException"/>。</summary>
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

        var list = new List<ChatMessage>();

        // instructions 相当于系统提示，置于消息之前
        if (root.TryGetProperty("instructions", out var inst) && inst.ValueKind == JsonValueKind.String)
        {
            var text = inst.GetString() ?? "";
            if (text.Length > 0) list.Add(new ChatMessage("system", text));
        }

        if (root.TryGetProperty("input", out var input))
        {
            switch (input.ValueKind)
            {
                case JsonValueKind.String:
                    list.Add(new ChatMessage("user", input.GetString() ?? ""));
                    break;
                case JsonValueKind.Array:
                    foreach (var item in input.EnumerateArray()) CollectItem(item, list);
                    break;
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    list.Add(new ChatMessage("user", ""));
                    break;
                default:
                    throw new ChatRequestException("invalid_request_error",
                        "input 必须是字符串或 item 数组", 400);
            }
        }
        else
        {
            list.Add(new ChatMessage("user", ""));
        }

        if (list.Count == 0)
            throw new ChatRequestException("invalid_request_error", "input 里没有可用内容", 400);

        double? temperature = root.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble() : null;
        int? maxTokens = root.TryGetProperty("max_output_tokens", out var mo) && mo.ValueKind == JsonValueKind.Number
            ? mo.GetInt32() : null;
        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        var ignored = UnsupportedFields.Where(field => root.TryGetProperty(field, out _)).ToList();
        if (ignored.Count > 0)
            AppLog.Info("pool", "responses 请求里忽略了不支持的字段：" + string.Join(", ", ignored));

        return new ChatRequest(model!, list, stream, temperature, maxTokens);
    }

    /// <summary>取一条 input item：只认 message，其余类型（工具调用/思考等）跳过。</summary>
    private static void CollectItem(JsonElement item, List<ChatMessage> list)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : "message";
        if (!string.IsNullOrEmpty(type) && type != "message") return;

        var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()! : "user";
        if (role == "developer") role = "user";
        var content = item.TryGetProperty("content", out var c) ? FlattenContent(c) : "";
        list.Add(new ChatMessage(role, content));
    }

    /// <summary>content 可能是字符串或块数组；文本段拼接，图片记为占位，其余跳过。</summary>
    private static string FlattenContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                parts.Add(block.GetString() ?? "");
                continue;
            }
            if (block.ValueKind != JsonValueKind.Object) continue;

            var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : "";
            if (type is "input_image" or "image_url")
            {
                parts.Add("[image]");
            }
            else if (block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
            {
                parts.Add(tx.GetString() ?? "");
            }
        }
        return string.Join("\n", parts);
    }

    /// <summary>非流式响应体。</summary>
    public static string RenderResponse(ChatCompletion completion, string requestId, long unixSeconds)
    {
        var incomplete = string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
        var response = Skeleton(requestId, completion.Model, unixSeconds,
            incomplete ? "incomplete" : "completed",
            Output(requestId, completion.Content),
            Usage(null, null),
            incomplete ? new { reason = "max_output_tokens" } : null);
        response["stop_reason"] = null;
        return Js(response);
    }

    /// <summary>OpenAI 形状的错误体（type 复用 Chat 协议的词表，两种 OpenAI 系接口保持一致）。</summary>
    public static string RenderError(ChatUpstreamError error)
        => Js(new
        {
            error = new
            {
                message = error.Message,
                type = ChatProtocol.MapErrorType(error.Type),
                code = (string?)null,
            },
        });

    /// <summary>response.created：只在真有载荷时发一次，也是唯一带 sequence_number 的事件。</summary>
    public static string RenderCreated(string requestId, string model, long unixSeconds)
        => Event("response.created", new
        {
            type = "response.created",
            response = Skeleton(requestId, model, unixSeconds, "in_progress", Array.Empty<object>(), null),
            sequence_number = 0,
        });

    /// <summary>正文开始。</summary>
    public static string RenderOutputItemAdded(string requestId)
        => Event("response.output_item.added", new
        {
            type = "response.output_item.added",
            output_index = 0,
            item = new
            {
                id = ItemId(requestId),
                type = "message",
                status = "in_progress",
                role = "assistant",
                content = Array.Empty<object>(),
            },
        });

    /// <summary>正文增量。</summary>
    public static string RenderTextDelta(string requestId, string text)
        => Event("response.output_text.delta", new
        {
            type = "response.output_text.delta",
            item_id = ItemId(requestId),
            output_index = 0,
            content_index = 0,
            delta = text,
        });

    /// <summary>正文结束（带全文）。</summary>
    public static string RenderOutputItemDone(string requestId, string text)
        => Event("response.output_item.done", new
        {
            type = "response.output_item.done",
            output_index = 0,
            item = new
            {
                id = ItemId(requestId),
                type = "message",
                status = "completed",
                role = "assistant",
                content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } },
            },
        });

    /// <summary>流正常收尾。</summary>
    public static string RenderCompleted(string requestId, string model, long unixSeconds, string text,
        UpstreamOutcome outcome)
        => Event("response.completed", new
        {
            type = "response.completed",
            response = Skeleton(requestId, model, unixSeconds, "completed", Output(requestId, text),
                Usage(outcome.PromptTokens, outcome.CompletionTokens)),
        });

    /// <summary>已开流之后的失败：以 response.failed 收尾（output 为空）。</summary>
    public static string RenderFailed(string requestId, string model, long unixSeconds, ChatUpstreamError error)
        => Event("response.failed", new
        {
            type = "response.failed",
            response = Skeleton(requestId, model, unixSeconds, "failed", Array.Empty<object>(), null,
                incompleteDetails: null,
                error: new { code = (string?)null, message = error.Message }),
        });

    private static string ItemId(string requestId) => requestId + "_msg_0";

    /// <summary>output 数组：一个 assistant 消息项；文本为空时给空数组（与上游一致）。</summary>
    private static object Output(string requestId, string text)
        => text.Length == 0
            ? Array.Empty<object>()
            : new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = ItemId(requestId),
                    ["type"] = "message",
                    ["status"] = "completed",
                    ["role"] = "assistant",
                    ["content"] = new[]
                    {
                        new { type = "output_text", text, annotations = Array.Empty<object>() },
                    },
                },
            };

    /// <summary>usage 块；用量未知时记 0。</summary>
    private static object Usage(int? promptTokens, int? completionTokens)
    {
        var input = promptTokens ?? 0;
        var output = completionTokens ?? 0;
        return new
        {
            input_tokens = input,
            input_tokens_details = new { cached_tokens = 0 },
            output_tokens = output,
            output_tokens_details = new { reasoning_tokens = 0 },
            total_tokens = input + output,
        };
    }

    /// <summary>response 对象的公共骨架（字段顺序与上游一致，便于比对）。</summary>
    private static Dictionary<string, object?> Skeleton(string id, string model, long unixSeconds, string status,
        object output, object? usage, object? incompleteDetails = null, object? error = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = unixSeconds,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["parallel_tool_calls"] = true,
            ["tool_choice"] = "auto",
            ["tools"] = Array.Empty<object>(),
            ["incomplete_details"] = incompleteDetails,
            ["error"] = error,
        };
        if (usage != null) response["usage"] = usage;
        return response;
    }

    private static string Event(string type, object payload)
        => "event: " + type + "\n" + "data: " + Js(payload) + "\n\n";
}
