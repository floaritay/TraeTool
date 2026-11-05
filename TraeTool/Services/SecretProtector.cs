using System.Security.Cryptography;
using System.Text;

namespace TraeTool.Services;

/// <summary>
/// DPAPI（当前 Windows 用户作用域）加解密，用于 Token/Session/DeviceId 落盘保护。
/// 加密失败抛异常（由调用方兜底保留旧文件）；解密失败返回 null（换机/换用户 → 需重新登录）。
/// </summary>
public static class SecretProtector
{
    public static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return blob;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
