using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>账号列表行：包装 Account 提供绑定用的派生展示字段。</summary>
public partial class AccountRowViewModel : ObservableObject
{
    public Account Account { get; }

    public AccountRowViewModel(Account account, int index)
    {
        Account = account;
        _displayName = string.IsNullOrWhiteSpace(account.Name)
            ? (string.IsNullOrWhiteSpace(account.ScreenName) ? $"账号 {index + 1}" : account.ScreenName!)
            : account.Name!;
        _isCurrent = false;
        Refresh();
    }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>状态行：登录/今日签到/剩余额摘要。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>资料行：UID + 脱敏手机。</summary>
    [ObservableProperty]
    private string _detailText = "";

    public string Id => Account.Id;

    /// <summary>账号分组（LostFocus 落盘）；改名不触发整表重建，筛选下拉在下次进页时刷新。</summary>
    public string Group
    {
        get => Account.Group ?? "";
        set
        {
            var v = value?.Trim() ?? "";
            if ((Account.Group ?? "") == v) return;
            Account.Group = v.Length == 0 ? null : v;
            OnPropertyChanged();
            AppServices.SaveConfig();
        }
    }

    /// <summary>是否持有登录凭据（按钮文案/未登录徽标联动）。</summary>
    [ObservableProperty]
    private bool _isLoggedIn;

    /// <summary>状态等级 ok/warn/info —— 决定点与文字颜色。</summary>
    [ObservableProperty]
    private string _statusLevel = "info";

    /// <summary>头像图（AvatarUrl 下载缓存后加载；null = 显示首字兜底）。</summary>
    [ObservableProperty]
    private ImageSource? _avatarImage;

    /// <summary>是否学生认证（student_verification）。</summary>
    [ObservableProperty]
    private bool _studentVerified;

    private string? _loadedAvatarUrl;

    /// <summary>头像色相（由 Id 稳定推导，0~359）。</summary>
    public double AvatarHue => HueOf(Account.Id);

    /// <summary>头像首字。</summary>
    public string Initial =>
        string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.TrimStart()[..1].ToUpperInvariant();

    /// <summary>Guid 前 6 位十六进制 → 稳定色相（进程重启不变）。</summary>
    public static double HueOf(string id)
    {
        try { return Convert.ToInt32(id.Length >= 6 ? id[..6] : "0", 16) % 360; }
        catch { return 210; }
    }

    /// <summary>设备号尾 4 位（展示定位用，不泄露全号）。</summary>
    public string DeviceIdTail =>
        string.IsNullOrEmpty(Account.DeviceId) ? "—" : "…" + Account.DeviceId[^4..];

    /// <summary>参与批量签到开关（双向绑 Account.Enabled，改动即落盘）。</summary>
    public bool Enabled
    {
        get => Account.Enabled;
        set
        {
            if (Account.Enabled == value) return;
            Account.Enabled = value;
            OnPropertyChanged();
            AppServices.SaveConfig();
        }
    }

    /// <summary>刷新派生展示字段（资料回填 / 签到状态变化后调用）。</summary>
    public void Refresh()
    {
        var loggedIn = !string.IsNullOrEmpty(Account.Token) || !string.IsNullOrEmpty(Account.Session);
        var signedToday = Account.LastCheckinDate == DateOnly.FromDateTime(DateTime.Today);

        IsLoggedIn = loggedIn;
        StatusLevel = !loggedIn ? "warn" : signedToday ? "ok" : "info";
        StudentVerified = Account.StudentVerified;

        if (!string.IsNullOrEmpty(Account.AvatarUrl) && Account.AvatarUrl != _loadedAvatarUrl)
        {
            _loadedAvatarUrl = Account.AvatarUrl;
            _ = LoadAvatarAsync(Account.AvatarUrl);
        }

        var parts = new List<string>
        {
            !loggedIn ? "未登录" : signedToday ? "今日已签 ✓" : "已登录，待签到",
        };
        if (Account.RemainingCredits >= 0)
            parts.Add($"剩 {Account.RemainingCredits:0.#}");
        StatusText = string.Join(" · ", parts);

        var details = new List<string>();
        if (!string.IsNullOrEmpty(Account.AccountUid)) details.Add("UID " + Account.AccountUid);
        if (!string.IsNullOrEmpty(Account.MobileMasked)) details.Add(Account.MobileMasked);
        details.Add("设备 " + DeviceIdTail);
        DetailText = string.Join("  |  ", details);

        OnPropertyChanged(nameof(DeviceIdTail));
        OnPropertyChanged(nameof(Initial));
    }

    private async Task LoadAvatarAsync(string url)
    {
        var src = await AvatarCacheService.GetAsync(url);
        if (src != null && _loadedAvatarUrl == url) AvatarImage = src;
    }

    /// <summary>Token 掩码展示（设置页 Token 卡）：前 8 + … + 后 6；未登录显示占位。</summary>
    public string TokenMask
    {
        get
        {
            var t = Account.Token;
            if (string.IsNullOrEmpty(t)) return "未登录";
            return t.Length <= 16 ? t[..4] + "…" : t[..8] + "…" + t[^6..];
        }
    }

    /// <summary>Token 最后更新时间（设置页）。</summary>
    public string TokenUpdatedText =>
        Account.TokenUpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}
