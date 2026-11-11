using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>自检结果行（环境自检卡）。</summary>
public sealed record DiagRow(string Name, string Status, string Detail);

/// <summary>设置页 · 环境自检卡：逐项检查运行环境（只读）。</summary>
public partial class SettingsPageViewModel
{
    public ObservableCollection<DiagRow> DiagItems { get; } = new();

    [ObservableProperty]
    private string _diagStatus = "尚未自检";

    /// <summary>逐项自检运行环境（只读，不改动状态）。</summary>
    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (Busy) return;
        Busy = true;
        DiagItems.Clear();
        DiagStatus = "正在自检…";
        try
        {
            var log = new Progress<string>(m => DiagStatus = m);
            var items = await DiagnosticsService.RunAsync(AppServices.Config, log, AppServices.Gateway);
            foreach (var it in items) DiagItems.Add(new DiagRow(it.Name, it.Status, it.Detail));
            int bad = items.Count(i => i.Status == "fail");
            int warn = items.Count(i => i.Status == "warn");
            DiagStatus = bad > 0
                ? $"自检完成：{bad} 项失败、{warn} 项警告"
                : warn > 0 ? $"自检完成：{warn} 项警告，其余正常" : "自检完成：全部正常";
            AppLog.Info("diag", DiagStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("diag", "自检异常: " + ex);
            DiagStatus = "自检异常：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
