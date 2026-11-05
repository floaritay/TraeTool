using System.Windows.Threading;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 每日自动签到：30s DispatcherTimer 检查一次。
/// 语义：开启 && 当前时刻 ≥ 设定时间 && 今日尚未触发 → 执行。
/// 触发日期**先置位再执行**（防重入）；进程重启后到点仍会补跑（置位只在内存，
/// 但每账号 LastCheckinDate/服务器状态会挡住重复签到）。
/// </summary>
public sealed class AutoCheckinService(Func<AppSettings> getConfig, Func<bool> isBusy, Func<Task> runBatch)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateOnly _ranDate = DateOnly.MinValue;
    private bool _running;
    private bool _wired;

    public event Action<string>? StatusChanged;

    // 主构造函数参数（getConfig/isBusy/runBatch）由编译器捕获为字段；
    // Tick 处理器不能在字段初始化器里引用实例方法，故在首次 Start 时挂接（幂等）。
    public void Start()
    {
        if (!_wired)
        {
            _wired = true;
            _timer.Tick += async (_, _) => await TickAsync();
        }
        _timer.Start();
    }
    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_running) return;
        try
        {
            var cfg = getConfig();
            if (!cfg.AutoCheckinEnabled) return;
            if (!TimeSpan.TryParse(cfg.AutoCheckinTime, out var due)) return;

            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            if (now.TimeOfDay < due) return;                 // 未到点
            // 今日已触发（双保险：进程内 _ranDate + 持久化 LastAutoRunDate——
            // 上一版只记内存，每次重启都会重跑一轮并弹"签到完成"气泡）
            if (_ranDate == DateOnly.FromDateTime(now)) return;
            if (cfg.LastAutoRunDate == today) return;
            if (isBusy()) return;                            // 手动批次进行中，下轮再看

            // 全部启用账号今日已签 → 本轮完全不发起（不发请求、不弹任何提示）
            var enabled = cfg.Accounts.Where(a => a.Enabled).ToList();
            if (enabled.Count > 0 && enabled.All(a => a.LastCheckinDate == DateOnly.FromDateTime(now)))
            {
                _ranDate = DateOnly.FromDateTime(now);
                cfg.LastAutoRunDate = today;
                ViewModels.AppServices.SaveConfig();
                AppLog.Info("auto", "全部账号今日已签，跳过本轮自动签到");
                return;
            }

            _ranDate = DateOnly.FromDateTime(now);           // 先置位防重入（进程内）
            cfg.LastAutoRunDate = today;                     // 持久化：重启也不会再触发
            ViewModels.AppServices.SaveConfig();
            var late = (now.TimeOfDay - due).TotalSeconds > 120;
            var msg = late
                ? $"已超过设定时间 {cfg.AutoCheckinTime}，执行补签…"
                : $"到达自动签到时间 {cfg.AutoCheckinTime}，开始签到…";
            AppLog.Info("auto", msg);
            StatusChanged?.Invoke(msg);

            _running = true;
            try
            {
                await runBatch();
            }
            finally
            {
                _running = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("auto", "自动签到异常: " + ex.Message);
        }
    }

    /// <summary>供设置页保存新时间后复位（如改时间想立即重新评估）。</summary>
    public void ResetTodayGuard() => _ranDate = DateOnly.MinValue;

    /// <summary>
    /// 撤回今日进程内触发守卫（与配置层 LastAutoRunDate 撤回配套）——
    /// 全部账号"未登录"空跑后调用，否则同一次会话里"登录后自动补签"永不兑现。
    /// </summary>
    public void RollbackTodayGuard() => _ranDate = DateOnly.MinValue;
}
