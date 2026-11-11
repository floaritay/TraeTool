using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>
/// 设置页：行为开关 / 外观 / Token 查看 / 更新 / 设备标识重置 / 备份与恢复 / 环境自检。
/// 账号增删登录等操作仍在账号管理页（<see cref="AccountsPageViewModel"/>），这里只借用其行集合展示 Token。
///
/// 每张设置卡一个文件：出站代理见 SettingsPageViewModel.Proxy.cs、本机网关见
/// SettingsPageViewModel.Gateway.cs、模型列表见 SettingsPageViewModel.Models.cs、
/// 设备标识见 SettingsPageViewModel.Device.cs、备份见 SettingsPageViewModel.Backup.cs、
/// 更新见 SettingsPageViewModel.Update.cs、环境自检见 SettingsPageViewModel.Diagnostics.cs。
/// </summary>
public partial class SettingsPageViewModel : ObservableObject
{
    /// <summary>账号 VM：Token 卡复用其行集合，避免再维护一份账号列表。</summary>
    public AccountsPageViewModel Accounts { get; }

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _busy;

    // ---- 行为设置 ----
    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _useClientDeviceId;

    /// <summary>深色主题开关（true = dark）。</summary>
    [ObservableProperty]
    private bool _themeDark;

    public SettingsPageViewModel(AccountsPageViewModel accounts)
    {
        Accounts = accounts;
        _startWithWindows = AppServices.Config.StartWithWindows;
        _closeToTray = AppServices.Config.CloseToTray;
        _useClientDeviceId = AppServices.Config.DeviceIdMode == AppSettings.DeviceIdModes.FromTraeClient;
        _themeDark = string.Equals(AppServices.Config.Theme, "dark", StringComparison.OrdinalIgnoreCase);
        _updateFeedUrl = AppServices.Config.UpdateFeedUrl ?? "";
        _updateStatus = $"当前版本 {UpdateService.CurrentVersionText}";

        // 代理三态单选：先置同步位再回填，避免回填触发写回配置
        var proxy = ProxyConfig.From(AppServices.Config);
        _syncingProxy = true;
        try
        {
            _proxyIsNone = proxy.Mode == AppSettings.ProxyModes.None;
            _proxyIsSystem = proxy.Mode == AppSettings.ProxyModes.System;
            _proxyIsManual = proxy.Mode == AppSettings.ProxyModes.Manual;
            _proxyUrl = AppServices.Config.ProxyUrl ?? "";
        }
        finally
        {
            _syncingProxy = false;
        }
        RefreshProxyStatus();

        _gatewayEnabled = AppServices.Config.GatewayEnabled;
        _gatewayPortText = AppServices.Config.GatewayPort.ToString();
        _gatewayChatEnabled = AppServices.Config.GatewayChatEnabled;
        RefreshGatewayStatus();
        RefreshModelListStatus();
    }

    partial void OnThemeDarkChanged(bool value)
    {
        AppServices.Config.Theme = value ? "dark" : "light";
        AppServices.SaveConfig();
        ThemeService.Apply(AppServices.Config.Theme);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        AppServices.Config.StartWithWindows = value;
        AppServices.SaveConfig();
        try { AutoStartManager.SetEnabled(value); }
        catch { StatusMessage = "设置开机自启失败（注册表写入被拒绝）"; }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        AppServices.Config.CloseToTray = value;
        AppServices.SaveConfig();
    }

    partial void OnUseClientDeviceIdChanged(bool value)
    {
        AppServices.Config.DeviceIdMode = value
            ? AppSettings.DeviceIdModes.FromTraeClient
            : AppSettings.DeviceIdModes.Random;
        AppServices.SaveConfig();
    }

    // ==================== 复制到剪贴板 ====================

    /// <summary>复制任意一段网关文本（端点、base_url）到剪贴板。</summary>
    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetDataObject(text);
            StatusMessage = "已复制：" + text;
        }
        catch (Exception ex)
        {
            StatusMessage = "复制失败：" + ex.Message;
        }
    }

    /// <summary>复制该账号完整 Token 到剪贴板（Token 卡）。</summary>
    [RelayCommand]
    private void CopyToken(AccountRowViewModel? row)
    {
        if (row == null || string.IsNullOrEmpty(row.Account.Token))
        {
            StatusMessage = "该账号未登录，没有可复制的 Token";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetDataObject(row.Account.Token);
            StatusMessage = $"已复制「{row.DisplayName}」的 Token";
        }
        catch (Exception ex)
        {
            StatusMessage = "复制失败：" + ex.Message;
        }
    }
}
