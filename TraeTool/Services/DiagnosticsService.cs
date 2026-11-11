using System.IO;
using System.Net.Http;
using TraeTool.Models;
using TraeTool.Services.Switch;

namespace TraeTool.Services;

/// <summary>一项自检结果：Status = ok / warn / fail。</summary>
public sealed record DiagItem(string Name, string Status, string Detail);

/// <summary>
/// 环境自检：逐项验证运行环境（数据目录、数据库、WebView2 运行时、客户端、
/// 签到接口连通性、更新源、账号凭据）。纯只读，不改动任何状态。
/// 配置由调用方传入（不读取全局状态），便于离线测试。
/// </summary>
public static class DiagnosticsService
{
    private static HttpClient Http => HttpClients.For(8);

    public static async Task<List<DiagItem>> RunAsync(AppSettings cfg, IProgress<string>? log = null,
        LocalGateway? gateway = null)
    {
        var items = new List<DiagItem>();

        // 1. 数据目录可写
        log?.Report("检查数据目录…");
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var probe = Path.Combine(Paths.Root, ".diag_" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            items.Add(new DiagItem("数据目录", "ok", Paths.Root));
        }
        catch (Exception ex)
        {
            items.Add(new DiagItem("数据目录", "fail", "不可写：" + ex.Message));
        }

        // 2. 数据库
        log?.Report("检查数据库…");
        try
        {
            using var conn = Database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM checkin_record";
            var n = Convert.ToInt64(cmd.ExecuteScalar());
            items.Add(new DiagItem("数据库", "ok", $"{n} 条签到记录 · {Paths.DatabaseFile}"));
        }
        catch (Exception ex)
        {
            items.Add(new DiagItem("数据库", "fail", ex.Message));
        }

        // 3. WebView2 运行时
        log?.Report("检查 WebView2 运行时…");
        try
        {
            var ver = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            items.Add(new DiagItem("WebView2 运行时",
                string.IsNullOrEmpty(ver) ? "warn" : "ok",
                string.IsNullOrEmpty(ver) ? "未检测到（登录功能不可用）" : ver));
        }
        catch (Exception ex)
        {
            items.Add(new DiagItem("WebView2 运行时", "warn", "未安装或不可用：" + ex.Message));
        }

        // 4. Trae 客户端
        log?.Report("检查 Trae 客户端…");
        var exe = ClientControl.ResolveExe(cfg.ClientExePath);
        bool dataRoot = Directory.Exists(CarrierDefs.DataRoot);
        items.Add(new DiagItem("Trae 客户端",
            exe != null ? "ok" : "warn",
            exe != null
                ? $"{exe}（数据目录{(dataRoot ? "存在" : "缺失")}）"
                : "未检测到客户端 exe（切换/导入功能不可用）"));

        // 5. 签到接口连通性
        log?.Report("检查签到接口连通性…");
        items.Add(await ProbeAsync("签到接口", "https://api.trae.cn/trae/api/v2/ug/checkin_credits/status"));

        // 6. 更新源
        log?.Report("检查更新源…");
        var feed = string.IsNullOrWhiteSpace(cfg.UpdateFeedUrl)
            ? UpdateService.DefaultFeedUrl
            : cfg.UpdateFeedUrl!;
        items.Add(await ProbeAsync("更新源", feed));
        items.Add(new DiagItem("更新源配置", "ok", feed));

        // 6.1 出站代理与本机管理网关
        items.Add(new DiagItem("出站代理", "ok", HttpClients.Proxy.Describe()));
        if (gateway != null)
        {
            var g = cfg.GatewayEnabled
                ? (gateway.IsRunning && gateway.Port == cfg.GatewayPort
                    ? new DiagItem("管理网关", "ok", $"运行中 · {gateway.Endpoint}（仅回环地址）")
                    : new DiagItem("管理网关", "fail",
                        gateway.LastError ?? $"已启用但未在端口 {cfg.GatewayPort} 监听"))
                : new DiagItem("管理网关", "ok", "未启用");
            items.Add(g);
        }

        // 7. 账号凭据
        log?.Report("检查账号凭据…");
        var accounts = cfg.Accounts;
        if (accounts.Count == 0)
        {
            items.Add(new DiagItem("账号凭据", "warn", "尚未添加账号"));
        }
        else
        {
            int ok = 0, expired = 0, missing = 0;
            var problems = new List<string>();
            for (int i = 0; i < accounts.Count; i++)
            {
                var a = accounts[i];
                var name = a.DisplayName(i + 1);
                bool hasToken = !string.IsNullOrEmpty(a.Token);
                bool hasSession = !string.IsNullOrEmpty(a.Session);
                if (!hasToken && !hasSession) { missing++; problems.Add($"{name}(未登录)"); continue; }

                var exp = JwtHelper.TryGetExpiry(a.Token);
                if (hasToken && exp is { } e && e <= DateTime.Now && !hasSession)
                {
                    expired++; problems.Add($"{name}(登录态过期且无会话)");
                    continue;
                }
                if (AccountHealth.IsSuspended(a, DateTime.Today))
                {
                    problems.Add($"{name}(已暂停)");
                    ok++;
                    continue;
                }
                ok++;
            }
            var status = problems.Count == 0 ? "ok" : "warn";
            var detail = $"{accounts.Count} 个账号：可用 {ok} · 未登录 {missing} · 需重登 {expired}";
            if (problems.Count > 0) detail += " · " + string.Join("、", problems);
            items.Add(new DiagItem("账号凭据", status, detail));
        }

        log?.Report("自检完成");
        return items;
    }

    private static async Task<DiagItem> ProbeAsync(string name, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req);
            // 任何 HTTP 应答都说明网络可达（401/404 同样证明连通，仅状态码异常时记为 warn）
            return new DiagItem(name, "ok", $"可达（HTTP {(int)resp.StatusCode}）");
        }
        catch (Exception ex)
        {
            return new DiagItem(name, "warn", "不可达：" + ex.Message);
        }
    }
}
