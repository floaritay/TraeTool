# -*- coding: utf-8 -*-
"""从原型 index.html 提取 Lucide SVG symbol，转成 WPF Geometry C# 源码。
输出: TraeTool/Resources/Icons.cs
用法: python -I tools/gen_icons.py   （在仓库根目录执行）
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(_ROOT, "docs", "prototype", "index.html")
OUT = os.path.join(_ROOT, "TraeTool", "Resources", "Icons.cs")

html = open(SRC, "r", encoding="utf-8").read()
symbols = re.findall(r'<symbol id="(ic-[a-z0-9-]+)"[^>]*>(.*?)</symbol>', html, re.S)
if not symbols:
    print("no symbols found", file=sys.stderr)
    sys.exit(1)


def num(v):
    """格式化数字：去尾零。"""
    try:
        f = float(v)
    except (TypeError, ValueError):
        return str(v)
    if f == int(f):
        return str(int(f))
    return ("%.4f" % f).rstrip("0").rstrip(".")


def rect_path(x, y, w, h, rx):
    x, y, w, h = (float(x), float(y), float(w), float(h))
    rx = float(rx) if rx else 0.0
    ry = float(rx) if rx else 0.0
    if rx <= 0:
        return "M%s %s H%s V%s H%s Z" % (num(x), num(y), num(x + w), num(y + h), num(x))
    rx = min(rx, w / 2)
    ry = min(ry, h / 2)
    # 圆角矩形四段弧，顺时针（y 轴向下 sweep=1）；每段弧的终点必须是相邻边的起点：
    # 上边 H 到 (x+w-rx,y) → 右上角弧到 (x+w,y+ry) → 右边 V 到 (x+w,y+h-ry)
    # → 右下角弧到 (x+w-rx,y+h) → 下边 H 到 (x+rx,y+h) → 左下角弧到 (x,y+h-ry)
    # → 左边 V 到 (x,y+ry) → 左上角弧回到 (x+rx,y) 闭合
    return ("M{x0} {yT} H{xR} A{rx} {ry} 0 0 1 {xW} {yRT} V{yRB} A{rx} {ry} 0 0 1 {xR} {yB} "
            "H{x0} A{rx} {ry} 0 0 1 {xL} {yRB} V{yRT} A{rx} {ry} 0 0 1 {x0} {yT} Z").format(
        x0=num(x + rx), xR=num(x + w - rx), xW=num(x + w), xL=num(x),
        yT=num(y), yRT=num(y + ry), yRB=num(y + h - ry), yB=num(y + h),
        rx=num(rx), ry=num(ry))


def circle_path(cx, cy, r):
    cx, cy, r = float(cx), float(cy), float(r)
    return ("M{a} {b} A{r} {r} 0 1 0 {c} {b} A{r} {r} 0 1 0 {a} {b} Z").format(
        a=num(cx - r), b=num(cy), c=num(cx + r), r=num(r))


def line_path(x1, y1, x2, y2):
    return "M%s %s L%s %s" % (num(x1), num(y1), num(x2), num(y2))


def polyline_path(points):
    pts = points.replace(",", " ").split()
    if len(pts) < 4:
        return ""
    it = iter(pts)
    out = ["M%s %s" % (next(it), next(it))]
    for x, y in zip(it, it):
        out.append("L%s %s" % (x, y))
    return " ".join(out)


def shapes_to_d(inner):
    """<symbol> 内容 → 单条 path d 串（多个形状拼接）。"""
    try:
        root = ET.fromstring("<svg>" + inner + "</svg>")
    except ET.ParseError as e:
        print("XML parse error: %s\n%s" % (e, inner[:160]), file=sys.stderr)
        sys.exit(1)
    parts = []
    for el in root.iter():
        tag = el.tag.split("}")[-1]
        if tag == "path":
            d = el.get("d")
            if d:
                parts.append(d.strip())
        elif tag == "rect":
            parts.append(rect_path(el.get("x", 0), el.get("y", 0),
                                   el.get("width"), el.get("height"), el.get("rx")))
        elif tag == "circle":
            parts.append(circle_path(el.get("cx"), el.get("cy"), el.get("r")))
        elif tag == "line":
            parts.append(line_path(el.get("x1"), el.get("y1"), el.get("x2"), el.get("y2")))
        elif tag == "polyline" and el.get("points"):
            parts.append(polyline_path(el.get("points")))
        elif tag in ("polygon",):
            print("warn: polygon not supported", file=sys.stderr)
    # 拼接守则：多段 path 合成一条时，各段必须自带绝对的前导 moveto 序列——
    # 原始每段 path 元素以 (0,0) 为基准，直接拼接后段首 m/隐式坐标对会以
    # 上一段终点为基准继续偏移（× 变 < 之类）。归一化：把段首的 moveto +
    # 隐式 lineto 序列全部换算成绝对坐标（基准 (0,0)），其余命令原样保留。
    fixed = []
    for p in parts:
        if not p:
            continue
        fixed.append(_normalize_leading_moves(p))
    return " ".join(fixed)


_num_re = re.compile(r"\s*([-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?)")


def _normalize_leading_moves(d):
    """把段首 `m/M + 若干数对`（隐式 lineto）换算成绝对 M/L 序列；
    停在第一个非数对字符处，其余部分原样返回。"""
    if d[0] not in "mM":
        return d  # 非 moveto 起始（Lucide 罕见），交给运行时报错暴露
    rel = d[0] == "m"
    pos = 1
    cx = cy = 0.0  # 原始上下文：段首基准 (0,0)
    out = []
    first = True
    while pos < len(d):
        m1 = _num_re.match(d, pos)
        if not m1:
            break
        m2 = _num_re.match(d, m1.end())
        if not m2:
            break
        x, y = float(m1.group(1)), float(m2.group(1))
        if first:
            # 首对：m 从 (0,0) 起算，换算即绝对坐标；M 本就绝对
            cx, cy = (cx + x, cy + y) if rel else (x, y)
            out.append("M%s %s" % (num(cx), num(cy)))
            first = False
        else:
            # 后续隐式对：小写=相对，大写=绝对 → 统一输出绝对 L
            cx, cy = (cx + x, cy + y) if rel else (x, y)
            out.append("L%s %s" % (num(cx), num(cy)))
        pos = m2.end()
    rest = d[pos:].lstrip()
    if not out:
        return d
    return " ".join(out) + (" " + rest if rest else "")


def has_unsupported(d):
    """检测平滑命令 S/s/T/t（WPF StreamGeometry 不支持）。"""
    out = []
    i = 0
    while i < len(d):
        c = d[i]
        if c in "SsTt":
            # 只有作为独立命令（前一个是分隔符/数字后空白或开头）才算
            prev = d[i - 1] if i else " "
            if not (prev.isdigit() or prev == "." or prev == "-"):
                out.append(c)
        i += 1
    return out


def cs_name(icon_id):
    body = icon_id[len("ic-"):] if icon_id.startswith("ic-") else icon_id
    parts = re.split(r"[-_]", body)
    return "".join(p.capitalize() for p in parts)


warn_total = 0
lines = []
for icon_id, inner in symbols:
    d = shapes_to_d(inner)
    if not d:
        print("empty:", icon_id, file=sys.stderr)
        continue
    bad = has_unsupported(d)
    if bad:
        warn_total += 1
        print("UNSUPPORTED cmds %s %s: %s" % (icon_id, set(bad), d[:80]), file=sys.stderr)
    lines.append('    public static readonly Geometry %s = G("%s");' % (cs_name(icon_id), d))

src = (
    "using System.Windows.Media;\n"
    "\n"
    "namespace TraeTool.Resources;\n"
    "\n"
    "/// <summary>Lucide 线性图标几何（stroke 1.75 / 24 网格，转自 UI 原型 SVG 雪碧图）。\n"
    "/// 用法: Data=\"{x:Static icons:Icons.Xxx}\"，配 Path Stretch=Uniform + Stroke。</summary>\n"
    "public static class Icons\n"
    "{\n"
    "    private static Geometry G(string d)\n"
    "    {\n"
    "        var g = Geometry.Parse(d);\n"
    "        g.Freeze(); // 跨线程安全（静态初始化可能不在 UI 线程）\n"
    "        return g;\n"
    "    }\n"
    "\n"
    + "\n".join(lines) + "\n"
    "}\n"
)
import os
os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, "w", encoding="utf-8", newline="\r\n").write(src)
print("wrote %s (%d icons, %d unsupported)" % (OUT, len(lines), warn_total))
