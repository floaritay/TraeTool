  using System.IO;
  using System.Text;
  using System.Text.RegularExpressions;

  namespace TraeTool.Services;

  /// <summary>本机客户端凭证探查结果（纯只读，不含任何写操作）。</summary>
  public sealed record LocalImportProbe(
      bool Ok, string Message, string? Token, string? Session, string? DeviceId, string Source);

  /// <summary>
  /// 从本机 Trae 桌面客户端的数据目录提取登录态（只读、免浏览器登录）。
  ///
  /// 数据源（依据公开可观察的客户端行为，格式未承诺、可能随版本变化）：
  ///   1) User\globalStorage\storage.json：
  ///      · 设备号藏在键名里：iCubeAuthInfo://icube-dc:<16位数字>
  ///      · 正文也可能直接出现 JWT(eyJ…)
  ///   2) Local Storage\leveldb\* 与 Session Storage\*：
  ///      按 Latin-1 读原始字节，其中以明文散布着
  ///      JWT(eyJ…) 与 "X-Cloudide-Session" 后跟的会话值。
  ///   （storage.json 的 iCubeAuthInfo 加密袋不碰——实测高熵加密，
  ///     本导入器不尝试解密，只走上述明文位置。）
  /// 找不到时给出明确可执行的下一步，不做静默兜底；全程不修改客户端文件。
  /// </summary>
  public static class LocalClientImporter
  {
      /// <summary>已知客户端数据目录名（新旧命名都试）。</summary>
      private static readonly string[] ClientDirNames =
          ["Trae CN", "TRAE CN", "TRAE", "TRAE SOLO CN", "TRAE SOLO", "Trae"];

      private const long MaxScanBytes = 24L * 1024 * 1024; // 单文件扫描上限
      private const int MaxScanFiles = 200;                 // 总文件上限
      private const string SessionKey = "X-Cloudide-Session";

      private static readonly Regex JwtRe = new(
          @"eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}",
          RegexOptions.Compiled);

      private static readonly Regex DcIdRe = new(
          "iCubeAuthInfo://icube-dc:(\\d{" + DeviceIdFactory.MinDigits + "," + DeviceIdFactory.MaxDigits + "})",
          RegexOptions.Compiled);

      /// <summary>后台探查（内部 IO 与扫描不进 UI 线程）。</summary>
      public static Task<LocalImportProbe> ProbeAsync() => Task.Run(Probe);

      private static LocalImportProbe Probe()
      {
          try
          {
              var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
              var dirs = ClientDirNames.Select(n => Path.Combine(appData, n))
                  .Where(Directory.Exists).ToList();
              if (dirs.Count == 0)
              {
                  return new LocalImportProbe(false,
                      "未找到本机 Trae 客户端数据目录（%APPDATA%\\Trae CN 等）。请先安装并登录客户端，或改用「登录」按钮走浏览器登录。",
                      null, null, null, "");
              }

              string? token = null, session = null, deviceId = null;
              string source = "";
              int scanned = 0;
              bool sawEncryptedBag = false; // 客户端把凭证放加密袋的标志（决定失败文案）

              foreach (var dir in dirs)
              {
                  // ---- storage.json：设备号（键名内数字）+ 正文 JWT / 会话 ----
                  var storage = Path.Combine(dir, "User", "globalStorage", "storage.json");
                  if (File.Exists(storage))
                  {
                      scanned++;
                      var text = ReadText(storage, 8L * 1024 * 1024);
                      if (text.Contains("iCubeAuthInfo://icube.cloudide")) sawEncryptedBag = true;
                      if (deviceId == null)
                      {
                          var dm = DcIdRe.Match(text);
                          if (dm.Success) deviceId = dm.Groups[1].Value;
                      }
                      if (token == null)
                      {
                          var jm = JwtRe.Match(text);
                          if (jm.Success)
                          {
                              token = jm.Value;
                              source = Path.GetFileName(dir) + @"\storage.json";
                          }
                      }
                      session ??= ExtractSession(text);
                  }

                  // ---- leveldb / Session Storage：JWT 与会话的主要来源 ----
                  foreach (var sub in new[] { Path.Combine("Local Storage", "leveldb"), "Session Storage" })
                  {
                      var root = Path.Combine(dir, sub);
                      if (!Directory.Exists(root)) continue;
                      foreach (var f in LargestFirst(root))
                      {
                          if (++scanned > MaxScanFiles) break;
                          var text = ReadText(f, MaxScanBytes);
                          if (text.Length == 0) continue;
                          if (token == null)
                          {
                              var jm = JwtRe.Match(text);
                              if (jm.Success)
                              {
                                  token = jm.Value;
                                  source = Path.GetFileName(dir) + @"\" + sub + @"\" + Path.GetFileName(f);
                              }
                          }
                          session ??= ExtractSession(text);
                          if (token != null && session != null) break;
                      }
                      if (token != null) break;
                  }
                  if (token != null) break;
              }

              // 设备号是设备指纹，日志只记有无，不落具体值
              AppLog.Info("import",
                  $"探查完成：token={(token != null)} session={(session != null)} device={(deviceId != null)} 扫描{scanned}个文件");

              if (token == null && session == null)
              {
                  return new LocalImportProbe(false,
                      sawEncryptedBag
                          ? "该客户端版本的登录凭证是加密存储（iCubeAuthInfo 加密袋），只读提取无法解开——" +
                            "请改用「登录」按钮完成一次浏览器登录（WebView 有缓存，通常免验证码）。"
                          : deviceId != null
                              ? "找到客户端目录与设备号，但没读到登录凭证——客户端可能尚未登录。请先在 Trae 客户端登录一次后重试，或改用「登录」按钮。"
                              : "在客户端数据里没读到登录凭证。请先在 Trae 客户端登录一次后重试，或改用「登录」按钮。",
                      null, null, deviceId, "");
              }

              return new LocalImportProbe(true,
                  token != null ? "已读取到客户端登录凭证" : "已读取到客户端会话（将自动换取 Token）",
                  token, session, deviceId, source);
          }
          catch (Exception ex)
          {
              AppLog.Error("import", "探查异常: " + ex);
              return new LocalImportProbe(false, "读取客户端数据失败：" + ex.Message, null, null, null, "");
          }
      }

      /// <summary>目录内文件按大小降序（大文件更可能含目标键值）。</summary>
      private static IEnumerable<string> LargestFirst(string dir)
      {
          try
          {
              return new DirectoryInfo(dir).GetFiles()
                  .OrderByDescending(f => f.Length)
                  .Select(f => f.FullName)
                  .ToList();
          }
          catch
          {
              return Array.Empty<string>();
          }
      }

      /// <summary>Latin-1 逐字节读取（二进制 leveldb 也不丢字节，便于文本匹配）。</summary>
      private static string ReadText(string path, long maxBytes)
      {
          try
          {
              using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
              var len = Math.Min(fs.Length, maxBytes);
              var buf = new byte[len];
              int read = 0;
              while (read < len)
              {
                  int n = fs.Read(buf, read, (int)(len - read));
                  if (n <= 0) break;
                  read += n;
              }
              return Encoding.Latin1.GetString(buf, 0, read);
          }
          catch
          {
              return "";
          }
      }

      /// <summary>定位 X-Cloudide-Session 之后的会话值（键值间分隔符不固定，取首个 ≥16 位合法串）。</summary>
      private static string? ExtractSession(string text)
      {
          int idx = text.IndexOf(SessionKey, StringComparison.OrdinalIgnoreCase);
          if (idx < 0) return null;
          int i = idx + SessionKey.Length;
          while (i < text.Length && !IsSessionChar(text[i])) i++;
          int start = i;
          while (i < text.Length && IsSessionChar(text[i])) i++;
          return i - start >= 16 ? text[start..i] : null;
      }

      private static bool IsSessionChar(char c)
          => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '+';
  }