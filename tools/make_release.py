# -*- coding: utf-8 -*-
"""打包发布产物到 dist/，并输出同名 .sha256 校验和（更新源可直接引用）。

两种模式：
  目录模式（默认）  : 打包 bin/Release/net8.0-windows 全目录 → dist/TraeTool-<ver>.zip
  单文件模式        : 先 dotnet publish 单文件（框架依赖，profile win-x64-singlefile），
                      再打包 publish 目录 → dist/TraeTool-<ver>.zip

用法: python -I tools/make_release.py [version] [--single-file]
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
    ver = detect_version(sys.argv[1:])
    src = publish_single_file() if single else BIN

    exe = os.path.join(src, "TraeTool.exe")
    if not os.path.isfile(exe):
        sys.exit(f"missing {exe} — build/publish Release first")

    os.makedirs(DIST, exist_ok=True)
    zip_path = os.path.join(DIST, f"TraeTool-{ver}{'-singlefile' if single else ''}.zip")

    skip_ext = {".pdb", ".xml"}
    n = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for r, _dirs, files in os.walk(src):
            for name in files:
                if os.path.splitext(name)[1].lower() in skip_ext:
                    continue
                p = os.path.join(r, name)
                rel = os.path.relpath(p, src).replace("\\", "/")
                z.write(p, rel)
                n += 1

    with zipfile.ZipFile(zip_path) as z:
        if "TraeTool.exe" not in z.namelist():
            sys.exit("TraeTool.exe not at zip root")

    digest = sha256_of(zip_path)
    sha_path = zip_path + ".sha256"
    # sha256sum 格式：<hex>  <文件名>（UpdateService 解析首段十六进制）
    with open(sha_path, "w", encoding="utf-8") as f:
        f.write(f"{digest}  {os.path.basename(zip_path)}\n")

    print(f"OK {zip_path} files={n} size={os.path.getsize(zip_path)} bytes")
    print(f"OK {sha_path} sha256={digest}")


if __name__ == "__main__":
    main()
