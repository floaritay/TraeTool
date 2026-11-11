using System.Net;
using System.Net.Http;
using TraeTool.Models;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class ProxyConfigTests
{
    [Fact]
    public void Default_config_is_follow_system()
    {
        var cfg = ProxyConfig.From(new AppSettings());

        Assert.Equal(AppSettings.ProxyModes.System, cfg.Mode);
        Assert.False(cfg.IsManual);
        Assert.Null(cfg.ManualUri);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_system()
    {
        var cfg = ProxyConfig.From(new AppSettings { ProxyMode = "whatever" });

        Assert.Equal(AppSettings.ProxyModes.System, cfg.Mode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890/")]   // 漏写协议 → 补 http://
    [InlineData("  127.0.0.1:7890  ", "http://127.0.0.1:7890/")] // 首尾空白去掉
    public void Manual_address_is_parsed(string input, string expected)
    {
        var cfg = ProxyConfig.From(new AppSettings { ProxyMode = AppSettings.ProxyModes.Manual, ProxyUrl = input });

        Assert.Equal(AppSettings.ProxyModes.Manual, cfg.Mode);
        Assert.True(cfg.IsManual);
        Assert.Equal(expected, cfg.ManualUri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ftp://example.com:21")]
    [InlineData("http://")]           // 无主机名
    public void Unusable_manual_address_falls_back_to_system(string? url)
    {
        var cfg = ProxyConfig.From(new AppSettings { ProxyMode = AppSettings.ProxyModes.Manual, ProxyUrl = url });

        Assert.Equal(AppSettings.ProxyModes.System, cfg.Mode);
        Assert.False(cfg.IsManual);
    }

    [Fact]
    public void ApplyTo_none_disables_proxy()
    {
        var handler = new SocketsHttpHandler();
        new ProxyConfig(AppSettings.ProxyModes.None, null).ApplyTo(handler);

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void ApplyTo_manual_sets_web_proxy()
    {
        var handler = new SocketsHttpHandler();
        new ProxyConfig(AppSettings.ProxyModes.Manual, "http://127.0.0.1:7890").ApplyTo(handler);

        Assert.True(handler.UseProxy);
        Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("http://127.0.0.1:7890/", ((WebProxy)handler.Proxy!).Address!.ToString());
    }

    [Fact]
    public void ApplyTo_manual_bypasses_loopback_hosts()
    {
        var handler = new SocketsHttpHandler();
        new ProxyConfig(AppSettings.ProxyModes.Manual, "http://127.0.0.1:7890").ApplyTo(handler);
        var proxy = handler.Proxy!;

        // .NET 的 WebProxy 默认不绕过回环，必须由 BypassList 显式提供
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:8766/")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:8766/")));
        Assert.False(proxy.IsBypassed(new Uri("https://api.trae.cn/")));
    }

    [Fact]
    public void ApplyTo_system_leaves_default_proxy_resolution()
    {
        // Proxy=null + UseProxy=true → 由 HttpClient.DefaultProxy（系统代理）决定
        var handler = new SocketsHttpHandler();
        new ProxyConfig(AppSettings.ProxyModes.System, null).ApplyTo(handler);

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void BrowserArguments_none_forces_direct()
    {
        var args = new ProxyConfig(AppSettings.ProxyModes.None, null).BrowserArguments();

        Assert.Contains("direct://", args);
        // 回环由 Chromium 隐式白名单保证，注入 <-loopback> 会把回环改成走代理
        Assert.DoesNotContain("--proxy-bypass-list", args);
    }

    [Fact]
    public void BrowserArguments_system_injects_nothing()
    {
        Assert.Equal("", new ProxyConfig(AppSettings.ProxyModes.System, null).BrowserArguments());
    }

    [Fact]
    public void BrowserArguments_manual_passes_address_without_bypass_list()
    {
        var args = new ProxyConfig(AppSettings.ProxyModes.Manual, "http://127.0.0.1:7890").BrowserArguments();

        Assert.Contains("--proxy-server=http://127.0.0.1:7890/", args);
        Assert.DoesNotContain("--proxy-bypass-list", args);
    }

    [Fact]
    public void BrowserArguments_never_unbypasses_loopback()
    {
        // <-loopback> 的含义是「把回环从隐式绕过名单里去掉」= 回环也走代理，任何模式下都不能出现
        foreach (var mode in new[]
                 {
                     AppSettings.ProxyModes.None, AppSettings.ProxyModes.System, AppSettings.ProxyModes.Manual,
                 })
        {
            var args = new ProxyConfig(mode, "socks5://127.0.0.1:1080").BrowserArguments();
            Assert.DoesNotContain("loopback", args);
        }
    }

    [Theory]
    [InlineData(AppSettings.ProxyModes.None)]
    [InlineData(AppSettings.ProxyModes.System)]
    [InlineData(AppSettings.ProxyModes.Manual)]
    public void Describe_is_never_empty(string mode)
    {
        var uri = mode == AppSettings.ProxyModes.Manual ? "http://127.0.0.1:7890" : null;
        Assert.NotEmpty(new ProxyConfig(mode, uri).Describe());
    }
}
