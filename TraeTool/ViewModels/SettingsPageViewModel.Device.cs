using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Services;
using TraeTool.Services.Switch;
using TraeTool.Views;

namespace TraeTool.ViewModels;

/// <summary>设置页 · 设备标识重置卡：四类标识按勾选重置，重置前关客户端并备份原文件。</summary>
public partial class SettingsPageViewModel
{
    [ObservableProperty] private bool _resetClientMachineId = true;
    [ObservableProperty] private bool _resetTelemetry = true;
    [ObservableProperty] private bool _resetAccountDeviceIds = true;
    [ObservableProperty] private bool _resetMachineGuid;

    /// <summary>重置未覆盖的本机标识结构（界面列明，避免误认为已彻底重置）。</summary>
    public IReadOnlyList<string> UncoveredStructures => DeviceResetService.UncoveredStructures;

    /// <summary>设备标识重置：关客户端 → 危险确认 → 备份后重置所勾选项。</summary>
    [RelayCommand]
    private async Task ResetDeviceIdentifiersAsync()
    {
        if (Busy) return;
        if (!ResetClientMachineId && !ResetTelemetry && !ResetAccountDeviceIds && !ResetMachineGuid)
        {
            StatusMessage = "请至少勾选一项要重置的标识";
            return;
        }

        if (ClientControl.IsRunning())
        {
            var close = DialogWindow.Show("需要关闭客户端",
                "重置设备标识需要先关闭 Trae 客户端（文件被占用）。是否自动关闭？",
                DialogKind.Warn, primary: "关闭并继续", secondary: "取消");
            if (!close) return;
            Busy = true;
            try
            {
                if (!await ClientControl.KillAllAsync())
                {
                    StatusMessage = "客户端无法关闭，已中止";
                    return;
                }
            }
            finally
            {
                Busy = false;
            }
        }

        var items = new List<string>();
        if (ResetClientMachineId) items.Add("machineid");
        if (ResetTelemetry) items.Add("storage.json 遥测三项");
        if (ResetAccountDeviceIds) items.Add("全部账号 x-device-id");
        if (ResetMachineGuid) items.Add("Windows MachineGuid（系统级）");
        var go = DialogWindow.Show("重置设备标识",
            "将执行：" + string.Join("、", items) + "\n原文件会先备份到 %TEMP%\\traedevreset_*。",
            DialogKind.Warn,
            small: ResetMachineGuid
                ? "MachineGuid 影响系统与部分软件的设备识别，且需要管理员权限；下列未覆盖结构不会被改动。"
                : "客户端下次启动即使用新标识；下列未覆盖结构不会被改动。",
            primary: "开始重置", secondary: "取消");
        if (!go) return;

        Busy = true;
        try
        {
            var log = new Progress<string>(m => StatusMessage = m);
            var results = await DeviceResetService.ResetClientAsync(
                ResetClientMachineId, ResetTelemetry, ResetMachineGuid, log);

            if (ResetAccountDeviceIds)
            {
                var used = new HashSet<string?>();
                foreach (var a in AppServices.Config.Accounts)
                {
                    a.DeviceId = DeviceIdFactory.GenerateUnique(used);
                    used.Add(a.DeviceId);
                }
                AppServices.SaveConfig();
                results.Add(new DeviceResetItem("账号设备号", true,
                    AppServices.Config.Accounts.Count + " 个账号已重新生成"));
            }

            var ok = results.Where(r => r.Ok).Select(r => r.Item);
            var bad = results.Where(r => !r.Ok).Select(r => r.Item + "(" + r.Detail + ")");
            StatusMessage = "重置完成：" + string.Join("、", ok)
                            + (bad.Any() ? "；失败：" + string.Join("、", bad) : "");
            AppLog.Info("devreset", StatusMessage);
            Accounts.Rebuild(); // 设备号尾号展示刷新
        }
        catch (Exception ex)
        {
            AppLog.Error("devreset", "重置异常: " + ex);
            StatusMessage = "重置失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
