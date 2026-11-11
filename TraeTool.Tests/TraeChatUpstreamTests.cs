using System.Net.Http;
using System.Text;
using TraeTool.Models;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

/// <summary>上游适配器的纯逻辑部分：SSE/JSON 响应解析与请求头写入。</summary>
public class TraeChatUpstreamTests
{
    /// <summary>按客户端实际返回帧格式构造的 SSE（event: meta / event: output / [DONE]）。</summary>
    private static string RealSse(params string[] outputs)
    {
        var nl = "\n";
        var sb = new StringBuilder();
        sb.Append("event: meta").Append(nl);
        sb.Append("data: {\"id\":\"202611071737064849629CE12C6C0DD1E7\",\"model\":\"S-CodeFusionContext\",\"ab_versions\":[\"\"]}");
        sb.Append(nl).Append(nl);
        foreach (var text in outputs)
        {
            sb.Append("event: output").Append(nl);
            sb.Append("data: {\"created\":0,\"choices\":[{\"text\":\"").Append(Escape(text)).Append("\"}]}");
            sb.Append(nl).Append(nl);
        }
        sb.Append("event: done").Append(nl);
        sb.Append("data: [DONE]").Append(nl).Append(nl);
        return sb.ToString();
    }
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    [Fact]
    public void ParseResponse_recognizes_sse_and_reads_choices_text()
    {
        var res = TraeChatUpstream.ParseResponse(RealSse("hello "), "trae");

        Assert.True(res.Ok, res.Error?.Message);
        Assert.Equal("hello ", res.Completion!.Content);
        Assert.Equal("stop", res.Completion.FinishReason);
        Assert.Equal("trae", res.Completion.Model);
    }

    [Fact]
    public void ParseSse_concatenates_multiple_output_frames()
    {
        var res = TraeChatUpstream.ParseResponse(RealSse("abc", "def", "gHi"), "m");

        Assert.True(res.Ok, res.Error?.Message);
        Assert.Equal("abcdefgHi", res.Completion!.Content);
    }

    [Fact]
    public void ParseSse_picks_up_finish_reason_from_choice()
    {
        var sse = """
            event: output
            data: {"choices":[{"text":"x","finish_reason":"length"}]}

            """;
        var res = TraeChatUpstream.ParseResponse(sse, "m");

        Assert.True(res.Ok, res.Error?.Message);
        Assert.Equal("length", res.Completion!.FinishReason);
    }

    [Fact]
    public void ParseSse_without_text_is_format_error()
    {
        var res = TraeChatUpstream.ParseResponse("event: meta\ndata: {\"id\":\"1\"}\n\n", "m");

        Assert.False(res.Ok);
        Assert.Equal("upstream_error", res.Error!.Type);
        Assert.Contains("没有可用文本", res.Error.Message);
    }

    [Fact]
    public void ParseSse_garbage_data_in_output_is_error()
    {
        var res = TraeChatUpstream.ParseResponse("event: output\ndata: {not json}\n\n", "m");

        Assert.False(res.Ok);
        Assert.Contains("不是合法 JSON", res.Error!.Message);
    }

    [Fact]
    public void ParseResponse_routes_json_body_to_json_branch()
    {
        var res = TraeChatUpstream.ParseResponse(
            """{"choices":[{"message":{"role":"assistant","content":"chat text"},"finish_reason":"stop"}]}""", "m");

        Assert.True(res.Ok, res.Error?.Message);
        Assert.Equal("chat text", res.Completion!.Content);
    }

    [Fact]
    public void ParseResponse_accepts_completion_style_text_field()
    {
        var res = TraeChatUpstream.ParseResponse(
            """{"choices":[{"text":"inline text"}]}""", "m");

        Assert.True(res.Ok, res.Error?.Message);
        Assert.Equal("inline text", res.Completion!.Content);
    }

    [Fact]
    public void ParseResponse_unrecognized_json_reports_excerpt()
    {
        var res = TraeChatUpstream.ParseResponse("""{"code":0,"message":"whatever"}""", "m");

        Assert.False(res.Ok);
        Assert.Contains("响应格式与预期不符", res.Error!.Message);
        Assert.Contains("\"code\":0", res.Error.Message);
    }

    [Fact]
    public void Observed_headers_include_cloud_ide_jwt_and_static_client_id()
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/x");
        var account = new Account { Token = "jwt-token", Session = "sess", DeviceId = "1000000000000009" };

        TraeChatUpstream.ApplyObservedHeaders(msg, account);

        Assert.True(msg.Headers.TryGetValues("authorization", out var auth));
        // 鉴权方案是 Cloud-IDE-JWT（Bearer 会被上游判未认证）
        Assert.Equal("Cloud-IDE-JWT jwt-token", auth!.First());
        Assert.True(msg.Headers.TryGetValues("x-app-id", out var appId));
        Assert.NotEmpty(appId!);
        Assert.True(msg.Headers.TryGetValues("x-device-id", out var deviceId));
        Assert.Equal("1000000000000009", deviceId!.First());
        Assert.True(msg.Headers.TryGetValues("x-request-id", out var reqId));
        Assert.NotEmpty(reqId!);
    }

    [Fact]
    public void BuildBody_emits_openai_shaped_json()
    {
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "hi") }, false, 0.5, 128);
        var json = TraeChatUpstream.BuildBody(req, "uid-1", "1000000000000001");

        Assert.Contains(Q("model") + ":" + Q("m"), json);
        Assert.Contains(Q("role") + ":" + Q("user"), json);
        Assert.Contains(Q("content") + ":[{" + Q("type") + ":" + Q("text") + "," + Q("text") + ":" + Q("hi") + "}]", json);
        Assert.Contains(Q("temperature") + ":0.5", json);
        Assert.Contains(Q("max_tokens") + ":128", json);
    }

    [Fact]
    public void BuildBody_carries_required_upstream_fields()
    {
        var json = TraeChatUpstream.BuildBody(
            new ChatRequest("DeepSeek-V4-Flash", new[] { new ChatMessage("user", "hi") }, false, null, null),
            "uid-1", "1000000000000001");

        Assert.Contains(Q("config_name") + ":" + Q("DeepSeek-V4-Flash"), json);
        Assert.Contains(Q("model_name") + ":" + Q("deepseek_v4_flash__dev"), json);
        Assert.Contains(Q("function") + ":" + Q("solo_work_lite"), json);
        Assert.Contains(Q("prompt_max_tokens") + ":168000", json);
        Assert.Contains(Q("max_tokens") + ":4096", json);
        Assert.Contains(Q("mode") + ":" + Q("FunctionCall"), json);
        Assert.Contains(Q("user_id") + ":" + Q("uid-1"), json);
        Assert.Contains(Q("device_id") + ":" + Q("1000000000000001"), json);
        Assert.Contains(Q("stream") + ":true", json);
    }

    [Fact]
    public void BuildBody_maps_solo_agent_models_to_their_view()
    {
        var json = TraeChatUpstream.BuildBody(
            new ChatRequest("deepseek-v4.1-flash", new[] { new ChatMessage("user", "hi") }, false, null, null),
            null, null);

        Assert.Contains(Q("config_name") + ":" + Q("deepseek-v4.1-flash"), json);
        Assert.Contains(Q("model_name") + ":" + Q("deepseek-v4.1-flash__dev"), json);
        Assert.Contains(Q("function") + ":" + Q("solo_agent"), json);
    }

    [Fact]
    public void BuildBody_unknown_model_follows_dev_naming_rule()
    {
        var json = TraeChatUpstream.BuildBody(
            new ChatRequest("brand-new-model", new[] { new ChatMessage("user", "hi") }, false, null, null),
            null, null);

        Assert.Contains(Q("config_name") + ":" + Q("brand-new-model"), json);
        Assert.Contains(Q("model_name") + ":" + Q("brand-new-model__dev"), json);
        Assert.Contains(Q("function") + ":" + Q("solo_work_lite"), json);
    }

    /// <summary>拼 JSON 片段（避免在测试里写转义字符串）。</summary>
    private static string Q(string s) => "\"" + s + "\"";

    [Fact]
    public void ParseSse_error_frame_surfaces_upstream_code_and_message()
    {
        var sse = @"id:1
event:error
data:{""code"":2001,""message"":""[LLMUtilsChat.resolveByUsage] function is empty, cannot resolve model by usage="",""extra"":null}

id:2
event:done
data:{""finish_reason"":""stop""}
";
        var res = TraeChatUpstream.ParseSse(sse, "m");

        Assert.False(res.Ok);
        Assert.Contains("2001", res.Error!.Message);
        Assert.Contains("resolveByUsage", res.Error.Message);
        Assert.Contains("function is empty", res.Error.Message);
    }

    [Fact]
    public void ParseSse_done_without_text_reports_upstream_empty()
    {
        var sse = @"event:done
data:{""finish_reason"":""stop""}
";
        var res = TraeChatUpstream.ParseSse(sse, "m");

        Assert.False(res.Ok);
        Assert.Contains("完成但没有文本", res.Error!.Message);
    }
}
