using TraeTool.Services;
using TraeTool.Services.Gateway;
using Xunit;

namespace TraeTool.Tests;

/// <summary>上游 SSE 逐行解析器：帧结算、结束原因、用量与错误路径（纯字符串，不碰网络）。</summary>
public class TraeSseParserTests
{
    /// <summary>把若干行喂进解析器，返回按序产出的增量。</summary>
    private static List<UpstreamDelta> Feed(TraeSseParser parser, params string[] lines)
    {
        var list = new List<UpstreamDelta>();
        foreach (var line in lines) list.AddRange(parser.Feed(line));
        return list;
    }

    [Fact]
    public void One_frame_per_blank_line_and_text_comes_out_immediately()
    {
        var parser = new TraeSseParser();

        // 空行之前不结算：客户端要的是「上游吐一段就转发一段」
        Assert.Empty(Feed(parser, "event: output"));
        Assert.Empty(Feed(parser, """data: {"response":"hi"}"""));

        var delta = Assert.Single(Feed(parser, ""));
        Assert.Equal("hi", delta.Content);
        Assert.Null(delta.Outcome);
    }

    [Fact]
    public void Multiple_data_lines_in_one_frame_are_joined()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: output",
            "data: {\"response\":\"a\",", "data: \"usage\":null}", ""));
        Assert.Equal("a", delta.Content);
    }

    [Fact]
    public void Done_frame_supplies_finish_reason_and_usage()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");
        Feed(parser, "event: done", """data: {"finish_reason":"length"}""", "");

        var tail = parser.Finish();
        var outcome = Assert.Single(tail).Outcome!;
        Assert.True(outcome.Ok);
        Assert.Equal("length", outcome.FinishReason);
        Assert.False(outcome.Truncated);
    }

    [Fact]
    public void Usage_frame_carries_real_token_counts()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");
        Feed(parser, "event: output", """data: {"usage":{"prompt_tokens":11,"completion_tokens":22}}""", "");
        Feed(parser, "data: [DONE]", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.Equal(11, outcome.PromptTokens);
        Assert.Equal(22, outcome.CompletionTokens);
    }

    [Fact]
    public void Usage_frame_accepts_input_output_naming()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");
        Feed(parser, "event: output", """data: {"input_tokens":7,"output_tokens":8}""", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.Equal(7, outcome.PromptTokens);
        Assert.Equal(8, outcome.CompletionTokens);
    }

    [Fact]
    public void Bare_done_marker_ends_the_stream()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");
        Feed(parser, "data: [DONE]", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.True(outcome.Ok);
        Assert.Equal("stop", outcome.FinishReason);
        Assert.False(outcome.Truncated);
    }

    [Fact]
    public void Error_frame_surfaces_upstream_code_as_business_error()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: error",
            """data: {"code":2001,"message":"resolveByUsage failed"}""", ""));

        var error = delta.Outcome!.Error!;
        Assert.Equal("api_error", error.Type);
        Assert.Equal(200, error.Status);
        Assert.Contains("2001", error.Message);
        Assert.Contains("resolveByUsage", error.Message);
        Assert.True(delta.IsTerminal);
    }

    [Fact]
    public void Broken_json_in_output_frame_is_a_format_error()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: output", "data: {not json}", ""));

        Assert.Contains("不是合法 JSON", delta.Outcome!.Error!.Message);
        Assert.Equal("upstream_error", delta.Outcome.Error.Type);
    }

    [Fact]
    public void Broken_json_in_meta_frame_is_ignored()
    {
        var parser = new TraeSseParser();
        Assert.Empty(Feed(parser, "event: meta", "data: {not json}", ""));
    }

    [Fact]
    public void Eof_without_done_keeps_the_text_and_marks_truncated()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.True(outcome.Ok);
        Assert.Equal("stop", outcome.FinishReason);
        Assert.True(outcome.Truncated);
    }

    [Fact]
    public void Done_without_any_text_is_an_empty_completion()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: done", """data: {"finish_reason":"stop"}""", "");

        var error = Assert.Single(parser.Finish()).Outcome!.Error!;
        Assert.Equal("empty_completion", error.Type);
        Assert.Equal(502, error.Status);
    }

    [Fact]
    public void No_frame_at_all_is_not_an_sse_body()
    {
        var parser = new TraeSseParser();
        Feed(parser, "<html>503</html>");

        var error = Assert.Single(parser.Finish()).Outcome!.Error!;
        Assert.Equal("upstream_error", error.Type);
        Assert.Contains("不是 SSE", error.Message);
    }

    [Fact]
    public void Tool_calls_upgrade_a_plain_stop_finish_reason()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output",
            """data: {"tool_calls":[{"id":"call_1","type":"function","function":{"name":"f","arguments":"{}"}}]}""", "");
        Feed(parser, "event: done", """data: {"finish_reason":"stop"}""", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.Equal("tool_calls", outcome.FinishReason);
        Assert.True(parser.SawToolCalls);
    }

    [Fact]
    public void Unknown_finish_reason_falls_back_to_stop()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output", """data: {"response":"x"}""", "");
        Feed(parser, "event: done", """data: {"finish_reason":"weird_new_word"}""", "");

        Assert.Equal("stop", Assert.Single(parser.Finish()).Outcome!.FinishReason);
    }

    [Fact]
    public void Reasoning_frame_is_kept_separate_from_content()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: output", """data: {"reasoning_content":"想一想"}""", ""));

        Assert.Null(delta.Content);
        Assert.Equal("想一想", delta.Reasoning);
        Assert.True(delta.HasPayload);
    }

    [Fact]
    public void Thought_event_is_not_dropped_by_the_compatibility_endpoint()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: thought", """data: {"text":"想"}""", ""));

        Assert.Equal("想", delta.Content);
    }

    [Fact]
    public void Completion_style_choices_are_read_and_finish_reason_is_remembered()
    {
        var parser = new TraeSseParser();
        var delta = Assert.Single(Feed(parser, "event: output",
            """data: {"choices":[{"text":"abc","finish_reason":"length"}]}""", ""));
        Assert.Equal("abc", delta.Content);

        // 结束原因来自 choices 帧；没有终止帧时仍按截断处理（上游没明说「我说完了」）
        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.Equal("length", outcome.FinishReason);
        Assert.True(outcome.Truncated);
    }

    [Fact]
    public void Finish_reason_from_a_choice_is_not_a_substitute_for_a_done_frame()
    {
        var parser = new TraeSseParser();
        Feed(parser, "event: output",
            """data: {"choices":[{"text":"abc","finish_reason":"stop"}]}""", "");
        Feed(parser, "data: [DONE]", "");

        var outcome = Assert.Single(parser.Finish()).Outcome!;
        Assert.Equal("stop", outcome.FinishReason);
        Assert.False(outcome.Truncated);
    }
}
