using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 账号凭证有效性维护：JWT（~8h）失效时用 Session Cookie（~14d）静默换新。
/// 返回是否持有可用 token；换新成功会写回 Account，由调用方决定是否 Save。
/// </summary>
public static class TokenRenewer
{
    /// <summary>
    /// 1. 现有 Token 走 status 探测，code==0 即可用；
    /// 2. 失效且有 Session → GetUserToken 换新 → 再探测确认；
    /// 3. 否则失败（提示重新登录）。
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
            var probe = await TraeApiClient.GetStatusAsync(account.Token, account.DeviceId);
            if (probe.HttpError == null && probe.Code == 0) return true;
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
        return again.HttpError == null && again.Code == 0;
    }
}
