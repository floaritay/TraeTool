# -*- coding: utf-8 -*-
"""搭建本地自动更新自检源：把当前构建打包成 <csproj 版本> + version.json（含 sha256）。
python -I tools/make_updatefeed.py"""
import hashlib
import json
import os
import re
import time
import zipfile

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(_ROOT, "TraeTool", "bin", "Release", "net8.0-windows")
FEED = os.path.join(_ROOT, "_updatefeed")
CSPROJ = os.path.join(_ROOT, "TraeTool", "TraeTool.csproj")

# 版本号单一来源：csproj <Version>
_text = open(CSPROJ, encoding="utf-8").read()
_m = re.search(r"<Version>([^<]+)</Version>", _text)
if not _m:
    raise SystemExit("csproj has no <Version>")
VERSION = _m.group(1).strip()
ZIP_NAME = f"TraeTool-{VERSION}.zip"

os.makedirs(FEED, exist_ok=True)

# 安装标记（bat 覆盖后应出现在程序目录 = 全链路证据）
marker = os.path.join(BIN, "update-selftest-marker.txt")
with open(marker, "w", encoding="utf-8") as f:
    f.write("TraeTool update selftest OK %s\n" % time.strftime("%Y-%m-%d %H:%M:%S"))

zip_path = os.path.join(FEED, ZIP_NAME)
n = 0
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(BIN):
        for name in files:
            p = os.path.join(root, name)
            rel = os.path.relpath(p, BIN)
            z.write(p, rel)
            n += 1

_h = hashlib.sha256()
with open(zip_path, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        _h.update(chunk)
digest = _h.hexdigest()

feed = {
    "version": VERSION,
    "url": "http://127.0.0.1:8765/" + ZIP_NAME,
    "sha256": digest,
    "notes": "端到端自检更新（本地 feed，验证后可删）",
}
with open(os.path.join(FEED, "version.json"), "w", encoding="utf-8") as f:
    json.dump(feed, f, ensure_ascii=False, indent=2)

print("feed ready: v%s, %d files, zip=%d bytes, sha256=%s"
      % (VERSION, n, os.path.getsize(zip_path), digest))
