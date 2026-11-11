namespace TraeTool.Models;

/// <summary>
/// 一个 Trae 账号。每个账号独立 Token(JWT)/Session(Cookie)/DeviceId。
/// 不包含任何 Trae 桌面客户端路径字段——客户端 vault 按 Id/Uid 键控，与显示名无关。
/// </summary>
public class Account
{
    /// <summary>账号唯一标识（Guid "N"），同时用于隔离 WebView2 用户数据目录。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户备注名；为空时 UI 显示「账号 n」或昵称。</summary>
    public string? Name { get; set; }

    /// <summary>账号分组（自由文本，空 = 未分组）；用于账号页筛选与批量管理。</summary>
    public string? Group { get; set; }

    /// <summary>JWT（Cloud-IDE-Token，约 8 小时有效），失效时用 Session 静默换新。</summary>
    public string? Token { get; set; }

    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天有效）。</summary>
    public string? Session { get; set; }

    /// <summary>x-device-id（16 位数字，风控关键），每账号必须独立；服务端按此值每日去重 claim。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>账号唯一 UID（JWT payload data.id），跨登录会话恒定，用于查重与展示。</summary>
    public string? AccountUid { get; set; }

    public DateTime? TokenUpdatedAt { get; set; }

    /// <summary>该账号最近一次签到日期（yyyy-MM-dd），本地当日去重键。</summary>
    public DateOnly? LastCheckinDate { get; set; }

    /// <summary>连续签到失败次数（成功或已签即清零）；达到阈值后当日暂停签到。</summary>
    public int FailStreak { get; set; }

    /// <summary>暂停签到截止日期（含当日，次日自动恢复）；为空表示未暂停。</summary>
    public DateOnly? SuspendedUntil { get; set; }

    /// <summary>该账号最近一次剩余积分；-1 表示尚未获取过。</summary>
    public double RemainingCredits { get; set; } = -1;

    /// <summary>是否参与批量/自动签到。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否会员：非会员每日仅基础 150 分，会员另有连签 extra_credits。</summary>
    public bool IsMember { get; set; }

    // ---- 账号资料（GetUserInfo 回填，纯展示用）----
    public string? ScreenName { get; set; }
    public string? MobileMasked { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>是否学生认证（/v3/student_verification/status，status==1）。</summary>
    public bool StudentVerified { get; set; }

    /// <summary>展示名：备注名 → 昵称 → 账号 n（由调用方传入序号）。</summary>
    public string DisplayName(int index)
        => !string.IsNullOrWhiteSpace(Name) ? Name!
         : !string.IsNullOrWhiteSpace(ScreenName) ? ScreenName!
         : $"账号 {index}";
}
