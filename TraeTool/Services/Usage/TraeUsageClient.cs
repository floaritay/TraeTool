using System.Net.Http;
using System.Text;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services.Usage;

/// <summary>
/// Trae 用量统计客户端（接口逆向自 www.trae.cn/dashboard#usage 页面）。
/// 认证与签到一致：Cloud-IDE-JWT + X-Cloudide-Session Cookie + Referer/Origin。
/// 核心：POST /trae/api/v1/pay/query_user_usage_group_by_session（按会话聚合，分页）；
/// 附带：POST /trae/api/v2/pay/expired_ents（资格包过期提醒）。
/// </summary>
public sealed class TraeUsageClient
{
    private const string BaseUrl = "https://api.trae.cn";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string? LastError { get; private set; }

    /// <summary>拉取一页按会话聚合的用量。</summary>
    public async Task<UsagePageResult> GetUsageSessionsAsync(
        string token, string? session, long startSeconds, long endSeconds, int pageNum, int pageSize = 20)
    {
        var result = new UsagePageResult();
        var body = $"{{\"start_time\":{startSeconds},\"end_time\":{endSeconds},\"page_size\":{pageSize},\"page_num\":{pageNum},\"usage_type\":[7]}}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/trae/api/v1/pay/query_user_usage_group_by_session");
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            AppLog.Info("usage", $"query_usage page={pageNum} -> HTTP {(int)resp.StatusCode} len={json.Length}");
            if (string.IsNullOrWhiteSpace(json))
            {
                LastError = $"HTTP {(int)resp.StatusCode} 空响应";
                return result;
            }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("total", out var totalEl))
            {
                LastError = "响应缺少 total 字段（可能接口失效/风控）";
                return result;
            }
            result.Total = ReadInt(totalEl);

            if (!doc.RootElement.TryGetProperty("user_usage_group_by_sessions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                LastError = "响应缺少 user_usage_group_by_sessions 字段";
                return result;
            }
            foreach (var el in arr.EnumerateArray())
                result.Items.Add(ParseSession(el));
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("usage", "query_usage 异常: " + ex.Message);
            return result;
        }
    }

    /// <summary>整段拉取时间范围内全部会话（自动翻页，防失控上限 maxPages 页）。</summary>
    public async Task<List<UsageSessionRecord>> FetchAllAsync(
        string token, string? session, DateTime start, DateTime end, int pageSize = 20, int maxPages = 20)
    {
        var items = new List<UsageSessionRecord>();
        long startSec = new DateTimeOffset(start.ToLocalTime()).ToUnixTimeSeconds();
        long endSec = new DateTimeOffset(end.ToLocalTime()).ToUnixTimeSeconds();
        int page = 1, total = 1;
        while (page <= maxPages && (items.Count < total || total <= 0))
        {
            var r = await GetUsageSessionsAsync(token, session, startSec, endSec, page, pageSize);
            if (r.Items.Count > 0)
            {
                items.AddRange(r.Items);
                total = r.Total;
            }
            else break;              // 空页 = 到底
            if (r.Items.Count < pageSize) break; // 未满页 = 到底
            page++;
        }
        return items;
    }

    /// <summary>资格包过期记录（临期/已过期提醒）。</summary>
    public async Task<List<ExpiredEnt>> GetExpiredEntsAsync(string token, string? session)
    {
        var list = new List<ExpiredEnt>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/trae/api/v2/pay/expired_ents");
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("expired_ent_list", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var el in arr.EnumerateArray())
            {
                var ent = new ExpiredEnt
                {
                    Name = ReadString(el, "name") ?? "",
                    CreditsLimit = ReadDouble(el, "credits_limit", 0),
                    ExpireTimeMs = ReadLong(el, "expire_time_ms"),
                };
                if (!string.IsNullOrEmpty(ent.Name) || ent.CreditsLimit > 0)
                    list.Add(ent);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("usage", "expired_ents 异常: " + ex.Message);
        }
        return list;
    }

    // ---------- 解析 ----------

    private static UsageSessionRecord ParseSession(JsonElement el)
    {
        var rec = new UsageSessionRecord
        {
            SessionId = ReadString(el, "session_id") ?? "",
            UsageTime = ReadLong(el, "usage_time"),
            ModelName = ReadString(el, "model_name") ?? "",
            Mode = ReadString(el, "mode") ?? "",
            UseMaxMode = ReadBool(el, "use_max_mode", false),
            CreditsFloat = ReadDouble(el, "credits_float", ReadDouble(el, "amount_float", 0)),
            CostMoneyFloat = ReadDouble(el, "cost_money_float", 0),
            UserInputPreview = ReadString(el, "user_input_preview") ?? "",
        };
        if (el.TryGetProperty("extra_info", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            rec.InputToken = ReadLong(extra, "input_token");
            rec.OutputToken = ReadLong(extra, "output_token");
            rec.CacheReadToken = ReadLong(extra, "cache_read_token");
            rec.CacheWriteToken = ReadLong(extra, "cache_write_token");
        }
        rec.FetchedAt = DateTime.Now;
        return rec;
    }

    private static string? ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l2)) return l2;
        return 0;
    }

    private static double ReadDouble(JsonElement el, string name, double def = 0)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return def;
    }

    private static int ReadInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var i2)) return i2;
        return 0;
    }

    private static bool ReadBool(JsonElement el, string name, bool def)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        return v.ValueKind == JsonValueKind.True;
    }
}
