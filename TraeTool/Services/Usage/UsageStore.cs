using System.IO;
using TraeTool.Models;

namespace TraeTool.Services.Usage;

/// <summary>
/// 用量记录本地持久化：%APPDATA%\TraeTool\data\usage_&lt;accountId&gt;.jsonl，每行一条。
/// 每次抓取按 SessionId 增量合并（只追加缺失），读取时去重保留最新、按用量时间倒序。
/// </summary>
public static class UsageStore
{
    private static readonly object IoLock = new();

    private static string PathFor(string accountId) => Path.Combine(Paths.DataDir, $"usage_{accountId}.jsonl");

    /// <summary>读取某账号全部本地记录（按 UsageTime 倒序，SessionId 去重保留最新 FetchedAt）。</summary>
    public static List<UsageSessionRecord> LoadAll(string accountId)
    {
        var list = new List<UsageSessionRecord>();
        lock (IoLock)
        {
            var path = PathFor(accountId);
            if (!File.Exists(path)) return list;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = System.Text.Json.JsonSerializer.Deserialize<UsageSessionRecord>(line);
                        if (rec != null && !string.IsNullOrEmpty(rec.SessionId)) list.Add(rec);
                    }
                    catch { /* 跳过损坏行 */ }
                }
            }
            catch { /* 读失败返回已解析部分 */ }
        }

        var dedup = new Dictionary<string, UsageSessionRecord>();
        foreach (var r in list)
        {
            if (!dedup.TryGetValue(r.SessionId, out var old) || r.FetchedAt > old.FetchedAt)
                dedup[r.SessionId] = r;
        }
        return dedup.Values.OrderByDescending(r => r.UsageTime).ToList();
    }

    /// <summary>增量写入新抓到的会话（按 SessionId 去重，只追加缺失项）。</summary>
    public static void Merge(string accountId, IEnumerable<UsageSessionRecord> fetched)
    {
        var existing = LoadAll(accountId);
        var known = new HashSet<string>(existing.Select(e => e.SessionId));
        var toAppend = fetched
            .Where(r => !string.IsNullOrEmpty(r.SessionId) && known.Add(r.SessionId))
            .OrderBy(r => r.UsageTime)
            .ToList();
        if (toAppend.Count == 0) return;

        lock (IoLock)
        {
            try
            {
                var path = PathFor(accountId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var sw = File.AppendText(path);
                foreach (var r in toAppend)
                    sw.WriteLine(System.Text.Json.JsonSerializer.Serialize(r));
            }
            catch (Exception ex)
            {
                AppLog.Error("usage", "写入用量记录失败: " + ex.Message);
            }
        }
    }
}
