using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>签到页：一键批量签到、自动签到设置、当前激活账号积分、记录列表。</summary>
public partial class CheckinPageViewModel : ObservableObject
{
    public ObservableCollection<CheckinRecordRow> Records { get; } = new();

    /// <summary>本轮执行明细（状态卡日志盒，逐行追加）。</summary>
    public ObservableCollection<string> RunLog { get; } = new();

    private const int MaxRunLog = 80;

    private void AppendRun(string msg)
    {
        void Apply()
        {
            RunLog.Add($"{DateTime.Now:HH:mm:ss} {msg}");
            while (RunLog.Count > MaxRunLog) RunLog.RemoveAt(0);
        }
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    /// <summary>下拉选项（record 合成属性可被 WPF 绑定；ValueTuple 是字段不可绑）。筛选与账号切换共用。</summary>
    public sealed record AccountChoice(string Id, string Name);

    /// <summary>记录筛选下拉：Id 为空项 = 全部账号。</summary>
    public ObservableCollection<AccountChoice> RecordFilters { get; } = new();

    /// <summary>头部「切换账号」下拉（不含「全部」项）。</summary>
    public ObservableCollection<AccountChoice> AccountChoices { get; } = new();

    [ObservableProperty]
    private string? _selectedFilterId;

    /// <summary>当前激活账号 Id（绑定头部切换下拉；改动即写配置并广播刷新）。</summary>
    [ObservableProperty]
    private string? _currentAccountId;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _activeAccountLabel = "无激活账号";

    [ObservableProperty]
    private string _activeAccountCredits = "—";

    /// <summary>激活账号今日是否已签（状态点颜色联动）。</summary>
    [ObservableProperty]
    private bool _isSignedToday;

    /// <summary>激活账号今日获得（头部提示行）。</summary>
    [ObservableProperty]
    private string _activeRewardText = "+0";

    /// <summary>激活账号连签天数（头部提示行）。</summary>
    [ObservableProperty]
    private string _activeStreakText = "0";

    /// <summary>页头圆片：N 个账号参与签到。</summary>
    [ObservableProperty]
    private string _enabledCountText = "";

    [ObservableProperty]
    private bool _autoCheckinEnabled;

    [ObservableProperty]
    private string _autoCheckinTime = "09:00";

    [ObservableProperty]
    private int _checkinIntervalSeconds = 5;

    /// <summary>一键签到按钮是否可用。</summary>
    public bool CanCheckin => !Busy;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanCheckin));

    public CheckinPageViewModel()
    {
        _autoCheckinEnabled = AppServices.Config.AutoCheckinEnabled;
        _autoCheckinTime = AppServices.Config.AutoCheckinTime;
        _checkinIntervalSeconds = AppServices.Config.CheckinIntervalSeconds;
        RefreshActiveAccount();
        ReloadRecords();
    }

    partial void OnAutoCheckinEnabledChanged(bool value)
    {
        AppServices.Config.AutoCheckinEnabled = value;
        AppServices.SaveConfig();
    }

    partial void OnAutoCheckinTimeChanged(string value)
    {
        if (!TimeSpan.TryParse(value, out _)) return; // 非法时间不落盘（失焦时 UI 校验提示）
        AppServices.Config.AutoCheckinTime = TimeSpan.Parse(value).ToString(@"hh\:mm");
        AppServices.SaveConfig();
    }

    partial void OnCheckinIntervalSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 60);
        AppServices.Config.CheckinIntervalSeconds = clamped;
        AppServices.SaveConfig();
    }

    partial void OnSelectedFilterIdChanged(string? value) => ReloadRecords();

    partial void OnCurrentAccountIdChanged(string? value)
    {
        // 防重入：清空下拉/回写同步时 value 会是 null 或已是激活 Id，直接忽略；
        // 仅当用户在下拉里选了另一个账号才触发切换（SetActive 内部会 Save + 广播 AccountsChanged，
        // 广播又会回来把 CurrentAccountId 校准回配置值，不会形成环）。
        if (string.IsNullOrEmpty(value) || value == AppServices.Config.ActiveAccountId) return;
        if (AppServices.Config.Accounts.All(a => a.Id != value)) return;
        AppServices.SetActive(value);
    }

    // ==================== 命令 ====================

    /// <summary>一键签到：只跑 Enabled 账号，返回汇总文案。</summary>
    [RelayCommand]
    private async Task CheckinAsync()
    {
        if (AppServices.BatchBusy) return;
        var enabled = AppServices.Config.Accounts.Where(a => a.Enabled).ToList();
        if (enabled.Count == 0)
        {
            StatusMessage = "没有启用的账号，请到「账号管理」页添加并勾选";
            return;
        }

        // 未登录账号不弹窗阻塞批次，在结果中标注
        AppServices.BatchBusy = true;
        Busy = true;
        StatusMessage = $"正在签到，共 {enabled.Count} 个启用账号…";
        RunLog.Clear();
        AppendRun($"开始批量签到（{enabled.Count} 个启用账号）");
        try
        {
            var progress = new Progress<string>(m =>
            {
                StatusMessage = m;
                AppendRun(m);
            });
            var results = await AppServices.Checkin.BatchCheckinAsync(
                enabled, AppServices.Config.CheckinIntervalSeconds, progress);

            double total = results.Sum(r => r.Gained);
            var summary = BuildBatchSummary(results, total);
            StatusMessage = summary;
            AppLog.Info("batch", summary);
            AppendRun(summary);
            foreach (var r in results)
            {
                // 结果行标签：把「已签过（跳过）」与「新签成功」明确分开，便于区分
                string tag = !r.Ok ? "失败"
                    : r.Reason == "今日已签" ? "已签过"
                    : r.Gained > 0 ? $"新签 +{r.Gained:0}"
                    : "完成";
                AppendRun($"{tag} {r.Name}{(!r.Ok && r.Reason is { Length: > 0 } reason ? $"（{reason}）" : "")}");
            }

            RefreshActiveAccount();
            ReloadRecords();
            AppServices.RaiseAccountsChanged();

            // 托盘气泡由主窗口订阅批次完成时展示（仅在成功时提示一次）
            if (total > 0) BatchCompleted?.Invoke(summary);
        }
        catch (Exception ex)
        {
            // RelayCommand 异常会被静默吞掉——必须落日志+状态行，否则批次看起来"卡住"
            AppLog.Error("batch", "批量签到异常: " + ex);
            StatusMessage = "签到执行异常：" + ex.Message;
            AppendRun("异常：" + ex.Message);
        }
        finally
        {
            AppServices.BatchBusy = false;
            Busy = false;
        }
    }

    /// <summary>批次完成后通知（主窗口转托盘气泡）。</summary>
    public event Action<string>? BatchCompleted;

    /// <summary>刷新激活账号展示 + 记录。</summary>
    public void RefreshActiveAccount()
    {
        void Apply()
        {
            var acc = AppServices.ActiveAccount;
            if (acc == null)
            {
                ActiveAccountLabel = "无激活账号（请先到账号管理页添加）";
                ActiveAccountCredits = "—";
                IsSignedToday = false;
                CurrentAccountId = null;
                return;
            }
            // 账号名由头部下拉框展示，这里只显示状态与 UID
            var signed = acc.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today);
            IsSignedToday = signed;
            ActiveAccountLabel = $"{(signed ? "今日已签 ✓" : "今日未签")} · UID {acc.AccountUid ?? "-"}";
            ActiveAccountCredits = acc.RemainingCredits >= 0 ? acc.RemainingCredits.ToString("0.#") : "—";
            ActiveRewardText = "+" + CheckinRecordRepo.GetTodayCreditsFor(acc.Id).ToString("0");
            ActiveStreakText = ComputeStreakFor(acc.Id).ToString();
            CurrentAccountId = acc.Id; // 校准下拉选中项（外部变更后回写）
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    /// <summary>激活账号连签天数（以全局已签日期集合计算，与仪表盘一致）。</summary>
    private static int ComputeStreakFor(string accountId)
    {
        var dates = CheckinRecordRepo.SignedDates(accountId);
        if (dates.Count == 0) return 0;
        int n = 0;
        var cur = DateTime.Today;
        if (!dates.Contains(cur.ToString("yyyy-MM-dd"))) cur = cur.AddDays(-1);
        while (dates.Contains(cur.ToString("yyyy-MM-dd")))
        {
            n++;
            cur = cur.AddDays(-1);
        }
        return n;
    }

    /// <summary>重载记录列表与筛选项。</summary>
    public void ReloadRecords()
    {
        void Apply()
        {
            // 「全部」固定为首项
            if (RecordFilters.Count == 0 || RecordFilters[0].Id != "")
                RecordFilters.Insert(0, new AccountChoice("", "全部账号"));

            // 筛选项与账号集合同步
            var wanted = AppServices.Config.Accounts
                .Select((a, i) => new AccountChoice(a.Id, a.DisplayName(i + 1)))
                .ToList();
            foreach (var item in wanted)
            {
                if (RecordFilters.All(f => f.Id != item.Id))
                    RecordFilters.Add(item);
            }
            for (int i = RecordFilters.Count - 1; i >= 0; i--)
            {
                if (RecordFilters[i].Id != "" && wanted.All(w => w.Id != RecordFilters[i].Id))
                    RecordFilters.RemoveAt(i);
            }
            if (SelectedFilterId != null && wanted.All(w => w.Id != SelectedFilterId))
                SelectedFilterId = null;

            // 头部切换下拉同步（不含「全部」项）：与账号列表保持一致
            AccountChoices.Clear();
            foreach (var item in wanted)
                AccountChoices.Add(item);
            int enabledCount = AppServices.Config.Accounts.Count(a => a.Enabled);
            EnabledCountText = AppServices.Config.Accounts.Count == 0
                ? "尚未添加账号"
                : $"{enabledCount} 个账号参与签到";
            // 清空重建期间 ComboBox 可能把选中值推成 null，最后统一校准回激活账号
            CurrentAccountId = AppServices.Config.ActiveAccountId;

            Records.Clear();
            foreach (var r in CheckinRecordRepo.Recent(100, SelectedFilterId))
                Records.Add(r);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    /// <summary>主窗口 AccountsChanged 转发进来时刷新全部。</summary>
    public void OnAccountsChanged()
    {
        RefreshActiveAccount();
        ReloadRecords();
    }

    /// <summary>
    /// 批次结果汇总（手动与自动共用）：新签 / 已签过 / 未登录 / 失败 分开计数。
    /// 「已签过」=服务器或本地判定今日已签的正常跳过——既不算失败也要能一眼区分。
    /// </summary>
    public static string BuildBatchSummary(IReadOnlyList<CheckinResult> results, double total)
    {
        int fresh = results.Count(r => r.Ok && r.Reason != "今日已签");
        int already = results.Count(r => r.Ok && r.Reason == "今日已签");
        int notLoggedIn = results.Count(r => !r.Ok && r.Reason == "未登录");
        var fails = results.Where(r => !r.Ok && r.Reason != "未登录").ToList();

        var parts = new List<string> { $"新签 {fresh}（+{total:0}）" };
        if (already > 0) parts.Add($"已签过 {already}");
        if (notLoggedIn > 0) parts.Add($"未登录 {notLoggedIn}");
        var summary = $"共 {results.Count} 个账号：" + string.Join(" · ", parts);
        if (fails.Count > 0)
            summary += "；失败：" + string.Join("、", fails.Select(f => $"{f.Name}({f.Reason})"));
        return summary;
    }
}
