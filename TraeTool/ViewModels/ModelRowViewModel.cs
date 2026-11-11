using CommunityToolkit.Mvvm.ComponentModel;
using TraeTool.Services;
using TraeTool.Services.Gateway;

namespace TraeTool.ViewModels;

/// <summary>
/// 模型列表行：承载表格展示值（与 <see cref="ModelEntry"/> 同名属性，表格绑定可原样复用），
/// 以及「编辑」展开表单里的草稿值。落盘由页面 VM 的保存命令统一处理，行本身不碰配置。
/// </summary>
public partial class ModelRowViewModel : ObservableObject
{
    public ModelRowViewModel(ModelEntry entry, bool isHidden, bool isDraft = false)
    {
        Entry = entry;
        IsDraft = isDraft;
        _isHidden = isHidden;
        _editId = isDraft ? "" : entry.Id;
        _editDisplay = entry.Display;
        _editVendor = entry.Vendor ?? "";
        _editRate = entry.Rate ?? "";
        _editEfforts = entry.Efforts ?? "";
        _editContext = entry.Context ?? "";
        _editLongContext = entry.LongContext ?? "";
        _editImage = ImageText(entry.Image);
    }

    /// <summary>合并后的条目（展示值来源）。</summary>
    public ModelEntry Entry { get; }

    /// <summary>自建行：还没落盘的草稿，保存后才进列表。</summary>
    public bool IsDraft { get; }

    /// <summary>有覆盖项（可以「删除」掉这条覆盖）。</summary>
    public bool IsCustom => Entry.Source == "custom";

    /// <summary>是否在编辑态（该行下方展开表单）。</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>是否被隐藏（勾选「显示已隐藏」时才出现，可恢复）。</summary>
    [ObservableProperty]
    private bool _isHidden;

    /// <summary>表格「模型 ID」列：草稿行显示正在填的 id。</summary>
    public string IdText => IsDraft ? EditId : Entry.Id;

    public string DisplayText => IsDraft ? EditId : Entry.DisplayText;

    public string VendorText => IsDraft ? "—" : Entry.VendorText;

    public string RateText => IsDraft ? "—" : Entry.RateText;

    public string EffortsText => IsDraft ? "—" : Entry.EffortsText;

    public string ContextText => IsDraft ? "—" : string.IsNullOrEmpty(Entry.Context) ? "—" : Entry.Context!;

    public string LongContextText => IsDraft ? "—" : Entry.LongContextText;

    public string ImageCellText => IsDraft ? "—" : Entry.ImageText;

    // ==================== 编辑表单字段（保存时才落盘） ====================

    [ObservableProperty]
    private string _editId = "";

    [ObservableProperty]
    private string _editDisplay = "";

    [ObservableProperty]
    private string _editVendor = "";

    [ObservableProperty]
    private string _editRate = "";

    [ObservableProperty]
    private string _editEfforts = "";

    [ObservableProperty]
    private string _editContext = "";

    [ObservableProperty]
    private string _editLongContext = "";

    /// <summary>图片支持：三选一（跟随 / 支持 / 不支持）。</summary>
    [ObservableProperty]
    private string _editImage = "跟随";

    partial void OnEditIdChanged(string value)
    {
        if (IsDraft) OnPropertyChanged(nameof(IdText));
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value || !IsDraft) return;
        OnPropertyChanged(nameof(IdText));
    }

    /// <summary>图片三态 → 表格文案。</summary>
    private static string ImageText(bool? image) => image switch
    {
        true => "支持",
        false => "不支持",
        _ => "跟随",
    };
}
