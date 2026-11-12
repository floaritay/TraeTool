using System.Net;
using System.Security.Cryptography;
using System.Text;
using TraeTool.Models;
using TraeTool.ViewModels;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 管理端点：/status（账号与签到状态汇总）与 /checkin（异步受理批量签到），
/// 以及它们使用的两套鉴权（管理 token / 客户端惯例头）。
/// </summary>
public sealed partial class LocalGateway
{
    /// <summary>最近一次网关触发签到的结果（/status 汇总用，跨请求保留）。</summary>
    private string? _lastRunAt;
    private string? _lastRunSummary;

    private bool Authorized(HttpListenerRequest req, HttpListenerContext ctx)
    {
        var got = req.Headers["X-TraeTool-Token"] ?? req.QueryString["token"] ?? "";
        if (TokenMatches(got)) return true;
        Write(ctx, 401, new { error = "missing or invalid token", hint = "X-TraeTool-Token header or ?token=" });
        return false;
    }

    /// <summary>
    /// 恒定时间比较令牌：普通字符串比较在首个不同字符处即返回，
    /// 攻击者能按响应时间差逐字节试出令牌。
    /// </summary>
    private bool TokenMatches(string got)
        => Token.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(Token));

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
        List<Account> accounts;
        lock (ConfigStore.SyncRoot) accounts = cfg.Accounts.ToList();
        var enabled = accounts.Count(a => a.Enabled);
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
                var list = accounts.Where(a => a.Enabled).ToList();
                var results = await AppServices.Checkin.BatchCheckinAsync(
                    list, cfg.CheckinIntervalSeconds);
                var total = results.Sum(r => r.Gained);
                var summary = CheckinPageViewModel.BuildBatchSummary(results, total);
                RecordRun(summary);
                AppLog.Info("gateway", "网关触发签到完成：" + summary);
                // 通知失败只记日志：不能反过来把已记录的成功摘要覆盖成「异常」
                try { AppServices.RaiseAccountsChanged(); }
                catch (Exception ex) { AppLog.Error("gateway", "账号变更通知失败: " + ex.Message); }
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
        if (TokenMatches(token)) return true;
        WriteRaw(ctx, 401, ChatProtocol.RenderError(new("missing or invalid api key", "invalid_request_error", 401)));
        return false;
    }

    /// <summary>转发未开启（设置项默认关闭）。</summary>
    private static void Disabled(HttpListenerContext ctx)
        => WriteRaw(ctx, 403, ChatProtocol.RenderError(new("该网关的对话转发未开启", "permission_denied", 403)));

    /// <summary>/status 的响应体：账号、汇总、最近一次网关签到与近期签到记录。</summary>
    private object BuildStatus(int limit)
    {
        var cfg = _config();
        List<Account> source;
        lock (ConfigStore.SyncRoot) source = cfg.Accounts.ToList();

        var accounts = new List<object>();
        for (int i = 0; i < source.Count; i++)
        {
            var a = source[i];
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

        var active = source.FirstOrDefault(x => x.Id == cfg.ActiveAccountId) ?? source.FirstOrDefault();
        var activeIdx = active == null ? 0 : source.IndexOf(active) + 1;

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
                accounts = source.Count,
                enabled = source.Count(a => a.Enabled),
                checkedToday = source.Count(x =>
                    x.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today)),
            },
            lastRun = new { at = _lastRunAt, summary = _lastRunSummary },
            accounts,
            records = CheckinRecordRepo.Recent(limit),
        };
    }
}
