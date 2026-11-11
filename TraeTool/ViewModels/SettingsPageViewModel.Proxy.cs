using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>设置页 · 出站代理卡：三态单选（直连 / 跟随系统 / 手动），改动即刻重挂共享 HTTP 实例。</summary>
public partial class SettingsPageViewModel
{
    [ObservableProperty]
    private bool _proxyIsNone;

    [ObservableProperty]
    private bool _proxyIsSystem = true;

    [ObservableProperty]
    private bool _proxyIsManual;

    /// <summary>手动代理地址（LostFocus 提交）。</summary>
    [ObservableProperty]
    private string _proxyUrl = "";

    /// <summary>代理状态行（生效说明 + 连通性测试结果）。</summary>
    [ObservableProperty]
    private string _proxyStatus = "";

    private bool _syncingProxy;

    partial void OnProxyIsNoneChanged(bool value) => SetProxyMode(value, AppSettings.ProxyModes.None);

    partial void OnProxyIsSystemChanged(bool value) => SetProxyMode(value, AppSettings.ProxyModes.System);

    partial void OnProxyIsManualChanged(bool value) => SetProxyMode(value, AppSettings.ProxyModes.Manual);

    partial void OnProxyUrlChanged(string value)
    {
        var v = value?.Trim() ?? "";
        if (AppServices.Config.ProxyUrl == v) return;
        AppServices.Config.ProxyUrl = v.Length == 0 ? null : v;
        AppServices.SaveConfig();
        if (AppServices.Config.ProxyMode == AppSettings.ProxyModes.Manual)
        {
            AppServices.ApplyProxy(); // 地址改了立刻重挂，不要等下次点开设置页
            RefreshProxyStatus();
        }
    }

    private void SetProxyMode(bool isOn, string mode)
    {
        if (_syncingProxy || !isOn) return;
        _syncingProxy = true;
        try
        {
            ProxyIsNone = mode == AppSettings.ProxyModes.None;
            ProxyIsSystem = mode == AppSettings.ProxyModes.System;
            ProxyIsManual = mode == AppSettings.ProxyModes.Manual;
        }
        finally
        {
            _syncingProxy = false;
        }
        if (AppServices.Config.ProxyMode == mode) return;
        AppServices.Config.ProxyMode = mode;
        AppServices.SaveConfig();
        AppServices.ApplyProxy();
        RefreshProxyStatus();
    }

    private void RefreshProxyStatus() => ProxyStatus = HttpClients.Proxy.Describe();

    /// <summary>按当前配置实测出网连通性（走共享 HTTP 实例，即走已配置的代理）。</summary>
    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (Busy) return;
        Busy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await HttpClients.Default.GetAsync("https://api.trae.cn/");
            sw.Stop();
            ProxyStatus = $"{HttpClients.Proxy.Describe()} · HTTP {(int)resp.StatusCode} · {sw.ElapsedMilliseconds}ms";
        }
        catch (Exception ex)
        {
            sw.Stop();
            ProxyStatus = $"{HttpClients.Proxy.Describe()} · 测试失败（{sw.ElapsedMilliseconds}ms）：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}
