using System.IO;

namespace TraeTool.Services;

/// <summary>文件系统清理助手：删除失败一律忽略（残留于 %TEMP% 无害，不能因此中断主流程）。</summary>
internal static class FileSystemUtil
{
    /// <summary>递归删除目录；不存在或删除失败时不抛。</summary>
    public static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 删除失败无害 */ }
    }

    /// <summary>删除文件；不存在或删除失败时不抛。</summary>
    public static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删除失败无害 */ }
    }
}
