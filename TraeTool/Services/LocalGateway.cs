using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TraeTool.Models;
using TraeTool.ViewModels;

namespace TraeTool.Services;

/// <summary>
/// 本机管理网关：只监听 127.0.0.1 的 HTTP，供本机脚本/面板读取账号状态与触发批量签到。
/// 不代理任何第三方流量，不拦截其他程序的连接，不解密任何 HTTPS。
/// 端点：
///   GET  /            端点索引（无鉴权）
///   GET  /healthz     存活探针（无鉴权）
///   GET  /status      账号/签到状态 + 近期记录（需 token）
///   POST /checkin     触发批量签到，返回 202 异步受理（需 token）
/// token 通过 X-TraeTool-Token 请求头或 ?token= 传递。
/// </summary>
public sealed class LocalGateway
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

    /// <summary>最近一次网关触发签到的结果（/status 汇总用，跨请求保留）。</summary>
    private string? _lastRunAt;
    private string? _lastRunSummary;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port { get; private set; }

    /// <summary>网关基址（只含回环地址）。</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}/";

    /// <summary>访问令牌（Start 后可用）。</summary>
    public string Token { get; private set; } = "";

    /// <summary>生成 32 字节随机令牌（base64url，无填充）。</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 启动（先 Stop）。令牌缺失时在此生成并写入 cfg，由调用方负责落盘——本类不直接写配置文件。
    /// 失败写 LastError，不影响应用其他功能。
    /// </summary>
    public bool Start(AppSettings cfg)
    {
        Stop();
        if (string.IsNullOrEmpty(cfg.GatewayToken))
            cfg.GatewayToken = NewToken();

        var port = cfg.GatewayPort;
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _listener = listener;
                _cts = cts;
            }
            Port = port;
            Token = cfg.GatewayToken!;
            IsRunning = true;
            LastError = null;
            _ = Task.Run(() => LoopAsync(listener, cts.Token));
            AppLog.Info("gateway", $"网关已启动 {Endpoint}（仅回环地址）");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            AppLog.Error("gateway", $"启动失败（端口 {port}）: {ex.Message}");
            return false;
        }
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
            AppLog.Error("gateway", "请求处理异常: " + ex.Message);
            try { Write(ctx, 500, new { error = ex.Message }); }
            catch { /* 连接已断开 */ }
        }
    }

    private bool Authorized(HttpListenerRequest req, HttpListenerContext ctx)
    {
        var got = req.Headers["X-TraeTool-Token"] ?? req.QueryString["token"] ?? "";
        if (Token.Length > 0 && string.Equals(got, Token, StringComparison.Ordinal)) return true;
        Write(ctx, 401, new { error = "missing or invalid token", hint = "X-TraeTool-Token header or ?token=" });
        return false;
    }

    private static int ReadLimit(HttpListenerRequest req)
    {
        var raw = req.QueryString["limit"];
        if (int.TryParse(raw, out var n)) return Math.Clamp(n, 1, 200);
        return 20;
    }

    /// <summary>触发批量签到：已在跑回 409；否则异步受理返回 202，结果写入 /status 的 lastRun。</summary>
    private void HandleCheckin(HttpListenerContext ctx)
    {
        var cfg = _config();
        var enabled = cfg.Accounts.Count(a => a.Enabled);
        if (enabled == 0)
        {
            Write(ctx, 400, new { error = "no enabled account" });
            return;
        }
        if (AppServices.BatchBusy)
        {
            Write(ctx, 409, new { error = "busy", detail = "已有签到批次在执行" });
            return;
        }
        if (!AppServices.TryEnterBatch())
        {
            Write(ctx, 409, new { error = "busy" });
            return;
        }

        Write(ctx, 202, new { accepted = true, enabled });
        _ = Task.Run(async () =>
        {
            try
            {
                var list = cfg.Accounts.Where(a => a.Enabled).ToList();
                var results = await AppServices.Checkin.BatchCheckinAsync(
                    list, cfg.CheckinIntervalSeconds);
                var total = results.Sum(r => r.Gained);
                var summary = CheckinPageViewModel.BuildBatchSummary(results, total);
                RecordRun(summary);
                AppLog.Info("gateway", "网关触发签到完成：" + summary);
                AppServices.RaiseAccountsChanged();
            }
            catch (Exception ex)
            {
                AppLog.Error("gateway", "网关触发签到异常: " + ex.Message);
                RecordRun("异常：" + ex.Message);
            }
            finally
            {
                AppServices.BatchBusy = false;
            }
        });
    }

    private void RecordRun(string summary)
    {
        _lastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _lastRunSummary = summary;
    }

    /// <summary>
    /// /v1/* 客户端鉴权，按客户端惯例依次接受：
    /// Authorization: Bearer（OpenAI 系）、x-api-key（Anthropic 系）、X-TraeTool-Token、?token=。
    /// </summary>
    private bool OpenAiAuthorized(HttpListenerRequest req, HttpListenerContext ctx)
    {
        var bearer = req.Headers["Authorization"] ?? "";
        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearer["Bearer ".Length..].Trim()
            : "";
        if (token.Length == 0) token = (req.Headers["x-api-key"] ?? "").Trim();
        if (token.Length == 0)
            token = req.Headers["X-TraeTool-Token"] ?? req.QueryString["token"] ?? "";
        if (Token.Length > 0 && string.Equals(token, Token, StringComparison.Ordinal)) return true;
        WriteRaw(ctx, 401, ChatProtocol.RenderError(new("missing or invalid api key", "invalid_request_error", 401)));
        return false;
    }

    /// <summary>转发未开启（设置项默认关闭）。</summary>
    private static void Disabled(HttpListenerContext ctx)
        => WriteRaw(ctx, 403, ChatProtocol.RenderError(new("该网关的对话转发未开启", "permission_denied", 403)));

    /// <summary>/v1/models：真实模型注册表（带别名），上游不可用时回退内置列表。</summary>
    private async Task HandleModelsAsync(HttpListenerContext ctx)
    {
        var cfg = _config();
        var account = _pool.Candidates(cfg, DateTime.Now).FirstOrDefault();
        IReadOnlyList<string> ids;
        ModelSources sources;
        try
        {
            (ids, sources) = await ModelCatalog.AggregateAsync(cfg, account, Transport);
        }
        catch (Exception ex)
        {
            AppLog.Info("pool", "模型目录拉取被取消：" + ex.Message);
            ids = ModelCatalog.KnownIds;
            sources = ModelCatalog.Snapshot(cfg);
        }
        AppLog.Info("pool",
            $"模型列表：人工 {sources.Manual} · 官网 {sources.Official}{(sources.Live ? "" : "(回退)")} · " +
            $"内置 {sources.Builtin} · 推断 {sources.Inferred} · 合计 {ids.Count}");

        var list = new List<string>(ids);
        if (!list.Contains(ModelCatalog.Alias, StringComparer.OrdinalIgnoreCase))
            list.Add(ModelCatalog.Alias);

        WriteRaw(ctx, 200, ChatProtocol.RenderModels(list, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>首个可转发增量的等待上限；超时视为该账号失败并换号（HttpClient 超时只覆盖响应头）。</summary>
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>同一账号最多重试几次（退避重试，来自 <see cref="RetryPolicy"/> 的 RetrySame）。</summary>
    private const int MaxSameAccountTries = 3;

    /// <summary>同一账号上累计退避的上限：本地网关要照顾首字节延迟。</summary>
    private static readonly TimeSpan MaxSameAccountBackoff = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 解析请求体 → 记下客户端请求的模型名 → 按模型目录解析真实 model id。
    /// 只处理请求本身：不接触上游，也不写任何响应字节（换号成功前不能提交响应）。
    /// </summary>
    private async Task<(ChatRequest? Parsed, ChatUpstreamError? Error, int Status)>
        ResolveAsync(string raw, Func<string, ChatRequest> parse)
    {
        ChatRequest parsed;
        try
        {
            parsed = parse(raw);
        }
        catch (ChatRequestException cre)
        {
            return (null, new ChatUpstreamError(cre.Message, cre.Type, cre.Status), cre.Status);
        }
        catch (JsonException)
        {
            return (null, new ChatUpstreamError("请求体不是合法 JSON", "invalid_request_error", 400), 400);
        }

        var cfg = _config();
        var now = DateTime.Now;

        // 第 4 层：把客户端请求的模型名记下来（原样名去重、别名不记、条数有上限），供列表与名称推断使用
        var rawModel = parsed.Model;
        if (ModelCatalog.RecordRequested(cfg, rawModel)) AppServices.SaveConfig();

        var (ids, _) = await ModelCatalog.AggregateAsync(cfg, _pool.Candidates(cfg, now).FirstOrDefault(),
            Transport);
        return (parsed with { Model = ModelCatalog.Resolve(rawModel, ids) }, null, 200);
    }

    /// <summary>
    /// 失败处置：凭据被拒要作废校验缓存，并按决策给的时长冷却该账号；
    /// 其余只在本次请求内换号（cooldown 为 null）。
    /// </summary>
    private void ClassifyFailover(Account account, ChatUpstreamError error, DateTime now, TimeSpan? cooldown)
    {
        if (error.Type == "auth_error") TokenRenewer.Invalidate(account.Id);
        _pool.MarkFail(account, now, cooldown);
        AppLog.Info("pool", $"上游失败 {account.DisplayName(0)}：{error.Type} HTTP {error.Status} {error.Message}");
    }

    /// <summary>上游失败对应的对外状态码：凭据失效不回 401（客户端的 Bearer 是对的），限流原样透出。</summary>
    private static int StatusFor(ChatUpstreamError error)
        => error.Type == "auth_error" ? 502
            : error.Status == 429 ? 429
            : error.Status is >= 400 and <= 599 ? error.Status : 502;

    /// <summary>
    /// 非流式转发：按池轮询取号，把整段回答拼好再交给调用方渲染。
    /// 同号重试由 <see cref="RetryPolicy"/> 决定，退避累计不超过 <see cref="MaxSameAccountBackoff"/>。
    /// </summary>
    private async Task<(ChatCompletion? Ok, ChatUpstreamError? Error, int Status)>
        CompleteBufferedAsync(ChatRequest parsed, CancellationToken ct)
    {
        var cfg = _config();
        var now = DateTime.Now;
        var candidateCount = Math.Max(1, _pool.Candidates(cfg, now).Count);
        var budget = MaxSameAccountBackoff;
        Account? account = null;
        ChatUpstreamError? last = null;
        var tries = 0;
        var guard = 0;

        while (guard++ < candidateCount + MaxSameAccountTries + 1)
        {
            if (account == null)
            {
                account = _pool.Next(cfg, now);
                if (account == null) break;
                tries = 0;
                // 换到新账号＝上一次失败已被处置：换号后仍失败才用新错误，
                // 池子被取空时保留最后一个错误（否则会把 502 说成 503）
                last = null;
            }

            // 转发前先续期：过期 JWT 会被上游以 401/code 1001 拒绝
            // （TokenRenewer 内部按 TTL 复用校验结果，不是每请求都打上游 status）。
            // 该账号无法确认可用时只跳过它、继续试池里下一个——凭据坏掉一个账号
            // 不能把整个池的请求都判死。
            var beforeToken = account.Token;
            if (!await _ensureToken(account))
            {
                AppLog.Info("pool", $"账号 {account.DisplayName(0)} 凭据不可用，换下一个");
                _pool.MarkFail(account, now, AccountPool.AuthCooldown);
                account = null;
                continue;
            }
            // 续期写回过 Token 就落盘（校验由调用方提供时不写，避免测试配置落到真实 config.json）
            if (_persistRenewedToken && !string.Equals(beforeToken, account.Token, StringComparison.Ordinal))
                ConfigStore.Save(cfg);

            var res = await _upstream.CompleteAsync(account, parsed, ct);
            if (res.Ok)
            {
                _pool.MarkSuccess(account);
                return (res.Completion!, null, 200);
            }

            last = res.Error!;
            var decision = RetryPolicy.Decide(last, tries);
            ClassifyFailover(account, last, now, decision.Cooldown);

            if (decision.Action == RetryAction.Fatal) return (null, last, StatusFor(last));
            if (decision.Action == RetryAction.RetrySame && decision.Delay > TimeSpan.Zero &&
                decision.Delay <= budget)
            {
                tries++;
                budget -= decision.Delay;
                await Task.Delay(decision.Delay, ct);
                continue;
            }
            account = null;
        }

        if (last == null) return (null, PoolEmpty(), 503);
        return (null, last, StatusFor(last));
    }

    /// <summary>池内无可用账号时的统一错误。</summary>
    private static ChatUpstreamError PoolEmpty()
        => new("账号池内没有可用账号（未登录/凭据失效/已暂停/冷却中）", "insufficient_quota", 503);

    /// <summary>
    /// 流式转发：先干跑换号（此阶段不向客户端写任何字节），
    /// 首个可转发增量到手才提交 200 + text/event-stream，之后逐帧落 socket。
    /// </summary>
    private async Task RelayStreamAsync(HttpListenerContext ctx, ChatRequest parsed,
        StreamProfile profile, CancellationToken ct)
    {
        var cfg = _config();
        var now = DateTime.Now;
        var candidateCount = Math.Max(1, _pool.Candidates(cfg, now).Count);
        var budget = MaxSameAccountBackoff;
        Account? account = null;
        ChatUpstreamError? last = null;
        var tries = 0;
        var guard = 0;

        while (guard++ < candidateCount + MaxSameAccountTries + 1)
        {
            if (account == null)
            {
                account = _pool.Next(cfg, now);
                if (account == null) break;
                tries = 0;
                last = null;   // 同上：换号后仍失败才用新错误
            }

            var beforeToken = account.Token;
            if (!await _ensureToken(account))
            {
                AppLog.Info("pool", $"账号 {account.DisplayName(0)} 凭据不可用，换下一个");
                _pool.MarkFail(account, now, AccountPool.AuthCooldown);
                account = null;
                continue;
            }
            if (_persistRenewedToken && !string.Equals(beforeToken, account.Token, StringComparison.Ordinal))
                ConfigStore.Save(cfg);

            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            streamCts.CancelAfter(FirstTokenTimeout);
            var enumerator = _upstream.StreamAsync(account, parsed, streamCts.Token)
                .GetAsyncEnumerator(streamCts.Token);

            UpstreamDelta? first = null;
            UpstreamOutcome? failure = null;
            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var delta = enumerator.Current;
                    if (delta.Outcome is { } outcome)
                    {
                        failure = outcome;
                        last = outcome.Error ?? new ChatUpstreamError("上游返回没有可用文本", "upstream_error", 502);
                        break;
                    }
                    // 该协议不转发的增量（例如 Anthropic 侧丢弃的思考）不算首字节
                    if (!profile.Emits(delta)) continue;
                    first = delta;
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && streamCts.IsCancellationRequested)
            {
                last = new ChatUpstreamError("上游首字节超时", "upstream_error", 504);
            }
            catch (OperationCanceledException)
            {
                await enumerator.DisposeAsync();
                return;   // 网关停服
            }

            if (first == null)
            {
                last ??= new ChatUpstreamError("上游返回没有可用文本", "upstream_error", 502);
                var decision = RetryPolicy.Decide(last, tries, failure?.RetryAfter);
                ClassifyFailover(account, last, now, decision.Cooldown);
                await enumerator.DisposeAsync();

                if (decision.Action == RetryAction.Fatal)
                {
                    WriteRaw(ctx, StatusFor(last), profile.ErrorBody(last));
                    return;
                }
                if (decision.Action == RetryAction.RetrySame && decision.Delay > TimeSpan.Zero &&
                    decision.Delay <= budget)
                {
                    tries++;
                    budget -= decision.Delay;
                    await Task.Delay(decision.Delay, ct);
                    continue;   // 同一个账号再试
                }
                account = null;
                continue;
            }

            // 提交：越过此点不能再换号，也撤掉首字节期限
            streamCts.CancelAfter(Timeout.Infinite);
            _pool.MarkSuccess(account);

            using var writer = new SseWriter(ctx.Response);
            writer.Start();
            profile.Start(writer);
            profile.Delta(writer, first);

            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var delta = enumerator.Current;
                    if (delta.Outcome is { } outcome)
                    {
                        profile.Stop(writer, outcome);
                        return;
                    }
                    if (profile.Emits(delta)) profile.Delta(writer, delta);
                }
                // 上游没给终止帧就断了：补一个收尾，避免客户端一直挂着
                profile.Stop(writer, UpstreamOutcome.Done("stop", truncated: true));
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                // 客户端断开：取消上游读取，账号按正常释放（不算失败）
                AppLog.Info("gateway", "客户端断开，已取消上游读取");
                streamCts.Cancel();
            }
            catch (OperationCanceledException)
            {
                streamCts.Cancel();   // 网关停服
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
            return;
        }

        var final = last ?? PoolEmpty();
        WriteRaw(ctx, StatusFor(final), profile.ErrorBody(final));
    }

    /// <summary>写失败是否属于「客户端已经断开」。</summary>
    private static bool IsDisconnect(Exception ex)
        => ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException;

    /// <summary>OpenAI 协议的对话转发（/v1/chat/completions）。</summary>
    private async Task HandleChatAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) = await ResolveAsync(await ReadBodyAsync(req), ChatProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, ChatProtocol.RenderError(error!));
            return;
        }

        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new OpenAiStream(id, parsed.Model, unix), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, ChatProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, ChatProtocol.RenderCompletion(ok, id, unix));
    }

    /// <summary>Anthropic 协议的对话转发（/v1/messages）。</summary>
    private async Task HandleMessagesAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) =
            await ResolveAsync(await ReadBodyAsync(req), AnthropicProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, AnthropicProtocol.RenderError(error!));
            return;
        }

        var id = "msg_" + Guid.NewGuid().ToString("N")[..24];
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new AnthropicStream(id, parsed.Model), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, AnthropicProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, AnthropicProtocol.RenderResponse(ok, id, parsed.Model));
    }

    /// <summary>OpenAI Responses API 的对话转发（/v1/responses）。</summary>
    private async Task HandleResponsesAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) =
            await ResolveAsync(await ReadBodyAsync(req), ResponsesProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, ResponsesProtocol.RenderError(error!));
            return;
        }

        var id = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new ResponsesStream(id, parsed.Model, unix), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, ResponsesProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, ResponsesProtocol.RenderResponse(ok, id, unix));
    }

    /// <summary>
    /// 按协议把上游增量渲染成客户端帧。提交前的失败用 <see cref="ErrorBody"/>（普通 JSON 错误体），
    /// 提交后的失败由 <see cref="Stop"/> 收尾（各协议的流内错误形状不同）。
    /// </summary>
    private abstract class StreamProfile
    {
        /// <summary>该协议是否转发这个增量（例如 Anthropic 侧丢弃思考）。</summary>
        public abstract bool Emits(UpstreamDelta delta);

        /// <summary>提交后写协议起始帧。</summary>
        public abstract void Start(SseWriter writer);

        public abstract void Delta(SseWriter writer, UpstreamDelta delta);

        /// <summary>写终止帧；<paramref name="outcome"/> 带错误时写该协议的流内错误形状。</summary>
        public abstract void Stop(SseWriter writer, UpstreamOutcome outcome);

        /// <summary>尚未提交时的错误体（JSON，非 SSE）。</summary>
        public abstract string ErrorBody(ChatUpstreamError error);
    }

    /// <summary>OpenAI Chat Completions：正文 / 思考 / 工具调用都外发。</summary>
    private sealed class OpenAiStream(string requestId, string model, long unixSeconds) : StreamProfile
    {
        public override bool Emits(UpstreamDelta delta) => delta.HasPayload;

        public override void Start(SseWriter writer)
            => writer.Write(ChatProtocol.RenderStreamStart(requestId, model, unixSeconds));

        public override void Delta(SseWriter writer, UpstreamDelta delta)
            => writer.Write(ChatProtocol.RenderStreamDelta(requestId, model, unixSeconds,
                delta.Content, delta.Reasoning, delta.ToolCalls));

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            if (outcome.Error is { } error)
            {
                writer.Write(ChatProtocol.RenderStreamError(error));
                writer.Write(ChatProtocol.DoneMarker);
                return;
            }
            writer.Write(ChatProtocol.RenderStreamStop(requestId, model, unixSeconds,
                outcome.FinishReason, outcome.PromptTokens, outcome.CompletionTokens));
            writer.Write(ChatProtocol.DoneMarker);
        }

        public override string ErrorBody(ChatUpstreamError error) => ChatProtocol.RenderError(error);
    }

    /// <summary>Anthropic Messages：只外发正文（思考块回传要签名，上游不给，索性不发）。</summary>
    private sealed class AnthropicStream(string requestId, string model) : StreamProfile
    {
        public override bool Emits(UpstreamDelta delta) => delta.Content is { Length: > 0 };

        public override void Start(SseWriter writer)
        {
            writer.Write(AnthropicProtocol.RenderStreamStart(requestId, model));
            writer.Write(AnthropicProtocol.RenderBlockStart());
        }

        public override void Delta(SseWriter writer, UpstreamDelta delta)
            => writer.Write(AnthropicProtocol.RenderTextDelta(delta.Content!));

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            writer.Write(AnthropicProtocol.RenderBlockStop());
            if (outcome.Error is { } error)
            {
                writer.Write(AnthropicProtocol.RenderErrorFrame(error));
                return;   // 流内错误不发 message_stop
            }
            writer.Write(AnthropicProtocol.RenderMessageDelta(outcome.FinishReason,
                outcome.PromptTokens, outcome.CompletionTokens));
            writer.Write(AnthropicProtocol.RenderMessageStop());
        }

        public override string ErrorBody(ChatUpstreamError error) => AnthropicProtocol.RenderError(error);
    }

    /// <summary>OpenAI Responses API：只外发正文，收尾帧需要全文。</summary>
    private sealed class ResponsesStream(string requestId, string model, long unixSeconds) : StreamProfile
    {
        private readonly StringBuilder _text = new();
        private bool _itemStarted;

        public override bool Emits(UpstreamDelta delta) => delta.Content is { Length: > 0 };

        public override void Start(SseWriter writer)
            => writer.Write(ResponsesProtocol.RenderCreated(requestId, model, unixSeconds));

        public override void Delta(SseWriter writer, UpstreamDelta delta)
        {
            if (!_itemStarted)
            {
                writer.Write(ResponsesProtocol.RenderOutputItemAdded(requestId));
                _itemStarted = true;
            }
            _text.Append(delta.Content);
            writer.Write(ResponsesProtocol.RenderTextDelta(requestId, delta.Content!));
        }

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            if (outcome.Error is { } error)
            {
                writer.Write(ResponsesProtocol.RenderFailed(requestId, model, unixSeconds, error));
                return;
            }
            if (_itemStarted) writer.Write(ResponsesProtocol.RenderOutputItemDone(requestId, _text.ToString()));
            writer.Write(ResponsesProtocol.RenderCompleted(requestId, model, unixSeconds, _text.ToString(), outcome));
        }

        public override string ErrorBody(ChatUpstreamError error) => ResponsesProtocol.RenderError(error);
    }

    /// <summary>SSE 写出口：逐帧落 socket（不缓冲整段），提交后不可再改状态码与响应头。</summary>
    private sealed class SseWriter : IDisposable
    {
        private readonly HttpListenerResponse _response;
        private readonly Stream _output;
        private bool _closed;

        public SseWriter(HttpListenerResponse response)
        {
            _response = response;
            _output = response.OutputStream;
        }

        /// <summary>提交响应头；只在首个可转发增量到手后调用一次。</summary>
        public void Start()
        {
            _response.StatusCode = 200;
            _response.ContentType = "text/event-stream; charset=utf-8";
            _response.Headers["Cache-Control"] = "no-cache";
            // 不能设 ContentLength64：那会要求先知道长度，等于把整段缓冲起来
            _response.SendChunked = true;
            ApplyCors(_response);
        }

        public void Write(string frame)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            _output.Write(bytes, 0, bytes.Length);
            _output.Flush();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _response.Close(); }
            catch { /* 客户端已断开 */ }
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return "";
        }
    }

    private static void ApplyCors(HttpListenerResponse res)
    {
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Headers"] = "X-TraeTool-Token, Authorization, x-api-key, Content-Type";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    /// <summary>写 JSON 字符串（协议层已序列化，不再二次编码）。</summary>
    private static void WriteRaw(HttpListenerContext ctx, int status, string json)
    {
        try
        {
            var res = ctx.Response;
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            ApplyCors(res);
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

    private object BuildStatus(int limit)
    {
        var cfg = _config();
        var accounts = new List<object>();
        for (int i = 0; i < cfg.Accounts.Count; i++)
        {
            var a = cfg.Accounts[i];
            var checkedToday = a.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today);
            accounts.Add(new
            {
                id = a.Id,
                name = a.DisplayName(i + 1),
                uid = a.AccountUid,
                group = a.Group,
                enabled = a.Enabled,
                loggedIn = !string.IsNullOrEmpty(a.Token) || !string.IsNullOrEmpty(a.Session),
                health = AccountHealth.Evaluate(a, DateTime.Today),
                checkedToday,
                credits = a.RemainingCredits,
                member = a.IsMember,
                failStreak = a.FailStreak,
                suspendedUntil = a.SuspendedUntil?.ToString("yyyy-MM-dd"),
            });
        }

        var active = cfg.Accounts.FirstOrDefault(x => x.Id == cfg.ActiveAccountId) ?? cfg.Accounts.FirstOrDefault();
        var activeIdx = active == null ? 0 : cfg.Accounts.IndexOf(active) + 1;

        return new
        {
            app = "TraeTool",
            version = UpdateService.CurrentVersionText,
            now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            busy = new { batch = AppServices.BatchBusy, switchBusy = AppServices.SwitchBusy },
            activeAccount = active == null ? null : new
            {
                id = active.Id,
                name = active.DisplayName(activeIdx),
                checkedToday = active.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today),
            },
            summary = new
            {
                accounts = cfg.Accounts.Count,
                enabled = cfg.Accounts.Count(a => a.Enabled),
                checkedToday = cfg.Accounts.Count(x =>
                    x.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today)),
            },
            lastRun = new { at = _lastRunAt, summary = _lastRunSummary },
            accounts,
            records = CheckinRecordRepo.Recent(limit),
        };
    }

    private static void Write(HttpListenerContext ctx, int status, object? body)
    {
        if (body == null)
        {
            try
            {
                var res = ctx.Response;
                res.StatusCode = status;
                ApplyCors(res);
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
