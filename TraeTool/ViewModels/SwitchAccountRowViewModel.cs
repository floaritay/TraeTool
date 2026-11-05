using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TraeTool.Models;
using TraeTool.Services;

namespace TraeTool.ViewModels;

/// <summary>客户端切换页的账号行：绑定账号 + 其 vault 档案状态与客户端身份信息。</summary>
public partial class SwitchAccountRowViewModel : ObservableObject
{
    public Account Account { get; }

    public SwitchAccountRowViewModel(Account account, int index)
    {
        Account = account;
        _displayName = account.DisplayName(index);
        if (!string.IsNullOrEmpty(account.AvatarUrl)) _ = LoadAvatarAsync(account.AvatarUrl);
    }

    private async Task LoadAvatarAsync(string url)
    {
        var src = await AvatarCacheService.GetAsync(url);
        if (src != null) AvatarImage = src;
    }

    public string Id => Account.Id;

    /// <summary>头像色相（与账号管理页同源，同一账号同色）。</summary>
    public double AvatarHue => AccountRowViewModel.HueOf(Account.Id);

    /// <summary>头像首字。</summary>
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.TrimStart()[..1].ToUpperInvariant();

    /// <summary>头像图（AvatarUrl 下载缓存后加载；null = 首字兜底）。</summary>
    [ObservableProperty]
    private ImageSource? _avatarImage;

    [ObservableProperty]
    private string _displayName = "";

    /// <summary>档案状态：无备份 / 已备份。</summary>
    [ObservableProperty]
    private string _archiveState = "无备份";

    /// <summary>"—" 或 "12 文件 · 2026-11-03 10:00"。</summary>
    [ObservableProperty]
    private string _archiveDetail = "—";

    /// <summary>身份备注：客户端当前 = X / 客户端未登录 / 客户端非此账号。</summary>
    [ObservableProperty]
    private string _identityNote = "";

    /// <summary>客户端当前登录身份是否就是本行账号的档案身份（行内「当前」徽标）。</summary>
    [ObservableProperty]
    private bool _isClientCurrent;

    public void ApplyVaultInfo(bool hasVault, int fileCount, DateTime? createdUtc)
    {
        ArchiveState = hasVault ? "已备份" : "无备份";
        ArchiveDetail = hasVault
            ? $"{fileCount} 文件 · {createdUtc?.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "—";
    }
}
