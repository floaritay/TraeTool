using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 非流式转发的取号与失败处置：按池轮询取号，失败后由 <see cref="RetryPolicy"/> 决定
/// 是重试同号、换号还是直接失败。流式侧的同类逻辑见 LocalGateway.Streaming.cs。
/// </summary>
public sealed partial class LocalGateway
{
    /// <summary>同一账号最多重试几次（退避重试，来自 <see cref="RetryPolicy"/> 的 RetrySame）。</summary>
    private const int MaxSameAccountTries = 3;

    /// <summary>同一账号上累计退避的上限：本地网关要照顾首字节延迟。</summary>
    private static readonly TimeSpan MaxSameAccountBackoff = TimeSpan.FromSeconds(10);

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
                // 取到新账号时才清 last：换号后仍失败才用新错误，
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

            // 单次尝试的时限：HttpClient 超时只覆盖响应头，正文迟迟不来会一直挂着
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(UpstreamAttemptTimeout);
            UpstreamResult res;
            try
            {
                res = await _upstream.CompleteAsync(account, parsed, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                res = UpstreamResult.Fail(
                    $"上游响应超时（{UpstreamAttemptTimeout.TotalSeconds:0} 秒）", "upstream_error", 504);
            }
            if (res.Ok)
            {
                _pool.MarkSuccess(account);
                return (res.Completion!, null, 200);
            }

            last = res.Error!;
            var decision = RetryPolicy.Decide(last, tries, res.RetryAfter);
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
}
