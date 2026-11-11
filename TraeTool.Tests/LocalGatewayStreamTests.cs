using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Gateway;
using Xunit;

namespace TraeTool.Tests;

/// <summary>
/// 网关流式转发：逐增量转发（不是攒完整段再发）、首字节超时/预 token 错误换号、
/// 中途截断补收尾、客户端断开取消上游、三种协议的流式形状。
/// </summary>
public class LocalGatewayStreamTests
{
    /// <summary>脚本化的假上游：按 (等待, 增量) 列表产出，可断言是否被取消。</summary>
    private sealed class ScriptedUpstream : IChatUpstream
    {
        public List<string> Seen { get; } = new();

        /// <summary>每个账号的脚本；返回 null 表示用兜底脚本（一个正文增量 + 正常收尾）。</summary>
        public Func<Account, ChatRequest, List<(TimeSpan Delay, UpstreamDelta Delta)>?>? Script { get; set; }

        /// <summary>上游枚举因取消而结束（客户端断开或首字节超时）。</summary>
        public bool Canceled { get; private set; }

        public async IAsyncEnumerable<UpstreamDelta> StreamAsync(Account account, ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Seen.Add(account.Id);
            var script = Script?.Invoke(account, request) ?? new List<(TimeSpan, UpstreamDelta)>
            {
                (TimeSpan.Zero, new UpstreamDelta(Content: "来自 " + account.Id)),
                (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("stop"))),
            };

            try
            {
                foreach (var (delay, delta) in script)
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                    yield return delta;
                }
            }
            finally
            {
                if (ct.IsCancellationRequested) Canceled = true;
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ScriptedUpstream Upstream { get; } = new();
        public AppSettings Cfg { get; }
        public LocalGateway Gateway { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

        public const string Token = "{{SECRET_7wjxl9bd}}";

        public Fixture(Action<AppSettings>? setup = null)
        {
            Cfg = new AppSettings { GatewayEnabled = true, GatewayChatEnabled = true, GatewayToken = Token };
            setup?.Invoke(Cfg);
            Gateway = new LocalGateway(Upstream, () => Cfg, ensureToken: _ => Task.FromResult(true));
            for (int i = 0; i < 20 && !Gateway.IsRunning; i++)
            {
                Cfg.GatewayPort = 19700 + i;
                Gateway.Start(Cfg);
            }
            if (!Gateway.IsRunning) throw new InvalidOperationException("无法绑定测试端口");
        }

        public HttpRequestMessage Post(string path, string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Gateway.Endpoint + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        public void Dispose()
        {
            Gateway.Stop();
            Client.Dispose();
        }
    }

    private static Account Acc(string id) => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        Token = "token-of-" + id,
        DeviceId = "1000000000000001",
    };

    private static string ChatBody(bool stream = true)
        => $$"""{"model":"trae","messages":[{"role":"user","content":"hi"}],"stream":{{(stream ? "true" : "false")}}}""";

    private const string MessagesBody =
        """{"model":"trae","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"stream":true}""";

    private const string ResponsesBody =
        """{"model":"trae","input":"hi","stream":true}""";

    private static string Text(UpstreamDelta delta) => delta.Content!;

    /// <summary>读完整条响应体（非流式或短流式用例）。</summary>
    private static string ReadAll(HttpResponseMessage resp)
        => resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    [Fact]
    public void Stream_forwards_each_delta_before_the_upstream_finishes()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) =>
        [
            (TimeSpan.Zero, new UpstreamDelta(Content: "第一段")),
            (TimeSpan.FromMilliseconds(800), UpstreamDelta.Terminal(UpstreamOutcome.Done("stop"))),
        ];

        using var _ = fx;
        var sw = Stopwatch.StartNew();
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var reader = new StreamReader(resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult());
        TimeSpan? firstDeltaAt = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (firstDeltaAt == null && line.StartsWith("data: ") && line.Contains("第一段"))
                firstDeltaAt = sw.Elapsed;
            if (line.Contains("[DONE]")) break;
        }
        var total = sw.Elapsed;

        Assert.NotNull(firstDeltaAt);
        // 首字节必须远早于末字节：这是「边收边发」与「攒完整段再发」的差别
        Assert.True(firstDeltaAt < TimeSpan.FromMilliseconds(500), $"首字节 {firstDeltaAt}");
        Assert.True(total >= TimeSpan.FromMilliseconds(600), $"总时长 {total}");
    }

    [Fact]
    public void Stream_switches_account_when_first_token_never_arrives()
    {
        var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }));
        fx.Gateway.FirstTokenTimeout = TimeSpan.FromMilliseconds(200);
        fx.Upstream.Script = (acc, _) => acc.Id == "a1"
            ? [(TimeSpan.FromSeconds(5), new UpstreamDelta(Content: "太晚了"))]
            : [(TimeSpan.Zero, new UpstreamDelta(Content: "来自 a2")),
               (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("stop")))];

        using var _ = fx;
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("来自 a2", body);
        Assert.Equal(new[] { "a1", "a2" }, fx.Upstream.Seen);
        Assert.True(fx.Upstream.Canceled);
    }

    [Fact]
    public void Stream_switches_account_on_pre_token_upstream_error()
    {
        var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }));
        fx.Upstream.Script = (acc, _) => acc.Id == "a1"
            ? [(TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                new ChatUpstreamError("上游已返回完成但没有文本内容", "empty_completion", 502))))]
            : [(TimeSpan.Zero, new UpstreamDelta(Content: "来自 a2")),
               (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("stop")))];

        using var _ = fx;
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("来自 a2", ReadAll(resp));
        Assert.Equal(new[] { "a1", "a2" }, fx.Upstream.Seen);
    }

    [Fact]
    public void Stream_closes_with_a_terminal_frame_when_upstream_just_stops()
    {
        var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }));
        // a1 只吐了一段就断了（没有终止帧）：已提交的内容要原样发完并补收尾，不能再换号重来
        fx.Upstream.Script = (_, _) => [(TimeSpan.Zero, new UpstreamDelta(Content: "断在这里"))];

        using var _ = fx;
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("断在这里", body);
        Assert.Contains("[DONE]", body);
        Assert.Contains("\"finish_reason\":\"stop\"", body);
        Assert.Equal(new[] { "a1" }, fx.Upstream.Seen);
    }

    [Fact]
    public void Client_disconnect_does_not_penalize_the_account_or_wedge_the_gateway()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        // 流要长到「客户端挂断时请求仍在进行中」
        var chunk = new string('x', 2048);
        var script = new List<(TimeSpan, UpstreamDelta)>();
        for (int i = 0; i < 40; i++)
            script.Add((TimeSpan.FromMilliseconds(20), new UpstreamDelta(Content: chunk)));
        fx.Upstream.Script = (_, _) => script;

        using var _ = fx;
        using var cts = new CancellationTokenSource();
        using var req = fx.Post("v1/chat/completions", ChatBody());
        var resp = fx.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .GetAwaiter().GetResult();
        var reader = new StreamReader(resp.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult());
        reader.ReadLine();       // 至少读到一帧，确保账号已提交、上游已在流中

        // 客户端挂断：关掉响应流并取消请求
        reader.Dispose();
        resp.Dispose();
        cts.Cancel();
        Thread.Sleep(300);

        // 断连属于客户端行为：账号照常释放，不进冷却，网关继续服务后续请求
        Assert.Equal(0, fx.Gateway.CoolingAccounts(DateTime.Now));
        using var again = fx.Post("v1/chat/completions", ChatBody());
        using var ok = fx.Client.SendAsync(again).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public void OpenAi_stream_carries_reasoning_deltas()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) =>
        [
            (TimeSpan.Zero, new UpstreamDelta(Reasoning: "先想一想")),
            (TimeSpan.Zero, new UpstreamDelta(Content: "答案")),
            (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("stop", promptTokens: 5, completionTokens: 6))),
        ];

        using var _ = fx;
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Contains("先想一想", body);
        Assert.Contains("\"reasoning_content\"", body);
        Assert.Contains("答案", body);   // 正文照常外发
        Assert.Contains("\"prompt_tokens\":5", body);
        Assert.Contains("\"completion_tokens\":6", body);
    }

    [Fact]
    public void Anthropic_stream_never_emits_reasoning()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) =>
        [
            (TimeSpan.Zero, new UpstreamDelta(Reasoning: "先想一想")),
            (TimeSpan.Zero, new UpstreamDelta(Content: "答案")),
            (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("length"))),
        ];

        using var _ = fx;
        using var req = fx.Post("v1/messages", MessagesBody);
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("答案", body);
        Assert.DoesNotContain("先想一想", body);
        // Anthropic 的结束原因词表与 OpenAI 不同
        Assert.Contains("\"stop_reason\":\"max_tokens\"", body);
        Assert.Contains("message_stop", body);
    }

    [Fact]
    public void Responses_stream_uses_its_own_event_sequence_and_no_done_marker()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) =>
        [
            (TimeSpan.Zero, new UpstreamDelta(Content: "答")),
            (TimeSpan.Zero, new UpstreamDelta(Content: "案")),
            (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Done("stop"))),
        ];

        using var _ = fx;
        using var req = fx.Post("v1/responses", ResponsesBody);
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("response.created", body);
        Assert.Contains("response.output_text.delta", body);
        Assert.Contains("response.output_item.done", body);
        Assert.Contains("response.completed", body);
        Assert.DoesNotContain("[DONE]", body);
        // 事件顺序：created 在 delta 之前，completed 在最后
        Assert.True(body.IndexOf("response.created", StringComparison.Ordinal) <
                    body.IndexOf("response.output_text.delta", StringComparison.Ordinal));
        Assert.True(body.IndexOf("response.output_item.done", StringComparison.Ordinal) <
                    body.IndexOf("response.completed", StringComparison.Ordinal));
    }

    [Fact]
    public void Responses_non_stream_returns_a_response_object()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        using var _ = fx;
        using var req = fx.Post("v1/responses", """{"model":"trae","input":"hi"}""");
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"object\":\"response\"", body);
        Assert.Contains("\"status\":\"completed\"", body);
        Assert.Contains("\"output_text\"", body);
        Assert.Contains("\"input_tokens\"", body);
    }

    [Fact]
    public void Responses_stream_error_after_commit_is_reported_as_response_failed()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) =>
        [
            (TimeSpan.Zero, new UpstreamDelta(Content: "开头")),
            (TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                new ChatUpstreamError("上游流中断", "upstream_error", 502), truncated: true))),
        ];

        using var _ = fx;
        using var req = fx.Post("v1/responses", ResponsesBody);
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        Assert.Contains("response.failed", body);
        Assert.Contains("\"status\":\"failed\"", body);
        Assert.DoesNotContain("response.completed", body);
    }

    [Fact]
    public void Stream_fails_before_commit_with_a_plain_json_error_body()
    {
        var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Script = (_, _) => [(TimeSpan.Zero, UpstreamDelta.Terminal(UpstreamOutcome.Failed(
            new ChatUpstreamError("请求体不被接受", "invalid_request_error", 400))))];

        using var _ = fx;
        using var req = fx.Post("v1/chat/completions", ChatBody());
        using var resp = fx.Client.SendAsync(req).GetAwaiter().GetResult();
        var body = ReadAll(resp);

        // 还没写出任何字节，所以是普通 JSON 错误体（不是 SSE）
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.StartsWith("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("invalid_request_error", body);
    }
}
