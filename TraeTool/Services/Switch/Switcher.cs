using TraeTool.ViewModels;
using System.IO;
namespace TraeTool.Services.Switch;

/// <summary>冷切换结果。</summary>
public enum SwitchOutcome
{
    /// <summary>已进入目标账号（凭据键全程齐全）；storage.json 已回写档案。</summary>
    Active,

    /// <summary>明显未进入（凭据被客户端清空）——已自动回滚到切换前账号并重启。</summary>
    RolledBack,

    /// <summary>信号不足（超时仍读不到凭据）——回滚快照保留，需用户人工确认。</summary>
    NeedsConfirm,

    /// <summary>恢复过程中 IO 失败——已回滚快照并尝试重启客户端。</summary>
    Failed,

    /// <summary>前置条件不满足（找不到 exe / 客户端关不掉 / 无档案）——live 未被动过。</summary>
    Aborted,
}

public sealed record SwitchResult(SwitchOutcome Outcome, string Detail, string? RollbackDir = null);

/// <summary>
/// 冷切换编排：解析 exe → 关客户端 → live 快照到回滚区 → 恢复目标档案 → 拉起
/// → 守护轮询凭据键（20s）→ 成功回写 storage.json；失败按态回滚。
/// 所有重 IO/进程操作在后台线程执行，进度经 IProgress&lt;string&gt; 报给 UI。
/// </summary>
public sealed class Switcher(VaultStore vault)
{
    public async Task<SwitchResult> SwitchAsync(
        string accountId, IProgress<string>? log = null, CancellationToken ct = default)
    {
        void Say(string msg)
        {
            AppLog.Info("switch", msg);
            log?.Report(msg);
        }

        // ① 前置：exe 与档案
        var exe = ClientControl.ResolveExe(AppServices.Config.ClientExePath);
        if (exe == null)
        {
            Say("未找到 Trae 客户端可执行文件，请在页面顶部手动指定");
            return new SwitchResult(SwitchOutcome.Aborted, "未找到客户端 exe");
        }
        if (!VaultStore.Exists(accountId))
        {
            Say("该账号尚无档案，无法切换");
            return new SwitchResult(SwitchOutcome.Aborted, "无档案");
        }

        // ② 关闭客户端（关不掉就中止——live 一个字节都还没动）
        if (ClientControl.IsRunning())
        {
            Say("正在关闭 Trae 客户端（先优雅关闭，超时再强杀）…");
            if (!await ClientControl.KillAllAsync(ct))
                return new SwitchResult(SwitchOutcome.Aborted, "客户端无法关闭，已中止");
        }
        else
        {
            Say("客户端未在运行");
        }

        // ③ 回滚快照（必须在动 live 之前建立）
        var rollbackDir = Path.Combine(Path.GetTempPath(), "traeswitch_rollback_" + Guid.NewGuid().ToString("N"));
        try
        {
            await VaultStore.SnapshotLiveAsync(rollbackDir, log, ct);
        }
        catch (Exception ex)
        {
            Say("建立回滚快照失败，中止（live 未改动）: " + ex.Message);
            FileSystemUtil.TryDeleteDir(rollbackDir);
            return new SwitchResult(SwitchOutcome.Aborted, "快照失败: " + ex.Message);
        }

        // ④ 恢复目标档案（前置清理 code.lock/WAL 在 VaultStore 内；失败抛 VaultIoException）
        try
        {
            Say("正在写回目标账号登录态…");
            await vault.RestoreAsync(accountId, log, ct);
        }
        catch (Exception ex)
        {
            Say("恢复失败，正在回滚: " + ex.Message);
            AppLog.Error("switch", "恢复失败: " + ex);
            try
            {
                await VaultStore.RestoreFromAsync(rollbackDir, log, ct);
                ClientControl.Launch(exe);
                FileSystemUtil.TryDeleteDir(rollbackDir);
            }
            catch (Exception rex)
            {
                AppLog.Error("switch", "回滚也失败: " + rex);
                return new SwitchResult(SwitchOutcome.Failed,
                    $"恢复失败且回滚异常，快照保留在 {rollbackDir}", rollbackDir);
            }
            return new SwitchResult(SwitchOutcome.Failed, "恢复失败，已回滚: " + ex.Message);
        }

        // ⑤ 拉起
        try
        {
            Say("正在启动客户端…");
            ClientControl.Launch(exe);
        }
        catch (Exception ex)
        {
            Say("启动客户端失败，回滚: " + ex.Message);
            await RollbackAsync(rollbackDir, exe, log, ct);
            return new SwitchResult(SwitchOutcome.Failed, "启动失败已回滚: " + ex.Message);
        }

        // ⑥ 守护：稳定判定（恢复动作自己会写凭据键，必须等进程存活 + 凭据持续保持
        //    ≥5 秒、给客户端鉴权失败留出删键窗口；键被清空则提前回滚）
        Say("守护中：需进程存活且凭据稳定保持 5 秒（最长 20 秒）…");
        var state = await AuthProbe.WaitStableAsync(
            CarrierDefs.DataRoot, ClientControl.IsRunning, TimeSpan.FromSeconds(5), ct);

        switch (state)
        {
            case AuthState.Active:
                Say("进程存活且凭据持续稳定，已进入目标账号");
                try
                {
                    await vault.SyncBackStorageJsonAsync(accountId); // refresh 轮换回写，防二次切换 401
                    Say("已回写 storage.json 到档案（凭证保鲜）");
                }
                catch (Exception ex)
                {
                    AppLog.Error("switch", "回写失败（下次切换该账号可能 401，届时重新建档）: " + ex.Message);
                    Say("回写 storage.json 失败（不影响本次切换）");
                }
                FileSystemUtil.TryDeleteDir(rollbackDir);
                return new SwitchResult(SwitchOutcome.Active, "切换成功");

            case AuthState.RolledBack:
                Say("客户端未进入目标账号（凭据被清除），自动回滚");
                var outcome = await RollbackAsync(rollbackDir, exe, log, ct);
                return new SwitchResult(outcome,
                    outcome == SwitchOutcome.Failed ? "回滚异常" : "已回滚到切换前账号并重启");

            default: // Transient → 超时
                Say("超时未观察到凭据键，无法自动判定——回滚快照已保留，请人工确认");
                return new SwitchResult(SwitchOutcome.NeedsConfirm,
                    "请确认客户端登录态：若已是目标账号则成功；否则请重新切换或先在该账号上登录建档",
                    rollbackDir);
        }
    }

    /// <summary>回滚：快照写回 live + 重启客户端。成功删快照；异常保留快照目录。</summary>
    private static async Task<SwitchOutcome> RollbackAsync(
        string rollbackDir, string exe, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            if (ClientControl.IsRunning())
                await ClientControl.KillAllAsync(ct);
            await VaultStore.RestoreFromAsync(rollbackDir, log, ct);
            ClientControl.Launch(exe);
            FileSystemUtil.TryDeleteDir(rollbackDir);
            return SwitchOutcome.RolledBack;
        }
        catch (Exception ex)
        {
            AppLog.Error("switch", "回滚失败: " + ex);
            log?.Report("回滚失败，快照保留于 " + rollbackDir);
            return SwitchOutcome.Failed;
        }
    }
}
