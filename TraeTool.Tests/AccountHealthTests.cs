using TraeTool.Models;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class AccountHealthTests
{
    private static readonly DateTime Today = new(2026, 3, 10);

    private static Account LoggedIn() => new() { Token = "tok", Session = "sess" };

    [Fact]
    public void Evaluate_reports_offline_without_credentials()
    {
        Assert.Equal("offline", AccountHealth.Evaluate(new Account(), Today));
    }

    [Fact]
    public void Evaluate_reports_ok_with_token()
    {
        Assert.Equal("ok", AccountHealth.Evaluate(LoggedIn(), Today));
    }

    [Fact]
    public void Evaluate_reports_expired_when_token_past_and_no_session()
    {
        var expired = Helpers.JwtWithExpiry(Today.AddDays(-1));
        var account = new Account { Token = expired };
        Assert.Equal("expired", AccountHealth.Evaluate(account, Today));
    }

    [Fact]
    public void Evaluate_keeps_ok_when_expired_but_session_present()
    {
        // 有 Session 时可静默续期，不算过期
        var account = new Account { Token = Helpers.JwtWithExpiry(Today.AddDays(-1)), Session = "sess" };
        Assert.Equal("ok", AccountHealth.Evaluate(account, Today));
    }

    [Fact]
    public void OnFailure_suspends_only_after_reaching_threshold()
    {
        var account = LoggedIn();

        Assert.False(AccountHealth.OnFailure(account, Today, 3));
        Assert.False(AccountHealth.OnFailure(account, Today, 3));
        Assert.True(AccountHealth.OnFailure(account, Today, 3));
        Assert.Equal(3, account.FailStreak);
        Assert.True(AccountHealth.IsSuspended(account, Today));
    }

    [Fact]
    public void Suspension_expires_the_next_day()
    {
        var account = LoggedIn();
        for (int i = 0; i < 3; i++) AccountHealth.OnFailure(account, Today, 3);

        Assert.True(AccountHealth.IsSuspended(account, Today));
        Assert.False(AccountHealth.IsSuspended(account, Today.AddDays(1)));
        Assert.Equal("suspended", AccountHealth.Evaluate(account, Today));
        Assert.Equal("ok", AccountHealth.Evaluate(account, Today.AddDays(1)));
    }

    [Fact]
    public void Fail_streak_restarts_after_expired_suspension()
    {
        var account = LoggedIn();
        for (int i = 0; i < 3; i++) AccountHealth.OnFailure(account, Today, 3);

        // 次日首次失败不应立刻再暂停（计数已过期清零）
        var nextDay = Today.AddDays(1);
        Assert.False(AccountHealth.OnFailure(account, nextDay, 3));
        Assert.Equal(1, account.FailStreak);
    }

    [Fact]
    public void OnSuccess_clears_streak_and_suspension()
    {
        var account = LoggedIn();
        for (int i = 0; i < 3; i++) AccountHealth.OnFailure(account, Today, 3);

        AccountHealth.OnSuccess(account);

        Assert.Equal(0, account.FailStreak);
        Assert.Null(account.SuspendedUntil);
        Assert.False(AccountHealth.IsSuspended(account, Today));
    }

    [Fact]
    public void Once_suspended_further_failures_do_not_re_trigger()
    {
        var account = LoggedIn();
        for (int i = 0; i < 3; i++) AccountHealth.OnFailure(account, Today, 3);

        Assert.False(AccountHealth.OnFailure(account, Today, 3));
    }
}
