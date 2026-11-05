# -*- coding: utf-8 -*-
"""iCube 凭证袋结构勘察（用户已授权）。
规则：绝不打印任何完整密文/令牌值——JSON 只打键名与值长度；
base64 解出的二进制只打 ASCII 字段名（<=24 且非密文长相），
JWT 只报 有/无+位置+长度，长的 ASCII 串一律 <redacted len=N>。
python -I inspect_authbag.py
"""
import base64
import io
import json
import os
import re

p = os.path.join(os.environ["APPDATA"], "Trae CN", "User", "globalStorage", "storage.json")
d = json.load(io.open(p, encoding="utf-8"))

SECRET_RE = re.compile(r"^[A-Za-z0-9_\-.+/=]{20,}$")
JWT_RE = re.compile(rb"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")
ASCII_RE = re.compile(rb"[\x20-\x7e]{4,}")


def looks_secret(s):
    return bool(SECRET_RE.match(s))


def walk(o, pre="", depth=0):
    if depth > 5:
        return
    if isinstance(o, dict):
        for k, v in o.items():
            key = str(k)
            if isinstance(v, (dict, list)):
                print("  " * depth + f"KEY {key} : {type(v).__name__}")
                walk(v, pre, depth + 1)
            elif isinstance(v, str):
                mark = ""
                lk = key.lower()
                if any(t in lk for t in ("token", "session", "jwt", "auth", "secret", "credential")):
                    mark = " <FIELD-OF-INTEREST>"
                print("  " * depth + f"KEY {key} : str len={len(v)}{mark}")
            else:
                print("  " * depth + f"KEY {key} : {type(v).__name__} = {v}")
    elif isinstance(o, list):
        print("  " * depth + f"list len={len(o)}")
        if o:
            walk(o[0], pre, depth + 1)


for name in ("iCubeAuthInfo://icube.cloudide",
             "iCubeAuthInfo://icube-dc:1871831329934523",
             "iCubeAuthInfo://usertag",
             "iCubeServerData://icube.cloudide"):
    raw = d.get(name)
    print("=" * 8, name, "type", type(raw).__name__,
          "len" if isinstance(raw, str) else "", len(raw) if isinstance(raw, str) else "")
    if not isinstance(raw, str) or not raw:
        continue

    # 1) 是不是明文 JSON？
    try:
        obj = json.loads(raw)
        print("  format: plain JSON")
        walk(obj)
        continue
    except Exception:
        pass

    # 2) base64？
    blob = None
    try:
        pad = "=" * (-len(raw) % 4)
        blob = base64.b64decode(raw + pad, validate=False)
        print(f"  format: base64 -> {len(blob)} bytes, head={blob[:8].hex()}")
    except Exception as e:
        print("  format: opaque (not json, not base64):", type(e).__name__)
        print("  raw head (structure only):", repr(raw[:24]))
        continue

    if blob is None or not blob:
        continue

    # 3) 有没有 JWT？（只报位置与长度）
    m = JWT_RE.search(blob)
    print("  JWT_FLAG:", bool(m), "len", len(m.group()) if m else 0,
          "pos", m.start() if m else -1)

    # 4) 熵判断（是否像加密）
    unique_ratio = len(set(blob)) / max(1, len(blob))
    print(f"  unique_byte_ratio={unique_ratio:.2f}",
          "(>0.85 looks encrypted/compressed)")

    # 5) ASCII 字段名（<=24 且非密文长相才显示；其余只报长度）
    seen = set()
    shown = 0
    for mm in ASCII_RE.finditer(blob):
        s = mm.group().decode("ascii", "ignore")
        if s in seen:
            continue
        seen.add(s)
        if looks_secret(s) or len(s) > 24:
            print(f"    <redacted run len={len(s)} at={mm.start()}>")
        else:
            print(f"    field? {s!r} at={mm.start()}")
            shown += 1
        if shown > 40:
            break
