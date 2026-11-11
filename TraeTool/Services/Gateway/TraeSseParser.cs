using System.Text;
using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 上游 SSE 的逐行解析器：纯逻辑，不接触 socket，可直接喂字符串测试。
/// 调用方按行 <see cref="Feed"/>（空行结算一帧），流结束时调 <see cref="Finish"/> 收尾。
/// 一帧最多产出一个增量：正文 / 思考 / 工具 / 终末。
/// </summary>
public sealed class TraeSseParser
{
    /// <summary>结束原因的取值白名单，词表外的词一律归 stop。</summary>
    private static readonly string[] FinishWhitelist =
        ["stop", "length", "tool_calls", "function_call", "content_filter"];

    private readonly StringBuilder _data = new();
    private readonly StringBuilder _raw = new();   // 错误文案要引原文，只留开头一段

    private const int RawLimit = 512;

    private string _event = "";
    private string _finish = "";
    private int? _promptTokens;
    private int? _completionTokens;

    /// <summary>是否出现过任何一帧。</summary>
    public bool SawAnyFrame { get; private set; }

    /// <summary>是否出现过终止帧（done / [DONE] / error）。</summary>
    public bool SawDone { get; private set; }

    /// <summary>是否产出过正文或思考。</summary>
    public bool SawText { get; private set; }

    /// <summary>是否产出过工具调用。</summary>
    public bool SawToolCalls { get; private set; }

    /// <summary>结算后的结束原因（白名单外的词归 stop；出现过工具调用而结果是 stop 则升级为 tool_calls）。</summary>
    public string FinishReason
    {
        get
        {
            var finish = string.IsNullOrEmpty(_finish) ? "stop" : _finish;
            if (Array.IndexOf(FinishWhitelist, finish) < 0) finish = "stop";
            if (SawToolCalls && finish == "stop") finish = "tool_calls";
            return finish;
        }
    }

    /// <summary>喂入一行原始行（可带 \r），返回该行结算出的增量（通常为空）。</summary>
    public IReadOnlyList<UpstreamDelta> Feed(string rawLine)
    {
        var line = rawLine.TrimEnd('\r');
        Remember(line);

        if (line.Length == 0)
        {
            var frame = Flush();
            return frame == null ? Array.Empty<UpstreamDelta>() : new[] { frame };
        }

        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            _event = line["event:".Length..].Trim();
        else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(line["data:".Length..].TrimStart());
        }
        return Array.Empty<UpstreamDelta>();
    }

    /// <summary>流结束：结算残留帧并按累积状态给出终末项。</summary>
    public IReadOnlyList<UpstreamDelta> Finish()
    {
        var frame = Flush();
        if (frame is { IsTerminal: true }) return new[] { frame };

        var tail = new List<UpstreamDelta>(2);
        if (frame != null) tail.Add(frame);

        if (!SawAnyFrame)
        {
            tail.Add(Fail("响应不是 SSE，也不含帧内容：" + Truncate(_raw.ToString())));
        }
        else if (!SawText && !SawToolCalls)
        {
            // 有终止帧说明上游正常收尾，只是没产出文本；两者要能区分开
            tail.Add(SawDone
                ? UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                    new ChatUpstreamError("上游已返回完成但没有文本内容：" + Truncate(_raw.ToString()),
                        "empty_completion", 502)))
                : Fail("SSE 里没有可用文本：" + Truncate(_raw.ToString())));
        }
        else
        {
            // 没有终止帧就结束了：有内容时按正常收尾补一个终末项，并标记截断
            tail.Add(UpstreamDelta.Terminal(UpstreamOutcome.Done(FinishReason, truncated: !SawDone,
                _promptTokens, _completionTokens)));
        }
        return tail;
    }

    /// <summary>结算当前累积的一帧；无可结算时返回 null。</summary>
    private UpstreamDelta? Flush()
    {
        if (_data.Length == 0 && _event.Length == 0) return null;

        var data = _data.ToString();
        var evt = _event;
        _data.Clear();
        _event = "";
        SawAnyFrame = true;

        // 业务错误帧：HTTP 200 也算失败，必须原样透出
        if (evt.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            SawDone = true;
            return UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                new ChatUpstreamError(TraeChatUpstream.DescribeUpstreamError(data), "api_error", 200)));
        }

        if (evt.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            SawDone = true;
            ReadFinish(data);
            return null;
        }

        if (data.Trim() == "[DONE]")
        {
            SawDone = true;
            if (string.IsNullOrEmpty(_finish)) _finish = "stop";
            return null;
        }

        if (data.Length == 0) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            // meta 帧的 data 若非 JSON 则忽略；输出帧的坏 JSON 会截断整条流，必须报错
            return evt is "output" or "content" or "message" or "thought"
                ? Fail("SSE 帧不是合法 JSON：" + Truncate(data))
                : null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            ReadUsage(root);

            // 输出帧：response = 正文，reasoning / reasoning_content = 思考，tool_calls = 工具
            if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.String)
            {
                var content = responseEl.GetString() ?? "";
                var reasoning = ReadReasoning(root);
                var tools = ReadToolCalls(root);
                SawText |= content.Length > 0 || reasoning.Length > 0;
                SawToolCalls |= tools != null;
                if (content.Length == 0 && reasoning.Length == 0 && tools == null) return null;
                return new UpstreamDelta(
                    Content: content.Length > 0 ? content : null,
                    Reasoning: reasoning.Length > 0 ? reasoning : null,
                    ToolCalls: tools);
            }

            // 只有思考的帧（部分端点把思考单独成帧）
            if (root.TryGetProperty("reasoning_content", out _) || root.TryGetProperty("reasoning", out _))
            {
                var reasoning = ReadReasoning(root);
                var tools = ReadToolCalls(root);
                SawText |= reasoning.Length > 0;
                SawToolCalls |= tools != null;
                if (reasoning.Length == 0 && tools == null) return null;
                return new UpstreamDelta(
                    Reasoning: reasoning.Length > 0 ? reasoning : null,
                    ToolCalls: tools);
            }

            // 只带工具调用的帧（OpenAI 形状的增量）
            if (ReadToolCalls(root) is { } standaloneTools)
            {
                SawToolCalls = true;
                return new UpstreamDelta(ToolCalls: standaloneTools);
            }

            string? chunk = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    chunk ??= TraeChatUpstream.ReadText(choice);
                    if (string.IsNullOrEmpty(_finish) &&
                        choice.TryGetProperty("finish_reason", out var fr) &&
                        fr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fr.GetString()))
                        _finish = fr.GetString()!;
                }
            }
            chunk ??= TraeChatUpstream.ReadText(root);
            if (string.IsNullOrEmpty(chunk)) return null;

            SawText = true;
            return new UpstreamDelta(Content: chunk);
        }
    }

    private static UpstreamDelta Fail(string message)
        => UpstreamDelta.Terminal(UpstreamOutcome.Failed(
            new ChatUpstreamError(message, "upstream_error", 502)));

    private void ReadFinish(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("finish_reason", out var fr) &&
                fr.ValueKind == JsonValueKind.String)
                _finish = fr.GetString() ?? _finish;
        }
        catch (JsonException)
        {
            // done 帧没有 JSON（例如 data: [DONE]）时忽略
        }
    }

    /// <summary>取用量：兼容 input/output 与 prompt/completion 两套命名，可嵌在 usage 对象里。</summary>
    private void ReadUsage(JsonElement root)
    {
        var scope = root;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            scope = usage;

        _promptTokens ??= FindInt(scope, "prompt_tokens", "input_tokens");
        _completionTokens ??= FindInt(scope, "completion_tokens", "output_tokens");
    }

    private static int? FindInt(JsonElement scope, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (scope.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt32(out var value))
                return value;
        }
        return null;
    }

    private static string ReadReasoning(JsonElement root)
    {
        foreach (var key in new[] { "reasoning_content", "reasoning" })
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? "";
        }
        return "";
    }

    /// <summary>工具调用：原样保留 JSON（客户端按自己的协议再加工）。</summary>
    private static string? ReadToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var el)) return null;
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() == 0) return null;
        if (el.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return null;
        return el.GetRawText();
    }

    private void Remember(string line)
    {
        if (_raw.Length >= RawLimit) return;
        if (_raw.Length > 0) _raw.Append('\n');
        _raw.Append(line.Length > RawLimit ? line[..RawLimit] : line);
    }

    private static string Truncate(string text) => TraeChatUpstream.Truncate(text);
}
