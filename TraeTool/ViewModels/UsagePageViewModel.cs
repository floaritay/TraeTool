using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Usage;

namespace TraeTool.ViewModels;

/// <summary>用量页账号下拉项。</summary>
public sealed record UsageAccount(string Id, string Name);

/// <summary>一条用量明细（展示行）。</summary>
public sealed record UsageRow(
    string TimeText, string Model, string Mode, string CreditsText, string TokensText,
    string CacheHitText, string Preview);

/// <summary>模型分布聚合行（用量页「模型分布」卡）。</summary>
public sealed record ModelStat(
    string Model, int Sessions, string CreditsText, string TokensText, string HitText, double BarWidth);

/// <summary>
/// 用量统计页：按会话展示模型消耗（积分/token/预览），近 7/30 天汇总，
/// 数据源 query_user_usage_group_by_session，本地 jsonl 增量缓存。
/// </summary>
public partial class UsagePageViewModel : ObservableObject
{
    public ObservableCollection<UsageAccount> Accounts { get; } = new();
    public ObservableCollection<UsageRow> Rows { get; } = new();

    /// <summary>模型分布（按当前账号+时间范围聚合）。</summary>
    public ObservableCollection<ModelStat> ModelStats { get; } = new();

    [ObservableProperty] private string? _selectedAccountId;
    [ObservableProperty] private int _rangeDays = 7;
    [ObservableProperty] private string _statusMessage = "进入页面即自动加载本地缓存，点「拉取最新」联网刷新";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _expiredText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _autoLoaded;

    // 汇总带（三格 + 口径；cost_money 语义未核实，不做折合展示）
    [ObservableProperty] private string _sumSessions = "—";
    [ObservableProperty] private string _sumCredits = "—";
    [ObservableProperty] private string _sumTokens = "—";
    [ObservableProperty] private string _sumScope = "";

    private readonly TraeUsageClient _client = new();

    public UsagePageViewModel()
    {
        SyncAccounts();
        ReloadFromCache();
    }

    partial void OnSelectedAccountIdChanged(string? value) => ReloadFromCache();

    partial void OnRangeDaysChanged(int value) => ReloadFromCache();

    public void OnAccountsChanged()
    {
        SyncAccounts();
        ReloadFromCache();
    }

    public void OnNavEnter()
    {
        if (AutoLoaded) return;
        AutoLoaded = true;
        _ = RefreshAsync(); // 首次进入自动联网拉一次
    }

    private void SyncAccounts()
    {
        var cfg = AppServices.Config;
        var activeId = cfg.ActiveAccountId;
        Accounts.Clear();
        for (int i = 0; i < cfg.Accounts.Count; i++)
            Accounts.Add(new UsageAccount(cfg.Accounts[i].Id, cfg.Accounts[i].DisplayName(i + 1)));
        if (SelectedAccountId == null || cfg.Accounts.All(a => a.Id != SelectedAccountId))
            SelectedAccountId = activeId ?? cfg.Accounts.FirstOrDefault()?.Id;
    }

    /// <summary>联网拉取当前账号近 N 天用量（凭证失效自动换新）→ 合并缓存 → 重载展示。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Busy) return;
        var id = SelectedAccountId;
        var acc = AppServices.Config.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc == null)
        {
            StatusMessage = "请先在「账号管理」添加账号";
            return;
        }
        if (string.IsNullOrEmpty(acc.Token) && string.IsNullOrEmpty(acc.Session))
        {
            StatusMessage = $"「{acc.DisplayName(0)}」未登录——请先到账号管理页登录";
            ReloadFromCache();
            return;
        }

        Busy = true;
        StatusMessage = "正在拉取用量…";
        try
        {
            var valid = await TokenRenewer.EnsureValidTokenAsync(acc);
            if (!valid)
            {
                StatusMessage = "凭证失效，请重新登录";
                ReloadFromCache();
                return;
            }
            AppServices.SaveConfig();

            var start = DateTime.Today.AddDays(-RangeDays);
            var fetched = await _client.FetchAllAsync(acc.Token!, acc.Session, start, DateTime.Today);
            UsageStore.Merge(acc.Id, fetched);

            // 临期资格包（尽力而为）
            ExpiredText = "";
            var expired = await _client.GetExpiredEntsAsync(acc.Token!, acc.Session);
            if (expired.Count > 0)
            {
                ExpiredText = "临期积分包：" + string.Join("；",
                    expired.Take(3).Select(e =>
                        $"{e.Name} {e.CreditsLimit:0} 分 截至 {DateTimeOffset.FromUnixTimeMilliseconds(e.ExpireTimeMs).LocalDateTime:yyyy-MM-dd}"));
            }

            StatusMessage = $"拉取完成：新增 {fetched.Count} 条会话记录";
            AppLog.Info("usage", $"refresh {acc.DisplayName(0)} range={RangeDays}d fetched={fetched.Count}");
            ReloadFromCache();
        }
        catch (Exception ex)
        {
            StatusMessage = "拉取失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>从本地缓存重载列表与汇总（不联网）。</summary>
    private void ReloadFromCache()
    {
        void Apply()
        {
            Rows.Clear();
            SummaryText = "";
            if (string.IsNullOrEmpty(SelectedAccountId)) return;

            var since = new DateTimeOffset(DateTime.Today.AddDays(-RangeDays)).ToUnixTimeSeconds();
            var records = UsageStore.LoadAll(SelectedAccountId)
                .Where(r => r.UsageTime >= since)
                .ToList();

            foreach (var r in records.Take(200))
            {
                var t = r.UsageTime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(r.UsageTime).LocalDateTime.ToString("MM-dd HH:mm")
                    : "-";
                var tokens = r.InputToken + r.OutputToken > 0
                    ? $"{r.InputToken / 1000.0:0.#}k → {r.OutputToken / 1000.0:0.#}k"
                    : "-";
                Rows.Add(new UsageRow(
                    t,
                    r.ModelName,
                    string.IsNullOrEmpty(r.Mode) ? "-" : r.Mode,
                    r.CreditsFloat.ToString("0.##"),
                    tokens,
                    CacheHitText(r),
                    r.UserInputPreview.Length > 60 ? r.UserInputPreview[..60] + "…" : r.UserInputPreview));
            }

            // ---- 模型分布聚合 ----
            ModelStats.Clear();
            var groups = records
                .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelName) ? "未标注模型" : r.ModelName)
                .Select(g => new
                {
                    Model = g.Key,
                    Sessions = g.Count(),
                    Credits = g.Sum(x => x.CreditsFloat),
                    Tokens = g.Sum(x => x.InputToken + x.OutputToken),
                    Cache = g.Sum(x => x.CacheReadToken),
                    Input = g.Sum(x => x.InputToken),
                })
                .OrderByDescending(g => g.Credits)
                .ToList();
            double maxCredits = groups.Count > 0 ? Math.Max(1, groups.Max(g => g.Credits)) : 1;
            foreach (var g in groups)
            {
                var denom = g.Input + g.Cache;
                var hit = denom > 0 ? g.Cache * 100.0 / denom : 0;
                ModelStats.Add(new ModelStat(
                    g.Model,
                    g.Sessions,
                    g.Credits.ToString("0.##"),
                    g.Tokens >= 1000 ? $"{g.Tokens / 1000.0:0.#}k" : g.Tokens.ToString(),
                    g.Cache > 0 ? $"{hit:0}%" : "—",
                    Math.Max(3, Math.Round(110 * g.Credits / maxCredits))));
            }

            static string CacheHitText(UsageSessionRecord r)
            {
                var denom = r.InputToken + r.CacheReadToken;
                if (r.CacheReadToken <= 0 || denom <= 0) return "—";
                return $"{r.CacheReadToken * 100.0 / denom:0}%";
            }

            string accName = AppServices.Config.Accounts
                .FirstOrDefault(a => a.Id == SelectedAccountId)?.DisplayName(0) ?? "-";
            if (records.Count > 0)
            {
                double credits = records.Sum(r => r.CreditsFloat);
                long tokens = records.Sum(r => r.InputToken + r.OutputToken);
                SummaryText = "";
                SumSessions = records.Count.ToString();
                SumCredits = credits.ToString("0.##");
                SumTokens = tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();
                SumScope = $"近 {RangeDays} 天 · {accName}";
            }
            else
            {
                SummaryText = $"近 {RangeDays} 天暂无记录（点「拉取最新」联网获取）";
                SumSessions = "0";
                SumCredits = "0";
                SumTokens = "0";
                SumScope = $"近 {RangeDays} 天 · {accName}";
            }
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }
}
