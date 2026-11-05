# -*- coding: utf-8 -*-
"""只看结构不看值：Trae CN 客户端凭证袋/设备标识格式勘察。python -I inspect_trae_client.py"""
import io
import json
import os

base = os.path.join(os.environ["APPDATA"], "Trae CN")
p = os.path.join(base, "User", "globalStorage", "storage.json")
d = json.load(io.open(p, encoding="utf-8"))

# 1) telemetry 设备标识的“形状”（只打印前10字符，用于等格式重新生成）
for k in ("telemetry.machineId", "telemetry.sqmId", "telemetry.devDeviceId"):
    v = d.get(k, "")
    print("FMT", k, "=", repr(v[:10]), "len", len(v))

# 2) 凭证袋结构（只打印键名与值类型/长度，值截断6字符）
def walk(o, pre=""):
    if isinstance(o, dict):
        for kk, vv in o.items():
            if isinstance(vv, (dict, list)):
                print("  " * pre, kk, ":", type(vv).__name__)
                walk(vv, pre + 1)
            else:
                s = str(vv)
                print("  " * pre, kk, ":", type(vv).__name__, "len", len(s), "head", repr(s[:6]))

for k in ("iCubeAuthInfo://icube.cloudide",
          "iCubeAuthInfo://icube-dc:1871831329934523",
          "iCubeServerData://icube.cloudide"):
    raw = d.get(k)
    print("---", k, type(raw).__name__)
    if isinstance(raw, str):
        try:
            walk(json.loads(raw))
        except Exception as e:
            print("  not json:", e, repr(raw[:30]))

# 3) leveldb 是否存在 web 同款 token 键
ls = os.path.join(base, "Local Storage", "leveldb")
for f in os.listdir(ls):
    try:
        b = open(os.path.join(ls, f), "rb").read()
        if b"Cloud-IDE-Token" in b:
            print("LEVELDB Cloud-IDE-Token in", f)
        if b"Cloudide-Session" in b or b"X-Cloudide" in b:
            print("LEVELDB session marker in", f)
    except Exception:
        pass
print("leveldb files:", os.listdir(ls))

# 4) aha / ahanet 目录文件名
for sub in ("aha", "ahanet"):
    root = os.path.join(base, sub)
    if os.path.isdir(root):
        for r, dirs, files in os.walk(root):
            rel = os.path.relpath(r, base)
            print("DIR", rel, "->", files[:8])
            if rel.count(os.sep) >= 2:
                dirs.clear()
