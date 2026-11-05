# -*- coding: utf-8 -*-
"""打包 Release 构建为 dist/TraeTool-<ver>.zip（zip 根 = TraeTool.exe，供自动更新解压即用）。
用法: python -I tools/make_release.py [version]   （默认取 csproj <Version>）"""
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "TraeTool", "bin", "Release", "net8.0-windows")
DIST = os.path.join(ROOT, "dist")
CSPROJ = os.path.join(ROOT, "TraeTool", "TraeTool.csproj")


def detect_version() -> str:
    if len(sys.argv) > 1:
        return sys.argv[1]
    text = open(CSPROJ, encoding="utf-8").read()
    m = re.search(r"<Version>([^<]+)</Version>", text)
    if not m:
        sys.exit("csproj has no <Version>")
    return m.group(1).strip()


def main() -> None:
    ver = detect_version()
    exe = os.path.join(BIN, "TraeTool.exe")
    if not os.path.isfile(exe):
        sys.exit("missing TraeTool.exe — build Release first")

    os.makedirs(DIST, exist_ok=True)
    zip_path = os.path.join(DIST, f"TraeTool-{ver}.zip")

    skip_ext = {".pdb", ".xml"}
    n = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for r, _dirs, files in os.walk(BIN):
            for name in files:
                if os.path.splitext(name)[1].lower() in skip_ext:
                    continue
                p = os.path.join(r, name)
                rel = os.path.relpath(p, BIN).replace("\\", "/")
                z.write(p, rel)
                n += 1

    with zipfile.ZipFile(zip_path) as z:
        if "TraeTool.exe" not in z.namelist():
            sys.exit("TraeTool.exe not at zip root")

    print(f"OK {zip_path} files={n} size={os.path.getsize(zip_path)} bytes")


if __name__ == "__main__":
    main()
