using System.Text.Json;
using TraeTool.Services;
using TraeTool.Services.Gateway;
using Xunit;

namespace TraeTool.Tests;

public class AnthropicProtocolTests
{
    [Fact]
    public void ParseRequest_takes_system_as_first_message()
    {
        var req = AnthropicProtocol.ParseRequest("""
            {"model":"claude-x","max_tokens":1024,"system":"你是一个助手",
             "messages":[{"role":"user","content":"你好"}]}
            """);

        Assert.Equal("claude-x", req.Model);
        Assert.Equal(1024, req.MaxTokens);
        Assert.False(req.Stream);
        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("你是一个助手", req.Messages[0].Content);
        Assert.Equal("user", req.Messages[1].Role);
    }

    [Fact]
    public void ParseRequest_flattens_text_and_tool_blocks()
    {
        var req = AnthropicProtocol.ParseRequest("""
            {"model":"m","max_tokens":10,
             "messages":[{"role":"assistant","content":[{"type":"text","text":"answer"}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}]}
            """);

        Assert.Contains("answer", req.Messages[0].Content);
        Assert.Contains("[tool_result]", req.Messages[1].Content);
        Assert.Contains("t1", req.Messages[1].Content);
    }

    [Fact]
    public void ParseRequest_stream_flag()
    {
        var req = AnthropicProtocol.ParseRequest(
            """{"model":"m","max_tokens":10,"stream":true,"messages":[{"role":"user","content":"x"}]}""");

        Assert.True(req.Stream);
    }

    [Fact]
    public void ParseRequest_missing_model_is_400()
    {
        var ex = Assert.Throws<ChatRequestException>(() =>
            AnthropicProtocol.ParseRequest("""{"max_tokens":10,"messages":[{"role":"user","content":"x"}]}"""));

        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void ParseRequest_empty_messages_is_400()
    {
        var ex = Assert.Throws<ChatRequestException>(() =>
            AnthropicProtocol.ParseRequest("""{"model":"m","max_tokens":10,"messages":[]}"""));

        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void RenderResponse_is_anthropic_message_shape()
    {
        var json = AnthropicProtocol.RenderResponse(new ChatCompletion("hello", "stop", "real-id"), "msg_1", "real-id");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("assistant", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal("real-id", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("text", doc.RootElement.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void RenderResponse_maps_length_to_max_tokens()
    {
        var json = AnthropicProtocol.RenderResponse(new ChatCompletion("x", "length", "m"), "msg_1", "m");

        Assert.Contains("\"stop_reason\":\"max_tokens\"", json);
    }

    [Fact]
    public void RenderFrames_follows_anthropic_event_sequence()
    {
        var frames = AnthropicProtocol.RenderFrames(
            new ChatCompletion("hi", "stop", "m"), "msg_1", "m").ToList();

        Assert.Equal(6, frames.Count);
        Assert.StartsWith("event: message_start\n", frames[0]);
        Assert.StartsWith("event: content_block_start\n", frames[1]);
        Assert.StartsWith("event: content_block_delta\n", frames[2]);
        Assert.Contains("\"type\":\"text_delta\"", frames[2]);
        Assert.StartsWith("event: content_block_stop\n", frames[3]);
        Assert.StartsWith("event: message_delta\n", frames[4]);
        Assert.Contains("end_turn", frames[4]);
        Assert.StartsWith("event: message_stop\n", frames[5]);
        Assert.All(frames, f => Assert.Contains("data: ", f));
    }

    [Fact]
    public void RenderError_uses_anthropic_error_envelope()
    {
        var json = AnthropicProtocol.RenderError(new ChatUpstreamError("boom", "api_error", 502));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("boom", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("invalid_request_error", 400, "invalid_request_error")]
    [InlineData("auth_error", 502, "authentication_error")]
    [InlineData("permission_denied", 403, "permission_error")]
    [InlineData("not_found_error", 404, "not_found_error")]
    [InlineData("rate_limit_error", 429, "rate_limit_error")]
    [InlineData("insufficient_quota", 503, "rate_limit_error")]
    [InlineData("upstream_error", 502, "api_error")]
    [InlineData("upstream_error", 400, "invalid_request_error")]
    public void MapErrorType_normalizes_to_anthropic_vocabulary(string internalType, int status, string expected)
    {
        Assert.Equal(expected, AnthropicProtocol.MapErrorType(new ChatUpstreamError("x", internalType, status)));
    }
}
