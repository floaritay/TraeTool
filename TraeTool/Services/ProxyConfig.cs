using System.Net.Http;
using System.Net;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 出站代理配置解析结果。仅约束本工具自身的出网请求：
/// .NET 侧（签到/状态/更新/头像）与 WebView2 登录页两套通道共用同一组开关。
/// 三种模式：none 直连 / system 跟随系统 / manual 使用 <see cref="Url"/>。
/// 不涉及任何第三方软件的流量拦截，也不引入证书或解密环节。
/// </summary>
public sealed record ProxyConfig(string Mode, string? Url)
{
    /// <summary>
    /// 是否已配置可用的手动代理地址。只认 http://——.NET 的 SocketsHttpHandler/WebProxy
    /// 只讲 HTTP 代理协议，socks5 会在 .NET 通道上静默失败（仅浏览器侧可用），故不受理。
    /// </summary>
    public bool IsManual => Mode == AppSettings.ProxyModes.Manual && Uri.TryCreate(Url, UriKind.Absolute, out var u) &&
                            u.Scheme is "http";

    /// <summary>手动代理地址（仅 <see cref="IsManual"/> 时非空）。</summary>
    public Uri? ManualUri => IsManual ? new Uri(Url!) : null;

    /// <summary>
    /// 由配置构造：非法模式回退跟随系统；手动地址漏写协议时补 http://。
    /// </summary>
    public static ProxyConfig From(AppSettings cfg)
    {
        var mode = cfg.ProxyMode is AppSettings.ProxyModes.None or AppSettings.ProxyModes.Manual
            ? cfg.ProxyMode
            : AppSettings.ProxyModes.System;

        var raw = cfg.ProxyUrl?.Trim();
        if (!string.IsNullOrEmpty(raw) && !raw.Contains("://", StringComparison.Ordinal))
            raw = "http://" + raw; // 常见写法 127.0.0.1:7890 → http://127.0.0.1:7890

        var cfg2 = new ProxyConfig(mode, string.IsNullOrEmpty(raw) ? null : raw);
        if (mode == AppSettings.ProxyModes.Manual && !cfg2.IsManual)
            mode = AppSettings.ProxyModes.System; // 空地址/不支持的协议 → 不要让请求全部失败
        return new ProxyConfig(mode, string.IsNullOrEmpty(raw) ? null : raw);
    }

    /// <summary>
    /// 回环地址绕过项（<see cref="WebProxy.BypassList"/> 是正则，按此写法匹配主机名）。
    /// .NET 的 WebProxy 默认不绕过回环（127.0.0.1 与 localhost 都会交给代理），
    /// 与「本机地址不走代理」的约定不符，手动模式必须显式登记。
    /// </summary>
    private static readonly string[] LoopbackBypass = ["127\\.0\\.0\\.1", "localhost", "\\[::1\\]"];

    /// <summary>把当前模式写入 handler（HTTP 通道）。</summary>
    public void ApplyTo(SocketsHttpHandler handler)
    {
        switch (Mode)
        {
            case AppSettings.ProxyModes.None:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;
            case AppSettings.ProxyModes.Manual:
                if (ManualUri is { } uri)
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(uri) { BypassList = LoopbackBypass };
                }
                else
                {
                    handler.UseProxy = false;
                    handler.Proxy = null;
                }
                break;
            default: // 跟随系统：UseProxy=true 且 Proxy=null → 读 HttpClient.DefaultProxy
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
        }
    }

    /// <summary>
    /// WebView2 浏览器进程的额外启动参数（Chromium 侧独立于 .NET 代理设置）。
    /// 空 = 不注入，保持系统代理；直连模式显式给 direct://。
    ///
    /// 不追加 --proxy-bypass-list：Chromium 本来就默认把 localhost / 127.0.0.1 / [::1] 等
    /// 回环地址排除在代理之外，无需声明；而 &lt;-loopback&gt; 是从这份隐式白名单里「减掉」回环，
    /// 效果是强制回环流量也交给代理，会连带影响以本机地址提供服务的页面。
    /// </summary>
    public string BrowserArguments()
        => Mode switch
        {
            AppSettings.ProxyModes.None => "--proxy-server=direct://",
            AppSettings.ProxyModes.Manual when ManualUri is { } uri => $"--proxy-server={uri}",
            _ => "",
        };

    /// <summary>给人看的生效说明（设置页状态行 / 自检项）。</summary>
    public string Describe()
        => Mode switch
        {
            AppSettings.ProxyModes.None => "不使用代理（直连）",
            AppSettings.ProxyModes.Manual when ManualUri is { } uri => $"手动代理 {uri}",
            AppSettings.ProxyModes.Manual => "手动代理地址无效，已回退跟随系统",
            _ => "跟随系统代理",
        };
}
