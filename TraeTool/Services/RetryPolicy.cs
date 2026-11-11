namespace TraeTool.Services;

/// <summary>上游失败后的处置动作。</summary>
public enum RetryAction
{
    /// <summary>同一账号退避后重试（上游瞬时抖动，换号也躲不开）。</summary>
    RetrySame,

    /// <summary>换下一个账号（该账号本身不可用）。</summary>
    SwitchAccount,

    /// <summary>请求自身的问题，换号也解决不了，直接把错误回给客户端。</summary>
    Fatal,
}

/// <summary>一次失败的处置方案：动作、同号重试前的等待、该账号的冷却时长（null = 不冷却）。</summary>
public sealed record RetryDecision(RetryAction Action, TimeSpan Delay, TimeSpan? Cooldown)
{
    public static RetryDecision Same(TimeSpan delay) => new(RetryAction.RetrySame, delay, null);

    public static RetryDecision Switch(TimeSpan? cooldown = null)
        => new(RetryAction.SwitchAccount, TimeSpan.Zero, cooldown);

    public static RetryDecision Stop() => new(RetryAction.Fatal, TimeSpan.Zero, null);
}

/// <summary>
/// 按「HTTP 状态 + 上游响应文本」决定失败后怎么办（纯函数，便于逐条断言）。
/// 只做决策，不等待、不发请求；同号退避的等待由调用方执行。
/// 判定自上而下，先匹配者生效。
/// </summary>
public static class RetryPolicy
{
    /// <summary>同号重试的最长单次等待；本地网关要照顾首字节延迟，不能无限退避。</summary>
    public static readonly TimeSpan MaxSameAccountDelay = TimeSpan.FromSeconds(10);

    /// <summary>429 无 Retry-After 时的同号退避序列（按第几次重试取）。</summary>
    private static readonly TimeSpan[] RateLimitDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)];

    /// <summary>503/529 的同号退避序列（按第几次重试取）。</summary>
    private static readonly TimeSpan[] OverloadDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)];

    /// <summary>
    /// 账号级业务码（HTTP 400 携带）：<c>11128</c> 调用渠道被拦、<c>11140</c> 账号存在异常/已被限制使用——
    /// 换号才有用。
    /// </summary>
    private static readonly string[] AccountCodes = ["11128", "11140"];

    /// <summary>凭据类业务码（上游以 HTTP 200 + error 帧返回，<c>1001</c> 为凭据过期）。</summary>
    private static readonly string[] AuthCodes = ["1001", "1002", "1003"];

    /// <summary>
    /// 决策：<paramref name="sameAccountTries"/> 是本账号已经重试过几次（0 = 还没重试），
    /// <paramref name="retryAfter"/> 是上游 429 带回来的 Retry-After。
    /// </summary>
    public static RetryDecision Decide(ChatUpstreamError error, int sameAccountTries = 0,
        TimeSpan? retryAfter = null)
    {
        var status = error.Status;
        var text = error.Message ?? "";

        // 凭据被拒：换号并长冷却（调用方另行作废校验缓存）
        if (error.Type == "auth_error" || status is 401 or 403)
            return RetryDecision.Switch(AccountPool.AuthCooldown);

        // 空完成：上游正常收尾但零正文，多半栽在账号侧，换号重来
        if (error.Type == "empty_completion")
            return RetryDecision.Switch();

        // 业务错误帧：上游以 HTTP 200 返回 event: error，只能按响应文本里的 code 判断
        if (status is 0 or 200 && error.Type == "api_error")
            return HasAnyCode(text, AuthCodes) ? RetryDecision.Switch(AccountPool.AuthCooldown)
                : RetryDecision.Switch();

        // 限流：优先按上游给的 Retry-After 同号退避，退完仍限流再换号
        if (status == 429)
        {
            if (retryAfter is { } ra && ra > TimeSpan.Zero)
                return RetryDecision.Same(Min(ra, MaxSameAccountDelay));
            return sameAccountTries < RateLimitDelays.Length
                ? RetryDecision.Same(RateLimitDelays[sameAccountTries])
                : RetryDecision.Switch(AccountPool.TransientCooldown);
        }

        // 上游过载：同号退避后换号
        if (status is 503 or 529)
            return sameAccountTries < OverloadDelays.Length
                ? RetryDecision.Same(Min(OverloadDelays[sameAccountTries], MaxSameAccountDelay))
                : RetryDecision.Switch(AccountPool.TransientCooldown);

        // 网关瞬时抖动：同号重试一次足矣
        if (status == 502)
            return sameAccountTries == 0
                ? RetryDecision.Same(TimeSpan.FromMilliseconds(500))
                : RetryDecision.Switch();

        // 账号级受限 / 额度耗尽（HTTP 400 携带业务码）
        if (status == 400 && (HasAnyCode(text, AccountCodes) ||
                              Mentions(text, "credit", "exhaust", "额度", "积分", "restricted", "受限")))
            return RetryDecision.Switch(AccountPool.TransientCooldown);

        // 思考签名不被接受：同号重试一次（多为客户端回传的历史块问题）
        if (status == 400 && Mentions(text, "signature", "thinking"))
            return sameAccountTries == 0
                ? RetryDecision.Same(TimeSpan.FromMilliseconds(200))
                : RetryDecision.Stop();

        // 其余 4xx：请求本身的问题，换号没意义
        if (status is >= 400 and < 500) return RetryDecision.Stop();

        // 其它 5xx
        if (status >= 500) return RetryDecision.Switch();

        // 无状态码的失败（传输中断等）
        return RetryDecision.Switch();
    }

    private static bool HasAnyCode(string text, string[] codes)
        => codes.Any(code => IsNumberToken(text, code));

    /// <summary>
    /// 响应文本里是否出现该业务码：取值必须是独立的数字（前后不接数字），
    /// 于是 <c>{"code":11140}</c> 命中而 <c>111280</c> 不会命中 <c>11128</c>。
    /// 上游错误的文本形态有两种——HTTP 200 的 error 帧被整理成「上游 1001: …」，
    /// 4xx 则是「上游返回 HTTP 400：&lt;原始响应&gt;」，两种都能命中。
    /// </summary>
    private static bool IsNumberToken(string text, string code)
    {
        for (var at = text.IndexOf(code, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(code, at + 1, StringComparison.Ordinal))
        {
            var end = at + code.Length;
            if (end < text.Length && char.IsAsciiDigit(text[end])) continue;
            if (at > 0 && char.IsAsciiDigit(text[at - 1])) continue;
            return true;
        }
        return false;
    }

    private static bool Mentions(string text, params string[] words)
        => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
