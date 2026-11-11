using System.Net;
using System.Net.Http;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 进程内共享的 HttpClient 提供器。每个 HttpClient 实例各自持有连接池与 DNS 缓存，
/// 分散 new 出来会让同一目标重复建连；这里按超时分档共享实例。
/// 单次请求若要更短/更长的期限，优先传 CancellationToken，而不是再 new 一个实例。
/// 出站代理开关在此统一施加：变更代理后丢弃旧实例（在用的请求不受影响，下次取实例即新设置）。
/// </summary>
public static class HttpClients
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, HttpClient> ByTimeout = new();

    /// <summary>当前代理配置（<see cref="Configure"/> 写入）。</summary>
    private static ProxyConfig _proxy = ProxyConfig.From(new AppSettings());

    /// <summary>默认超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>默认实例（30s）。</summary>
    public static HttpClient Default => For(DefaultTimeoutSeconds);

    /// <summary>当前生效的代理配置。</summary>
    public static ProxyConfig Proxy
    {
        get { lock (Gate) return _proxy; }
    }

    /// <summary>应用代理配置；与当前不同则重建全部分档实例。</summary>
    public static void Configure(AppSettings cfg)
    {
        var next = ProxyConfig.From(cfg);
        lock (Gate)
        {
            if (_proxy == next) return;
            _proxy = next;
            // 只丢引用不 Dispose：正在飞行的请求用旧实例跑完，避免中途被连接池关闭打断
            ByTimeout.Clear();
        }
        AppLog.Info("proxy", "出站代理已应用：" + next.Describe());
    }

    /// <summary>取该超时档位的共享实例（首次访问时创建）。</summary>
    public static HttpClient For(int timeoutSeconds)
    {
        if (timeoutSeconds < 1) timeoutSeconds = DefaultTimeoutSeconds;
        if (ByTimeout.TryGetValue(timeoutSeconds, out var hit)) return hit;
        lock (Gate)
        {
            if (ByTimeout.TryGetValue(timeoutSeconds, out var hit2)) return hit2;
            var created = Create(timeoutSeconds);
            ByTimeout[timeoutSeconds] = created;
            return created;
        }
    }

    private static HttpClient Create(int seconds)
    {
        var handler = new SocketsHttpHandler
        {
            // 同一目标复用连接；服务端下发 gzip/deflate 时自动解压
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _proxy.ApplyTo(handler);

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds) };
        // 部分接口要求带 UA；需要伪装成客户端 UA 的请求在各自请求头上覆盖
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TraeTool/1.0");
        return client;
    }
}
