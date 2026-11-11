using System.Net.Http;
using System.Text;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

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
/// 分层合并见 ModelCatalog.Layers.cs，内置表见 ModelCatalog.Builtin.cs，官网拉取见 ModelCatalog.Sync.cs。
/// </summary>
public static partial class ModelCatalog
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

    private static bool IsOfficialLive
    {
        get { lock (Gate) return _officialLive; }
    }

    // ==================== 名称解析 ====================

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
