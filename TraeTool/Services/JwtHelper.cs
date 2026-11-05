using System.Text;
using System.Text.Json;

namespace TraeTool.Services;

/// <summary>
/// Cloud-IDE-Token（JWT）解析：取 data.id 作账号 UID、exp 作过期时间。
/// 只解 payload 不验签（本机自用展示）。
/// </summary>
public static class JwtHelper
{
    /// <summary>解析 payload 中的 data.id（跨登录会话恒定的账号 UID）。失败返回 null。</summary>
    public static string? ParseAccountUid(string? token)
    {
        if (TryParsePayload(token) is not { } payload) return null;
        try
        {
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("id", out var id))
            {
                return id.ValueKind switch
                {
                    JsonValueKind.String => id.GetString(),
                    JsonValueKind.Number => id.GetRawText(),
                    _ => null,
                };
            }
        }
        catch { /* 解析异常视为无 UID */ }
        return null;
    }

    /// <summary>解析 exp（Unix 秒）为本地时间；无 exp 或非法返回 null。</summary>
    public static DateTime? TryGetExpiry(string? token)
    {
        if (TryParsePayload(token) is not { } payload) return null;
        try
        {
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("exp", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    private static JsonElement? TryParsePayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}
