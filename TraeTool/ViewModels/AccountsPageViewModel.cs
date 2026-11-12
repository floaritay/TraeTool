using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTool.Models;
using TraeTool.Services;
using TraeTool.Services.Switch;
using TraeTool.Views;

namespace TraeTool.ViewModels;

/// <summary>账号管理页：账号增删/登录/分组筛选/状态刷新与凭据导入。</summary>
public partial class AccountsPageViewModel : ObservableObject
{
    public ObservableCollection<AccountRowViewModel> Rows { get; } = new();

    /// <summary>分组筛选下拉项（Id: ""=全部 / "-unassigned"=未分组 / 其余=组名）。</summary>
    public sealed record GroupChoice(string Id, string Name);

    public ObservableCollection<GroupChoice> GroupChoices { get; } = new();

    /// <summary>当前分组筛选（"" = 全部——必须初始化为 ""，否则 SelectedValue=null 下拉显示空白）。</summary>
    [ObservableProperty]
    private string? _groupFilter = "";

    private bool _rebuilding;

    partial void OnGroupFilterChanged(string? value)
    {
        if (_rebuilding) return; // 集合重建期间 ComboBox 的回推不算用户选择
        Rebuild();
    }

    /// <summary>列表因分组筛选而空（区别于"一个账号都没有"，空态文案不同）。</summary>
    [ObservableProperty]
    private bool _showFilteredEmpty;

    /// <summary>一个账号都没有（显示添加引导空态）。</summary>
    [ObservableProperty]
    private bool _showNoAccounts;

    [ObservableProperty]
    private AccountRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>页头副行：N 个账号 · M 个参与签到。</summary>
    [ObservableProperty]
    private string _metaText = "";

    [ObservableProperty]
    private bool _busy;

    public AccountsPageViewModel()
    {
        Rebuild();
    }

    /// <summary>
    /// 从本机 Trae 客户端导入登录态（只读提取 → 会话换 Token → 按 UID 查重：
    /// 已存在则只更新该账号凭证，否则新建账号并走资料回填）。
    /// </summary>
    [RelayCommand]
    private async Task ImportClientCredentialsAsync()
    {
        if (Busy) return;
        Busy = true;
        StatusMessage = "正在只读扫描本机客户端数据…";
        try
        {
            var probe = await LocalClientImporter.ProbeAsync();
            if (!probe.Ok)
            {
                StatusMessage = probe.Message;
                return;
            }

            // 只有会话时用它换 JWT（与登录态续期同一接口）
            var token = probe.Token;
            if (token == null)
            {
                token = await TraeApiClient.GetUserTokenAsync(probe.Session!);
                if (token == null)
                {
                    StatusMessage = "已读取到客户端会话，但换取 Token 失败（会话可能已过期 ~14 天）——" +
                                    "请先在客户端里保持登录后重试，或改用「登录」按钮。";
                    return;
                }
            }

            // 资料 → UID → 查重
            var (screen, _mobile, avatar, uid) =
                await TraeApiClient.GetUserProfileAsync(token, probe.Session);

            if (!string.IsNullOrEmpty(uid))
            {
                var dup = AppServices.Config.Accounts.FirstOrDefault(a => a.AccountUid == uid);
                if (dup != null)
                {
                    dup.Token = token;
                    if (!string.IsNullOrEmpty(probe.Session)) dup.Session = probe.Session;
                    dup.TokenUpdatedAt = DateTime.Now;
                    if (string.IsNullOrEmpty(dup.ScreenName) && !string.IsNullOrEmpty(screen))
                        dup.ScreenName = screen;
                    if (string.IsNullOrEmpty(dup.AvatarUrl) && !string.IsNullOrEmpty(avatar))
                        dup.AvatarUrl = avatar;
                    AppServices.SaveConfig();
                    AppServices.RaiseAccountsChanged();
                    StatusMessage =
                        $"已从本机客户端更新「{dup.DisplayName(0)}」的登录态（来源：{probe.Source}）";
                    AppLog.Info("import", StatusMessage);
                    return;
                }
            }

            // 新账号：设备号优先用客户端 dc id（格式合法且未被占用）
            var acc = new Account { Token = token, Session = probe.Session };
            if (probe.DeviceId is { } clientDeviceId && DeviceIdFactory.IsValid(clientDeviceId) &&
                AppServices.Config.Accounts.All(a => a.DeviceId != clientDeviceId))
                acc.DeviceId = clientDeviceId;

            lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.Add(acc);
            AppServices.Config.ActiveAccountId = acc.Id;
            AppServices.SaveConfig();
            FinalizeNewAccount(acc); // 设备号兜底 + 资料回填 + UID 二次查重
            StatusMessage =
                $"已从本机客户端导入账号「{acc.DisplayName(0)}」（来源：{probe.Source}）";
            AppLog.Info("import", StatusMessage);
        }
        catch (Exception ex)
        {
            AppLog.Error("import", "客户端导入异常: " + ex);
            StatusMessage = "导入失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
            AppServices.RaiseAccountsChanged();
        }
    }

    /// <summary>按配置重建行集合（启动与 AccountsChanged 时调用）。</summary>
    public void Rebuild()
    {
        void Apply()
        {
            if (_rebuilding) return; // 防重入：下拉在清空/回填集合时会把 SelectedValue 回推成 null，
                                     // 触发 OnGroupFilterChanged 再进 Rebuild——不拦会双份选项+过滤被清
            _rebuilding = true;
            try
            {
                var activeId = AppServices.Config.ActiveAccountId;
                var selectedId = SelectedRow?.Id;
                var keepFilter = GroupFilter;
                int n = AppServices.Config.Accounts.Count;
                int en = AppServices.Config.Accounts.Count(a => a.Enabled);
                MetaText = n == 0 ? "尚未添加账号" : $"{n} 个账号 · {en} 个参与签到";
                ShowNoAccounts = n == 0;

                // 分组筛选下拉（全部 / 未分组 / 各组名）
                GroupChoices.Clear();
                GroupChoices.Add(new GroupChoice("", "全部账号"));
                GroupChoices.Add(new GroupChoice("-unassigned", "未分组"));
                foreach (var g in AppServices.Config.Accounts
                             .Where(a => !string.IsNullOrEmpty(a.Group))
                             .Select(a => a.Group!)
                             .Distinct().OrderBy(g => g))
                    GroupChoices.Add(new GroupChoice(g, g));
                // 组被删空 → 回到全部；集合重建期间被回推成 null → 恢复原选择
                //（恢复走属性：改值+广播；此时 _rebuilding=true，OnGroupFilterChanged 不会再入）
                if (GroupFilter != null && GroupChoices.All(c => c.Id != GroupFilter))
                    GroupFilter = "";
                else if (GroupFilter == null)
                    GroupFilter = keepFilter;

                Rows.Clear();
                for (int i = 0; i < AppServices.Config.Accounts.Count; i++)
                {
                    var acc = AppServices.Config.Accounts[i];
                    // 应用分组筛选
                    if (!string.IsNullOrEmpty(GroupFilter))
                    {
                        if (GroupFilter == "-unassigned"
                            ? !string.IsNullOrEmpty(acc.Group)
                            : acc.Group != GroupFilter)
                            continue;
                    }
                    var row = new AccountRowViewModel(acc, i)
                    {
                        IsCurrent = acc.Id == activeId,
                    };
                    Rows.Add(row);
                }
                SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
                ShowFilteredEmpty = n > 0 && Rows.Count == 0;
            }
            finally
            {
                _rebuilding = false;
            }
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    // ==================== 命令 ====================

    /// <summary>添加账号：先落一个带设备号的新账号，弹 WebView 登录；成功则回填凭证，取消则撤销。</summary>
    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (Busy) return;
        Busy = true;
        StatusMessage = "";
        Account? acc = null;
        try
        {
            acc = new Account();
            ReissueDeviceId(acc);
            lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.Add(acc);
            AppServices.Config.ActiveAccountId = acc.Id;
            AppServices.SaveConfig();

            var (ok, clientSource) = await PromptLoginAsync(acc);
            if (!ok)
            {
                // 两个通道都没拿到凭证：撤销刚创建的空账号
                lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.RemoveAll(a => a.Id == acc.Id);
                if (AppServices.Config.ActiveAccountId == acc.Id)
                    AppServices.Config.ActiveAccountId = AppServices.Config.Accounts.FirstOrDefault()?.Id;
                AppServices.SaveConfig();
                return;
            }

            FinalizeNewAccount(acc);
            StatusMessage = clientSource != null
                ? $"账号「{acc.DisplayName(0)}」添加成功（登录态来自本机客户端：{clientSource}）"
                : $"账号「{acc.DisplayName(0)}」添加成功";

            // 向导（两处引导之一）：可选地立即到客户端登录并自动建档，启用「客户端切换」
            if (AppServices.Config.Accounts.Any(a => a.Id == acc.Id))
            {
                var go = DialogWindow.Show(
                    "启用客户端切换",
                    $"账号「{acc.DisplayName(0)}」已添加，签到即可用。\n\n" +
                    "是否现在到 Trae 客户端登录该账号并自动建档（启用「客户端切换」）？",
                    DialogKind.Info,
                    small: "建档会关闭并重启客户端；也可以稍后在「客户端切换」页补建。每个账号仅需一次。",
                    primary: "现在建档", secondary: "稍后再说");
                if (go)
                {
                    var meta = await ArchiveFlow.GuideAsync(acc, acc.DisplayName(0));
                    StatusMessage = meta != null
                        ? $"账号「{acc.DisplayName(0)}」添加成功，客户端切换档案已就绪"
                        : "账号已添加；客户端档案未完成，可稍后在「客户端切换」页补建";
                }
                else
                {
                    StatusMessage = $"账号「{acc.DisplayName(0)}」添加成功（客户端切换档案可稍后补建）";
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "添加账号异常: " + ex.Message);
            StatusMessage = "添加失败：" + ex.Message;
            if (acc != null)
            {
                lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.RemoveAll(a => a.Id == acc.Id);
                AppServices.SaveConfig();
            }
        }
        finally
        {
            Busy = false;
            AppServices.RaiseAccountsChanged();
        }
    }

    /// <summary>对已有账号（重新）登录。</summary>
    [RelayCommand]
    private async Task LoginAsync(AccountRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row == null || Busy) return;
        Busy = true;
        try
        {
            var (ok, clientSource) = await PromptLoginAsync(row.Account);
            if (ok)
            {
                FinalizeNewAccount(row.Account);
                StatusMessage = clientSource != null
                    ? $"「{row.DisplayName}」登录成功（登录态来自本机客户端：{clientSource}）"
                    : $"「{row.DisplayName}」登录成功";
            }
            else
            {
                StatusMessage = "登录取消";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "登录命令异常: " + ex); // RelayCommand 会吞异常，必须落日志
            StatusMessage = "登录失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
            AppServices.RaiseAccountsChanged();
        }
    }

    // 账号切换入口在「每日签到」页头部下拉框（AppServices.SetActive 是唯一写入点）。

    /// <summary>删除账号：确认 → 移除配置 → 删 WebView profile；历史记录保留。</summary>
    [RelayCommand]
    private void Delete(AccountRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row == null) return;

        try
        {
            var confirm = DialogWindow.Show(
                "删除账号",
                $"确定删除账号「{row.DisplayName}」？\n签到历史记录会保留，登录态将被清除。",
                DialogKind.Danger,
                small: "删除后该账号的浏览器登录态会被清除——想再签到需重新「登录」一次。",
                primary: "删除", secondary: "取消", dangerPrimary: true);
            if (!confirm) return;

            var id = row.Id;
            var name = row.DisplayName;
            lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.RemoveAll(a => a.Id == id);
            if (AppServices.Config.ActiveAccountId == id)
                AppServices.Config.ActiveAccountId = AppServices.Config.Accounts.FirstOrDefault()?.Id;
            AppServices.SaveConfig();
            Paths.TryDeleteProfile(id);
            StatusMessage = $"已删除「{name}」";
            AppLog.Info("accounts", $"删除账号 {name} ({id})");
            AppServices.RaiseAccountsChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "删除账号异常: " + ex);
            StatusMessage = "删除失败：" + ex.Message;
        }
    }

    /// <summary>刷新全部账号状态（令牌有效性 + 今日签到状态 + 剩余额 + 资料回填）。</summary>
    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (Busy) return;
        Busy = true;
        StatusMessage = "正在刷新账号状态…";
        try
        {
            // 刷新范围取全部账号：Rows 只是当前筛选/分组后的视图，
            // 按它循环会让被筛掉的账号永远刷不到
            foreach (var acc in AppServices.Config.Accounts.ToList())
            {
                var row = Rows.FirstOrDefault(r => r.Account.Id == acc.Id);
                if (string.IsNullOrEmpty(acc.Token) && string.IsNullOrEmpty(acc.Session))
                {
                    row?.Refresh();
                    continue;
                }

                var valid = await TokenRenewer.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await TraeApiClient.GetStatusAsync(acc.Token!, acc.DeviceId);
                    if (st.HttpError == null && st.Code == 0)
                    {
                        // 服务器真值校准本地今日去重键
                        if (st.Body is { } b &&
                            b.TryGetProperty("checked_in", out var ci) &&
                            ci.ValueKind == System.Text.Json.JsonValueKind.True)
                        {
                            acc.LastCheckinDate = DateOnly.FromDateTime(DateTime.Today);
                        }
                        var rem = await TraeApiClient.GetRemainingCreditsAsync(acc.Token!, acc.DeviceId);
                        if (rem >= 0)
                        {
                            acc.RemainingCredits = rem;
                            CheckinRecordRepo.UpsertSnapshot(acc.Id, rem);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(acc.Token) && string.IsNullOrEmpty(acc.ScreenName))
                {
                    var (screen, mobile, avatar, uid) =
                        await TraeApiClient.GetUserProfileAsync(acc.Token!, acc.Session);
                    if (!string.IsNullOrEmpty(screen)) acc.ScreenName = screen;
                    if (!string.IsNullOrEmpty(mobile)) acc.MobileMasked = mobile;
                    if (!string.IsNullOrEmpty(avatar)) acc.AvatarUrl = avatar;
                    if (!string.IsNullOrEmpty(uid)) acc.AccountUid = uid;
                }

                MemberDetector.TryRefreshAndSave(acc, isActive: acc.Id == AppServices.Config.ActiveAccountId);

                // 学生认证（student_verification，独立接口；失败保持原值）
                if (!string.IsNullOrEmpty(acc.Token))
                {
                    var sv = await TraeApiClient.GetStudentVerifiedAsync(acc.Token!, acc.Session);
                    if (sv.HasValue) acc.StudentVerified = sv.Value;
                }

                AppServices.SaveConfig();
                row?.Refresh();
            }
            StatusMessage = "账号状态已刷新";
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "刷新状态异常: " + ex);
            StatusMessage = "刷新失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 双通道获取登录凭证：WebView 登录窗与本机客户端只读探查同时启动，
    /// 任一通道拿到可用凭据即采用；客户端探查同时也是登录窗不可用（WebView2 运行时缺失、
    /// 窗口打开异常）时的降级通道。返回 ok 与来源说明（非空 = 本次凭证来自本机客户端）。
    /// </summary>
    private async Task<(bool Ok, string? ClientSource)> PromptLoginAsync(Account account)
    {
        // 与登录窗并行：登录窗占用 UI 期间，探查在后台线程完成（只读，不改动客户端文件）
        var probeTask = LocalClientImporter.ProbeAsync();

        LoginWindow? win = null;
        string? openError = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                win = new LoginWindow(account.Id, account.Token)
                {
                    Owner = Application.Current.MainWindow,
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                openError = ex.Message;
                AppLog.Error("accounts", "登录窗不可用，改用本机客户端通道: " + ex.Message);
            }
        });

        if (win?.Result != null)
        {
            account.Token = win.Result.Token;
            if (!string.IsNullOrEmpty(win.Result.Session)) account.Session = win.Result.Session;
            account.TokenUpdatedAt = DateTime.Now;
            var uid = JwtHelper.ParseAccountUid(win.Result.Token);
            if (!string.IsNullOrEmpty(uid)) account.AccountUid = uid;
            AppServices.SaveConfig();
            return (true, null);
        }

        // —— 降级通道：本机客户端只读探查 ——
        LocalImportProbe probe;
        try
        {
            probe = await probeTask;
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "客户端探查异常: " + ex.Message);
            probe = new LocalImportProbe(false, "读取客户端数据失败：" + ex.Message,
                null, null, null, "");
        }

        if (!probe.Ok)
        {
            StatusMessage = openError != null ? "登录窗不可用：" + openError + "；" + probe.Message : probe.Message;
            return (false, null);
        }

        // 登录窗被取消但客户端有登录态：降级前先征得确认，避免误把客户端当前账号当成目标账号
        if (openError == null)
        {
            var use = DialogWindow.Show("改用它处登录态",
                "浏览器登录未完成，但本机 Trae 客户端里读到了登录凭证" +
                (string.IsNullOrEmpty(probe.Source) ? "。" : $"（来源：{probe.Source}）。") +
                "\n\n是否改用该登录态添加账号？",
                DialogKind.Info,
                small: "选择「取消」则放弃本次添加；也可以先在客户端登录目标账号后重试。",
                primary: "改用它", secondary: "取消");
            if (!use)
            {
                StatusMessage = "已取消添加";
                return (false, null);
            }
        }

        var token = probe.Token;
        if (token == null)
        {
            token = await TraeApiClient.GetUserTokenAsync(probe.Session!);
            if (token == null)
            {
                StatusMessage = "已读取到客户端会话，但换取 Token 失败（会话可能已过期 ~14 天）——" +
                                "请先在客户端里保持登录后重试，或改用「登录」按钮。";
                return (false, null);
            }
        }

        account.Token = token;
        if (!string.IsNullOrEmpty(probe.Session)) account.Session = probe.Session;
        account.TokenUpdatedAt = DateTime.Now;
        var uid2 = JwtHelper.ParseAccountUid(token);
        if (!string.IsNullOrEmpty(uid2)) account.AccountUid = uid2;
        AppServices.SaveConfig();
        return (true, probe.Source);
    }

    /// <summary>登录成功后的收尾：补设备号、查重 UID、异步拉资料。</summary>
    private void FinalizeNewAccount(Account account)
    {
        ReissueDeviceId(account);

        // 同一手机号（UID 相同）重复添加 → 撤销新账号，指向已有账号
        if (!string.IsNullOrEmpty(account.AccountUid))
        {
            var dup = AppServices.Config.Accounts.FirstOrDefault(
                a => a.Id != account.Id && a.AccountUid == account.AccountUid);
            if (dup != null)
            {
                lock (ConfigStore.SyncRoot) AppServices.Config.Accounts.RemoveAll(a => a.Id == account.Id);
                AppServices.Config.ActiveAccountId = dup.Id;
                AppServices.SaveConfig();
                StatusMessage = "该账号已存在（UID 相同），已切换到已有账号";
                AppLog.Info("accounts", $"检测到重复 UID {account.AccountUid}，撤销新增");
                return;
            }
        }

        AppServices.SaveConfig();
        var captured = account;
        _ = Task.Run(async () =>
        {
            try
            {
                var (screen, mobile, avatar, uid) =
                    await TraeApiClient.GetUserProfileAsync(captured.Token!, captured.Session);
                if (!string.IsNullOrEmpty(screen) || !string.IsNullOrEmpty(mobile))
                {
                    if (!string.IsNullOrEmpty(screen)) captured.ScreenName = screen;
                    if (!string.IsNullOrEmpty(mobile)) captured.MobileMasked = mobile;
                    if (!string.IsNullOrEmpty(avatar)) captured.AvatarUrl = avatar;
                    if (!string.IsNullOrEmpty(uid)) captured.AccountUid = uid;
                    AppServices.SaveConfig();
                }

                // 学生认证回填（独立接口，尽力而为）
                var sv = await TraeApiClient.GetStudentVerifiedAsync(captured.Token!, captured.Session);
                if (sv.HasValue)
                {
                    captured.StudentVerified = sv.Value;
                    AppServices.SaveConfig();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("accounts", "资料回填失败: " + ex.Message);
            }
        });
    }

    /// <summary>按设置给账号补发不重复的设备号（FromTraeClient 模式优先读本机客户端真实设备号）。</summary>
    private void ReissueDeviceId(Account account)
    {
        if (AppServices.Config.DeviceIdMode == AppSettings.DeviceIdModes.FromTraeClient)
        {
            var real = DeviceIdFactory.TryReadFromTraeClient();
            if (!string.IsNullOrEmpty(real) &&
                AppServices.Config.Accounts.All(a => a.Id == account.Id || a.DeviceId != real))
            {
                account.DeviceId = real;
                return;
            }
        }
        CheckinService.EnsureDeviceIdUnique(account, AppServices.Config);
    }
}
