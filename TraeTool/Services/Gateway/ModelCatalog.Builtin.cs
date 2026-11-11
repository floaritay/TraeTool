namespace TraeTool.Services.Gateway;

/// <summary>第 3 层：内置参考表（观测到的模型配置名与元数据），官网不可用时作为兜底列表。</summary>
public static partial class ModelCatalog
{
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
}
