using CommunityToolkit.Mvvm.ComponentModel;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Switch;

namespace TraeTool.ViewModels;

/// <summary>
/// 主窗口 VM：持有两个页面 VM，扇出 AccountsChanged 事件（不引入消息总线）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public AccountsPageViewModel Accounts { get; }
    public SettingsPageViewModel Settings { get; }
    public CheckinPageViewModel Checkin { get; }
    public SwitchPageViewModel Switch { get; }
    public DashboardViewModel Dashboard { get; }
    public UsagePageViewModel Usage { get; }

    [ObservableProperty]
    private int _selectedPageIndex;

    [ObservableProperty]
    private string _titleSubtitle = "多账号签到 · 客户端切换";

    /// <summary>标题栏忙碌药丸文案；空 = 隐藏。</summary>
    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty] private bool _isBatchRunning;
    [ObservableProperty] private bool _isSwitchRunning;

    partial void OnIsBatchRunningChanged(bool value) => RefreshBusy();
    partial void OnIsSwitchRunningChanged(bool value) => RefreshBusy();

    private void RefreshBusy()
    {
        BusyText = IsBatchRunning ? "批量签到执行中"
                 : IsSwitchRunning ? "客户端切换执行中"
                 : "";
    }

    private void OnBusyChanged()
    {
        // 标量属性跨线程更新由绑定引擎自动封送
        IsBatchRunning = AppServices.BatchBusy;
        IsSwitchRunning = AppServices.SwitchBusy;
    }

    public AutoCheckinService AutoCheckin { get; }

    /// <summary>档案定时保鲜（客户端关闭时给唯一匹配账号刷新档案）。</summary>
    public VaultRefreshService VaultRefresh { get; }

    public MainViewModel()
    {
        Accounts = new AccountsPageViewModel();
        Settings = new SettingsPageViewModel(Accounts); // 借用账号行集合展示 Token
        Checkin = new CheckinPageViewModel();
        Switch = new SwitchPageViewModel();
        Dashboard = new DashboardViewModel(Checkin); // 依赖 Checkin（快捷签到复用其批次命令）
        Usage = new UsagePageViewModel();

        AppServices.AccountsChanged += OnAccountsChanged;
        AppServices.BusyChanged += OnBusyChanged;
        Checkin.BatchCompleted += OnBatchCompleted;

        VaultRefresh = new VaultRefreshService();
        AutoCheckin = new AutoCheckinService(
            () => AppServices.Config,
            () => AppServices.BatchBusy,
            async () =>
            {
                // 自动签到复用手动批次逻辑（CheckinPageViewModel.CheckinAsync 是私有 command，
                // 这里直接调服务层，完成后刷新两页）
                List<Account> enabled;
                lock (ConfigStore.SyncRoot)
                    enabled = AppServices.Config.Accounts.Where(a => a.Enabled).ToList();
                if (enabled.Count == 0) return;
                if (!AppServices.TryEnterBatch()) return; // 与手动批次互斥（原子占用）
                try
                {
                    var results = await AppServices.Checkin.BatchCheckinAsync(
                        enabled, AppServices.Config.CheckinIntervalSeconds);
                    // 全部"未登录"= 本轮零网络请求：撤回"今日已触发"（配置 + 进程内守卫），
                    // 登录后下一轮 tick 自动补签
                    if (results.Count > 0 && results.All(r => r.Reason == "未登录"))
                    {
                        AppServices.Config.LastAutoRunDate = null;
                        AppServices.SaveConfig();
                        // 本回调由 AutoCheckin 实例触发，属性在构造末尾已完成赋值
                        AutoCheckin!.RollbackTodayGuard();
                        AppLog.Info("auto", "全部账号未登录，撤回今日自动触发（登录后将自动补签）");
                        Checkin.StatusMessage = "自动签到待执行：账号登录后将自动补签（本轮零网络请求）";
                    }
                    else
                    {
                        double total = results.Sum(r => r.Gained);
                        // 汇总区分「新签 / 已签过」，避免全跳过时显示成"成功 N/N +0"造成重复签到错觉
                        var summary = "自动签到完成 · " +
                            CheckinPageViewModel.BuildBatchSummary(results, total);
                        AppLog.Info("auto", summary);
                        Checkin.StatusMessage = summary;
                        OnAccountsChanged();
                        if (total > 0) OnBatchCompleted(summary);
                    }
                }
                finally
                {
                    AppServices.BatchBusy = false;
                }
            });
        AutoCheckin.StatusChanged += msg => Checkin.StatusMessage = msg;
    }

    public void StartAutoCheckin()
    {
        if (AppServices.Config.AutoCheckinEnabled)
            AutoCheckin.Start();
        VaultRefresh.Start();
        BackfillStudentAsync(); // 启动后台回填学生认证（无需手点「刷新状态」）
    }

    /// <summary>
    /// 后台逐账号回填学生认证状态（先静默续期再查接口）；有变化才落盘+广播。
    /// 单个账号失败不影响其他；结果全部写日志（设置页日志卡可查）。
    /// </summary>
    public void BackfillStudentAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                bool changed = false;
                List<Account> snapshot;
                lock (ConfigStore.SyncRoot) snapshot = AppServices.Config.Accounts.ToList();
                foreach (var acc in snapshot)
                {
                    if (string.IsNullOrEmpty(acc.Token) && string.IsNullOrEmpty(acc.Session)) continue;
                    try
                    {
                        if (!await TokenRenewer.EnsureValidTokenAsync(acc)) continue;
                        var sv = await TraeApiClient.GetStudentVerifiedAsync(acc.Token!, acc.Session);
                        if (sv.HasValue && acc.StudentVerified != sv.Value)
                        {
                            acc.StudentVerified = sv.Value;
                            changed = true;
                            AppLog.Info("api", $"学生认证回填：{acc.DisplayName(0)} = {sv.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("api", $"学生认证回填单账号异常: {ex.Message}");
                    }
                }
                if (changed)
                {
                    AppServices.SaveConfig();
                    AppServices.RaiseAccountsChanged();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("api", "学生认证回填异常: " + ex.Message);
            }
        });
    }

    /// <summary>设置页改了自动签到开关后由 MainWindow 调用。</summary>
    public void SyncAutoCheckinTimer()
    {
        if (AppServices.Config.AutoCheckinEnabled) AutoCheckin.Start();
        else AutoCheckin.Stop();
    }

    private void OnAccountsChanged()
    {
        Accounts.Rebuild();
        Checkin.OnAccountsChanged();
        Switch.OnAccountsChanged();
        Dashboard.OnAccountsChanged();
        Usage.OnAccountsChanged();
        RefreshSubtitle();
    }

    private void OnBatchCompleted(string summary)
    {
        MainWindowHost.ShowBalloon?.Invoke("签到完成", summary);
    }

    private bool _dashInitialLoaded;

    /// <summary>窗口首次激活时加载仪表盘（它默认显示，但 Refresh 需要被唤起一次）。</summary>
    public void EnsureDashboardLoaded()
    {
        if (_dashInitialLoaded) return;
        _dashInitialLoaded = true;
        Dashboard.OnNavEnter();
    }

    private void RefreshSubtitle()
    {
        var n = AppServices.Config.Accounts.Count;
        var active = AppServices.ActiveAccount;
        string activeName = "-";
        if (active != null)
        {
            int idx = AppServices.Config.Accounts.IndexOf(active) + 1;
            activeName = active.DisplayName(idx);
        }
        TitleSubtitle = n == 0 ? "还没有账号" : $"{n} 个账号 · 当前 {activeName}";
    }

    public void OnShown() => RefreshSubtitle();
}

/// <summary>主窗口反向依赖的小钩子（托盘气泡），由 MainWindow 静态注入。</summary>
public static class MainWindowHost
{
    public static Action<string, string>? ShowBalloon;
}
