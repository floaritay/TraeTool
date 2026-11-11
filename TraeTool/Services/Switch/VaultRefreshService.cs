using System.Windows.Threading;
using TraeTool.ViewModels;

namespace TraeTool.Services.Switch;

/// <summary>
/// 档案定时保鲜：
/// 每 10 分钟检查一次，仅当【客户端未运行】且能【唯一判定】当前登录账号时，
/// 给该账号的档案重新备份（保留原 CreatedUtc），防止登录态随时间陈旧
/// （refresh token 轮换、storage.json 漂移等）。
/// 条件不满足（客户端在跑 / 多档案并列 / 无人操作）就静默跳过。
/// </summary>
public sealed class VaultRefreshService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    /// <summary>唯一判定阈值：最佳分需领先第二名至少 20 个百分点（防同会话档案并列时刷错对象）。</summary>
    private const double UniqueLead = 0.20;
    /// <summary>档案已足够新（几乎全同）就不重写。</summary>
    private const double FreshEnough = 0.99;

    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private bool _wired;
    private bool _running;

    public void Start()
    {
        if (_wired) return;
        _wired = true;
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_running) return;
        if (AppServices.BatchBusy) return;            // 批量签到进行中，不与它抢
        if (ClientControl.IsRunning()) return;        // 客户端在跑，live 被运行时污染，不保鲜
        if (!AppServices.TryEnterSwitch()) return;    // 原子占用切换锁，期间阻止建档/切换并发写同一档案

        _running = true;
        try
        {
            var cfg = AppServices.Config;
            var scored = new List<(string Id, string Name, double Score)>();
            for (int i = 0; i < cfg.Accounts.Count; i++)
            {
                var acc = cfg.Accounts[i];
                if (!VaultStore.Exists(acc.Id)) continue;
                try
                {
                    var rep = new VaultStore().ScoreAgainstLive(acc.Id);
                    scored.Add((acc.Id, acc.DisplayName(i + 1), rep.Score));
                }
                catch (Exception ex)
                {
                    AppLog.Error("vault", $"保鲜评分失败({acc.DisplayName(i + 1)}): {ex.Message}");
                }
            }
            if (scored.Count == 0) return;

            var ordered = scored.OrderByDescending(s => s.Score).ToList();
            var best = ordered[0];
            bool unique = best.Score >= VaultStore.SimilarityPass &&
                          (ordered.Count == 1 || best.Score - ordered[1].Score >= UniqueLead);
            if (!unique)
            {
                AppLog.Info("vault", "保鲜跳过：无法唯一判定客户端当前账号（档案并列或均不吻合）");
                return;
            }
            if (best.Score >= FreshEnough)
            {
                AppLog.Info("vault", $"保鲜跳过：{best.Name} 档案已足够新（{best.Score:P0}）");
                return;
            }

            var meta = await new VaultStore().BackupAsync(best.Id, best.Name);
            AppLog.Info("vault", $"档案保鲜：{best.Name}（{best.Score:P0} → 已刷新 {meta.Entries.Count} 文件）");
        }
        catch (Exception ex)
        {
            AppLog.Error("vault", "档案保鲜失败: " + ex.Message);
        }
        finally
        {
            AppServices.SwitchBusy = false;
            _running = false;
        }
    }
}
