using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

/// <summary>上游失败处置表：状态码 + 响应文本 → 同号退避 / 换号 / 直接失败。</summary>
public class RetryPolicyTests
{
    private static ChatUpstreamError Err(int status, string message = "", string type = "upstream_error")
        => new(message, type, status);

    private static void AssertSwitch(RetryDecision d, TimeSpan? cooldown)
    {
        Assert.Equal(RetryAction.SwitchAccount, d.Action);
        Assert.Equal(cooldown, d.Cooldown);
    }

    [Fact]
    public void Auth_error_switches_with_long_cooldown()
        => AssertSwitch(RetryPolicy.Decide(Err(401, "凭据过期", "auth_error")), AccountPool.AuthCooldown);

    [Fact]
    public void Forbidden_switches_with_long_cooldown()
        => AssertSwitch(RetryPolicy.Decide(Err(403)), AccountPool.AuthCooldown);

    [Fact]
    public void Empty_completion_switches_without_cooldown()
        => AssertSwitch(RetryPolicy.Decide(Err(502, "上游已返回完成但没有文本内容", "empty_completion")), null);

    [Fact]
    public void Business_error_frame_with_auth_code_switches_with_long_cooldown()
        => AssertSwitch(
            RetryPolicy.Decide(Err(200, """{"code":1001,"message":"token expired"}""", "api_error")),
            AccountPool.AuthCooldown);

    [Fact]
    public void Business_error_frame_without_auth_code_switches()
        => AssertSwitch(
            RetryPolicy.Decide(Err(200, """{"code":2001,"message":"resolveByUsage"}""", "api_error")), null);

    [Fact]
    public void Rate_limit_follows_retry_after_when_given()
    {
        var d = RetryPolicy.Decide(Err(429), retryAfter: TimeSpan.FromSeconds(4));
        Assert.Equal(RetryAction.RetrySame, d.Action);
        Assert.Equal(TimeSpan.FromSeconds(4), d.Delay);
    }

    [Fact]
    public void Rate_limit_caps_a_huge_retry_after()
    {
        var d = RetryPolicy.Decide(Err(429), retryAfter: TimeSpan.FromMinutes(5));
        Assert.Equal(RetryPolicy.MaxSameAccountDelay, d.Delay);
    }

    [Fact]
    public void Rate_limit_backs_off_one_two_three_seconds_then_switches()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Decide(Err(429), 0).Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.Decide(Err(429), 1).Delay);
        Assert.Equal(TimeSpan.FromSeconds(3), RetryPolicy.Decide(Err(429), 2).Delay);
        AssertSwitch(RetryPolicy.Decide(Err(429), 3), AccountPool.TransientCooldown);
    }

    [Fact]
    public void Overload_backs_off_then_switches()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Decide(Err(503), 0).Delay);
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Decide(Err(503), 1).Delay); // 单次等待封顶 10s
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Decide(Err(529), 2).Delay);
        AssertSwitch(RetryPolicy.Decide(Err(503), 3), AccountPool.TransientCooldown);
    }

    [Fact]
    public void Bad_gateway_retries_once_then_switches()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPolicy.Decide(Err(502), 0).Delay);
        AssertSwitch(RetryPolicy.Decide(Err(502), 1), null);
    }

    [Fact]
    public void Credit_exhausted_on_400_switches_with_cooldown()
        => AssertSwitch(RetryPolicy.Decide(Err(400, "积分不足")), AccountPool.TransientCooldown);

    [Fact]
    public void Restricted_account_code_on_400_switches_with_cooldown()
        => AssertSwitch(
            RetryPolicy.Decide(Err(400, """{"code":11140,"message":"account restricted"}""")),
            AccountPool.TransientCooldown);

    [Fact]
    public void Restricted_code_is_still_matched_with_whitespace_around_the_colon()
        => AssertSwitch(
            RetryPolicy.Decide(Err(400, """{"code" : 11140, "message":"restricted"}""")),
            AccountPool.TransientCooldown);

    [Fact]
    public void Longer_code_does_not_collide_with_a_prefix()
    {
        // 111280 / 11141 不是账号受限码：不能因为前缀相同就换号
        Assert.Equal(RetryAction.Fatal, RetryPolicy.Decide(Err(400, """{"code":111280}""")).Action);
        Assert.Equal(RetryAction.Fatal, RetryPolicy.Decide(Err(400, """{"code":11141}""")).Action);
    }

    [Fact]
    public void Auth_code_survives_the_parser_message_shape()
    {
        // HTTP 200 的 error 帧被整理成「上游 1001: 凭据过期」，凭据类判定要认得这种形态
        AssertSwitch(
            RetryPolicy.Decide(Err(200, "上游 1001: token expired", "api_error")),
            AccountPool.AuthCooldown);
    }

    [Fact]
    public void Account_code_is_read_from_a_4xx_body()
    {
        // 4xx 的错误文本是「上游返回 HTTP 400：<原始响应>」
        AssertSwitch(
            RetryPolicy.Decide(Err(400, """上游返回 HTTP 400：{"code":11140,"message":"账号存在异常"}""")),
            AccountPool.TransientCooldown);
    }

    [Fact]
    public void Thinking_signature_is_retried_once_then_fatal()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), RetryPolicy.Decide(Err(400, "invalid thinking.signature"), 0).Delay);
        Assert.Equal(RetryAction.Fatal, RetryPolicy.Decide(Err(400, "invalid thinking.signature"), 1).Action);
    }

    [Fact]
    public void Plain_400_is_fatal_without_burning_other_accounts()
        => Assert.Equal(RetryAction.Fatal, RetryPolicy.Decide(Err(400, "context too long")).Action);

    [Fact]
    public void Other_4xx_is_fatal() => Assert.Equal(RetryAction.Fatal, RetryPolicy.Decide(Err(422)).Action);

    [Fact]
    public void Other_5xx_switches() => AssertSwitch(RetryPolicy.Decide(Err(500)), null);
}
