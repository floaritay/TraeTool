using System.Text.Json;
using TraeTool.Services;
using TraeTool.Services.Gateway;
using Xunit;

namespace TraeTool.Tests;

public class ChatProtocolTests
{
    private const string BasicBody = """
        {
          "model": "trae",
          "messages": [
            {"role": "system", "content": "你是助手"},
            {"role": "user", "content": "你好"}
          ],
          "stream": false,
          "temperature": 0.2,
          "max_tokens": 512
        }
        """;

    [Fact]
    public void ParseRequest_reads_fields()
    {
        var req = ChatProtocol.ParseRequest(BasicBody);

        Assert.Equal("trae", req.Model);
        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("你好", req.LastUserMessage);
        Assert.False(req.Stream);
        Assert.Equal(0.2, req.Temperature);
        Assert.Equal(512, req.MaxTokens);
    }

    [Fact]
    public void ParseRequest_accepts_stream_flag()
    {
        var req = ChatProtocol.ParseRequest("""{"model":"m","messages":[{"role":"user","content":"x"}],"stream":true}""");

        Assert.True(req.Stream);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("\"0\"", false)]
    public void ParseRequest_stream_only_true_variants_enable_streaming(string raw, bool expected)
    {
        // 任意字符串都当 true 会让 "false" 也被判成流式，与另两个协议不一致
        var req = ChatProtocol.ParseRequest(
            $$"""{"model":"m","messages":[{"role":"user","content":"x"}],"stream":{{raw}}}""");

        Assert.Equal(expected, req.Stream);
    }

    [Fact]
    public void MergeToolCalls_appends_incremental_arguments_by_index()
    {
        var first = """[{"index":0,"id":"c1","type":"function","function":{"name":"f","arguments":"{\"a\":1"}}]""";
        var next = """[{"index":0,"function":{"arguments":",\"b\":2}"}}]""";

        var merged = ChatProtocol.MergeToolCalls(first, next);

        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged!);
        var call = doc.RootElement[0];
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.Equal("f", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"a\":1,\"b\":2}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.False(call.TryGetProperty("index", out _));
    }

    [Fact]
    public void MergeToolCalls_whole_frame_replaces_when_it_contains_previous_arguments()
    {
        var first = """[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"a\":1}"}}]""";
        var whole = """[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"a\":1,\"b\":2}"}}]""";

        var merged = ChatProtocol.MergeToolCalls(first, whole);

        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged!);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("{\"a\":1,\"b\":2}",
            doc.RootElement[0].GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void RenderCompletion_emits_tool_calls_and_usage()
    {
        var completion = new ChatCompletion("", "tool_calls", "trae",
            """[{"id":"c1","type":"function","function":{"name":"f","arguments":"{}"}}]""", 7, 5);
        var json = ChatProtocol.RenderCompletion(completion, "chatcmpl-1", 1);
        using var doc = JsonDocument.Parse(json);

        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal("c1", message.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        var usage = doc.RootElement.GetProperty("usage");
        Assert.Equal(7, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ParseRequest_flattens_array_content()
    {
        var req = ChatProtocol.ParseRequest("""
            {"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}
            """);

        Assert.Equal("ab", req.Messages[0].Content);
    }

    [Fact]
    public void ParseRequest_missing_model_is_400()
    {
        var ex = Assert.Throws<ChatRequestException>(() =>
            ChatProtocol.ParseRequest("""{"messages":[{"role":"user","content":"x"}]}"""));

        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_request_error", ex.Type);
    }

    [Fact]
    public void ParseRequest_empty_messages_is_400()
    {
        var ex = Assert.Throws<ChatRequestException>(() =>
            ChatProtocol.ParseRequest("""{"model":"m","messages":[]}"""));

        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void RenderCompletion_is_openai_shape()
    {
        var json = ChatProtocol.RenderCompletion(new ChatCompletion("hi", "stop", "trae"), "chatcmpl-1", 1700000000);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("trae", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void RenderChunk_frames_are_sse()
    {
        var start = ChatProtocol.RenderChunk(new ChatCompletion("hi", "stop", "trae"), "id", 1, done: false, startChunk: true);
        var body = ChatProtocol.RenderChunk(new ChatCompletion("hi", "stop", "trae"), "id", 1, done: false, startChunk: false);
        var fin = ChatProtocol.RenderChunk(new ChatCompletion("hi", "stop", "trae"), "id", 1, done: true, startChunk: false);

        Assert.StartsWith("data: ", start);
        Assert.Contains("\"role\":\"assistant\"", start);
        Assert.Contains("\"content\":\"hi\"", body);
        Assert.Contains("\"finish_reason\":\"stop\"", fin);
        Assert.Equal("data: [DONE]\n\n", ChatProtocol.DoneMarker);
    }

    [Fact]
    public void RenderModels_is_openai_list()
    {
        var json = ChatProtocol.RenderModels(new[] { "trae" }, 1);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("trae", doc.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal("model", doc.RootElement.GetProperty("data")[0].GetProperty("object").GetString());
    }

    [Fact]
    public void RenderError_has_openai_error_envelope()
    {
        var json = ChatProtocol.RenderError(new ChatUpstreamError("boom", "upstream_error", 502));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("boom", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("invalid_request_error", "invalid_request_error")]
    [InlineData("auth_error", "authentication_error")]
    [InlineData("permission_denied", "permission_error")]
    [InlineData("not_found_error", "not_found_error")]
    [InlineData("rate_limit_error", "rate_limit_error")]
    [InlineData("insufficient_quota", "insufficient_quota")]
    [InlineData("upstream_error", "api_error")]
    [InlineData("api_error", "api_error")]
    [InlineData("没登记的类型", "api_error")]
    public void MapErrorType_normalizes_to_openai_vocabulary(string internalType, string expected)
    {
        Assert.Equal(expected, ChatProtocol.MapErrorType(internalType));
    }

    [Fact]
    public void ParseRequest_bad_json_throws_json_exception()
    {
        Assert.ThrowsAny<JsonException>(() => ChatProtocol.ParseRequest("{ not json"));
    }
}
