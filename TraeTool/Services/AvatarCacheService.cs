using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TraeTool.Services;

/// <summary>
/// 账号头像下载与本地缓存：按 URL 哈希存 png（%APPDATA%\TraeTool\avatars\），
/// 内存级去重同一 URL 的并发请求；返回冻结的 ImageSource（跨线程安全）。
/// </summary>
public static class AvatarCacheService
{
    private static HttpClient Http => HttpClients.For(10);
    private static readonly string Dir = Path.Combine(Paths.Root, "avatars");
    private static readonly Dictionary<string, Task<ImageSource?>> _mem = new();
    private static readonly object _lock = new();

    public static Task<ImageSource?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        lock (_lock)
        {
            if (_mem.TryGetValue(url!, out var cached)) return cached;
            var t = LoadAsync(url!);
            _mem[url!] = t;
            return t;
        }
    }

    private static async Task<ImageSource?> LoadAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..16];
            var file = Path.Combine(Dir, key + ".png");
            if (!File.Exists(file))
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024) return null;
                await File.WriteAllBytesAsync(file, bytes);
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            AppLog.Info("avatar", $"头像加载失败: {ex.Message}");
            return null; // 失败回退首字头像
        }
    }
}
