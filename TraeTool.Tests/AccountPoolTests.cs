using TraeTool.Models;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class AccountPoolTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 10, 0, 0);

    private static Account New(string id, bool enabled = true, string token = "t")
        => new() { Id = id, Name = id, Enabled = enabled, Token = token, DeviceId = "100000000000000" + id.Length };

    private static AppSettings Config(params Account[] accounts)
    {
        var cfg = new AppSettings();
        cfg.Accounts.AddRange(accounts);
        return cfg;
    }

    [Fact]
    public void Candidates_filters_disabled_logged_out_and_suspended()
    {
        var ok = New("a");
        var disabled = New("b", enabled: false);
        var loggedOut = New("c", token: "");
        loggedOut.Session = "";
        var suspended = New("d");
        suspended.FailStreak = 9;
        suspended.SuspendedUntil = DateOnly.FromDateTime(Now);

        var pool = new AccountPool();
        var list = pool.Candidates(Config(ok, disabled, loggedOut, suspended), Now);

        Assert.Equal(new[] { ok }, list);
    }

    [Fact]
    public void Next_round_robins_across_candidates()
    {
        var a = New("a");
        var b = New("b");
        var cfg = Config(a, b);
        var pool = new AccountPool();

        Assert.Equal(a, pool.Next(cfg, Now));
        Assert.Equal(b, pool.Next(cfg, Now));
        Assert.Equal(a, pool.Next(cfg, Now));
    }

    [Fact]
    public void Next_returns_null_when_pool_empty()
    {
        Assert.Null(new AccountPool().Next(Config(), Now));
    }

    [Fact]
    public void Cooldown_excludes_account_until_it_expires()
    {
        var a = New("a");
        var b = New("b");
        var cfg = Config(a, b);
        var pool = new AccountPool();

        pool.MarkFail(a, Now, AccountPool.AuthCooldown);

        Assert.NotEqual(a, pool.Next(cfg, Now));                 // 冷却中被跳过
        Assert.Equal(b, pool.Next(cfg, Now));
        Assert.Equal(1, pool.CoolingCount(Now));
        Assert.Equal(a, pool.Next(cfg, Now.Add(AccountPool.AuthCooldown).AddSeconds(1))); // 到期恢复
    }

    [Fact]
    public void Fail_without_cooldown_keeps_account_selectable()
    {
        var a = New("a");
        var cfg = Config(a);
        var pool = new AccountPool();

        pool.MarkFail(a, Now, null);

        Assert.Equal(a, pool.Next(cfg, Now));
        Assert.Equal(0, pool.CoolingCount(Now));
    }

    [Fact]
    public void Auth_cooldown_is_longer_than_transient_cooldown()
    {
        Assert.True(AccountPool.AuthCooldown > AccountPool.TransientCooldown);
    }

    [Fact]
    public void Reset_clears_cooldown_and_cursor()
    {
        var a = New("a");
        var b = New("b");
        var cfg = Config(a, b);
        var pool = new AccountPool();
        pool.MarkFail(a, Now, AccountPool.AuthCooldown);

        pool.Reset();

        Assert.Equal(0, pool.CoolingCount(Now));
        Assert.Equal(a, pool.Next(cfg, Now));
    }

    [Fact]
    public void Success_clears_cooldown()
    {
        var a = New("a");
        var cfg = Config(a);
        var pool = new AccountPool();
        pool.MarkFail(a, Now, AccountPool.AuthCooldown);

        pool.MarkSuccess(a);

        Assert.Equal(a, pool.Next(cfg, Now));
        Assert.Equal(0, pool.CoolingCount(Now));
    }
}
