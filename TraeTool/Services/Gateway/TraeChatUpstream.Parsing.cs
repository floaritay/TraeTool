using System.Text;
using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 上游响应解析：非流式路径（整段响应，SSE 或普通 JSON）与文本抽取。
/// 流式路径的逐行解析见 <see cref="TraeSseParser"/>。
/// </summary>
public sealed partial class TraeChatUpstream
{
    /// <summary>
    /// 上游响应解析：SSE（客户端实际返回）与普通 JSON 双分支。
    /// SSE 取全部 output 帧的 <c>choices[].text</c> 拼接；JSON 分支先按 OpenAI 结构取
    /// <c>choices[0].message.content</c>，再退回顶层 content/text/data；都不匹配记为格式不符并附原文摘要。
    /// </summary>
    public static UpstreamResult ParseResponse(string body, string model)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return ParseJson(body, model);
        return ParseSse(body, model);
    }

    /// <summary>解析整段 <c>event:/data:</c> 帧（空行分隔）；无返回帧视为截断，不额外报错。</summary>
    public static UpstreamResult ParseSse(string body, string model)
    {
        var parser = new TraeSseParser();
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var finish = "stop";
        ChatUpstreamError? error = null;

        foreach (var line in body.Split('\n'))
        {
            Absorb(parser.Feed(line));
            if (error != null) return new UpstreamResult(null, error);
        }
        Absorb(parser.Finish());
        // 没有可用文本时解析器已给出终末错误（空完成 / 不是 SSE），这里只剩可用的情形
        if (error != null) return new UpstreamResult(null, error);

        // 正式回答优先；完全没有回答时退回思考内容（总好过报无文本）
        if (text.Length == 0) text = reasoning;

        return UpstreamResult.From(new ChatCompletion(text.ToString(), finish, model));

        void Absorb(IReadOnlyList<UpstreamDelta> deltas)
        {
            foreach (var delta in deltas)
            {
                if (delta.Outcome is { } outcome)
                {
                    if (outcome.Error != null) { error = outcome.Error; return; }
                    finish = outcome.FinishReason;
                    continue;
                }
                if (delta.Content is { Length: > 0 } content) text.Append(content);
                if (delta.Reasoning is { Length: > 0 } reason) reasoning.Append(reason);
            }
        }
    }

    private static UpstreamResult ParseJson(string body, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(body);

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                var content = ReadText(first);
                var finish = first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                    ? fr.GetString() ?? "stop" : "stop";
                if (content.Length > 0)
                    return UpstreamResult.From(new ChatCompletion(content, finish, model));
            }

            var fallback = ReadText(root);
            if (fallback.Length > 0)
                return UpstreamResult.From(new ChatCompletion(fallback, "stop", model));

            return Fail(body);
        }
        catch (Exception ex)
        {
            return UpstreamResult.Fail("响应不是合法 JSON：" + ex.Message, "upstream_error", 502);
        }

        static UpstreamResult Fail(string body)
            => UpstreamResult.Fail("响应格式与预期不符：" + Truncate(body), "upstream_error", 502);
    }

    /// <summary>取文本：OpenAI 的 message.content / completions 的 text / 顶层 content、text、data。</summary>
    internal static string ReadText(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";

        if (node.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("content", out var mc))
            {
                if (mc.ValueKind == JsonValueKind.String) return mc.GetString() ?? "";
                if (mc.ValueKind == JsonValueKind.Array) return Flatten(mc);
            }
        }
        foreach (var key in new[] { "text", "content", "delta" })
        {
            if (!node.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString() ?? "";
                if (s.Length > 0) return s;
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                var inner = ReadText(v);
                if (inner.Length > 0) return inner;
            }
        }
        return "";
    }

    private static string Flatten(JsonElement array)
    {
        var sb = new StringBuilder();
        foreach (var seg in array.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.String) sb.Append(seg.GetString());
            else if (seg.ValueKind == JsonValueKind.Object &&
                     seg.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                sb.Append(tx.GetString());
        }
        return sb.ToString();
    }

    /// <summary>把上游 error 帧的 code/message 组成可读文案；解析失败回传原始 data。</summary>
    internal static string DescribeUpstreamError(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(data);

            var code = root.TryGetProperty("code", out var c)
                ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString() ?? ""
                : "";
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";
            if (msg.Length == 0) return Truncate(data);
            return code.Length > 0 ? $"上游 {code}: {msg}" : "上游错误: " + msg;
        }
        catch (JsonException)
        {
            return Truncate(data);
        }
    }

    /// <summary>日志与错误文案用的单行摘要（压掉换行并截断到 512 字）。</summary>
    internal static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 512 ? flat : flat[..512] + "…";
    }
}
