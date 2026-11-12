using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Switch;
using TraeTool.Views;

namespace TraeTool.ViewModels;

/// <summary>
/// 「客户端切换」页：每账号 建档/切换/校验/删除 + 客户端路径检测 + 执行日志。
/// 冷切换会关闭并重启 Trae 客户端；失败回滚快照在 %APPDATA%\TraeTool\switch\rollback_*。
/// </summary>
public partial class SwitchPageViewModel : ObservableObject
{
    public ObservableCollection<SwitchAccountRowViewModel> Rows { get; } = new();

    /// <summary>执行日志（步骤流水，UI 上滚动展示）。</summary>
    public ObservableCollection<string> Steps { get; } = new();

    private const int MaxSteps = 200;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _clientStatusText = "尚未检测客户端";

    /// <summary>客户端状态点：none=未检测 / idle=已检测未运行 / running=运行中。</summary>
    [ObservableProperty]
    private string _clientState = "none";

    /// <summary>exe 手动指定路径；改动即落盘（空 = 自动探测）。</summary>
    [ObservableProperty]
    private string _clientExePath = "";

    [ObservableProperty]
    private bool _busy;

    partial void OnBusyChanged(bool value) => AppServices.SwitchBusy = value;

    private readonly Switcher _switcher = new(new VaultStore());

    public SwitchPageViewModel()
    {
        _clientExePath = AppServices.Config.ClientExePath ?? "";
        Rebuild();                 // 启动即填充行（不依赖首次进入页面）
        _ = DetectClientAsync();   // 启动即探测客户端（纯读操作）
    }

    partial void OnClientExePathChanged(string value)
    {
        var v = value?.Trim();
        AppServices.Config.ClientExePath = string.IsNullOrEmpty(v) ? null : v;
        AppServices.SaveConfig();
    }

    /// <summary>账号集合变化后重建行（主窗口 AccountsChanged 扇入）。</summary>
    public void OnAccountsChanged() => Rebuild();

    /// <summary>切到本页时刷新：重建行 + 探测客户端 + 身份备注。</summary>
    public void OnNavEnter()
    {
        Rebuild();
        _ = DetectClientAsync();
    }

    public void Rebuild()
    {
        void Apply()
        {
            var accounts = AppServices.Config.Accounts;
            Rows.Clear();
            for (int i = 0; i < accounts.Count; i++)
            {
                var row = new SwitchAccountRowViewModel(accounts[i], i);
                var meta = VaultStore.TryReadMeta(accounts[i].Id);
                row.ApplyVaultInfo(meta != null, meta?.Entries.Count ?? 0, meta?.CreatedUtc);
                Rows.Add(row);
            }
            RefreshIdentityNotes();
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    /// <summary>
    /// 身份备注（相似度版）：usertag 每次登录会话都轮换，等值比较必误报——
    /// 改为对每个档案与 live 做「非噪声文件相似度」评分（后台线程，避免卡 UI），
    /// ≥75% 才亮「客户端当前」徽标。未建档行只报登录态有无。
    /// </summary>
    private void RefreshIdentityNotes()
    {
        bool liveLoggedIn = AuthProbe.TryGetIdentity(CarrierDefs.DataRoot) != null;

        // 未建档行：秒出
        foreach (var row in Rows.Where(r => r.ArchiveState != "已备份"))
        {
            row.IsClientCurrent = false;
            row.IdentityNote = liveLoggedIn ? "客户端存有登录态（此账号未建档）" : "客户端无登录态";
        }

        var vaultRows = Rows.Where(r => r.ArchiveState == "已备份").Select(r => (Row: r, r.Id)).ToList();
        if (vaultRows.Count == 0) return;

        int seq = ++_noteSeq;
        _ = Task.Run(() =>
        {
            var store = new VaultStore();
            var results = new List<(SwitchAccountRowViewModel Row, double Score, int Drift)>();
            foreach (var (row, id) in vaultRows)
            {
                try
                {
                    var rep = store.ScoreAgainstLive(id);
                    results.Add((row, rep.Score, rep.Drift.Count));
                }
                catch { /* 行保持原状 */ }
            }

            void Apply()
            {
                if (seq != _noteSeq) return; // 已有更新一轮的刷新在跑，丢弃本轮

                if (!liveLoggedIn)
                {
                    foreach (var r in Rows)
                    {
                        r.IsClientCurrent = false;
                        r.IdentityNote = r.ArchiveState == "已备份" ? "客户端无登录态（相似度仅供参考）" : r.IdentityNote;
                    }
                    return;
                }

                // 裁决：① 相似度唯一命中（≥75% 只有一个）→ 信它，并回写记忆
                //       ② 有记忆且记忆行也吻合 → 信记忆（解决"两档案同会话都高分"的并列）
                //       ③ 无记忆/记忆行不吻合 → 无徽标，按情况提示
                var passed = results.Where(x => x.Score >= VaultStore.SimilarityPass).ToList();
                var remembered = AppServices.Config.ClientActiveAccountId;
                SwitchAccountRowViewModel? badge = null;
                string note = "";

                if (passed.Count == 1)
                {
                    badge = passed[0].Row;
                    if (remembered != badge.Id)
                    {
                        AppServices.Config.ClientActiveAccountId = badge.Id; // 相似度检测到手动换号 → 更新记忆
                        AppServices.SaveConfig();
                    }
                    note = $"与客户端登录态吻合（相似度 {passed[0].Score:P0}）";
                }
                else if (passed.Count > 1)
                {
                    var rem = passed.FirstOrDefault(x => x.Row.Id == remembered);
                    if (rem.Row != null)
                    {
                        badge = rem.Row;
                        note = $"与客户端登录态吻合（相似度 {rem.Score:P0}，按记住的账号显示）";
                    }
                    else
                    {
                        note = "多个档案同时吻合（档案内容重复——请对非当前账号重新建档以区分）";
                    }
                }
                else
                {
                    note = "客户端登录态与所有档案都不吻合（可能手动登录了未建档账号）";
                }

                foreach (var (row, score, _) in results)
                {
                    bool match = badge != null && row.Id == badge.Id;
                    row.IsClientCurrent = match;
                    if (match)
                        row.IdentityNote = note;
                    else if (score >= VaultStore.SimilarityPass)
                        row.IdentityNote = $"相似度 {score:P0}（与多档案并列，无法据此判定）";
                    else
                        row.IdentityNote = $"与此档案不吻合（相似度 {score:P0}）";
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) Apply();
            else dispatcher.Invoke(Apply);
        });
    }

    /// <summary>身份刷新轮次（防旧结果覆盖新结果）。</summary>
    private int _noteSeq;

    // ==================== 命令 ====================

    /// <summary>探测客户端 exe（override → 运行进程 → 候选清单）。</summary>
    [RelayCommand]
    private Task DetectClientAsync()
    {
        var exe = ClientControl.ResolveExe(AppServices.Config.ClientExePath);
        bool running = ClientControl.IsRunning();
        ClientState = exe == null ? "none" : running ? "running" : "idle";
        ClientStatusText = exe != null
            ? $"已检测到：{exe}（{(running ? "运行中" : "未运行")}）"
            : "未检测到 Trae 客户端——请手动指定 exe 路径";
        return Task.CompletedTask;
    }

    /// <summary>清空执行日志（日志卡「清空」按钮）。</summary>
    [RelayCommand]
    private void ClearLog() => Steps.Clear();

    /// <summary>建档：走共享向导 ArchiveFlow（引导客户端登录 → 关客户端 → 交叉比对防错档 → 备份）。</summary>
    [RelayCommand]
    private async Task ArchiveAsync(SwitchAccountRowViewModel? row)
    {
        if (row == null || Busy || AppServices.SwitchBusy) return;
        Busy = true;
        try
        {
            var meta = await ArchiveFlow.GuideAsync(row.Account, row.DisplayName, MakeProgress());
            StatusMessage = meta != null
                ? $"建档完成：{row.DisplayName}（{meta.Entries.Count} 个文件）"
                : "建档未完成（已取消或失败）";
            Rebuild();
        }
        catch (Exception ex)
        {
            // RelayCommand 的异常会被静默吞掉——必须落到日志与状态行，否则表现为"按钮点了没反应"
            AppLog.Error("switch", "建档命令异常: " + ex);
            StatusMessage = "建档失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>切换：冷切换客户端登录态到该账号；无档案时先走同一套建档向导再继续。</summary>
    [RelayCommand]
    private async Task SwitchToAsync(SwitchAccountRowViewModel? row)
    {
        if (row == null || Busy || AppServices.SwitchBusy || AppServices.BatchBusy) return;
        Busy = true;
        try
        {
            // 无档案 → 共享建档向导（两处引导之一），完成后自动继续切换
            if (!VaultStore.Exists(row.Id))
            {
                StatusMessage = $"「{row.DisplayName}」尚无客户端档案，先进入建档引导…";
                var meta = await ArchiveFlow.GuideAsync(row.Account, row.DisplayName, MakeProgress());
                if (meta == null || !VaultStore.Exists(row.Id))
                {
                    StatusMessage = "建档未完成，切换已取消";
                    return;
                }
            }

            var confirm = DialogWindow.Show(
                "切换客户端登录态",
                $"切换到「{row.DisplayName}」？\n\n" +
                "· Trae 客户端将被关闭并在切换后自动重启\n" +
                "· 若切换失败会自动回滚到当前账号\n" +
                "· 成功后本程序的「当前账号」也会同步切换",
                DialogKind.Warn,
                small: "切换过程中请勿关闭本程序——守护步骤会校验客户端登录态。",
                primary: "确认切换", secondary: "取消");
            if (!confirm)
            {
                StatusMessage = "已取消切换";
                return;
            }

            StatusMessage = $"正在切换到「{row.DisplayName}」…";
            var result = await _switcher.SwitchAsync(row.Id, MakeProgress(), CancellationToken.None);
            switch (result.Outcome)
            {
                case SwitchOutcome.Active:
                    StatusMessage = $"已切换到「{row.DisplayName}」，客户端已重启并进入该账号";
                    AppServices.Config.ClientActiveAccountId = row.Id; // 记住客户端当前账号（徽标裁决依据）
                    AppServices.SetActive(row.Id); // 联动签到页/徽标/副标题（内部会 SaveConfig）
                    MemberDetector.TryRefreshAndSave(row.Account, isActive: true); // live 就是该账号：判会员
                    break;
                case SwitchOutcome.RolledBack:
                    StatusMessage = "切换未成功，已自动回滚到切换前账号并重启客户端";
                    break;
                case SwitchOutcome.NeedsConfirm:
                    StatusMessage = "无法自动判定切换结果，请人工确认。" + result.Detail +
                                    (result.RollbackDir != null ? $"（回滚快照：{result.RollbackDir}）" : "");
                    break;
                case SwitchOutcome.Aborted:
                    StatusMessage = "已中止：" + result.Detail;
                    break;
                default: // Failed
                    StatusMessage = "切换失败：" + result.Detail +
                                    (result.RollbackDir != null ? $"（快照保留：{result.RollbackDir}）" : "");
                    break;
            }
            Rebuild();
        }
        catch (Exception ex)
        {
            AppLog.Error("switch", "切换异常: " + ex);
            StatusMessage = "切换异常：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>校验档案与客户端当前登录态的一致性。</summary>
    [RelayCommand]
    private async Task VerifyAsync(SwitchAccountRowViewModel? row)
    {
        if (row == null || Busy || AppServices.SwitchBusy) return;
        if (!VaultStore.Exists(row.Id))
        {
            StatusMessage = "该账号尚无档案";
            return;
        }
        Busy = true;
        try
        {
            var report = await new VaultStore().VerifyAsync(row.Id, MakeProgress(), CancellationToken.None);
            // 判定基于「完整性 + 相似度阈值」——usertag 每次登录都轮换，绝不做等值身份比较
            StatusMessage =
                report.IntegrityBad > 0
                    ? $"排障：档案损坏（{report.IntegrityBad} 个文件与清单不符），建议对该账号重新建档"
                    : !report.IdentityKnown
                        ? $"客户端无登录态——仅完成完整性检查（完好）；相似度 {report.Score:P0} 仅供参考"
                        : report.Score >= VaultStore.SimilarityPass
                            ? $"校验通过：客户端登录态与该档案吻合（相似度 {report.Score:P0}，{report.Matched}/{report.Compared} 个稳定文件）"
                            : $"排障：不吻合，相似度仅 {report.Score:P0}（{report.Matched}/{report.Compared}，差异 {report.Drift.Count} 处见日志）——客户端不是此账号或登录态已更新，建议重新建档";
            Rebuild();
        }
        catch (Exception ex)
        {
            StatusMessage = "校验失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>删除档案（不影响签到历史，仅清 vault）。</summary>
    [RelayCommand]
    private void Delete(SwitchAccountRowViewModel? row)
    {
        if (row == null || Busy) return;
        try
        {
            var confirm = DialogWindow.Show(
                "删除客户端档案",
                $"删除「{row.DisplayName}」的客户端登录档案？\n仅删除本程序的备份，不影响该账号签到数据。",
                DialogKind.Danger,
                small: "删除后需重新建档才能把客户端切换到该账号。",
                primary: "删除", secondary: "取消", dangerPrimary: true);
            if (!confirm) return;

            var dir = VaultStore.VaultPath(row.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            StatusMessage = $"已删除「{row.DisplayName}」的档案";
            AppLog.Info("switch", $"删除档案 {row.DisplayName}");
            Rebuild();
        }
        catch (Exception ex)
        {
            AppLog.Error("switch", "删除档案异常: " + ex);
            StatusMessage = "删除失败：" + ex.Message;
        }
    }

    // ==================== 内部 ====================

    /// <summary>进度回调：追加到步骤日志（限长，UI 线程）。</summary>
    private IProgress<string> MakeProgress() => new Progress<string>(AppendStep);

    private void AppendStep(string msg)
    {
        void Apply()
        {
            Steps.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (Steps.Count > MaxSteps) Steps.RemoveAt(0);
        }
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }
}
