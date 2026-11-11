using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Gateway;

namespace TraeTool.ViewModels;

/// <summary>
/// 手工装配的服务定位（不引入 DI 容器）。
/// 账号集合的任何变更（增/删/登录/切换/保存）都必须 <see cref="RaiseAccountsChanged"/>，
/// 两个页面 VM 都订阅它来刷新各自的展示。
/// </summary>
public static class AppServices
{
    public static AppSettings Config { get; private set; } = new();

    public static CheckinService Checkin { get; private set; } = null!;

    /// <summary>本机管理网关（127.0.0.1）；开关见 <see cref="Config.GatewayEnabled"/>。</summary>
    public static LocalGateway Gateway { get; } = new();

    private static int _batchBusy;
    private static int _switchBusy;

    /// <summary>批次签到是否进行中（自动签到与手动按钮共用一把锁）。</summary>
    public static bool BatchBusy
    {
        get => Volatile.Read(ref _batchBusy) != 0;
        set => SetLock(ref _batchBusy, value);
    }

    /// <summary>切换页操作（建档/切换/排障）或档案保鲜进行中——与 BatchBusy 互为独立锁。</summary>
    public static bool SwitchBusy
    {
        get => Volatile.Read(ref _switchBusy) != 0;
        set => SetLock(ref _switchBusy, value);
    }

    private static void SetLock(ref int slot, bool value)
    {
        var v = value ? 1 : 0;
        if (Interlocked.Exchange(ref slot, v) == v) return; // 状态未变不广播
        BusyChanged?.Invoke();
    }

    /// <summary>
    /// 原子占用批次锁（false = 已有批次在执行）。
    /// 「先查后置」的非原子写法在两个入口（手动点击 / 自动触发）同时到达时会双开批次，
    /// 入口一律走本方法。
    /// </summary>
    public static bool TryEnterBatch()
    {
        if (Interlocked.CompareExchange(ref _batchBusy, 1, 0) != 0) return false;
        BusyChanged?.Invoke();
        return true;
    }

    /// <summary>原子占用切换锁；语义同 <see cref="TryEnterBatch"/>。</summary>
    public static bool TryEnterSwitch()
    {
        if (Interlocked.CompareExchange(ref _switchBusy, 1, 0) != 0) return false;
        BusyChanged?.Invoke();
        return true;
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
        ApplyProxy();  // 共享 HTTP 实例按配置的代理构建
        SyncGateway(); // 本机管理网关按配置启停
        AppLog.Info("app", $"启动，账号数={Config.Accounts.Count}，激活={Config.ActiveAccountId ?? "-"}");
    }

    public static void SaveConfig() => ConfigStore.Save(Config);

    /// <summary>把配置中的出站代理施加到全部共享 HTTP 实例（设置变更后调用）。</summary>
    public static void ApplyProxy() => HttpClients.Configure(Config);

    /// <summary>按配置启停本机管理网关（幂等）；令牌由网关生成后在此落盘。</summary>
    public static void SyncGateway()
    {
        if (!Config.GatewayEnabled)
        {
            Gateway.Stop();
            return;
        }
        var tokenMissing = string.IsNullOrEmpty(Config.GatewayToken);
        Gateway.Start(Config);
        if (tokenMissing) SaveConfig();
    }

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
