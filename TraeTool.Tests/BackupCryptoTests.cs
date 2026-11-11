using System.IO;
using System.Security.Cryptography;
using System.Text;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class BackupCryptoTests
{
    private static byte[] Sample() => Encoding.UTF8.GetBytes("TraeTool backup payload");

    [Fact]
    public void Round_trip_returns_original_bytes()
    {
        var plain = Sample();
        var container = BackupCrypto.Encrypt(plain, "p@ssw0rd-中文");

        Assert.Equal(plain, BackupCrypto.Decrypt(container, "p@ssw0rd-中文"));
    }

    [Fact]
    public void Container_is_recognised_as_encrypted()
    {
        var container = BackupCrypto.Encrypt(Sample(), "pw");

        Assert.True(BackupCrypto.IsEncrypted(container));
        Assert.False(BackupCrypto.IsEncrypted(Sample()));
        Assert.False(BackupCrypto.IsEncrypted(Array.Empty<byte>()));
    }

    [Fact]
    public void Wrong_password_fails_authentication()
    {
        var container = BackupCrypto.Encrypt(Sample(), "right");
        Assert.Throws<CryptographicException>(() => BackupCrypto.Decrypt(container, "wrong"));
    }

    [Fact]
    public void Tampered_ciphertext_fails_authentication()
    {
        var container = BackupCrypto.Encrypt(Sample(), "pw");
        container[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => BackupCrypto.Decrypt(container, "pw"));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        // 每次随机 salt + nonce：同样输入不应产生同样密文
        var a = BackupCrypto.Encrypt(Sample(), "pw");
        var b = BackupCrypto.Encrypt(Sample(), "pw");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IsEncryptedFile_detects_container_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "ttbak_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, BackupCrypto.Encrypt(Sample(), "pw"));
            Assert.True(BackupCrypto.IsEncryptedFile(path));

            File.WriteAllBytes(path, Sample());
            Assert.False(BackupCrypto.IsEncryptedFile(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
