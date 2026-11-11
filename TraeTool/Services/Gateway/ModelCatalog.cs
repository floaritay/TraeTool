using System.Net.Http;
using System.Text;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>模型列表的四层来源与各自贡献数（用于设置页状态行）。</summary>
public sealed record ModelSources(
    int Manual, int Official, int Builtin, int Inferred, int Hidden, bool Live, DateTime? SyncedAt);

/// <summary>
/// 带元数据的模型条目（设置页表格用）。官网响应里能取到展示名/倍率/图片/思考档位，
/// 厂商与上下文窗口上游不提供，由内置条目补齐；缺的字段表格显示「—」。
/// </summary>
public sealed record ModelEntry(
    string Id, string Display, string? Vendor, string? Rate, string? Efforts,
    string? Context, string? LongContext, bool? Image, string Source)
{
    /// <summary>图片支持的表格文案。</summary>
    public string ImageText => Image == true ? "✓" : Image == false ? "✗" : "—";

    /// <summary>思考档位文案。</summary>
    public string EffortsText => string.IsNullOrEmpty(Efforts) ? "—" : Efforts!;

    /// <summary>长上下文文案。</summary>
    public string LongContextText => string.IsNullOrEmpty(LongContext) ? "—" : LongContext!;

    /// <summary>厂商文案。</summary>
    public string VendorText => string.IsNullOrEmpty(Vendor) ? "—" : Vendor!;

    /// <summary>倍率文案。</summary>
    public string RateText => string.IsNullOrEmpty(Rate) ? "—" : Rate!;

    /// <summary>展示名（没有就用 id）。</summary>
    public string DisplayText => string.IsNullOrEmpty(Display) ? Id : Display;
}

/// <summary>
/// 模型目录：四层来源按优先级聚合出 /v1/models 的列表，并把客户端请求的模型名解析成上游可接受的 config_name。
///
/// 优先级（人工维护 &gt; 官网同步 &gt; 内置参考 &gt; 名称推断），高优先级在前、整体去重：
///   1) 人工维护 —— <see cref="AppSettings.ModelOverrides"/>：用户在设置页对条目增删改，
///      与低层同名者按字段覆盖（没填的字段交回低层补齐），标了 Hidden 的从所有层里剔除；
///   2) 官网同步 —— <see cref="DetailParamPath"/>（客户端启动后也会请求它），
///      返回 <c>function_configs[].config_info_list[]</c>，<c>config_name</c> 即请求体 <c>model</c> 字段的取值，
///      <c>display_config.display_name</c> 为界面名；只收 <c>config_switch=true</c> 且有显示名的项——
///      fast_apply / summary / title_generation 这类没有显示名的是功能开关，不是模型；
///   3) 内置参考 —— <see cref="KnownIds"/>（来自实际观测到的配置响应）；
///   4) 名称推断 —— 本网关收到过的请求名（<see cref="AppSettings.GatewaySeenModels"/>，自动累积），
///      外加解析时按归一化比对（去上下文标记、去厂商前缀、忽略大小写与分隔符）推断同名模型。
///
/// 官网同步 30 分钟缓存；同步失败保留上一次在线结果，再失败才回退内置参考。
/// </summary>
public static class ModelCatalog
{
    /// <summary>客户端在 base_url 里使用的别名（转发时解析为真实 id）。</summary>
    public const string Alias = "trae";

    /// <summary>默认模型：别名或未知请求名解析到它（须出现在内置参考或上游目录内）。</summary>
    public const string PreferredModel = "DeepSeek-V4-Flash";

    /// <summary>官网同步端点（观测到的客户端实际请求）。</summary>
    public static string DetailParamPath = "/api/ide/v1/batch_get_detail_param";

    /// <summary>该端点的请求体（观测到的实际取值）。</summary>
    public const string DetailParamBody =
        """{"functions":["builder"],"agent_type":"","current_config_info":{"config_name":"","is_custom_model":false},"mode_type":0,"access_type":1,"ab_force_vids":"","ab_autotest_advanced_mode":0}""";

    /// <summary>内置参考：观测到的模型配置名（均有显示名，含 chat_model 与 reasoning_model 两类能力）。</summary>
    public static readonly string[] KnownIds =
    [
        "DeepSeek-V4-Flash", "DeepSeek-V4-Pro",
        "Doubao-Seed-2.1-Turbo", "Doubao-Seed-2.1-Pro", "Doubao-Seed-Evolving", "Doubao-Seed-Code",
        "doubao-for-auto", "Doubao_1_6",
        "glm-5.2", "glm-5.1", "glm-5", "glm-4.7", "glm-4.6",
        "minimax-m3.1-flash", "minimax-m3", "minimax-m2.1", "minimax-m2",
        "qwen-3.7-plus", "qwen-3.5",
        "kimi-k2.7-code", "kimi-k2", "qwen3-coder",
        "custom_claude-sonnet-4", "custom_claude-3-7-sonnet", "custom_gemini-2.5-pro",
    ];

    /// <summary>
    /// 内置带元数据的模型条目（第 3 层的一部分）：id 与其展示名、厂商、倍率、思考档位、
    /// 默认/长上下文、图片支持。上游响应不含厂商与上下文窗口，这两列由本表提供。
    /// </summary>
    public static readonly ModelEntry[] Curated =
    [
        new("deepseek-v4.1-flash", "DeepSeek-V4.1-Flash", "DeepSeek", "0.11", "low / high / max", "1000k", null, true, "builtin"),
        new("glm-5.3", "GLM-5.3", "智谱", "0.69", "low / high / xhigh / max", "1000k", null, true, "builtin"),
        new("glm-5.3-flash", "GLM-5.3-Flash", "智谱", "0.08", "low / high / max", "1000k", "—", true, "builtin"),
        new("kimi-k3", "Kimi-K3", "Moonshot", "1.83", "low / high / xhigh", "200k", null, true, "builtin"),
        new("kimi-k3.1-pro", "Kimi-K3.1-Pro", "Moonshot", "0.97", "low / high / max", "1000k", "—", true, "builtin"),
        new("mimo-v2.6-pro", "Xiaomi MiMo-V2.6-Pro", "小米", "0.13", "high", "1000k", "—", true, "builtin"),
        new("minimax-m3.1-flash", "MiniMax-M3.1-Flash", "MiniMax", "0.08", "low / medium / high / xhigh / max", "1000k", "—", true, "builtin"),
        new("qwen3.8-flash", "Qwen3.8-Flash", "阿里·通义", "0.08", "low / medium / xhigh", "200k", "—", true, "builtin"),
        new("qwen3.8-max", "Qwen3.8-Max", "阿里·通义", "1.50", "low / medium / high / xhigh", "200k", null, true, "builtin"),
        new("step-5-preview", "Step-5-Preview", "阶跃星辰", "0.33", "low / medium / high", "1000k", "—", true, "builtin"),
        new("DeepSeek-V4-Pro-Official", "DeepSeek-V4-Pro", "DeepSeek", "0.72", null, "200k", null, false, "builtin"),
        new("Doubao-Seed-2.1-Pro", "Seed-2.1-Pro-0915", "字节·豆包", "0.80", null, "256k", "—", true, "builtin"),
        new("Doubao-Seed-2.1-Turbo", "Seed-2.1-Turbo", "字节·豆包", "0.20", null, "256k", "—", true, "builtin"),
        new("Doubao-Seed-Code", "Seed-Code", "字节·豆包", "0.06", null, "256k", "—", true, "builtin"),
        new("Doubao-Seed-Evolving", "Seed-Evolving", "字节·豆包", "0.80", null, "256k", null, true, "builtin"),
        new("glm-5.3-flashx", "GLM-5.3-FlashX", "智谱", "0.31", null, "200k", "—", true, "builtin"),
        new("mimo-v2.6-flash", "MiMo-V2.6-Flash", "小米", "0.06", null, "200k", "—", true, "builtin"),
        new("minimax-m3", "MiniMax-M3", "MiniMax", "0.26", null, "200k", null, true, "builtin"),
    ];

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static readonly object Gate = new();
    private static IReadOnlyList<string> _official = KnownIds;
    private static IReadOnlyList<ModelEntry> _officialEntries = Array.Empty<ModelEntry>();
    private static DateTime _syncAt = DateTime.MinValue;
    private static bool _officialLive;

    /// <summary>当前官网层结果（缓存；未同步过则等于内置参考）。</summary>
    public static IReadOnlyList<string> OfficialCurrent
    {
        get { lock (Gate) return _official; }
    }

    /// <summary>当前官网层条目（带元数据；未同步过则等于内置条目）。</summary>
    public static IReadOnlyList<ModelEntry> CurrentEntries
    {
        get { lock (Gate) return _officialEntries.Count > 0 ? _officialEntries : BuiltinEntries(); }
    }

    /// <summary>最近一次官网同步时间（UTC）；null = 尚未成功同步过。</summary>
    public static DateTime? LastSyncedAt
    {
        get { lock (Gate) return _officialLive ? _syncAt : null; }
    }

    /// <summary>清空官网同步缓存，下一次 <see cref="GetOfficialAsync"/> 重新拉取（更新按钮与测试用）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _official = KnownIds;
            _officialEntries = BuiltinEntries();
            _syncAt = DateTime.MinValue;
            _officialLive = false;
        }
    }

    // ==================== 四层聚合 ====================

    /// <summary>第 1 层：人工维护（逗号/分号/换行分隔）。</summary>
    public static IReadOnlyList<string> ParseManual(string? raw) => SplitNames(raw);

    /// <summary>第 4 层：网关收到过的请求名（自动累积，同样按分隔符切分）。</summary>
    public static IReadOnlyList<string> ParseSeen(string? raw) => SplitNames(raw);

    private static IReadOnlyList<string> SplitNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var part in raw.Split(new[] { ',', ';', '\r', '\n', '、' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Contains(part, StringComparer.OrdinalIgnoreCase)) list.Add(part);
        return list;
    }

    /// <summary>第 4 层保留的请求模型名上限（超出丢弃最早的，避免被任意客户端名无限撑大）。</summary>
    public const int MaxSeenModels = 32;

    /// <summary>单个请求名的长度上限；超出视为无效名，不进入第 4 层。</summary>
    private const int MaxSeenNameLength = 64;

    /// <summary>
    /// 记录一次客户端请求的模型名（第 4 层数据源），重复不追加；
    /// 超过 <see cref="MaxSeenModels"/> 时丢弃最早的条目。返回 true 表示配置有变化需落盘。
    /// </summary>
    public static bool RecordRequested(AppSettings cfg, string requested)
    {
        var name = StripMarkers(requested);
        if (name.Length == 0 || name.Length > MaxSeenNameLength) return false;
        if (string.Equals(name, Alias, StringComparison.OrdinalIgnoreCase)) return false;

        var list = ParseSeen(cfg.GatewaySeenModels);
        if (list.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;

        var kept = list.Append(name).TakeLast(MaxSeenModels).ToList();
        cfg.GatewaySeenModels = string.Join(Environment.NewLine, kept);
        return true;
    }

    /// <summary>不联网取各层计数（设置页状态行）。</summary>
    public static ModelSources Snapshot(AppSettings cfg)
    {
        int official;
        bool live;
        DateTime? synced;
        lock (Gate)
        {
            official = _official.Count;
            live = _officialLive;
            synced = _officialLive ? _syncAt : null;
        }
        var overrides = Overrides(cfg);
        return new ModelSources(
            overrides.Count(o => !o.Hidden && !string.IsNullOrWhiteSpace(o.Id)),
            official, KnownIds.Length, ParseSeen(cfg.GatewaySeenModels).Count,
            overrides.Count(o => o.Hidden && !string.IsNullOrWhiteSpace(o.Id)), live, synced);
    }

    /// <summary>官网同步（第 2 层）：带缓存；失败回退内置。返回 id 列表。</summary>
    public static async Task<IReadOnlyList<string>> GetOfficialAsync(AppSettings cfg, Account? account,
        HttpClient? http = null, CancellationToken ct = default)
        => (await GetOfficialEntriesAsync(cfg, account, http, ct)).Select(e => e.Id).ToList();

    /// <summary>官网同步（第 2 层）：带缓存的条目版（含展示名/倍率/图片/档位）。</summary>
    public static async Task<IReadOnlyList<ModelEntry>> GetOfficialEntriesAsync(AppSettings cfg, Account? account,
        HttpClient? http = null, CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _syncAt < Ttl && _officialEntries.Count > 0) return _officialEntries;
        }

        var entries = await FetchOfficialEntriesAsync(cfg, account, http, ct);
        lock (Gate)
        {
            if (entries is { Count: > 0 })
            {
                _officialEntries = entries;
                _official = entries.Select(e => e.Id).ToList();
                _officialLive = true;
                _syncAt = DateTime.UtcNow;
                return _officialEntries;
            }
            // 拿不到新数据：保留上一次在线结果；从未成功过才回退内置
            return _officialLive ? _officialEntries : BuiltinEntries();
        }
    }

    /// <summary>按四层优先级聚合出最终 id 列表（去重，高优先级在前）。</summary>
    public static async Task<(IReadOnlyList<string> Ids, ModelSources Sources)> AggregateAsync(
        AppSettings cfg, Account? account, HttpClient? http = null, CancellationToken ct = default)
    {
        var (entries, sources) = await AggregateEntriesAsync(cfg, account, http, ct);
        return (entries.Select(e => e.Id).ToList(), sources);
    }

    /// <summary>按四层优先级聚合出带元数据的条目；同 id 高优先级优先，空字段由低优先级补齐。</summary>
    public static async Task<(IReadOnlyList<ModelEntry> Entries, ModelSources Sources)> AggregateEntriesAsync(
        AppSettings cfg, Account? account, HttpClient? http = null, CancellationToken ct = default)
    {
        var official = await GetOfficialEntriesAsync(cfg, account, http, ct);
        return MergeLayers(cfg, official, includeHidden: false);
    }

    /// <summary>
    /// 用当前缓存合并四层（不联网；设置页状态行与列表用）。
    /// <paramref name="includeHidden"/> 为真时把隐藏项一并列出（灰显展示，供恢复）。
    /// </summary>
    public static (IReadOnlyList<ModelEntry> Entries, ModelSources Sources) MergeCached(
        AppSettings cfg, bool includeHidden = false)
        => MergeLayers(cfg, CurrentEntries, includeHidden);

    /// <summary>该 id 是否被隐藏（「删除」的可恢复形态）。</summary>
    public static bool IsHidden(AppSettings cfg, string id)
        => Overrides(cfg).Any(o => o.Hidden && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ModelOverride> Overrides(AppSettings cfg) => cfg.ModelOverrides ?? [];

    private static (IReadOnlyList<ModelEntry>, ModelSources) MergeLayers(
        AppSettings cfg, IReadOnlyList<ModelEntry> official, bool includeHidden)
    {
        var overrides = Overrides(cfg);
        var visible = overrides.Where(o => !o.Hidden && !string.IsNullOrWhiteSpace(o.Id)).ToList();
        var hidden = overrides.Where(o => o.Hidden && !string.IsNullOrWhiteSpace(o.Id)).ToList();
        var builtin = BuiltinEntries();
        var seen = ParseSeen(cfg.GatewaySeenModels);

        var merged = new List<ModelEntry>();
        var covered = new HashSet<string>(StringComparer.Ordinal);   // 第 1 层的归一化去重
        foreach (var o in visible) AddOverride(merged, o, covered);

        MergeInto(merged, official);
        MergeInto(merged, builtin);
        foreach (var id in seen)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (merged.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            // 归一化后与已有条目相同 → 已有条目是规范写法，丢弃变体
            var norm = Normalize(id);
            if (merged.Any(x => Normalize(x.Id) == norm)) continue;
            merged.Add(new ModelEntry(id, id, null, null, null, null, null, null, "seen"));
        }

        // 隐藏项要挡住所有层：官网同步、内置、推断都带不回来
        var hiddenNorm = hidden.Select(o => Normalize(o.Id)).ToHashSet(StringComparer.Ordinal);
        if (hiddenNorm.Count > 0) merged.RemoveAll(e => hiddenNorm.Contains(Normalize(e.Id)));

        if (includeHidden)
            foreach (var o in hidden) AddOverride(merged, o, covered);

        var sources = new ModelSources(visible.Count, official.Count, builtin.Count, seen.Count,
            hidden.Count, Live: IsOfficialLive, SyncedAt: LastSyncedAt);
        return (merged, sources);
    }

    /// <summary>第 1 层的一条覆盖项 → 条目（展示名缺省留空，由 <see cref="ModelEntry.DisplayText"/> 回落到 id）。</summary>
    private static void AddOverride(List<ModelEntry> target, ModelOverride o, HashSet<string> covered)
    {
        var norm = Normalize(o.Id);
        if (norm.Length == 0 || !covered.Add(norm)) return;
        target.Add(new ModelEntry(o.Id, o.Display ?? "", o.Vendor, o.Rate, o.Efforts, o.Context,
            o.LongContext, o.Image, "custom"));
    }

    /// <summary>按 id 合并：先到者保留非空字段，后到者只补齐空缺（厂商/上下文仅内置表提供）。</summary>
    private static void MergeInto(List<ModelEntry> target, IEnumerable<ModelEntry> src)
    {
        foreach (var e in src)
        {
            if (string.IsNullOrWhiteSpace(e.Id)) continue;
            var idx = target.FindIndex(x => string.Equals(x.Id, e.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { target.Add(e); continue; }
            var cur = target[idx];
            target[idx] = cur with
            {
                Display = string.IsNullOrEmpty(cur.Display) ? e.Display : cur.Display,
                Vendor = cur.Vendor ?? e.Vendor,
                Rate = cur.Rate ?? e.Rate,
                Efforts = cur.Efforts ?? e.Efforts,
                Context = cur.Context ?? e.Context,
                LongContext = cur.LongContext ?? e.LongContext,
                Image = cur.Image ?? e.Image,
            };
        }
    }

    /// <summary>内置条目（第 3 层）：带元数据的 Curated 与只有 id 的 KnownIds 按 id 去重合并。</summary>
    public static IReadOnlyList<ModelEntry> BuiltinEntries() => _builtin.Value;

    private static readonly Lazy<IReadOnlyList<ModelEntry>> _builtin = new(() =>
    {
        var list = new List<ModelEntry>();
        MergeInto(list, Curated);
        foreach (var id in KnownIds)
            if (!list.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                list.Add(new ModelEntry(id, id, null, null, null, null, null, null, "builtin"));
        return list;
    });

    private static bool IsOfficialLive
    {
        get { lock (Gate) return _officialLive; }
    }

    // ==================== 上游同步 ====================

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

    /// <summary>
    /// 解析模型配置，依次尝试三种形态：
    ///   1) <c>function_configs[].config_info_list[]</c> 或顶层 <c>config_info_list[]</c>（Trae 模型配置）；
    ///   2) <c>model_configs</c> 的键（代码补全注册表）；
    ///   3) <c>models[].id</c>。
    /// </summary>
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

    /// <summary>
    /// 把客户端请求的模型名解析成上游可接受的 config_name：
    ///   1) 去掉尾部上下文标记（如 <c>trae[1M]</c> → <c>trae</c>）与厂商前缀（<c>openai/</c>、<c>anthropic/</c>、<c>models/</c>）；
    ///   2) 别名或空 → 默认模型；
    ///   3) 目录内同名（大小写对齐）原样放行；
    ///   4) 名称推断：归一化后相等，或归一化后被某个候选包含（如 <c>claude-sonnet-4</c> → <c>custom_claude-sonnet-4</c>）；
    ///   5) 都不中 → 默认模型（目录里没有默认项则退回首项）。
    /// </summary>
    public static string Resolve(string requested, IReadOnlyList<string> available)
    {
        var list = available is { Count: > 0 } ? available : KnownIds;
        var raw = StripMarkers(requested);
        var name = Normalize(raw);

        if (name.Length == 0 || string.Equals(name, Alias, StringComparison.OrdinalIgnoreCase))
            return PickDefault(list);

        // 先按原样匹配（保留分隔符）：避免历史存入的归一化条目抢先命中
        foreach (var id in list)
            if (string.Equals(id, raw, StringComparison.OrdinalIgnoreCase)) return id;

        var inferred = Infer(name, list);
        return inferred ?? PickDefault(list);
    }

    /// <summary>去掉上下文标记与厂商前缀（保留分隔符，保留大小写）。</summary>
    public static string StripMarkers(string? raw)
    {
        var s = (raw ?? "").Trim();
        var br = s.IndexOf('[');
        if (br > 0) s = s[..br].Trim();
        if (s.EndsWith("-1m", StringComparison.OrdinalIgnoreCase)) s = s[..^3].Trim();

        foreach (var prefix in new[] { "openai/", "anthropic/", "google/", "models/" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].TrimStart('/');
                break;
            }
        }
        return s.Trim();
    }

    /// <summary>归一化：在 <see cref="StripMarkers"/> 基础上转小写并去掉分隔符（仅供比对，不作 id 使用）。</summary>
    public static string Normalize(string? raw)
    {
        var s = StripMarkers(raw);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>名称推断：归一化相等 → 该候选；被候选包含 → 取最短的候选（更贴近原名）。</summary>
    private static string? Infer(string normalizedRequested, IReadOnlyList<string> list)
    {
        if (normalizedRequested.Length == 0) return null;

        string? contained = null;
        foreach (var id in list)
        {
            var norm = Normalize(id);
            if (norm.Length == 0) continue;
            if (norm == normalizedRequested) return id;
            if (norm.Contains(normalizedRequested, StringComparison.Ordinal) &&
                (contained == null || norm.Length < Normalize(contained).Length))
                contained = id;
        }
        return contained;
    }

    private static string PickDefault(IReadOnlyList<string> list)
    {
        foreach (var id in list)
            if (string.Equals(id, PreferredModel, StringComparison.OrdinalIgnoreCase)) return id;
        return list[0];
    }
}
