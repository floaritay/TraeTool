using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;

namespace TraeTool.ViewModels;

/// <summary>设置页 · 本机管理网关卡：开关、端口、令牌与 OpenAI base_url 展示。</summary>
public partial class SettingsPageViewModel
{
    [ObservableProperty]
    private bool _gatewayEnabled;

    [ObservableProperty]
    private string _gatewayPortText = "";

    [ObservableProperty]
    private string _gatewayStatus = "";

    [ObservableProperty]
    private string _gatewayTokenMasked = "尚未生成";

    /// <summary>网关基址（配置值，启动与否都按配置展示）。</summary>
    [ObservableProperty]
    private string _gatewayEndpoint = "";

    /// <summary>启用 /v1/* 对话转发（在账号池间取号调上游）。</summary>
    [ObservableProperty]
    private bool _gatewayChatEnabled;

    /// <summary>OpenAI 客户端要填的 base_url（随端口变化）。</summary>
    [ObservableProperty]
    private string _gatewayOpenAiUrl = "";

    partial void OnGatewayEnabledChanged(bool value)
    {
        AppServices.Config.GatewayEnabled = value;
        AppServices.SaveConfig();
        AppServices.SyncGateway();
        RefreshGatewayStatus();
    }

    partial void OnGatewayPortTextChanged(string value)
    {
        // 留空或非法 = 用默认端口（用户可先填端口再开开关）
        if (!int.TryParse(value?.Trim(), out var port)) port = AppSettings.DefaultGatewayPort;
        port = Math.Clamp(port, 1024, 65535);
        if (AppServices.Config.GatewayPort == port) return;
        AppServices.Config.GatewayPort = port;
        AppServices.SaveConfig();
        AppServices.SyncGateway(); // 换端口即重启监听
        RefreshGatewayStatus();
        if (GatewayPortText != port.ToString()) GatewayPortText = port.ToString(); // 空/非法 → 显示解析后的端口
    }

    partial void OnGatewayChatEnabledChanged(bool value)
    {
        AppServices.Config.GatewayChatEnabled = value;
        AppServices.SaveConfig(); // 每次请求读配置，无需重启网关
        RefreshGatewayStatus();
    }

    private void RefreshGatewayStatus()
    {
        var cfg = AppServices.Config;
        var gw = AppServices.Gateway;
        GatewayEndpoint = $"http://127.0.0.1:{cfg.GatewayPort}/";
        GatewayOpenAiUrl = $"http://127.0.0.1:{cfg.GatewayPort}/v1";
        if (gw.IsRunning && gw.Port == cfg.GatewayPort)
        {
            GatewayStatus = cfg.GatewayChatEnabled
                ? "运行中 · 对话转发已开启 · 只监听本机"
                : "运行中 · 只监听本机";
            var t = cfg.GatewayToken ?? "";
            GatewayTokenMasked = t.Length <= 8 ? t : t[..4] + "…" + t[^4..];
        }
        else if (AppServices.Config.GatewayEnabled)
        {
            GatewayStatus = "启动失败：" + (gw.LastError ?? "未知错误");
        }
        else
        {
            GatewayStatus = "未启用";
            GatewayTokenMasked = string.IsNullOrEmpty(cfg.GatewayToken) ? "尚未生成" : "已停用（数据未清除）";
        }
    }

    /// <summary>复制网关令牌到剪贴板（本机脚本调用需带它）。</summary>
    [RelayCommand]
    private void CopyGatewayToken()
    {
        var t = AppServices.Config.GatewayToken;
        if (string.IsNullOrEmpty(t))
        {
            StatusMessage = "令牌尚未生成——先启用网关";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetDataObject(t);
            StatusMessage = "已复制网关令牌";
        }
        catch (Exception ex)
        {
            StatusMessage = "复制失败：" + ex.Message;
        }
    }
}
