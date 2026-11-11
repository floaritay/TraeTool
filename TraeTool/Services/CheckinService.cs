using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 签到执行：单账号与批量。依赖外部注入的配置持久化回调（SaveConfig），
/// 保证服务层不直接持有全局状态，便于测试与复用。
/// </summary>
public sealed class CheckinService(Func<AppSettings> getConfig, Action saveConfig)
{
    /// <summary>
    /// 单账号签到。流程：
    ///   未登录 → 本地今日已签跳过 → EnsureValidToken（Session 静默续期）
    ///   → status 预检（服务器已签 → 本地补记，不重签）
    ///   → claim（9074 风控 → 换新设备号重试一次）
    ///   → claim 响应不含所得积分 → 回查 status 解析（credits + 会员 extra_credits）
    ///   → 更新账号字段、写历史、刷新剩余额。
    /// </summary>
    public async Task<CheckinResult> CheckinOneAsync(Account account, string displayName, bool skipIfCheckedToday = true)
    {
        var cfg = getConfig();

        if (string.IsNullOrEmpty(account.Token) && string.IsNullOrEmpty(account.Session))
            return new CheckinResult(account.Id, displayName, false, 0, "未登录");

        // 连续失败已达阈值 → 当日暂停（次日自动恢复），不再发起请求
        if (AccountHealth.IsSuspended(account, DateTime.Today))
            return new CheckinResult(account.Id, displayName, false, 0, "已暂停（连续失败，次日自动恢复）");

        EnsureDeviceIdUnique(account, cfg);

        // 本地当日去重（服务器端还有按 device-id 的每日去重兜底）
        if (skipIfCheckedToday && account.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today))
        {
            AccountHealth.OnSuccess(account);
            saveConfig();
            return new CheckinResult(account.Id, displayName, true, 0, "今日已签");
        }

        if (!await TokenRenewer.EnsureValidTokenAsync(account))
            return Fail("会话失效，请重新登录");
        saveConfig(); // 续期可能已写回 Token

        var token = account.Token!;

        // —— status 预检：服务器已签而本地漏记 → 补记（不重复 claim）——
        var before = await TraeApiClient.GetStatusAsync(token, account.DeviceId);
        if (before.HttpError != null)
            return Fail(before.HttpError);
        if (before.Code != 0)
            return Fail(before.Message ?? $"status code={before.Code}");

        double beforeCredits = 0;
        if (before.Body is { } beforeBody)
            beforeCredits = ReadStatusCredits(beforeBody);

        if (ReadCheckedIn(before.Body))
        {
            account.LastCheckinDate = DateOnly.FromDateTime(DateTime.Today);
            AccountHealth.OnSuccess(account);
            saveConfig();
            CheckinRecordRepo.Insert(account, 0, 0);
            AppLog.Info("checkin", $"{displayName}: 服务器已签到，本地补记");
            return new CheckinResult(account.Id, displayName, true, 0, "今日已签");
        }

        // —— claim：9074 换号重试（镜像 checkin.py：随机 0.8~1.5s 间隔、多次尝试——
        //    0 秒间隔立即重发会撞上服务端同一冷却窗，实测只会连吃 9074）——
        const int MaxAttempts = 3; // 首次 + 2 次换号重试
        var claim = await TraeApiClient.ClaimAsync(token, account.DeviceId);
        if (claim.HttpError != null)
            return Fail(claim.HttpError);

        int attempt = 1;
        while (claim.Code == TraeApiClient.CodeDeviceRisk && attempt < MaxAttempts)
        {
            attempt++;
            var sleepMs = 800 + Random.Shared.Next(0, 700); // 0.8~1.5s 抖动
            AppLog.Info("checkin", $"{displayName}: 9074 限流，{sleepMs}ms 后换号重试（{attempt}/{MaxAttempts}）");
            await Task.Delay(sleepMs);
            account.DeviceId = DeviceIdFactory.GenerateUnique(
                getConfig().Accounts.Where(a => a.Id != account.Id).Select(a => a.DeviceId));
            saveConfig();
            claim = await TraeApiClient.ClaimAsync(token, account.DeviceId);
            if (claim.HttpError != null)
                return Fail(claim.HttpError);
        }

        bool claimedNow = claim.Code == 0 || ReadCheckedIn(claim.Body);
        if (!claimedNow)
            return Fail(claim.Message ?? $"签到失败（code={claim.Code}）");

        // 记一次失败并落盘；连续失败达阈值时当日暂停该账号
        CheckinResult Fail(string reason)
        {
            bool suspended = AccountHealth.OnFailure(account, DateTime.Today, cfg.CheckinFailSuspendThreshold);
            saveConfig();
            if (suspended)
                AppLog.Info("checkin", $"{displayName}: 连续失败 {account.FailStreak} 次，今日暂停签到（次日自动恢复）");
            return new CheckinResult(account.Id, displayName, false, 0, reason);
        }

        // —— 回查 status 解析本次所得积分（claim 响应不含积分字段）——
        double gained = 0;
        var after = await TraeApiClient.GetStatusAsync(token, account.DeviceId);
        if (after.Body is { } afterBody && ReadCheckedIn(afterBody))
        {
            double afterCredits = ReadStatusCredits(afterBody);
            gained = Math.Max(0, afterCredits - beforeCredits);
            // 首次签到（before=0）无法做差值时，用 status 直接给出的当日奖励
            if (gained == 0) gained = ReadStatusRewards(afterBody, account.IsMember);
        }

        account.LastCheckinDate = DateOnly.FromDateTime(DateTime.Today);
        AccountHealth.OnSuccess(account);
        saveConfig();
        CheckinRecordRepo.Insert(account, gained, claim.Code);
        AppLog.Info("checkin", $"{displayName}: 签到成功 +{gained:0}（device={account.DeviceId}）");

        // —— 刷新剩余额 ——
        var remaining = await TraeApiClient.GetRemainingCreditsAsync(token, account.DeviceId);
        if (remaining >= 0)
        {
            account.RemainingCredits = remaining;
            saveConfig();
            CheckinRecordRepo.UpsertSnapshot(account.Id, remaining);
        }

        return new CheckinResult(account.Id, displayName, true, gained, "");
    }

    /// <summary>
    /// 批量签到：只跑 Enabled；账号间隔 base*1000 ± 2000ms 抖动（降低请求密度规避风控）；
    /// 单账号异常不断后续；返回逐账号结果。
    /// </summary>
    public async Task<List<CheckinResult>> BatchCheckinAsync(
        IEnumerable<Account> enabledAccounts,
        int baseIntervalSeconds,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<CheckinResult>();
        var firstPass = new List<(Account Acc, string Name)>();
        int idx = 0;
        foreach (var acc in enabledAccounts)
        {
            if (ct.IsCancellationRequested) break;
            var name = acc.DisplayName(idx + 1);
            firstPass.Add((acc, name));

            if (idx++ > 0)
            {
                var baseMs = Math.Clamp(baseIntervalSeconds, 1, 60) * 1000;
                var delay = Math.Max(1000, baseMs + Random.Shared.Next(-2000, 2001));
                progress?.Report($"等待 {delay / 1000.0:0.0}s 后签到 {name}…");
                await Task.Delay(delay, ct);
            }

            try
            {
                progress?.Report($"正在签到 {name}…");
                results.Add(await CheckinOneAsync(acc, name));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error("checkin", $"{name} 异常: {ex.Message}");
                if (AccountHealth.OnFailure(acc, DateTime.Today, getConfig().CheckinFailSuspendThreshold))
                    AppLog.Info("checkin", $"{name}: 连续失败 {acc.FailStreak} 次，今日暂停签到（次日自动恢复）");
                saveConfig();
                results.Add(new CheckinResult(acc.Id, name, false, 0, "网络或接口异常"));
            }
        }

        // —— 9074 冷却补跑：服务端"参与用户太多"是分钟级限流，首轮命中的账号
        //    等 60~75s 后补一轮（每账号仅一次）。调用方在 BatchBusy 锁内等待，
        //    正好挡住冷却期内的手动/自动重复发起。 ——
        var rateLimited = results
            .Where(r => !r.Ok && r.Reason != null &&
                        (r.Reason.Contains("参与用户太多") || r.Reason.Contains("9074")))
            .Select(r => r.AccountId)
            .ToHashSet(StringComparer.Ordinal);
        if (rateLimited.Count > 0 && !ct.IsCancellationRequested)
        {
            var waitSec = 60 + Random.Shared.Next(0, 16);
            progress?.Report($"有 {rateLimited.Count} 个账号命中 9074 限流，冷却 {waitSec} 秒后自动补跑一轮（期间请勿重复点击）…");
            AppLog.Info("checkin", $"9074 冷却补跑：{rateLimited.Count} 个账号，等待 {waitSec}s");
            try { await Task.Delay(TimeSpan.FromSeconds(waitSec), ct); }
            catch (OperationCanceledException) { return results; }

            foreach (var (acc, name) in firstPass.Where(x => rateLimited.Contains(x.Acc.Id)))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    progress?.Report($"补跑 {name}…");
                    var r = await CheckinOneAsync(acc, name);
                    var i = results.FindIndex(x => x.AccountId == acc.Id && !x.Ok);
                    if (i >= 0) results[i] = r;
                    else results.Add(r);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Error("checkin", $"补跑 {name} 异常: {ex.Message}");
                }
            }
        }
        return results;
    }

    /// <summary>确保账号持有与其余账号不重复的设备号；缺号/撞号则换新（幂等）。多账号共用设备号会被服务端按日去重只签一个。</summary>
    public static void EnsureDeviceIdUnique(Account account, AppSettings cfg)
    {
        var used = cfg.Accounts.Where(a => a.Id != account.Id).Select(a => a.DeviceId);
        if (string.IsNullOrWhiteSpace(account.DeviceId) || used.Contains(account.DeviceId))
            account.DeviceId = DeviceIdFactory.GenerateUnique(used);
    }

    // ==================== status/claim body 解析 ====================

    private static bool ReadCheckedIn(System.Text.Json.JsonElement? body)
    {
        if (body is not { ValueKind: System.Text.Json.JsonValueKind.Object } b) return false;
        return b.TryGetProperty("checked_in", out var v) &&
               (v.ValueKind == System.Text.Json.JsonValueKind.True ||
                (v.ValueKind == System.Text.Json.JsonValueKind.String &&
                 bool.TryParse(v.GetString(), out var b2) && b2));
    }

    /// <summary>status.credits（数字或字符串）。</summary>
    private static double ReadStatusCredits(System.Text.Json.JsonElement body)
        => ReadDouble(body, "credits");

    /// <summary>当日奖励：credits + 会员 extra_credits。</summary>
    private static double ReadStatusRewards(System.Text.Json.JsonElement body, bool isMember)
    {
        var extra = isMember ? ReadDouble(body, "extra_credits") : 0;
        return ReadDouble(body, "credits") + extra;
    }

    private static double ReadDouble(System.Text.Json.JsonElement body, string prop)
    {
        if (!body.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => v.GetDouble(),
            System.Text.Json.JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => 0,
        };
    }
}
