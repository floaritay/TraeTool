using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TraeTool.Services;

/// <summary>
/// 备份口令加密容器（AES-256-GCM，密钥经 PBKDF2-SHA256 派生）。
/// 文件布局：magic "TTBAK1"(6) + 版本(1) + salt(16) + nonce(12) + tag(16) + 密文。
/// 口令不落盘；口令错误或数据被改动时 GCM 校验失败抛 <see cref="CryptographicException"/>。
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "TTBAK1"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, KeyLen = 32;
    private const int Iterations = 210_000;

    /// <summary>按文件头判断是否为口令加密容器。</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> head)
        => head.Length >= Magic.Length + 1 && head[..Magic.Length].SequenceEqual(Magic);

    /// <summary>按文件头判断是否为口令加密容器（读不到返回 false）。</summary>
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[Magic.Length + 1];
            int n = fs.Read(head);
            return IsEncrypted(head[..n]);
        }
        catch { return false; }
    }

    public static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = DeriveKey(password, salt);

        var ct = new byte[plain.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, plain, ct, tag, Magic);

        var buf = new byte[Magic.Length + 1 + SaltLen + NonceLen + TagLen + ct.Length];
        int p = 0;
        Magic.CopyTo(buf, p); p += Magic.Length;
        buf[p++] = FormatVersion;
        salt.CopyTo(buf, p); p += SaltLen;
        nonce.CopyTo(buf, p); p += NonceLen;
        tag.CopyTo(buf, p); p += TagLen;
        ct.CopyTo(buf, p);
        return buf;
    }

    public static byte[] Decrypt(byte[] container, string password)
    {
        if (!IsEncrypted(container))
            throw new CryptographicException("该文件不是口令加密的备份");

        int p = Magic.Length;
        var ver = container[p++];
        if (ver != FormatVersion)
            throw new CryptographicException($"不支持的备份格式版本：{ver}");

        var salt = container.AsSpan(p, SaltLen).ToArray(); p += SaltLen;
        var nonce = container.AsSpan(p, NonceLen).ToArray(); p += NonceLen;
        var tag = container.AsSpan(p, TagLen).ToArray(); p += TagLen;
        var ct = container.AsSpan(p).ToArray();

        var key = DeriveKey(password, salt);
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(key, TagLen);
        gcm.Decrypt(nonce, ct, tag, plain, Magic);
        return plain;
    }

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
}
