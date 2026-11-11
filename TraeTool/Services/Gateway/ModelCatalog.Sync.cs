using System.Net.Http;
using System.Text;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>第 2 层：从上游模型配置端点拉取模型表，并解析出带元数据的条目。</summary>
public static partial class ModelCatalog
{
    /// <summary>实际请求模型配置；任何失败返回 null（由调用方决定是否回退）。</summary>
    public static async Task<IReadOnlyList<ModelEntry>?> FetchOfficialEntriesAsync(AppSettings cfg, Account? account,
        HttpClient? http, CancellationToken ct)
    {
        if (account == null || string.IsNullOrEmpty(account.Token)) return null;

        // 用共享传输时先续期：过期 JWT 会被上游以 401/code 1001 拒绝。
        // 调用方注入了 HttpClient（测试用假 handler）则视为其已保证凭据有效，避免测试打真实网络。
        if (http == null)
        {
            var before = account.Token;
            if (!await TokenRenewer.EnsureValidTokenAsync(account)) return null;
            if (!string.Equals(before, account.Token, StringComparison.Ordinal)) ConfigStore.Save(cfg);
        }

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, TraeChatUpstream.BaseUrl + DetailParamPath)
            {
                Content = new StringContent(DetailParamBody, Encoding.UTF8, "application/json"),
            };
            TraeChatUpstream.ApplyObservedHeaders(msg, account);
            using var resp = await (http ?? HttpClients.Default).SendAsync(msg, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Info("pool", $"模型配置同步失败 HTTP {(int)resp.StatusCode}，响应前200字：{Brief(text)}");
                return null;
            }
            var entries = ParseEntries(text);
            if (entries.Count == 0)
            {
                AppLog.Info("pool", $"模型配置同步返回200但没解析出模型，响应前200字：{Brief(text)}");
                return null;
            }
            AppLog.Info("pool", $"模型配置同步成功：{entries.Count} 个");
            return entries;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Info("pool", "拉取模型配置失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>只取模型 id（/v1/models 与既有测试用）。</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
        => ParseEntries(json).Select(e => e.Id).ToList();

    /// <summary>
    /// 解析模型配置条目（含展示名/倍率/图片/思考档位）。
    /// 依次尝试：function_configs[].config_info_list[] 与顶层 config_info_list[]（Trae 模型配置）、
    /// model_configs 的键（代码补全注册表）、models[].id。
    /// </summary>
    public static IReadOnlyList<ModelEntry> ParseEntries(string json)
    {
        var result = new List<ModelEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            CollectConfigInfoList(doc.RootElement, result);
            if (result.Count > 0) return result;

            if (doc.RootElement.TryGetProperty("model_configs", out var cfg) &&
                cfg.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cfg.EnumerateObject()) AddBare(result, p.Name);
            }
            if (result.Count == 0 && doc.RootElement.TryGetProperty("models", out var models) &&
                models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Object &&
                        m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        AddBare(result, id.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应 → 空列表，由调用方回退
        }
        return result;
    }

    private static void CollectConfigInfoList(JsonElement root, List<ModelEntry> result)
    {
        if (root.TryGetProperty("function_configs", out var fcs) && fcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var fc in fcs.EnumerateArray())
            {
                if (fc.TryGetProperty("config_info_list", out var list) && list.ValueKind == JsonValueKind.Array)
                    CollectItems(list, result);
            }
        }
        if (root.TryGetProperty("config_info_list", out var top) && top.ValueKind == JsonValueKind.Array)
            CollectItems(top, result);
    }

    /// <summary>只收开着的、且有显示名的配置——没有显示名的是功能开关而非模型。</summary>
    private static void CollectItems(JsonElement list, List<ModelEntry> result)
    {
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("config_switch", out var sw) && sw.ValueKind == JsonValueKind.False)
                continue;

            var display = "";
            bool? image = null;
            if (item.TryGetProperty("display_config", out var dc) && dc.ValueKind == JsonValueKind.Object)
            {
                if (dc.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                    display = dn.GetString() ?? "";
                if (dc.TryGetProperty("multimodal", out var mm) &&
                    mm.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    image = mm.ValueKind == JsonValueKind.True;
            }
            if (string.IsNullOrWhiteSpace(display)) continue;

            if (!item.TryGetProperty("config_name", out var cn) || cn.ValueKind != JsonValueKind.String)
                continue;
            var id = cn.GetString()!;
            if (result.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(new ModelEntry(id, display, null, ReadRate(item), ReadEfforts(item),
                null, null, image, "official"));
        }
    }

    /// <summary>倍率：display_contact_config（JSON 串）→ consumption_rate.data.rate。</summary>
    private static string? ReadRate(JsonElement item)
    {
        if (!item.TryGetProperty("display_contact_config", out var dcc) ||
            dcc.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var doc = JsonDocument.Parse(dcc.GetString() ?? "");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("consumption_rate", out var cr) ||
                cr.ValueKind != JsonValueKind.Object) return null;
            if (!cr.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            if (!data.TryGetProperty("rate", out var rate)) return null;
            return rate.ValueKind switch
            {
                JsonValueKind.Number => rate.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String => rate.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>思考档位：reasoning_effort_config 里第一个字符串数组（支持但没档位列表时留空）。</summary>
    private static string? ReadEfforts(JsonElement item)
    {
        if (!item.TryGetProperty("reasoning_effort_config", out var cfg) ||
            cfg.ValueKind != JsonValueKind.Object) return null;
        if (cfg.TryGetProperty("support_thinking", out var st) && st.ValueKind == JsonValueKind.False)
            return null;

        foreach (var prop in cfg.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var items = new List<string>();
            foreach (var x in prop.Value.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) items.Add(x.GetString()!);
                else if (x.ValueKind == JsonValueKind.Object)
                {
                    if (x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        items.Add(n.GetString()!);
                    else if (x.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String)
                        items.Add(l.GetString()!);
                }
            }
            if (items.Count > 0) return string.Join(" / ", items);
        }
        return null;
    }

    /// <summary>日志用的响应摘要（压掉换行、截断，避免把整包配置写进日志）。</summary>
    private static string Brief(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    /// <summary>只带 id 的条目（无元数据的层用）。</summary>
    private static void AddBare(List<ModelEntry> list, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (list.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(new ModelEntry(id, id, null, null, null, null, null, null, "builtin"));
    }
}
