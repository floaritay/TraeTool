using System.IO;
using System.Text.Json;

namespace TraeTool.Services.Switch;

/// <summary>切换守护的判定结果。</summary>
public enum AuthState
{
    /// <summary>凭据键齐全——已进入目标账号。</summary>
    Active,

    /// <summary>文件可读但凭据键被客户端主动清空/缺失——未进入（refresh 失效或非此账号）。</summary>
    RolledBack,

    /// <summary>文件不可读/JSON 未写完（切换瞬间的瞬态）——继续轮询。</summary>
    Transient,
}

/// <summary>
/// 登录态探测：只读 storage.json 的两个凭据键。
/// 成功判定必须轮询「凭据键本身」而非文件时间戳——恢复动作自身就会改时间戳（假阳性），
/// 且鉴权失败时客户端会稍后 clearUserInfo 删键（时间戳判据会在删除前误报成功）。
/// </summary>
public static class AuthProbe
{
    public const string UsertagKey = "iCubeAuthInfo://usertag";
    public const string CloudIdeKey = "iCubeAuthInfo://icube.cloudide";

    private static string StoragePath(string root)
        => Path.Combine(root, "User", "globalStorage", "storage.json");

    /// <summary>读取客户端当前身份（usertag 原值）；读不到返回 null。仅用于展示。</summary>
    public static string? TryGetIdentity(string root)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(StoragePath(root)));
            if (doc.RootElement.TryGetProperty(UsertagKey, out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { /* 文件缺失/占用/未登录 */ }
        return null;
    }

    /// <summary>
    /// 凭据三态：<see cref="AuthState.Active"/> 双键齐全；
    /// <see cref="AuthState.RolledBack"/> 文件读得到但键缺失或空；
    /// <see cref="AuthState.Transient"/> 文件不可读/JSON 损坏（正在写入）。
    /// </summary>
    public static AuthState HasAuthCreds(string root)
    {
        string? text;
        try
        {
            text = File.ReadAllText(StoragePath(root));
        }
        catch
        {
            return AuthState.Transient; // 尚未创建或被占用：下轮再看
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return AuthState.Transient;
            return IsUsable(doc.RootElement, UsertagKey) && IsUsable(doc.RootElement, CloudIdeKey)
                ? AuthState.Active
                : AuthState.RolledBack;
        }
        catch
        {
            return AuthState.Transient; // 截断/半截 JSON：客户端正在写
        }

        static bool IsUsable(JsonElement root, string key)
            => root.TryGetProperty(key, out var v) &&
               v.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(v.GetString());
    }

    /// <summary>
    /// 稳定等待（切换守护）。
    /// 注意：恢复动作本身就会把凭据键写进 storage.json——瞬时轮询会把刚写入误判为 Active，
    /// 必须等待凭据稳定后再判定。语义：
    ///  - 出现「键被清空」→ 连续两次确认后提前返回 RolledBack（明确失败，触发回滚）；
    ///  - Active 必须**同时满足**：客户端进程在运行 && 凭据键连续保持 ≥ <paramref name="minHold"/>
    ///    （给鉴权轮换取时间；期间键一旦消失则重新计时）；
    ///  - 超时仍不稳定 → Transient（调用方按 NeedsConfirm 处理，保留回滚快照）。
    /// </summary>
    public static async Task<AuthState> WaitStableAsync(
        string root,
        Func<bool> clientRunning,
        TimeSpan minHold,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        DateTime? activeSince = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = HasAuthCreds(root);

            if (state == AuthState.RolledBack)
            {
                // 连续两次确认，避开客户端改写 storage.json 的瞬间空档
                await Task.Delay(400, ct);
                if (HasAuthCreds(root) == AuthState.RolledBack)
                    return AuthState.RolledBack;
                continue;
            }

            if (state == AuthState.Active && clientRunning())
            {
                activeSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - activeSince >= minHold)
                    return AuthState.Active;
            }
            else
            {
                activeSince = null; // 进程没起来或键短暂消失：重新计时
            }

            await Task.Delay(400, ct);
        }
        return AuthState.Transient;
    }
}
