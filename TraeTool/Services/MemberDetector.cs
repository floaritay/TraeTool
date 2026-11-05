using System.IO;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 会员状态判定。数据源（逆向自客户端 storage.json）：
/// <c>iCubeServerData://icube.cloudide</c>（JSON 字符串）→ entitlementInfo.identity / identityStr。
/// Free 用户 identity=0、identityStr="Free"。参考实现的 IsMember 字段实际从未赋值（死字段），
/// 我们用客户端已下发的资格数据补上这块。
/// </summary>
public static class MemberDetector
{
    /// <summary>从指定 storage.json 判定；null=读不到/字段缺失（调用方保持原值）。</summary>
    public static bool? TryDetect(string storageJsonPath)
    {
        try
        {
            if (!File.Exists(storageJsonPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(storageJsonPath));
            if (!doc.RootElement.TryGetProperty("iCubeServerData://icube.cloudide", out var raw) ||
                raw.ValueKind != JsonValueKind.String)
                return null;
            var inner = raw.GetString();
            if (string.IsNullOrWhiteSpace(inner)) return null;
            using var innerDoc = JsonDocument.Parse(inner);
            if (!innerDoc.RootElement.TryGetProperty("entitlementInfo", out var info) ||
                info.ValueKind != JsonValueKind.Object)
                return null;

            if (info.TryGetProperty("identity", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var iv))
                return iv > 0; // 0 = Free

            if (info.TryGetProperty("identityStr", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var v = s.GetString();
                if (string.IsNullOrWhiteSpace(v)) return null;
                return !v.Equals("Free", StringComparison.OrdinalIgnoreCase) && v != "免费";
            }
            return null;
        }
        catch { return null; } // 文件损坏/未登录：未知
    }

    /// <summary>live 客户端当前登录态的会员判定。</summary>
    public static bool? TryDetectLive()
        => TryDetect(Path.Combine(Switch.CarrierDefs.DataRoot, "User", "globalStorage", "storage.json"));

    /// <summary>某账号 vault 档案内的会员判定（离线）。</summary>
    public static bool? TryDetectVault(string accountId)
        => TryDetect(Path.Combine(Switch.VaultStore.VaultPath(accountId), "User", "globalStorage", "storage.json"));

    /// <summary>
    /// 判定指定账号：若是客户端当前登录账号（isActive=true）优先读 live，其次读其 vault 档案。
    /// null=未知（调用方保持原值）。
    /// </summary>
    public static bool? TryDetectFor(Account account, bool isActive)
    {
        if (isActive)
        {
            var live = TryDetectLive();
            if (live != null) return live;
        }
        return TryDetectVault(account.Id);
    }

    /// <summary>刷新并落盘 IsMember（仅当可判定且有变化）。返回是否有更新。</summary>
    public static bool TryRefreshAndSave(Account account, bool isActive)
    {
        var v = TryDetectFor(account, isActive);
        if (v == null || account.IsMember == v.Value) return false;
        account.IsMember = v.Value;
        AppLog.Info("member", $"{account.DisplayName(0)} 会员状态 → {(v.Value ? "会员" : "非会员")}");
        return true;
    }
}
