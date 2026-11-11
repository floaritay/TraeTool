using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 账号健康状态判定（纯逻辑，供 UI 徽标与自检复用）。
/// 取值：ok / suspended / expired / offline（未登录）。
/// </summary>
public static class AccountHealth
{
    /// <summary>连续签到失败达到该次数即在当日暂停（次日自动恢复）。</summary>
    public const int DefaultSuspendThreshold = AppSettings.DefaultFailSuspendThreshold;

    /// <summary>该账号当日是否处于「连续失败暂停」状态。</summary>
    public static bool IsSuspended(Account account, DateTime today)
    {
        if (account.SuspendedUntil is not { } until) return false;
        return until >= DateOnly.FromDateTime(today);
    }

    /// <summary>健康状态码（见类注释）。</summary>
    public static string Evaluate(Account account, DateTime today)
    {
        bool loggedIn = !string.IsNullOrEmpty(account.Token) || !string.IsNullOrEmpty(account.Session);
        if (!loggedIn) return "offline";
        if (IsSuspended(account, today)) return "suspended";

        // 无 Session 且 JWT 已过期 → 必须重新登录（有 Session 时可由刷新接口静默续期）
        if (string.IsNullOrEmpty(account.Session) &&
            JwtHelper.TryGetExpiry(account.Token) is { } exp && exp <= today)
            return "expired";

        return "ok";
    }

    /// <summary>失败计数变化：ok 时清零并解除暂停。</summary>
    public static void OnSuccess(Account account)
    {
        account.FailStreak = 0;
        account.SuspendedUntil = null;
    }

    /// <summary>
    /// 记一次失败；连续失败达到阈值则暂停当日（次日自动恢复）。
    /// 返回 true = 本次触发了暂停。
    /// </summary>
    public static bool OnFailure(Account account, DateTime today, int threshold = DefaultSuspendThreshold)
    {
        var day = DateOnly.FromDateTime(today);

        // 上一次暂停已过期 → 计数清零重来：否则恢复后当天一次偶发失败就会再次整日暂停
        if (account.SuspendedUntil is { } prev && prev < day) account.FailStreak = 0;

        account.FailStreak++;
        if (threshold < 1) threshold = 1;
        if (account.FailStreak >= threshold)
        {
            if (account.SuspendedUntil == day) return false; // 当日已在暂停中
            account.SuspendedUntil = day;
            return true;
        }
        return false;
    }
}
