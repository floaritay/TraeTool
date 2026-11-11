using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 账号池：在已登录、启用、未暂停的账号间轮流取号，失败的账号先冷却再换下一个。
/// 取号规则（纯逻辑，便于测试）：
///   候选 = Enabled ∩ 持有凭据 ∩ 未被签到熔断 ∩ 未在冷却中；
///   轮询游标每次从候选表头起算，因此失败换号后天然落到别的账号；
///   认证类失败冷却更久（凭据本身已失效），其余失败走短冷却。
/// </summary>
public sealed class AccountPool
{
    /// <summary>一般失败冷却时长（限流/网络/上游 5xx）。</summary>
    public static readonly TimeSpan TransientCooldown = TimeSpan.FromMinutes(5);

    /// <summary>凭据失效冷却时长（上游 401/403，需要重新登录才能恢复）。</summary>
    public static readonly TimeSpan AuthCooldown = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.Ordinal);
    private int _cursor;

    /// <summary>候选账号（按当前配置与冷却状态计算）。</summary>
    public List<Account> Candidates(AppSettings cfg, DateTime now)
    {
        var cutoff = new DateTimeOffset(now);
        var list = new List<Account>();
        foreach (var a in cfg.Accounts)
        {
            if (!a.Enabled) continue;
            if (string.IsNullOrEmpty(a.Token) && string.IsNullOrEmpty(a.Session)) continue;
            if (AccountHealth.IsSuspended(a, now)) continue;
            if (_cooldownUntil.TryGetValue(a.Id, out var until) && until > cutoff) continue;
            list.Add(a);
        }
        return list;
    }

    /// <summary>取下一个账号；池内无可用账号返回 null。</summary>
    public Account? Next(AppSettings cfg, DateTime now)
    {
        lock (_gate)
        {
            var list = Candidates(cfg, now);
            if (list.Count == 0) return null;
            var acc = list[_cursor % list.Count];
            _cursor++;
            return acc;
        }
    }

    /// <summary>调用成功：清冷却（并复位游标避免长期停在同一号上）。</summary>
    public void MarkSuccess(Account account)
    {
        lock (_gate) _cooldownUntil.Remove(account.Id);
    }

    /// <summary>
    /// 调用失败。cooldown 非空则按该时长冷却该账号（下一次取号自动跳过），
    /// 为 null 表示不冷却——只在本次请求里换号重试，避免单次上游抖动把账号停掉。
    /// </summary>
    public void MarkFail(Account account, DateTime now, TimeSpan? cooldown)
    {
        if (cooldown is not { } span) return;
        lock (_gate) _cooldownUntil[account.Id] = new DateTimeOffset(now) + span;
    }

    /// <summary>当前冷却中的账号数（状态展示用）。</summary>
    public int CoolingCount(DateTime now)
    {
        var cutoff = new DateTimeOffset(now);
        lock (_gate) return _cooldownUntil.Count(kv => kv.Value > cutoff);
    }

    /// <summary>清空全部冷却（设置页「恢复」用，凭据重新登录后需手动解除）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _cooldownUntil.Clear();
            _cursor = 0;
        }
    }
}
