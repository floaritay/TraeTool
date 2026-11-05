using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using Point = System.Windows.Point;
using PointCollection = System.Windows.Media.PointCollection;

namespace TraeTool.ViewModels;

/// <summary>日历单元格。</summary>
public sealed class CalendarCell
{
    public int Day { get; init; }
    /// <summary>blank / checked / today / empty。</summary>
    public string State { get; init; } = "blank";
    public string Tip { get; init; } = "";
}

/// <summary>仪表盘账号行（AvatarImage 异步回填需变更通知）。</summary>
public sealed class DashAccountRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public bool IsCurrent { get; init; }
    /// <summary>头像色相（AccountRowViewModel.HueOf 同源）。</summary>
    public double AvatarHue { get; init; }
    public string Initial { get; init; } = "?";

    private ImageSource? _avatarImage;
    /// <summary>头像图（null = 首字兜底）。</summary>
    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        set { _avatarImage = value; PropertyChanged?.Invoke(this, new(nameof(AvatarImage))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 仪表盘：当前账号积分/状态、今日总获、连签天数、当月签到日历（可翻月）、
/// 近 14 天剩余积分趋势折线（数据源 credits_snapshot）、账号切换列表与快捷签到。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly CheckinPageViewModel _checkin;

    public const double ChartWidth = 520;
    public const double ChartHeight = 200;

    [ObservableProperty] private string _dateText = DateTime.Today.ToString("yyyy-MM-dd dddd");
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private string _currentAccount = "未添加账号";
    [ObservableProperty] private string _remainingCredits = "—";
    [ObservableProperty] private string _todayStatus = "—";
    [ObservableProperty] private string _todayReward = "—";
    [ObservableProperty] private string _streakText = "连签 0 天";
    [ObservableProperty] private string _memberText = "";
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>中性信息横幅（快捷签到进度/汇总）——错误仍走 StatusMessage 红条。</summary>
    [ObservableProperty] private string _infoMessage = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _monthTitle = $"{DateTime.Now.Year} 年 {DateTime.Now.Month} 月";

    [ObservableProperty] private int _chartYMax;
    [ObservableProperty] private int _chartYMid;
    [ObservableProperty] private int _chartYMin;
    [ObservableProperty] private double _todayX;
    [ObservableProperty] private double _todayY;
    [ObservableProperty] private bool _hasTrend;

    private PointCollection _linePoints = new();
    public PointCollection LinePoints { get => _linePoints; private set => SetProperty(ref _linePoints, value); }

    private PointCollection _fillPoints = new();
    public PointCollection FillPoints { get => _fillPoints; private set => SetProperty(ref _fillPoints, value); }

    public ObservableCollection<CalendarCell> CalendarDays { get; } = new();
    public ObservableCollection<DashAccountRow> AccountRows { get; } = new();
    public ObservableCollection<string> AxisLabels { get; } = new();

    private static readonly string[] WeekHeaders = ["日", "一", "二", "三", "四", "五", "六"];
    public IReadOnlyList<string> WeekHeader => WeekHeaders;

    private int _viewYear = DateTime.Today.Year;
    private int _viewMonth = DateTime.Today.Month;
    private HashSet<string> _signedDates = new();

    public DashboardViewModel(CheckinPageViewModel checkin)
    {
        _checkin = checkin;
    }

    // ==================== 命令 ====================

    /// <summary>刷新：凭证校准 → status/剩余积分 → 会员判定 → 日历/连签/趋势/账号行。</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var cfg = AppServices.Config;
            var acc = AppServices.ActiveAccount;

            if (acc == null)
            {
                CurrentAccount = "未添加账号";
                RemainingCredits = "—";
                TodayStatus = "—";
                MemberText = "";
                RebuildCalendar();
                RebuildAccountRows();
                return;
            }

            int idx = cfg.Accounts.IndexOf(acc) + 1;
            CurrentAccount = $"{acc.DisplayName(idx)} · UID {acc.AccountUid ?? "-"}";
            MemberText = MemberDetector.TryRefreshAndSave(acc, isActive: acc.Id == cfg.ActiveAccountId)
                ? MemberHint(acc.IsMember) : MemberHint(acc.IsMember);
            AppServices.SaveConfig();

            // 凭证可用则拉最新状态与剩余积分
            if (!string.IsNullOrEmpty(acc.Token) || !string.IsNullOrEmpty(acc.Session))
            {
                var valid = await TokenRenewer.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await TraeApiClient.GetStatusAsync(acc.Token!, acc.DeviceId);
                    if (st.HttpError == null && st.Code == 0)
                    {
                        var signed = st.Body is { } b &&
                                     b.TryGetProperty("checked_in", out var ci) &&
                                     ci.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (signed) acc.LastCheckinDate = DateOnly.FromDateTime(DateTime.Today);
                        var rem = await TraeApiClient.GetRemainingCreditsAsync(acc.Token!, acc.DeviceId);
                        if (rem >= 0)
                        {
                            acc.RemainingCredits = rem;
                            CheckinRecordRepo.UpsertSnapshot(acc.Id, rem);
                        }
                    }
                    AppServices.SaveConfig();
                }
                else
                {
                    StatusMessage = "凭证失效，请到「账号管理」重新登录";
                }
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            TodayStatus = acc.LastCheckinDate == today ? "今日已签 ✓" : "今日未签";
            RemainingCredits = acc.RemainingCredits >= 0 ? acc.RemainingCredits.ToString("0.#") : "—";
            double reward = CheckinRecordRepo.GetTodayTotalCredits();
            TodayReward = reward > 0 ? "+" + reward.ToString("0") : "—";

            _signedDates = CheckinRecordRepo.SignedDates();
            StreakText = $"连签 {ComputeStreak()} 天";
            RebuildCalendar();
            BuildTrend(acc);
            RebuildAccountRows();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            AppLog.Error("dash", "刷新失败: " + ex.Message);
            StatusMessage = "刷新失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>快捷签到：复用签到页批次命令（完成后 AccountsChanged 会回到本页刷新）。</summary>
    [RelayCommand]
    private async Task QuickCheckinAsync()
    {
        if (Busy || AppServices.BatchBusy) return;
        if (AppServices.Config.Accounts.Count == 0)
        {
            StatusMessage = "请先到「账号管理」添加账号";
            return;
        }
        try
        {
            InfoMessage = "正在批量签到（进度详见「每日签到」页执行状态）…";
            await _checkin.CheckinCommand.ExecuteAsync(null);
            // 批次完成后的汇总回显到本页信息横幅（此前只有数据变化+气泡，看不出执行了没有）
            InfoMessage = _checkin.StatusMessage;
        }
        catch (Exception ex)
        {
            AppLog.Error("dash", "快捷签到异常: " + ex);
            InfoMessage = "签到执行异常：" + ex.Message;
        }
    }

    /// <summary>选中账号 → 切换激活（程序内）。</summary>
    [RelayCommand]
    private void SelectAccount(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (AppServices.Config.ActiveAccountId == id) return;
        AppServices.SetActive(id); // 广播 → OnAccountsChanged → Reload
    }

    [RelayCommand] private void PrevMonth() => ShiftMonth(-1);
    [RelayCommand] private void NextMonth() => ShiftMonth(+1);

    private void ShiftMonth(int delta)
    {
        var d = new DateTime(_viewYear, _viewMonth, 1).AddMonths(delta);
        if (d.Date > DateTime.Today.AddDays(31)) return; // 不翻到未来太远
        _viewYear = d.Year;
        _viewMonth = d.Month;
        MonthTitle = $"{_viewYear} 年 {_viewMonth} 月";
        RebuildCalendar();
    }

    /// <summary>账号变化/切换激活后由主 VM 扇入。</summary>
    public void OnAccountsChanged() { DateText = DateTime.Today.ToString("yyyy-MM-dd dddd"); _ = RefreshAsync(); }

    /// <summary>进入本页时刷新。</summary>
    public void OnNavEnter() => _ = RefreshAsync();

    // ==================== 构建 ====================

    private static string MemberHint(bool isMember)
        => isMember ? "会员：基础 150 + 连签奖励" : "非会员：基础签到积分";

    private int ComputeStreak()
    {
        if (_signedDates.Count == 0) return 0;
        int n = 0;
        var cur = DateTime.Today;
        if (!_signedDates.Contains(cur.ToString("yyyy-MM-dd"))) cur = cur.AddDays(-1);
        while (_signedDates.Contains(cur.ToString("yyyy-MM-dd")))
        {
            n++;
            cur = cur.AddDays(-1);
        }
        return n;
    }

    private void RebuildCalendar()
    {
        CalendarDays.Clear();
        var first = new DateTime(_viewYear, _viewMonth, 1);
        for (int i = 0; i < (int)first.DayOfWeek; i++)
            CalendarDays.Add(new CalendarCell { Day = 0, State = "blank" });

        int days = DateTime.DaysInMonth(_viewYear, _viewMonth);
        var today = DateTime.Today;
        for (int d = 1; d <= days; d++)
        {
            var date = new DateTime(_viewYear, _viewMonth, d);
            var key = date.ToString("yyyy-MM-dd");
            bool signed = _signedDates.Contains(key);
            string state = signed ? "checked" : d == today.Day && date == today ? "today" : "empty";
            CalendarDays.Add(new CalendarCell
            {
                Day = d,
                State = state,
                Tip = date.ToString("yyyy-MM-dd") + (signed ? "\n已签到" : "\n未签到"),
            });
        }
    }

    private void RebuildAccountRows()
    {
        AccountRows.Clear();
        var cfg = AppServices.Config;
        var today = DateOnly.FromDateTime(DateTime.Today);
        int enabled = cfg.Accounts.Count(a => a.Enabled);
        MetaText = cfg.Accounts.Count == 0
            ? "尚未添加账号"
            : $"今日 {enabled} 个账号参与签到";
        for (int i = 0; i < cfg.Accounts.Count; i++)
        {
            var a = cfg.Accounts[i];
            string name = a.DisplayName(i + 1);
            var row = new DashAccountRow
            {
                Id = a.Id,
                Name = name,
                Sub = (a.LastCheckinDate == today ? "今日已签 ✓" : "今日未签") +
                      (a.RemainingCredits >= 0 ? $" · 剩 {a.RemainingCredits:0.#}" : ""),
                IsCurrent = a.Id == cfg.ActiveAccountId,
                AvatarHue = AccountRowViewModel.HueOf(a.Id),
                Initial = name.Length > 0 ? name.TrimStart()[..1].ToUpperInvariant() : "?",
            };
            AccountRows.Add(row);
            if (!string.IsNullOrEmpty(a.AvatarUrl)) _ = LoadDashAvatarAsync(row, a.AvatarUrl);
        }
    }

    private static async Task LoadDashAvatarAsync(DashAccountRow row, string url)
    {
        var src = await Services.AvatarCacheService.GetAsync(url);
        if (src != null) row.AvatarImage = src;
    }

    /// <summary>近 14 天剩余积分趋势（数据源 credits_snapshot；算法与参考实现一致）。</summary>
    private void BuildTrend(Account acc)
    {
        var trend = CheckinRecordRepo.GetSnapshotTrend(acc.Id, 14);
        double[] values;
        if (trend.Count > 0)
        {
            values = trend.Select(t => t.Remaining).ToArray();
            AxisLabels.Clear();
            if (trend.Count >= 2)
            {
                AxisLabels.Add(trend[0].Date.ToString("M/d"));
                AxisLabels.Add(trend[^1].Date == DateTime.Today ? "今日" : trend[^1].Date.ToString("M/d"));
            }
            else AxisLabels.Add(trend[0].Date.ToString("M/d"));
        }
        else if (acc.RemainingCredits >= 0)
        {
            values = [acc.RemainingCredits, acc.RemainingCredits]; // 无历史：退化为单点横线
            AxisLabels.Clear();
            AxisLabels.Add("今日");
        }
        else
        {
            values = [];
        }

        _trendValues = values;
        if (values.Length == 0)
        {
            HasTrend = false;
            LinePoints = new PointCollection();
            FillPoints = new PointCollection();
            return;
        }
        HasTrend = true;
        BuildPoints();
    }

    // ==================== 图表自适应（原固定 520 Canvas → 按容器宽度重算） ====================

    private double[]? _trendValues;
    private double _chartW = ChartWidth;
    private double _chartH = ChartHeight;

    /// <summary>图表画布尺寸变化时由视图调用（SizeChanged）。</summary>
    public void SetChartSize(double width, double height)
    {
        if (width < 60 || height < 60) return;
        bool changed = Math.Abs(width - _chartW) > 0.5 || Math.Abs(height - _chartH) > 0.5;
        _chartW = width;
        _chartH = height;
        if (changed && _trendValues is { Length: > 0 }) BuildPoints();
    }

    private void BuildPoints()
    {
        var values = _trendValues!;
        const double pad = 12;
        double w = _chartW, h = _chartH;
        double min = values.Min(), max = values.Max();
        double range = max - min == 0 ? 1 : max - min;
        double stepX = values.Length > 1 ? (w - 2 * pad) / (values.Length - 1) : 0;

        var pts = new PointCollection();
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (h - 2 * pad) * (1 - (values[i] - min) / range);
            pts.Add(new Point(x, y));
        }
        LinePoints = pts;

        // 面积填充：基线 → 数据点 → 基线闭合
        var fill = new PointCollection { new(pad, h - pad) };
        foreach (var p in pts) fill.Add(p);
        fill.Add(new(pts.Count > 0 ? pts[^1].X : w - pad, h - pad));
        FillPoints = fill;

        ChartYMax = (int)Math.Ceiling(max);
        ChartYMid = (int)Math.Ceiling((max + min) / 2);
        ChartYMin = (int)Math.Floor(min);

        if (pts.Count > 0)
        {
            TodayX = pts[^1].X - 6;
            TodayY = pts[^1].Y - 6;
        }
    }
}
