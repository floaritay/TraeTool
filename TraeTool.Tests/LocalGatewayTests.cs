using System.Net;
using System.Net.Http;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Gateway;
using Xunit;

namespace TraeTool.Tests;

/// <summary>
/// 本机管理网关路由与鉴权（只覆盖不需要数据库的分支，保持测试可离线运行）。
/// </summary>
public class LocalGatewayTests
{
    private sealed class GatewayFixture : IDisposable
    {
        public LocalGateway Gateway { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>取一个能绑定成功的端口（CI 上端口偶发占用时换一个）。</summary>
        public GatewayFixture(int preferredPort)
        {
            for (int i = 0; i < 20; i++)
            {
                var gw = new LocalGateway();
                var c = new AppSettings
                {
                    GatewayEnabled = true,
                    GatewayPort = preferredPort + i,
                    GatewayToken = "test-token",
                };
                if (gw.Start(c)) { Gateway = gw; return; }
                gw.Stop();
            }
            throw new InvalidOperationException("无法绑定测试端口");
        }

        public Uri Url(string path) => new($"{Gateway.Endpoint}{path.TrimStart('/')}");

        public void Dispose()
        {
            Gateway.Stop();
            Client.Dispose();
        }
    }

    private static GatewayFixture Open(int port) => new(port);

    [Fact]
    public void Healthz_requires_no_token()
    {
        using var fx = Open(19080);
        var resp = fx.Client.GetAsync(fx.Url("/healthz")).Result;

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"ok\":true", resp.Content.ReadAsStringAsync().Result);
    }

    [Fact]
    public void Root_index_lists_endpoints_without_token()
    {
        using var fx = Open(19100);
        var body = fx.Client.GetStringAsync(fx.Url("/")).Result;

        Assert.Contains("/healthz", body);
        Assert.Contains("/checkin", body);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/checkin")]
    public void Protected_endpoints_reject_missing_token(string path)
    {
        using var fx = Open(19120);
        var resp = fx.Client.GetAsync(fx.Url(path)).Result;

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public void Protected_endpoints_reject_wrong_token()
    {
        using var fx = Open(19140);
        using var req = new HttpRequestMessage(HttpMethod.Get, fx.Url("/status"));
        req.Headers.Add("X-TraeTool-Token", "wrong");
        var resp = fx.Client.SendAsync(req).Result;

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public void Checkin_is_post_only()
    {
        using var fx = Open(19160);
        using var req = new HttpRequestMessage(HttpMethod.Get, fx.Url("/checkin"));
        req.Headers.Add("X-TraeTool-Token", "test-token");
        var resp = fx.Client.SendAsync(req).Result;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public void Unknown_path_is_404()
    {
        using var fx = Open(19180);
        var resp = fx.Client.GetAsync(fx.Url("/nope")).Result;

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public void Preflight_from_loopback_origin_is_answered_with_that_origin()
    {
        using var fx = Open(19240);
        using var req = new HttpRequestMessage(HttpMethod.Options, fx.Url("/status"));
        req.Headers.Add("Origin", "http://localhost:3000");
        var resp = fx.Client.SendAsync(req).Result;

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("http://localhost:3000", string.Join(",",
            resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("Origin", resp.Headers.GetValues("Vary"));
    }

    [Fact]
    public void Preflight_from_other_origin_gets_no_cors_grant()
    {
        using var fx = Open(19260);
        using var req = new HttpRequestMessage(HttpMethod.Options, fx.Url("/status"));
        req.Headers.Add("Origin", "http://example.com");
        var resp = fx.Client.SendAsync(req).Result;

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Checkin_returns_400_when_no_account_enabled()
    {
        // AppServices 未初始化 → Config 是空配置，没有可签账号
        using var fx = Open(19220);
        using var req = new HttpRequestMessage(HttpMethod.Post, fx.Url("/checkin"));
        req.Headers.Add("X-TraeTool-Token", "test-token");
        var resp = fx.Client.SendAsync(req).Result;

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("no enabled account", resp.Content.ReadAsStringAsync().Result);
    }

    [Fact]
    public void New_token_is_urlsafe_and_random()
    {
        var token = LocalGateway.NewToken();

        Assert.True(token.Length >= 40);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, LocalGateway.NewToken());
    }
}
