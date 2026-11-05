# -*- coding: utf-8 -*-
"""只遍历明文 JSON 键名与值长度（不解码、不打印任何值）。python -I inspect_serverdata.py"""
import io
import json
import os

p = os.path.join(os.environ["APPDATA"], "Trae CN", "User", "globalStorage", "storage.json")
d = json.load(io.open(p, encoding="utf-8"))

INTEREST = ("token", "session", "jwt", "auth", "secret", "credential", "device", "uid", "user")


def walk(o, depth=0):
    if depth > 6:
        return
    if isinstance(o, dict):
        for k, v in o.items():
            key = str(k)
            if isinstance(v, dict):
                print("  " * depth + "KEY " + key + " : dict")
                walk(v, depth + 1)
            elif isinstance(v, list):
                print("  " * depth + "KEY " + key + " : list len=" + str(len(v)))
            elif isinstance(v, str):
                mark = "  <INTEREST>" if any(t in key.lower() for t in INTEREST) else ""
                print("  " * depth + "KEY " + key + " : str len=" + str(len(v)) + mark)
            else:
                print("  " * depth + "KEY " + key + " : " + type(v).__name__)
    elif isinstance(o, list) and o:
        walk(o[0], depth + 1)


for name in ("iCubeServerData://icube.cloudide", "iCubeAuthInfo://usertag"):
    raw = d.get(name)
    print("=" * 6, name, type(raw).__name__)
    if isinstance(raw, str) and raw:
        try:
            walk(json.loads(raw))
        except Exception as e:
            print("  not plain JSON:", type(e).__name__)
