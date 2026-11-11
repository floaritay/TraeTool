using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>设置页 · 更新卡：检查更新源、下载并交给等待脚本覆盖安装。</summary>
public partial class SettingsPageViewModel
{
    /// <summary>更新源 URL（GitHub Releases API 或 version.json）。</summary>
    [ObservableProperty]
    private string _updateFeedUrl = "";

    /// <summary>更新状态行。</summary>
    [ObservableProperty]
    private string _updateStatus = "";

    /// <summary>发现新版本（安装按钮可见性）。</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    private UpdateInfo? _pendingUpdate;

    partial void OnUpdateFeedUrlChanged(string value)
    {
        AppServices.Config.UpdateFeedUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        AppServices.SaveConfig();
    }

    /// <summary>检查更新（更新源见 UpdateFeedUrl）。</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (Busy) return;
        Busy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            var r = await UpdateService.CheckAsync(UpdateFeedUrl);
            if (r.Error != null)
            {
                UpdateStatus = r.Error;
                UpdateAvailable = false;
                _pendingUpdate = null;
            }
            else if (r.UpToDate)
            {
                UpdateStatus = $"已是最新版本（{UpdateService.CurrentVersionText}）";
                UpdateAvailable = false;
                _pendingUpdate = null;
            }
            else
            {
                _pendingUpdate = r.Update;
                UpdateAvailable = true;
                var notes = string.IsNullOrWhiteSpace(r.Update!.Notes) ? "" : " · " + r.Update.Notes.Trim();
                UpdateStatus = $"发现新版本 v{r.Update.Version}（当前 {UpdateService.CurrentVersionText}）{notes}";
                AppLog.Info("update", $"发现新版本 v{r.Update.Version}");
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>下载更新并重启安装（等待脚本接管，见 UpdateService）。</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate == null || Busy) return;
        Busy = true;
        try
        {
            var (staging, error) = await UpdateService.DownloadAndStageAsync(_pendingUpdate);
            if (error != null || staging == null)
            {
                UpdateStatus = error ?? "更新失败";
                return;
            }
            UpdateStatus = "已下载，重启安装中…";
            AppLog.Info("update", "staging ready: " + staging);
            UpdateService.ApplyAndRestart(staging); // 不会再返回
        }
        finally
        {
            Busy = false;
        }
    }
}
