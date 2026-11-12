# -*- coding: utf-8 -*-
"""打包发布产物到 dist/，并输出同名 .sha256 校验和（更新源可直接引用）。

两种模式：
  目录模式（默认）  : 打包 bin/Release/net8.0-windows 全目录 → dist/TraeTool-<ver>.zip
  单文件模式        : 先 dotnet publish 单文件（框架依赖，profile win-x64-singlefile），
                      再打包 publish 目录 → dist/TraeTool-<ver>.zip

默认只保留 Windows 的原生库（runtimes/win-*/）：包内 runtimes/ 未压缩约 24.5 MB，
其中只有 win-* 会在 Windows 上加载，linux/osx/wasm 等既不会被用到又让更新包大出数倍。
加 --all-runtimes 可保留完整 runtimes/（排查或需要非 Windows 负载时用）。

用法: python -I tools/make_release.py [version] [--single-file] [--all-runtimes]
      version 省略时取 TraeTool.csproj 的 <Version>（版本号单一来源）。
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "TraeTool", "bin", "Release", "net8.0-windows")
SINGLE_FILE_BIN = os.path.join(BIN, "win-x64", "publish")
DIST = os.path.join(ROOT, "dist")
CSPROJ = os.path.join(ROOT, "TraeTool", "TraeTool.csproj")

# 保留的运行时标识前缀（runtimes/<rid>/...）
KEEP_RID_PREFIX = "win-"


def keep_entry(rel: str, all_runtimes: bool) -> bool:
    """该条目是否打进包里。rel 为相对打包根目录的路径。"""
    if all_runtimes or not rel.startswith("runtimes/"):
        return True
    parts = rel.split("/")
    return len(parts) > 1 and parts[1].startswith(KEEP_RID_PREFIX)


def detect_version(argv: list) -> str:
    args = [a for a in argv if not a.startswith("--")]
    if args:
        return args[0]
    text = open(CSPROJ, encoding="utf-8").read()
    m = re.search(r"<Version>([^<]+)</Version>", text)
    if not m:
        sys.exit("csproj has no <Version>")
    return m.group(1).strip()


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def publish_single_file() -> str:
    """调用 dotnet publish 单文件；需要联网还原 ILLink.Tasks（首次）。"""
    if shutil.which("dotnet") is None:
        sys.exit("dotnet not found — single-file publish needs the .NET 8 SDK")
    cmd = [
        "dotnet", "publish", os.path.join("TraeTool", "TraeTool.csproj"),
        "-c", "Release", "-p:PublishProfile=win-x64-singlefile", "--nologo",
    ]
    print("run:", " ".join(cmd))
    if subprocess.call(cmd, cwd=ROOT) != 0:
        sys.exit("dotnet publish failed")
    return SINGLE_FILE_BIN


def main() -> None:
    single = "--single-file" in sys.argv
    all_runtimes = "--all-runtimes" in sys.argv
    ver = detect_version(sys.argv[1:])
    src = publish_single_file() if single else BIN

    exe = os.path.join(src, "TraeTool.exe")
    if not os.path.isfile(exe):
        sys.exit(f"missing {exe} — build/publish Release first")

    os.makedirs(DIST, exist_ok=True)
    zip_path = os.path.join(DIST, f"TraeTool-{ver}{'-singlefile' if single else ''}.zip")

    skip_ext = {".pdb", ".xml"}
    n = 0
    skipped = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for r, _dirs, files in os.walk(src):
            for name in files:
                if os.path.splitext(name)[1].lower() in skip_ext:
                    continue
                p = os.path.join(r, name)
                rel = os.path.relpath(p, src).replace("\\", "/")
                if not keep_entry(rel, all_runtimes):
                    skipped += 1
                    continue
                z.write(p, rel)
                n += 1

    with zipfile.ZipFile(zip_path) as z:
        names = z.namelist()
        if "TraeTool.exe" not in names:
            sys.exit("TraeTool.exe not at zip root")
        # Windows 上下文必需的原生库缺失时直接失败，别产出一个装上也跑不起来的包
        for required in ("runtimes/win-x64/native/e_sqlite3.dll",
                         "runtimes/win-x64/native/WebView2Loader.dll"):
            if required not in names:
                sys.exit(f"missing {required} in package")

    digest = sha256_of(zip_path)
    sha_path = zip_path + ".sha256"
    # sha256sum 格式：<hex>  <文件名>（UpdateService 解析首段十六进制）
    with open(sha_path, "w", encoding="utf-8") as f:
        f.write(f"{digest}  {os.path.basename(zip_path)}\n")

    print(f"OK {zip_path} files={n} size={os.path.getsize(zip_path)} bytes "
          f"(skipped non-Windows runtime files: {skipped})")
    print(f"OK {sha_path} sha256={digest}")


if __name__ == "__main__":
    main()
