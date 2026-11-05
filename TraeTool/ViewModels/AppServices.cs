using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>
/// 手工装配的服务定位（一期不引入 DI 容器）。
/// 账号集合的任何变更（增/删/登录/切换/保存）都必须 <see cref="RaiseAccountsChanged"/>，
/// 两个页面 VM 都订阅它来刷新各自的展示。
/// </summary>
public static class AppServices
{
    public static AppSettings Config { get; private set; } = new();

    public static CheckinService Checkin { get; private set; } = null!;

    private static bool _batchBusy;
    private static bool _switchBusy;

    /// <summary>批次签到是否进行中（自动签到与手动按钮共用一把锁）。</summary>
    public static bool BatchBusy
    {
        get => _batchBusy;
        set { if (_batchBusy == value) return; _batchBusy = value; BusyChanged?.Invoke(); }
    }

    /// <summary>切换页操作（建档/切换/排障）或档案保鲜进行中——与 BatchBusy 互为独立锁。</summary>
    public static bool SwitchBusy
    {
        get => _switchBusy;
        set { if (_switchBusy == value) return; _switchBusy = value; BusyChanged?.Invoke(); }
    }

    /// <summary>任一忙碌锁状态变化（标题栏药丸/导航运行点用）。任意线程可触发。</summary>
    public static event Action? BusyChanged;

    /// <summary>强制退出标志：置位后 OnClosing 不再拦截到托盘（更新安装/托盘退出用）。</summary>
    public static bool ForceExit { get; set; }

    /// <summary>账号集合或激活账号发生变化。回调可能来自任意线程，订阅方自行切 UI 线程。</summary>
    public static event Action? AccountsChanged;

    public static void Init()
    {
        Paths.EnsureCreated();
        BackupService.ApplyImportStaging(); // 导入备份落位必须在打开库/读配置之前
        Database.Open();
        Config = ConfigStore.Load();
        Checkin = new CheckinService(() => Config, SaveConfig);
        SaveConfig(); // 首启立即落盘 config.json（便于确认数据位置；其后由各变更点保存）
        AppLog.Info("app", $"启动，账号数={Config.Accounts.Count}，激活={Config.ActiveAccountId ?? "-"}");
    }

    public static void SaveConfig() => ConfigStore.Save(Config);

    public static void RaiseAccountsChanged() => AccountsChanged?.Invoke();

    /// <summary>当前激活账号（无激活/失效回退第一个）。</summary>
    public static Account? ActiveAccount =>
        Config.Accounts.FirstOrDefault(a => a.Id == Config.ActiveAccountId)
        ?? Config.Accounts.FirstOrDefault();

    public static void SetActive(string accountId)
    {
        if (Config.Accounts.All(a => a.Id != accountId)) return;
        Config.ActiveAccountId = accountId;
        SaveConfig();
        RaiseAccountsChanged();
    }
}
