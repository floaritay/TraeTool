using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 四层来源的合并：人工维护（覆盖项）→ 官网同步 → 内置参考 → 名称推断，
/// 同 id 高优先级优先、空字段由低优先级补齐，隐藏项从所有层剔除。
/// </summary>
public static partial class ModelCatalog
{
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
}
