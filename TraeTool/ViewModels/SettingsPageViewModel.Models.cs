using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Gateway;
using TraeTool.Views;

namespace TraeTool.ViewModels;

/// <summary>
/// 设置页 · 模型列表卡：四层来源合并后的表格，以及条目的增删改（写入 AppSettings.ModelOverrides）。
/// </summary>
public partial class SettingsPageViewModel
{
    /// <summary>是否把隐藏（删除过）的模型一并列出，供恢复。</summary>
    [ObservableProperty]
    private bool _showHiddenModels;

    /// <summary>模型列表状态行：四层计数、合计与最近同步时间。</summary>
    [ObservableProperty]
    private string _modelListStatus = "";

    /// <summary>合并后的模型行（含元数据与编辑态，卡片表格展示）。</summary>
    public ObservableCollection<ModelRowViewModel> ModelEntries { get; } = new();

    /// <summary>列表是否为空（空态提示用；绑定到 Count 不会随集合变化更新）。</summary>
    [ObservableProperty]
    private bool _modelListEmpty;

    /// <summary>「图片」字段的三选一（跟随 = 不表态，交给官网层）。</summary>
    public string[] ImageOptions { get; } = ["跟随", "支持", "不支持"];

    partial void OnShowHiddenModelsChanged(bool value) => RefreshModelListStatus();

    /// <summary>重新同步：清缓存后强制拉取官网源，重建四层列表。</summary>
    [RelayCommand]
    private async Task UpdateModelListAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            ModelCatalog.Reset();
            var account = AppServices.Config.Accounts
                .FirstOrDefault(a => a.Enabled && !string.IsNullOrEmpty(a.Token));
            var (ids, src) = await ModelCatalog.AggregateAsync(AppServices.Config, account);
            AppServices.SaveConfig(); // 本轮可能累积了新的请求模型名
            RefreshModelListStatus();
            AppLog.Info("pool", "模型列表已更新：" + ModelListStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("pool", "更新模型列表失败: " + ex.Message);
            ModelListStatus = "更新失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>按当前缓存重算状态行与表格（不联网）。</summary>
    private void RefreshModelListStatus()
    {
        // ObservableCollection 只能在 UI 线程改：后台同步完成后会回到这里
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshModelListStatus);
            return;
        }

        var cfg = AppServices.Config;
        var (entries, src) = ModelCatalog.MergeCached(cfg, includeHidden: ShowHiddenModels);

        // 编辑态在刷新后要保留：只重建行对象，行内的编辑态按 id 恢复；
        // 还没保存的自建行（草稿）不在合并结果里，原样挂回列表尾部
        var editing = ModelEntries.Where(r => r.IsEditing && !r.IsDraft).Select(r => r.IdText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drafts = ModelEntries.Where(r => r.IsDraft).ToList();

        ModelEntries.Clear();
        foreach (var e in entries)
        {
            var row = new ModelRowViewModel(e, ModelCatalog.IsHidden(cfg, e.Id));
            if (editing.Contains(e.Id)) row.IsEditing = true;
            ModelEntries.Add(row);
        }
        foreach (var d in drafts) ModelEntries.Add(d);
        ModelListEmpty = ModelEntries.Count == 0;
        ModelListStatus = BuildModelListStatus(entries.Count, src);
    }

    private static string BuildModelListStatus(int total, ModelSources src)
    {
        // 未在线同步时官网层用的是内置兜底，不能显示成「官网同步 N」
        var official = src.Live ? src.Official.ToString() : "未拉取";
        var sync = src.SyncedAt is { } t
            ? $"在线同步于 {t.ToLocalTime():HH:mm:ss}"
            : "未同步，点「更新模型列表」拉取";
        var hidden = src.Hidden > 0 ? $" · 隐藏 {src.Hidden}" : "";
        return $"共 {total} 个 · 人工 {src.Manual} · 官网同步 {official} · 内置 {src.Builtin} · 推断 {src.Inferred}{hidden} · {sync}";
    }

    /// <summary>追加一条空白自建行并展开编辑表单。</summary>
    [RelayCommand]
    private void AddModel()
    {
        var draft = new ModelRowViewModel(
            new ModelEntry("", "", null, null, null, null, null, null, "custom"), isHidden: false, isDraft: true)
        {
            IsEditing = true,
        };
        ModelEntries.Add(draft);
        ModelListEmpty = false;
    }

    /// <summary>展开该行的编辑表单。</summary>
    [RelayCommand]
    private void EditModel(ModelRowViewModel? row)
    {
        if (row != null) row.IsEditing = !row.IsEditing;
    }

    /// <summary>收起编辑表单（草稿行直接丢弃）。</summary>
    [RelayCommand]
    private void CancelModelEdit(ModelRowViewModel? row)
    {
        if (row == null) return;
        if (row.IsDraft) ModelEntries.Remove(row);
        else row.IsEditing = false;
        ModelListEmpty = ModelEntries.Count == 0;
    }

    /// <summary>保存编辑：写入（或新建）对应的覆盖项。字段留空 = 交回官网 / 内置层补齐。</summary>
    [RelayCommand]
    private void SaveModel(ModelRowViewModel? row)
    {
        if (row == null) return;
        var id = (row.IsDraft ? row.EditId : row.IdText).Trim();
        if (id.Length == 0)
        {
            StatusMessage = "模型 ID 不能为空";
            return;
        }

        var over = EnsureOverride(id);
        over.Display = Blank(row.EditDisplay);
        over.Vendor = Blank(row.EditVendor);
        over.Rate = Blank(row.EditRate);
        over.Efforts = Blank(row.EditEfforts);
        over.Context = Blank(row.EditContext);
        over.LongContext = Blank(row.EditLongContext);
        over.Image = row.EditImage switch
        {
            "支持" => true,
            "不支持" => false,
            _ => null,
        };
        over.Hidden = false;

        if (row.IsDraft) ModelEntries.Remove(row);
        else row.IsEditing = false;

        AppServices.SaveConfig();
        RefreshModelListStatus();
        StatusMessage = $"已保存模型「{id}」";
    }

    /// <summary>隐藏该模型：官网同步 / 内置 / 推断都带不回来，勾选「显示已隐藏」后可恢复。</summary>
    [RelayCommand]
    private void HideModel(ModelRowViewModel? row)
    {
        if (row == null) return;
        if (!DialogWindow.Show("隐藏模型",
                $"确定从模型列表移除「{row.DisplayText}」？\n" +
                "该模型不再出现在 /v1/models，官网同步也带不回来；勾选「显示已隐藏」可一键恢复。",
                DialogKind.Danger, dangerPrimary: true))
            return;

        EnsureOverride(row.IdText).Hidden = true;
        AppServices.SaveConfig();
        RefreshModelListStatus();
        StatusMessage = $"已隐藏「{row.DisplayText}」";
    }

    /// <summary>恢复被隐藏的模型。</summary>
    [RelayCommand]
    private void RestoreModel(ModelRowViewModel? row)
    {
        if (row == null) return;
        var over = FindOverride(row.IdText);
        // 只有隐藏、别的字段都没改过的覆盖项，恢复后整条删掉，让官网层接管
        if (over == null || !over.Hidden) return;
        if (IsEmpty(over)) AppServices.Config.ModelOverrides.Remove(over);
        else over.Hidden = false;

        AppServices.SaveConfig();
        RefreshModelListStatus();
        StatusMessage = $"已恢复「{row.DisplayText}」";
    }

    /// <summary>删掉该模型的自建覆盖项（官网 / 内置层有同名条目时，显示会回到上游的值）。</summary>
    [RelayCommand]
    private void RemoveModelOverride(ModelRowViewModel? row)
    {
        if (row == null) return;
        var over = FindOverride(row.IdText);
        if (over == null) return;
        if (!DialogWindow.Show("删除模型",
                $"确定删除「{row.DisplayText}」的本地设置？\n" +
                "若要让它彻底不出现在列表里，请改用「隐藏」。",
                DialogKind.Danger, dangerPrimary: true))
            return;

        AppServices.Config.ModelOverrides.Remove(over);
        AppServices.SaveConfig();
        RefreshModelListStatus();
        StatusMessage = $"已删除「{row.DisplayText}」的本地设置";
    }

    private ModelOverride? FindOverride(string id)
        => AppServices.Config.ModelOverrides
            .FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    private ModelOverride EnsureOverride(string id)
    {
        var list = AppServices.Config.ModelOverrides;
        var over = FindOverride(id);
        if (over != null) return over;
        over = new ModelOverride { Id = id };
        list.Add(over);
        return over;
    }

    /// <summary>没有填任何字段、也没隐藏的覆盖项（恢复时直接丢弃，不留空壳）。</summary>
    private static bool IsEmpty(ModelOverride o)
        => !o.Hidden && string.IsNullOrEmpty(o.Display) && string.IsNullOrEmpty(o.Vendor) &&
           string.IsNullOrEmpty(o.Rate) && string.IsNullOrEmpty(o.Efforts) &&
           string.IsNullOrEmpty(o.Context) && string.IsNullOrEmpty(o.LongContext) && o.Image == null;

    private static string? Blank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>打开设置页时若还没同步过，后台拉一次官网源（30 分钟缓存，不会每次点开都请求）。</summary>
    public void EnsureModelListSynced()
    {
        if (ModelCatalog.LastSyncedAt != null || Busy) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var account = AppServices.Config.Accounts
                    .FirstOrDefault(a => a.Enabled && !string.IsNullOrEmpty(a.Token));
                await ModelCatalog.AggregateAsync(AppServices.Config, account);
                RefreshModelListStatus();
            }
            catch (Exception ex)
            {
                AppLog.Info("pool", "打开设置页时同步模型列表失败：" + ex.Message);
                RefreshModelListStatus();
            }
        });
    }
}
