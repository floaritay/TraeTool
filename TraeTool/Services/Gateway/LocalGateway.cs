using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TraeTool.Models;
using TraeTool.ViewModels;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 本机管理网关：只监听 127.0.0.1 的 HTTP，供本机脚本/面板读取账号状态与触发批量签到。
/// 不代理任何第三方流量，不拦截其他程序的连接，不解密任何 HTTPS。
/// 端点：
///   GET  /            端点索引（无鉴权）
///   GET  /healthz     存活探针（无鉴权）
///   GET  /status      账号/签到状态 + 近期记录（需 token）
///   POST /checkin     触发批量签到，返回 202 异步受理（需 token）
/// token 通过 X-TraeTool-Token 请求头或 ?token= 传递。
///
/// 按职责分文件：管理端点与状态见 LocalGateway.Admin.cs、对话路由见 LocalGateway.Routes.cs、
/// 取号换号见 LocalGateway.Failover.cs、流式转发见 LocalGateway.Streaming.cs。
/// </summary>
public sealed partial class LocalGateway
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>对话上游（构造注入，便于测试替换为假实现）。</summary>
    private readonly IChatUpstream _upstream;

    /// <summary>账号池：失败换号与冷却状态（网关存活期内跨请求保留）。</summary>
    private readonly AccountPool _pool = new();

    /// <summary>配置读取口（默认取全局配置；测试注入固定实例，避免测试写真实 config.json）。</summary>
    private readonly Func<AppSettings> _config;

    /// <summary>网关对外的 HTTP 客户端（默认共享实例；测试注入假 handler 以离线验证）。</summary>
    private readonly HttpClient _http;

    /// <summary>是否由调用方注入了传输（测试）；注入时跳过凭据续期与联网探测。</summary>
    private readonly bool _injectedTransport;

    /// <summary>传给上层的传输：生产为 null（= 用共享实例并由其负责续期），测试为注入的假实现。</summary>
    private HttpClient? Transport => _injectedTransport ? _http : null;

    /// <summary>转发前的凭据校验（默认真实续期；测试可注入假实现）。</summary>
    private readonly Func<Account, Task<bool>> _ensureToken;

    /// <summary>续期是否由本类负责落盘：只有用真实续期时才把换到的 Token 写回配置。</summary>
    private readonly bool _persistRenewedToken;

    public LocalGateway(IChatUpstream? upstream = null, Func<AppSettings>? config = null,
        HttpClient? http = null, Func<Account, Task<bool>>? ensureToken = null)
    {
        _upstream = upstream ?? new TraeChatUpstream();
        _config = config ?? (() => AppServices.Config);
        _http = http ?? HttpClients.Default;
        _injectedTransport = http != null;
        if (ensureToken != null)
        {
            // 校验逻辑由调用方提供（测试），令牌写回也归调用方
            _ensureToken = ensureToken;
            _persistRenewedToken = false;
        }
        else if (_injectedTransport)
        {
            // 传输被注入（测试）：视为调用方已保证凭据有效，不打真实网络
            _ensureToken = _ => Task.FromResult(true);
            _persistRenewedToken = false;
        }
        else
        {
            _ensureToken = TokenRenewer.EnsureValidTokenAsync;
            _persistRenewedToken = true;
        }
    }

    /// <summary>当前处于冷却中的账号数（状态展示与测试）。</summary>
    public int CoolingAccounts(DateTime now) => _pool.CoolingCount(now);

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port { get; private set; }

    /// <summary>网关基址（只含回环地址）。</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}/";

    /// <summary>访问令牌（Start 后可用）。</summary>
    public string Token { get; private set; } = "";

    /// <summary>首个可转发增量的等待上限；超时视为该账号失败并换号（HttpClient 超时只覆盖响应头）。</summary>
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 提交响应后的「上游读空闲」上限：每收到一个增量顺延一次。
    /// 上游中途卡死时用它按协议收尾，否则请求会一直挂到客户端超时。
    /// </summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>单次非流式上游尝试的时限（HttpClient 超时只覆盖响应头，正文读取可能一直挂着）。</summary>
    public TimeSpan UpstreamAttemptTimeout { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>生成 32 字节随机令牌（base64url，无填充）。</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>配置端口被占用时向后尝试的端口数（同机另一实例、上次退出残留的监听等）。</summary>
    private const int PortFallbackCount = 5;

    /// <summary>
    /// 启动（先 Stop）。令牌缺失时在此生成并写入 cfg，由调用方负责落盘——本类不直接写配置文件。
    /// 配置端口占用时往后找可用端口并写回 cfg.GatewayPort（落盘仍由调用方负责）。
    /// 失败写 LastError，不影响应用其他功能。
    /// </summary>
    public bool Start(AppSettings cfg)
    {
        Stop();
        if (string.IsNullOrEmpty(cfg.GatewayToken))
            cfg.GatewayToken = NewToken();

        var wanted = cfg.GatewayPort;
        HttpListener? listener = null;
        var port = wanted;
        Exception? lastError = null;
        for (var i = 0; i <= PortFallbackCount; i++)
        {
            port = wanted + i;
            if (port is < 1024 or > 65535) break;
            try
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                candidate.Start();
                listener = candidate;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;   // 端口占用：试下一个
            }
        }

        if (listener == null)
        {
            LastError = lastError?.Message ?? "没有可用端口";
            IsRunning = false;
            AppLog.Error("gateway", $"启动失败（自 {wanted} 起 {PortFallbackCount + 1} 个端口均不可用）: {LastError}");
            return false;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _listener = listener;
            _cts = cts;
        }
        Port = port;
        cfg.GatewayPort = port;
        Token = cfg.GatewayToken!;
        IsRunning = true;
        LastError = null;
        _ = Task.Run(() => LoopAsync(listener, cts.Token));
        AppLog.Info("gateway",
            port == wanted
                ? $"网关已启动 {Endpoint}（仅回环地址）"
                : $"配置端口 {wanted} 不可用，网关改用 {Endpoint}（仅回环地址）");
        return true;
    }

    public void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }
        cts?.Cancel();
        try { listener?.Stop(); }
        catch { /* 已停止时的重复调用，忽略 */ }
        try { listener?.Close(); }
        catch { /* 同上 */ }
        IsRunning = false;
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    AppLog.Error("gateway", "监听终止: " + ex.Message);
                    IsRunning = false;
                }
                return;
            }
            // 并发处理：签到等慢请求不得阻塞 /healthz 这类探针
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    /// <summary>按路径分发；端点索引与存活探针在此直接应答，其余交给各自的处理方法。</summary>
    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Write(ctx, 204, null);
                return;
            }

            switch (path)
            {
                case "/":
                    Write(ctx, 200, new
                    {
                        app = "TraeTool",
                        version = UpdateService.CurrentVersionText,
                        endpoints = new[]
                        {
                            "GET /healthz",
                            "GET /status?limit=20",
                            "POST /checkin",
                            "GET /v1/models",
                            "POST /v1/chat/completions",
                            "POST /v1/messages",
                            "POST /v1/responses",
                        },
                        auth = "X-TraeTool-Token header or ?token=；/v1/* 亦接受 Authorization: Bearer 与 x-api-key",
                    });
                    return;

                case "/healthz":
                    Write(ctx, 200, new { ok = true, app = "TraeTool", version = UpdateService.CurrentVersionText, port = Port });
                    return;

                case "/status":
                    if (!Authorized(req, ctx)) return;
                    Write(ctx, 200, BuildStatus(ReadLimit(req)));
                    return;

                case "/checkin":
                    if (!Authorized(req, ctx)) return;
                    if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Write(ctx, 405, new { error = "checkin 仅支持 POST" });
                        return;
                    }
                    HandleCheckin(ctx);
                    return;

                case "/v1/models":
                    if (!OpenAiAuthorized(req, ctx)) return;
                    if (!_config().GatewayChatEnabled) { Disabled(ctx); return; }
                    await HandleModelsAsync(ctx);
                    return;

                case "/v1/chat/completions":
                    if (!OpenAiAuthorized(req, ctx)) return;
                    if (!_config().GatewayChatEnabled) { Disabled(ctx); return; }
                    await HandleChatAsync(ctx, req, ct);
                    return;

                case "/v1/messages":
                    if (!OpenAiAuthorized(req, ctx)) return;
                    if (!_config().GatewayChatEnabled) { Disabled(ctx); return; }
                    await HandleMessagesAsync(ctx, req, ct);
                    return;

                case "/v1/responses":
                    if (!OpenAiAuthorized(req, ctx)) return;
                    if (!_config().GatewayChatEnabled) { Disabled(ctx); return; }
                    await HandleResponsesAsync(ctx, req, ct);
                    return;

                default:
                    Write(ctx, 404, new { error = "unknown endpoint", path });
                    return;
            }
        }
        catch (Exception ex)
        {
            // 原始异常消息可能含内部路径/实现细节，只记日志，对外给统一文案
            AppLog.Error("gateway", "请求处理异常: " + ex);
            try { Write(ctx, 500, new { error = "内部错误，请查看日志" }); }
            catch { /* 连接已断开 */ }
        }
    }

    // ==================== 响应写出 ====================

    /// <summary>
    /// 只对回环来源回显 Origin：回 <c>*</c> 等于允许任意网页用用户浏览器打本机 127.0.0.1 端口
    /// （本机其他服务与浏览器都可视作同一网络位置），非回环来源不回 CORS 头，交由浏览器拦截。
    /// </summary>
    private static void ApplyCors(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var origin = ctx.Request.Headers["Origin"];
        if (origin != null && IsLoopbackOrigin(origin))
        {
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
        }
        res.Headers["Access-Control-Allow-Headers"] = "X-TraeTool-Token, Authorization, x-api-key, Content-Type";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    /// <summary>是否为回环来源（http/https 且主机为 127.0.0.1 / localhost / [::1]）。</summary>
    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    /// <summary>写 JSON 字符串（协议层已序列化，不再二次编码）。</summary>
    private static void WriteRaw(HttpListenerContext ctx, int status, string json)
    {
        try
        {
            var res = ctx.Response;
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            ApplyCors(ctx);
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }
        catch
        {
            // 客户端提前断开
        }
    }

    private static void Write(HttpListenerContext ctx, int status, object? body)
    {
        if (body == null)
        {
            try
            {
                var res = ctx.Response;
                res.StatusCode = status;
                ApplyCors(ctx);
                res.Close();
            }
            catch
            {
                // 客户端提前断开
            }
            return;
        }
        WriteRaw(ctx, status, JsonSerializer.Serialize(body, JsonOpts));
    }
}
