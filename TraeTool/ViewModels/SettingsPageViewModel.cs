using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Switch;
using TraeTool.Views;

namespace TraeTool.ViewModels;

/// <summary>自检结果行（环境自检卡）。</summary>
public sealed record DiagRow(string Name, string Status, string Detail);

/// <summary>
/// 设置页：行为开关 / 外观 / Token 查看 / 更新 / 设备标识重置 / 备份与恢复 / 环境自检。
/// 账号增删登录等操作仍在账号管理页（<see cref="AccountsPageViewModel"/>），这里只借用其行集合展示 Token。
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

    // ---- 更新 ----
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

    // ---- 设备标识重置 ----
    [ObservableProperty] private bool _resetClientMachineId = true;
    [ObservableProperty] private bool _resetTelemetry = true;
    [ObservableProperty] private bool _resetAccountDeviceIds = true;
    [ObservableProperty] private bool _resetMachineGuid;

    /// <summary>重置未覆盖的本机标识结构（界面列明，避免误认为已彻底重置）。</summary>
    public IReadOnlyList<string> UncoveredStructures => DeviceResetService.UncoveredStructures;

    // ---- 备份 ----
    /// <summary>导出时使用口令加密封装（跨 Windows 账户/机器可用该口令恢复）。</summary>
    [ObservableProperty]
    private bool _encryptBackup;

    /// <summary>备份口令（导出与导入共用；PasswordBox 不做绑定，由视图 code-behind 写入）。</summary>
    public string BackupPassword { get; set; } = "";

    // ---- 环境自检 ----
    public ObservableCollection<DiagRow> DiagItems { get; } = new();

    [ObservableProperty]
    private string _diagStatus = "尚未自检";

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

    partial void OnUpdateFeedUrlChanged(string value)
    {
        AppServices.Config.UpdateFeedUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        AppServices.SaveConfig();
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

    // ==================== 出站代理 ====================

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

    // ==================== 本机管理网关 ====================

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

    // ==================== 模型列表增删改 ====================

    /// <summary>「图片」字段的三选一（跟随 = 不表态，交给官网层）。</summary>
    public string[] ImageOptions { get; } = ["跟随", "支持", "不支持"];

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

    // ==================== 环境自检 ====================

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

    // ==================== 更新 ====================

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

    // ==================== Token ====================

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

    // ==================== 设备标识重置 ====================

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

    // ==================== 备份与恢复 ====================

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

        StatusMessage = "导入已暂存，正在重启…";
        AppLog.Info("backup", "用户确认导入，重启应用");
        UpdateService.RestartApp();
    }
}
