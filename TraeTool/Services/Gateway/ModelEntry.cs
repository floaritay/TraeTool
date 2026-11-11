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
