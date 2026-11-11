using System.Windows;
using TraeTool.Models;
using TraeTool.ViewModels;
using TraeTool.Views;
using Application = System.Windows.Application; // 与 WinForms Application 冲突，需限定

namespace TraeTool.Services.Switch;

/// <summary>
/// 建档向导——「添加账号后」与「首次切换无档案」两处共用同一流程（五步向导窗）。
/// 硬边界：网页登录拿到的 JWT/Session 无法转成客户端登录态（客户端认证是自己的加密 blob），
/// 所以「在 Trae 客户端里登录该账号一次」是每个账号不可省的物理步骤；本向导把它包成
/// 五步引导（确认 → 打开客户端 → 关闭客户端 → 交叉比对 → 备份建档）。
/// </summary>
public static class ArchiveFlow
{
    /// <summary>
    /// 引导完成建档。返回 null = 用户取消或失败；否则返回新档案 meta。
    /// progress 可为 null（账号页发起时不进切换页日志面板，只走 AppLog/状态栏）。
    /// </summary>
    public static Task<VaultMeta?> GuideAsync(
        Account account, string displayName, IProgress<string>? progress = null)
    {
        VaultMeta? result = null;

        void Run()
        {
            try
            {
                ArchiveWizardWindow.Run(
                    Application.Current?.MainWindow,
                    $"为账号「{displayName}」备份一次 Trae 客户端登录态",
                    async c => result = await DriveAsync(c, account, displayName, progress));
            }
            catch (Exception ex)
            {
                AppLog.Error("switch", "建档向导启动失败: " + ex);
                throw;
            }
        }

        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) Run();
        else app.Dispatcher.Invoke(Run);
        return Task.FromResult(result);
    }

    private static async Task<VaultMeta?> DriveAsync(
        WizardController c, Account account, string displayName, IProgress<string>? progress)
    {
        try
        {
            AppLog.Info("wizard", $"建档流程开始：{displayName}");
            // ── ① 确认：交代硬边界与影响 ──
            AppLog.Info("wizard", "step0 确认");
            c.Step(0);
            c.SetBody(
                "启用「客户端切换」需要在客户端里登录一次",
                $"网页登录的凭证无法转成客户端登录态，此步骤每个账号（{displayName}）仅需一次。",
                [
                    "须先在 Trae 客户端手动登录该账号一次",
                    "建档会关闭并重新打开客户端",
                    "建档时自动与已有档案交叉比对，防止建错档",
                ]);
            c.SetStatus(null);
            c.SetPrimary("开始建档");
            if (!await c.WaitPrimaryAsync())
            {
                progress?.Report("已跳过建档");
                return null;
            }

            // ── ② 打开客户端并等待用户登录 ──
            AppLog.Info("wizard", "step1 打开客户端");
            c.Step(1);
            if (!ClientControl.IsRunning())
            {
                var exe = ClientControl.ResolveExe(AppServices.Config.ClientExePath);
                if (exe == null)
                {
                    c.SetBody("找不到客户端可执行文件",
                        "请先到「客户端切换」页手动指定 Trae CN.exe 路径，然后重试建档。",
                        null, "无法打开客户端——路径留空时按默认安装位置自动探测。");
                    c.SetStatus(null);
                    c.SetPrimary("关闭");
                    await c.WaitPrimaryAsync();
                    return null;
                }
                ClientControl.Launch(exe);
                c.SetProgressLine("已打开客户端");
                progress?.Report("已打开客户端，请在其中登录 " + displayName);
            }

            c.SetBody($"请在 Trae 客户端中登录「{displayName}」",
                "登录完成后点击「我已登录」继续——接下来会自动关闭客户端并备份登录态。");
            c.SetStatus("等待你在客户端完成登录…", running: true);
            c.SetPrimary("我已登录");
            if (!await c.WaitPrimaryAsync())
            {
                progress?.Report("已取消建档");
                return null;
            }

            // ── ③ 关闭客户端（释放文件占用） ──
            AppLog.Info("wizard", "step2 关闭客户端");
            c.Step(2);
            c.SetBody("关闭客户端", "建档需要登录态文件未被占用，正在关闭…");
            c.SetStatus("正在关闭客户端…", running: true);
            c.SetPrimary("");
            progress?.Report("关闭客户端（建档需要文件未被占用）…");
            c.SetProgressLine("关闭客户端（WM_CLOSE → 必要时强杀）");
            if (!await ClientControl.KillAllAsync())
            {
                c.SetBody("客户端无法关闭", "建档中止。请手动关闭 Trae 客户端后重试。", null, null);
                c.SetStatus("已中止");
                c.SetPrimary("关闭");
                await c.WaitPrimaryAsync();
                return null;
            }
            c.SetProgressLine("客户端已关闭");

            // ── ④ 交叉比对防错档：与「其他账号」的已有档案过分相似 → 警告并须勾选确认 ──
            AppLog.Info("wizard", "step3 交叉比对");
            c.Step(3);
            c.SetBody("交叉比对", "比对登录态文件指纹与已有档案，避免重复建档或串号。");
            c.SetStatus("正在比对…", running: true);
            c.SetPrimary("");

            var store = new VaultStore();
            var accounts = AppServices.Config.Accounts;
            double worstScore = 0;
            string? worstOther = null;
            for (int i = 0; i < accounts.Count; i++)
            {
                var other = accounts[i];
                if (other.Id == account.Id || !VaultStore.Exists(other.Id)) continue;
                var otherName = other.DisplayName(i + 1);
                c.SetStatus($"与账号「{otherName}」的档案交叉比对…", running: true);
                progress?.Report($"与账号「{otherName}」的档案交叉比对…");
                var score = store.ScoreAgainstLive(other.Id, progress).Score;
                c.SetProgressLine($"vs {otherName}：相似度 {score:P0}");
                if (score >= VaultStore.CrossWarnSimilarity && score > worstScore)
                {
                    worstScore = score;
                    worstOther = otherName;
                }
            }

            if (c.IsCancelRequested)
            {
                progress?.Report("已取消建档");
                return null;
            }

            if (worstOther != null)
            {
                // ≥0.85 警告：必须勾选确认才能继续
                c.SetBody("可能建错了档",
                    $"即将保存的客户端登录态与账号「{worstOther}」的已有档案相似度 {worstScore:P0}——" +
                    $"客户端里登录的很可能不是「{displayName}」，继续会存错档案！",
                    warn: $"请回到客户端确认登录的确实是「{displayName}」后再勾选继续。",
                    requireCheck: true,
                    checkText: $"我已确认客户端登录的是「{displayName}」，仍然建档");
                c.SetStatus("等待确认");
                // 放行位必须给 true：requireCheck 场景由「勾选」做闸门（SetPrimary(enabled)
                // 传 false 会把业务放行位钉死，勾选只做 AND → 按钮永远灰，用户点了没反应）
                c.SetPrimary("确认建档");
                if (!await c.WaitPrimaryAsync())
                {
                    progress?.Report("已取消建档（与" + worstOther + "档案过分相似）");
                    AppLog.Info("switch", $"建档交叉比对拦截：与 {worstOther} 相似度 {worstScore:P0}，用户取消");
                    return null;
                }
                AppLog.Info("switch", $"建档交叉比对警告：与 {worstOther} 相似度 {worstScore:P0}，用户确认仍保存");
                c.SetProgressLine($"警告已确认（vs {worstOther} {worstScore:P0}）");
            }
            else
            {
                c.SetProgressLine("比对通过：与现有档案无重复");
            }

            // ── ⑤ 备份建档 ──
            AppLog.Info("wizard", "step4 备份建档");
            c.Step(4);
            c.SetBody("备份建档", $"正在把「{displayName}」的客户端登录态保存为档案…");
            c.SetStatus("正在备份…", running: true);
            c.SetPrimary("");
            progress?.Report($"开始建档：{displayName}…");

            var meta = await store.BackupAsync(account.Id, displayName, progress);
            // 建档成功 = 客户端此刻就是此账号 → 记住（行徽标在相似度无法判定时以此裁决）
            AppServices.Config.ClientActiveAccountId = account.Id;
            AppServices.SaveConfig();
            MemberDetector.TryRefreshAndSave(account, isActive: true); // 刚备份的 live 就是此账号

            c.SetBody("建档完成",
                $"已保存 {meta.Entries.Count} 个文件。现在可以在「客户端切换」页把客户端切换到此账号。");
            c.SetProgressLine($"建档完成（{meta.Entries.Count} 个文件）");
            c.SetStatus("完成", running: false);
            c.SetPrimary("完成");
            progress?.Report($"建档完成（{meta.Entries.Count} 个文件）");
            await c.WaitPrimaryAsync();
            return meta;
        }
        catch (Exception ex)
        {
            AppLog.Error("switch", "建档向导异常: " + ex.Message);
            c.SetBody("建档失败", ex.Message, null,
                "已中止，不会留下半成品档案；可稍后重试。", false);
            c.SetStatus(null);
            c.SetPrimary("关闭");
            await c.WaitPrimaryAsync();
            return null;
        }
    }
}
