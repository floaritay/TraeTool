using System.Collections.Concurrent;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 账号凭证有效性维护：JWT（~8h）失效时用 Session Cookie（~14d）静默换新。
/// 返回是否持有可用 token；换新成功会写回 Account，由调用方决定是否 Save。
/// 探测结果按账号缓存 <see cref="VerifyTtl"/>，避免高频调用方（网关转发）每请求都打一次上游。
/// </summary>
public static class TokenRenewer
{
    /// <summary>最近一次校验通过的（账号 → 当时令牌 + 有效截止）。</summary>
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> Verified =
        new(StringComparer.Ordinal);

    /// <summary>校验结果的复用时长；到期后重新探测。</summary>
    public static TimeSpan VerifyTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>作废某账号的校验缓存：上游以凭据被拒回应时调用，使下次调用重新探测/续期。</summary>
    public static void Invalidate(string? accountId)
    {
        if (!string.IsNullOrEmpty(accountId)) Verified.TryRemove(accountId, out _);
    }

    /// <summary>清空全部校验缓存（手动刷新凭据状态、测试复位用）。</summary>
    public static void ResetVerified() => Verified.Clear();

    /// <summary>
    /// 1. Token 且校验仍在有效期内 → 直接可用（不再探测）；
    /// 2. 否则走 status 探测，code==0 即可用并记入缓存；
    /// 3. 失效且有 Session → GetUserToken 换新 → 再探测确认；
    /// 4. 否则失败（提示重新登录）。
    /// </summary>
    public static async Task<bool> EnsureValidTokenAsync(Account account)
    {
        if (string.IsNullOrEmpty(account.Token) && string.IsNullOrEmpty(account.Session))
        {
            AppLog.Info("token", "Token/Session 均为空，未登录");
            return false;
        }

        if (!string.IsNullOrEmpty(account.Token))
        {
            if (IsFresh(account)) return true;

            var probe = await TraeApiClient.GetStatusAsync(account.Token, account.DeviceId);
            if (probe.HttpError == null && probe.Code == 0)
            {
                MarkVerified(account);
                return true;
            }
            AppLog.Info("token", $"Token 失效探测 code={probe.Code} err={probe.HttpError ?? probe.Message}");
            if (string.IsNullOrEmpty(account.Session)) return false;
        }

        var renewed = await TraeApiClient.GetUserTokenAsync(account.Session!);
        if (string.IsNullOrEmpty(renewed))
        {
            AppLog.Error("token", "Session 换新失败（会话可能已过期 ~14 天）");
            return false;
        }

        account.Token = renewed;
        account.TokenUpdatedAt = DateTime.Now;
        var uid = JwtHelper.ParseAccountUid(renewed);
        if (!string.IsNullOrEmpty(uid)) account.AccountUid = uid;
        AppLog.Info("token", "Session 换新 Token 成功");

        var again = await TraeApiClient.GetStatusAsync(renewed, account.DeviceId);
        if (again.HttpError == null && again.Code == 0)
        {
            MarkVerified(account);
            return true;
        }
        return false;
    }

    /// <summary>缓存命中：仍是同一个令牌且未过期。令牌被换过（重新登录/续期）即视为未知。</summary>
    private static bool IsFresh(Account account)
    {
        if (string.IsNullOrEmpty(account.Id)) return false;
        if (!Verified.TryGetValue(account.Id, out var hit)) return false;
        return hit.Until > DateTimeOffset.Now &&
               string.Equals(hit.Token, account.Token, StringComparison.Ordinal);
    }

    private static void MarkVerified(Account account)
    {
        if (string.IsNullOrEmpty(account.Id) || string.IsNullOrEmpty(account.Token)) return;
        Verified[account.Id] = (account.Token, DateTimeOffset.Now + VerifyTtl);
    }
}
