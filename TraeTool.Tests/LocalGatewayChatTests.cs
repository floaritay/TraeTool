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

/// <summary>网关 /v1/* 转发：用假上游覆盖成功、流式、换号失败、池空、鉴权与开关。</summary>
public class LocalGatewayChatTests
{
    private sealed class FakeUpstream : IChatUpstream
    {
        public List<string> Seen { get; } = new();
        public Func<Account, ChatRequest, UpstreamResult>? Behaviour { get; set; }

        public async IAsyncEnumerable<UpstreamDelta> StreamAsync(Account account, ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Seen.Add(account.Id);
            var res = Behaviour?.Invoke(account, request)
                      ?? UpstreamResult.From(new ChatCompletion("来自 " + account.Id, "stop", request.Model));
            if (!res.Ok)
            {
                yield return UpstreamDelta.Terminal(UpstreamOutcome.Failed(res.Error!));
                yield break;
            }
            yield return new UpstreamDelta(Content: res.Completion!.Content);
            yield return UpstreamDelta.Terminal(UpstreamOutcome.Done(res.Completion.FinishReason));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public FakeUpstream Upstream { get; } = new();
        public AppSettings Cfg { get; }
        public LocalGateway Gateway { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };

        public const string Token = "gw-token";

        public Fixture(Action<AppSettings>? setup = null, Func<Account, Task<bool>>? ensureToken = null)
        {
            Cfg = new AppSettings
            {
                GatewayEnabled = true,
                GatewayChatEnabled = true,
                GatewayToken = Token,
            };
            setup?.Invoke(Cfg);
            // 凭据校验一律注入桩：转发测试不打真实网络
            Gateway = new LocalGateway(Upstream, () => Cfg,
                ensureToken: ensureToken ?? (_ => Task.FromResult(true)));
            for (int i = 0; i < 20 && !Gateway.IsRunning; i++)
            {
                Cfg.GatewayPort = 19600 + i;
                Gateway.Start(Cfg);
            }
            if (!Gateway.IsRunning) throw new InvalidOperationException("无法绑定测试端口");
        }

        public HttpRequestMessage Chat(string json, bool withToken = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Gateway.Endpoint + "v1/chat/completions");
            if (withToken) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
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

    private static string Body(string model = "trae", bool stream = false)
        => $$"""
           {"model":"{{model}}","messages":[{"role":"user","content":"hi"}],"stream":{{(stream ? "true" : "false")}}}
           """;

    [Fact]
    public async Task Models_requires_token()
    {
        using var fx = new Fixture();
        var resp = await fx.Client.GetAsync(fx.Gateway.Endpoint + "v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Models_lists_pool_models_when_enabled()
    {
        using var fx = new Fixture();
        using var req = new HttpRequestMessage(HttpMethod.Get, fx.Gateway.Endpoint + "v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.Token);
        var resp = await fx.Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("trae", body);
        Assert.Contains("\"model\"", body);
    }

    [Fact]
    public async Task Models_is_403_when_forwarding_disabled()
    {
        using var fx = new Fixture(c => c.GatewayChatEnabled = false);
        using var req = new HttpRequestMessage(HttpMethod.Get, fx.Gateway.Endpoint + "v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.Token);
        var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Chat_requires_token()
    {
        using var fx = new Fixture();
        var resp = await fx.Client.SendAsync(fx.Chat(Body(), withToken: false));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("invalid_request_error", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_returns_openai_completion_from_pool()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        var resp = await fx.Client.SendAsync(fx.Chat(Body()));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"object\":\"chat.completion\"", body);
        Assert.Contains("来自 a1", body);
        Assert.Equal(new[] { "a1" }, fx.Upstream.Seen);
    }

    [Fact]
    public async Task Chat_stream_returns_sse_frames()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        var resp = await fx.Client.SendAsync(fx.Chat(Body(stream: true)));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: ", body);
        Assert.Contains("data: [DONE]", body);
    }

    [Fact]
    public async Task Chat_is_503_when_pool_has_no_account()
    {
        using var fx = new Fixture();
        var resp = await fx.Client.SendAsync(fx.Chat(Body()));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("insufficient_quota", body);
        Assert.Empty(fx.Upstream.Seen);
    }

    [Fact]
    public async Task Chat_fails_over_to_next_account_on_auth_error()
    {
        using var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }));
        fx.Upstream.Behaviour = (acc, _) => acc.Id == "a1"
            ? UpstreamResult.Fail("凭据被上游拒绝（HTTP 401）", "auth_error", 401)
            : UpstreamResult.From(new ChatCompletion("ok via a2", "stop", "trae"));

        var resp = await fx.Client.SendAsync(fx.Chat(Body()));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("ok via a2", body);
        Assert.Equal(new[] { "a1", "a2" }, fx.Upstream.Seen);
        Assert.Equal(1, fx.Gateway.CoolingAccounts(DateTime.Now));
    }

    [Fact]
    public async Task Chat_maps_single_account_auth_failure_to_502()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        fx.Upstream.Behaviour = (_, _) => UpstreamResult.Fail("凭据被上游拒绝（HTTP 401）", "auth_error", 401);

        var resp = await fx.Client.SendAsync(fx.Chat(Body()));
        var body = await resp.Content.ReadAsStringAsync();

        // 不回 401：客户端的 Bearer 是对的，失效的是池里的账号
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        // 内部类型 auth_error 在出口归一化为 OpenAI 词表里的 authentication_error
        Assert.Contains("authentication_error", body);
    }

    [Fact]
    public async Task Chat_fails_over_when_one_account_credential_is_dead()
    {
        // 池里 a1 的凭据不可用：只跳过它，不要让整个请求失败在它身上
        using var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }),
            ensureToken: acc => Task.FromResult(acc.Id != "a1"));

        var resp = await fx.Client.SendAsync(fx.Chat(Body()));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("来自 a2", body);
        Assert.Equal(new[] { "a2" }, fx.Upstream.Seen); // a1 根本没被调用
        Assert.Equal(1, fx.Gateway.CoolingAccounts(DateTime.Now)); // a1 进冷却
    }

    [Fact]
    public async Task Chat_is_503_when_every_credential_is_dead()
    {
        using var fx = new Fixture(c => c.Accounts.AddRange(new[] { Acc("a1"), Acc("a2") }),
            ensureToken: _ => Task.FromResult(false));

        var resp = await fx.Client.SendAsync(fx.Chat(Body()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Empty(fx.Upstream.Seen);
    }

    [Fact]
    public async Task Messages_requires_token()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        using var req = new HttpRequestMessage(HttpMethod.Post, fx.Gateway.Endpoint + "v1/messages")
        {
            Content = new StringContent(
                """{"model":"trae","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };
        var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Messages_accepts_anthropic_style_x_api_key()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        using var req = new HttpRequestMessage(HttpMethod.Post, fx.Gateway.Endpoint + "v1/messages")
        {
            Content = new StringContent(
                """{"model":"trae","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", Fixture.Token);
        var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("来自 a1", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_accepts_anthropic_style_x_api_key()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        using var req = fx.Chat(Body(), withToken: false);
        req.Headers.TryAddWithoutValidation("x-api-key", Fixture.Token);
        var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Chat_rejects_malformed_body_with_400()
    {
        using var fx = new Fixture(c => c.Accounts.Add(Acc("a1")));
        var resp = await fx.Client.SendAsync(fx.Chat("""{"model":"m","messages":[]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(fx.Upstream.Seen);
    }
}
