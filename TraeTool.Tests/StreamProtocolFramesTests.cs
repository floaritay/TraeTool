using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

/// <summary>三种协议的流式帧形状与 Responses 的请求解析（纯字符串断言）。</summary>
public class StreamProtocolFramesTests
{
    private const long Unix = 1_760_000_000;

    // ==================== OpenAI Chat Completions ====================

    [Fact]
    public void Chat_start_frame_declares_the_assistant_role()
    {
        var frame = ChatProtocol.RenderStreamStart("chatcmpl-1", "trae", Unix);

        Assert.StartsWith("data: ", frame);
        Assert.EndsWith("\n\n", frame);
        Assert.Contains("\"object\":\"chat.completion.chunk\"", frame);
        Assert.Contains("\"role\":\"assistant\"", frame);
        Assert.Contains("\"finish_reason\":null", frame);
    }

    [Fact]
    public void Chat_delta_frame_only_carries_non_empty_fields()
    {
        var frame = ChatProtocol.RenderStreamDelta("chatcmpl-1", "trae", Unix, content: "hi");

        Assert.Contains("\"content\":\"hi\"", frame);
        Assert.DoesNotContain("reasoning_content", frame);
        Assert.DoesNotContain("tool_calls", frame);
    }

    [Fact]
    public void Chat_delta_frame_emits_reasoning_and_normalized_tool_calls()
    {
        var tools = """[{"id":"call_1","function":{"name":"f","arguments":"{}"}}]""";
        var frame = ChatProtocol.RenderStreamDelta("chatcmpl-1", "trae", Unix,
            content: null, reasoningContent: "想", toolCallsJson: tools);

        Assert.Contains("\"reasoning_content\":\"想\"", frame);
        Assert.Contains("\"tool_calls\"", frame);
        // 客户端按 OpenAI delta 形状解析：缺 index / type 会被判为非法
        Assert.Contains("\"index\":0", frame);
        Assert.Contains("\"type\":\"function\"", frame);
        Assert.Contains("\"name\":\"f\"", frame);
    }

    [Fact]
    public void Chat_delta_frame_drops_unparsable_tool_calls_instead_of_breaking_the_stream()
    {
        var frame = ChatProtocol.RenderStreamDelta("chatcmpl-1", "trae", Unix,
            content: "hi", toolCallsJson: "{not json}");

        Assert.Contains("\"content\":\"hi\"", frame);
        Assert.DoesNotContain("tool_calls", frame);
    }

    [Fact]
    public void Chat_stop_frame_carries_finish_reason_and_usage()
    {
        var frame = ChatProtocol.RenderStreamStop("chatcmpl-1", "trae", Unix, "stop", 11, 22);

        Assert.Contains("\"finish_reason\":\"stop\"", frame);
        Assert.Contains("\"prompt_tokens\":11", frame);
        Assert.Contains("\"completion_tokens\":22", frame);
        Assert.Contains("\"total_tokens\":33", frame);
    }

    [Fact]
    public void Chat_error_frame_is_a_data_frame_with_an_error_object()
    {
        var frame = ChatProtocol.RenderStreamError(new ChatUpstreamError("boom", "upstream_error", 502));

        Assert.StartsWith("data: ", frame);
        Assert.Contains("\"error\"", frame);
        Assert.Contains("boom", frame);
        Assert.Equal("data: [DONE]\n\n", ChatProtocol.DoneMarker);
    }

    // ==================== Anthropic Messages ====================

    [Fact]
    public void Anthropic_stream_frames_follow_the_messages_event_order()
    {
        var start = AnthropicProtocol.RenderStreamStart("msg_1", "trae");
        var blockStart = AnthropicProtocol.RenderBlockStart();
        var delta = AnthropicProtocol.RenderTextDelta("答");
        var blockStop = AnthropicProtocol.RenderBlockStop();
        var messageDelta = AnthropicProtocol.RenderMessageDelta("length", 3, 4);
        var stop = AnthropicProtocol.RenderMessageStop();

        Assert.StartsWith("event: message_start", start);
        Assert.Contains("\"content\":[]", start);
        Assert.StartsWith("event: content_block_start", blockStart);
        Assert.Contains("\"type\":\"text\"", blockStart);
        Assert.StartsWith("event: content_block_delta", delta);
        Assert.Contains("\"type\":\"text_delta\"", delta);
        Assert.StartsWith("event: content_block_stop", blockStop);
        Assert.StartsWith("event: message_delta", messageDelta);
        Assert.Contains("\"stop_reason\":\"max_tokens\"", messageDelta);
        Assert.Contains("\"input_tokens\":3", messageDelta);
        Assert.Contains("\"output_tokens\":4", messageDelta);
        Assert.StartsWith("event: message_stop", stop);
    }

    [Fact]
    public void Anthropic_stop_reason_mapping_covers_the_shared_finish_vocabulary()
    {
        Assert.Contains("\"max_tokens\"", AnthropicProtocol.RenderMessageDelta("length"));
        Assert.Contains("\"tool_use\"", AnthropicProtocol.RenderMessageDelta("tool_calls"));
        Assert.Contains("\"tool_use\"", AnthropicProtocol.RenderMessageDelta("function_call"));
        Assert.Contains("\"refusal\"", AnthropicProtocol.RenderMessageDelta("content_filter"));
        Assert.Contains("\"end_turn\"", AnthropicProtocol.RenderMessageDelta("stop"));
    }

    [Fact]
    public void Anthropic_error_frame_uses_its_own_error_vocabulary()
    {
        var frame = AnthropicProtocol.RenderErrorFrame(new ChatUpstreamError("boom", "auth_error", 401));

        Assert.StartsWith("event: error", frame);
        Assert.Contains("\"type\":\"authentication_error\"", frame);
        Assert.Contains("boom", frame);
        // 流内错误之后不得再发 message_stop，否则客户端会当成正常收尾
        Assert.DoesNotContain("message_stop", frame);
    }

    // ==================== Responses 请求解析 ====================

    [Fact]
    public void Responses_accepts_a_plain_string_input()
    {
        var req = ResponsesProtocol.ParseRequest("""{"model":"trae","input":"你好"}""");

        Assert.Equal("trae", req.Model);
        Assert.Single(req.Messages);
        Assert.Equal("user", req.Messages[0].Role);
        Assert.Equal("你好", req.Messages[0].Content);
        Assert.False(req.Stream);
    }

    [Fact]
    public void Responses_puts_instructions_in_front_as_a_system_message()
    {
        var req = ResponsesProtocol.ParseRequest(
            """{"model":"trae","instructions":"你是助手","input":"你好"}""");

        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("你是助手", req.Messages[0].Content);
    }

    [Fact]
    public void Responses_reads_item_arrays_and_flattens_content_blocks()
    {
        var req = ResponsesProtocol.ParseRequest("""
            {"model":"trae","input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"前"},{"type":"input_image","image_url":"x"}]},
              {"type":"message","role":"assistant","content":"后"}
            ]}
            """);

        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("前\n[image]", req.Messages[0].Content);
        Assert.Equal("assistant", req.Messages[1].Role);
    }

    [Fact]
    public void Responses_skips_non_message_items()
    {
        var req = ResponsesProtocol.ParseRequest("""
            {"model":"trae","input":[
              {"type":"function_call","name":"f","arguments":"{}"},
              {"type":"message","role":"user","content":"留下"}
            ]}
            """);

        Assert.Single(req.Messages);
        Assert.Equal("留下", req.Messages[0].Content);
    }

    [Fact]
    public void Responses_maps_output_token_and_sampling_parameters()
    {
        var req = ResponsesProtocol.ParseRequest(
            """{"model":"trae","input":"x","max_output_tokens":64,"temperature":0.25,"stream":true,"store":true}""");

        Assert.Equal(64, req.MaxTokens ?? 0);
        Assert.Equal(0.25, req.Temperature ?? 0);
        Assert.True(req.Stream);
    }

    [Fact]
    public void Responses_rejects_a_non_object_body()
        => Assert.Throws<ChatRequestException>(
            () => ResponsesProtocol.ParseRequest("[1,2,3]"));

    [Fact]
    public void Responses_rejects_unparsable_input()
    {
        var ex = Assert.Throws<ChatRequestException>(
            () => ResponsesProtocol.ParseRequest("""{"model":"trae","input":42}"""));

        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_request_error", ex.Type);
    }

    [Fact]
    public void Responses_requires_a_model()
        => Assert.Throws<ChatRequestException>(
            () => ResponsesProtocol.ParseRequest("""{"input":"x"}"""));

    // ==================== Responses 输出形状 ====================

    [Fact]
    public void Responses_non_stream_body_matches_the_responses_shape()
    {
        var body = ResponsesProtocol.RenderResponse(
            new ChatCompletion("答案", "stop", "trae"), "resp_1", Unix);

        Assert.Contains("\"object\":\"response\"", body);
        Assert.Contains("\"status\":\"completed\"", body);
        Assert.Contains("\"parallel_tool_calls\":true", body);
        Assert.Contains("\"tool_choice\":\"auto\"", body);
        Assert.Contains("\"tools\":[]", body);
        Assert.Contains("\"error\":null", body);
        Assert.Contains("\"stop_reason\":null", body);
        Assert.Contains("\"id\":\"resp_1_msg_0\"", body);
        Assert.Contains("\"type\":\"output_text\"", body);
        Assert.Contains("答案", body);
    }

    [Fact]
    public void Responses_marks_a_length_limited_answer_as_incomplete()
    {
        var body = ResponsesProtocol.RenderResponse(
            new ChatCompletion("半句", "length", "trae"), "resp_1", Unix);

        Assert.Contains("\"status\":\"incomplete\"", body);
        Assert.Contains("\"reason\":\"max_output_tokens\"", body);
    }

    [Fact]
    public void Responses_created_event_is_the_only_one_with_a_sequence_number()
    {
        var created = ResponsesProtocol.RenderCreated("resp_1", "trae", Unix);
        var added = ResponsesProtocol.RenderOutputItemAdded("resp_1");
        var delta = ResponsesProtocol.RenderTextDelta("resp_1", "答");
        var done = ResponsesProtocol.RenderOutputItemDone("resp_1", "答");
        var completed = ResponsesProtocol.RenderCompleted("resp_1", "trae", Unix, "答",
            UpstreamOutcome.Done("stop", promptTokens: 1, completionTokens: 2));

        Assert.StartsWith("event: response.created", created);
        Assert.Contains("\"sequence_number\":0", created);
        Assert.Contains("\"status\":\"in_progress\"", created);

        Assert.StartsWith("event: response.output_item.added", added);
        Assert.Contains("\"id\":\"resp_1_msg_0\"", added);
        Assert.DoesNotContain("sequence_number", added);

        Assert.StartsWith("event: response.output_text.delta", delta);
        Assert.Contains("\"content_index\":0", delta);
        Assert.DoesNotContain("sequence_number", delta);

        Assert.StartsWith("event: response.output_item.done", done);
        Assert.Contains("\"status\":\"completed\"", done);

        Assert.StartsWith("event: response.completed", completed);
        Assert.Contains("\"input_tokens\":1", completed);
        Assert.Contains("\"output_tokens\":2", completed);
        Assert.Contains("\"total_tokens\":3", completed);
        // Responses 的流没有 [DONE] 标记
        Assert.DoesNotContain("[DONE]", created + added + delta + done + completed);
    }

    [Fact]
    public void Responses_failed_event_keeps_an_empty_output()
    {
        var frame = ResponsesProtocol.RenderFailed("resp_1", "trae", Unix,
            new ChatUpstreamError("上游流中断", "upstream_error", 502));

        Assert.StartsWith("event: response.failed", frame);
        Assert.Contains("\"status\":\"failed\"", frame);
        Assert.Contains("\"output\":[]", frame);
        Assert.Contains("上游流中断", frame);
    }

    [Fact]
    public void Responses_error_body_reuses_the_openai_error_vocabulary()
    {
        var body = ResponsesProtocol.RenderError(new ChatUpstreamError("boom", "rate_limit_error", 429));

        Assert.Contains("\"type\":\"rate_limit_error\"", body);
        Assert.Contains("boom", body);
    }
}
