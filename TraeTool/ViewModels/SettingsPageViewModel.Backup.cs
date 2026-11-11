using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Services;
using TraeTool.Views;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace TraeTool.ViewModels;

/// <summary>设置页 · 备份与恢复卡：导出/导入整包（配置密文 + 签到库快照 + 用量缓存）。</summary>
public partial class SettingsPageViewModel
{
    /// <summary>导出时使用口令加密封装（跨 Windows 账户/机器可用该口令恢复）。</summary>
    [ObservableProperty]
    private bool _encryptBackup;

    /// <summary>备份口令（导出与导入共用；PasswordBox 不做绑定，由视图 code-behind 写入）。</summary>
    public string BackupPassword { get; set; } = "";

    /// <summary>导出备份 zip（配置密文 + 签到库快照 + 用量缓存）；勾选口令加密时整体加密封装。</summary>
    [RelayCommand]
    private void ExportBackup()
    {
        if (Busy) return;
        string? password = null;
        if (EncryptBackup)
        {
            if (string.IsNullOrEmpty(BackupPassword))
            {
                StatusMessage = "已勾选「口令加密」，请先填写备份口令";
                return;
            }
            password = BackupPassword;
        }

        var dlg = new WpfSaveFileDialog
        {
            FileName = $"TraeTool-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "TraeTool 备份 (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true) return;
        string? err;
        var path = BackupService.ExportTo(dlg.FileName, out err, password);
        StatusMessage = err != null ? "导出失败：" + err : $"已导出备份：{path}";
        AppLog.Info("backup", StatusMessage);
    }

    /// <summary>导入备份：必要时解口令 → 校验 → 危险确认 → 暂存 → 重启后覆盖生效。</summary>
    [RelayCommand]
    private void ImportBackup()
    {
        if (Busy) return;
        // 切换执行中直接退出会中断操作、损坏档案：导入要重启，先挡住
        if (AppServices.SwitchBusy)
        {
            StatusMessage = "客户端切换正在执行，稍后再导入";
            return;
        }
        var dlg = new WpfOpenFileDialog { Filter = "TraeTool 备份 (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;

        string? password = null;
        if (BackupService.IsEncryptedBackup(dlg.FileName))
        {
            if (string.IsNullOrEmpty(BackupPassword))
            {
                StatusMessage = "该备份已加密，请先填写导出时设置的口令再导入";
                return;
            }
            password = BackupPassword;
        }

        if (!BackupService.StageImport(dlg.FileName, out var error, password))
        {
            StatusMessage = "导入失败：" + error;
            return;
        }

        var go = DialogWindow.Show("导入备份",
            "已校验备份包。导入将覆盖当前的账号配置、签到记录与用量缓存（原文件自动留 .bak / .preimport）。",
            DialogKind.Danger,
            small: "覆盖在下次启动时生效——建议现在就重启程序。",
            primary: "覆盖并重启", secondary: "取消", dangerPrimary: true);
        if (!go)
        {
            try { Directory.Delete(Path.Combine(Paths.Root, "import_staging"), recursive: true); }
            catch { /* 暂存清理失败无害 */ }
            StatusMessage = "已取消导入（暂存已清除）";
            return;
        }

        // 确认对话框期间档案保鲜可能已开始：暂存保留，下次启动自然生效，不在此刻杀进程
        if (AppServices.SwitchBusy)
        {
            StatusMessage = "切换执行中，导入已暂存，将在下次启动生效";
            return;
        }

        StatusMessage = "导入已暂存，正在重启…";
        AppLog.Info("backup", "用户确认导入，重启应用");
        UpdateService.RestartApp();
    }
}
