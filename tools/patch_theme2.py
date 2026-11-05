# -*- coding: utf-8 -*-
"""全 XAML 扫描：StaticResource <key>Brush -> DynamicResource（主题换肤必需）。
同时处理页面里的 White 背景与斑马纹。python -I patch_theme2.py"""
import io
import glob
import os
import re

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool")
targets = [os.path.join(ROOT, "App.xaml")] + glob.glob(os.path.join(ROOT, "Views", "*.xaml"))

pat = re.compile(r"StaticResource ([A-Za-z0-9]+Brush)")
total = 0
for p in targets:
    s = io.open(p, encoding="utf-8").read()
    s2, n = pat.subn(r"DynamicResource \1", s)
    # 页面级语义化
    extra = []
    if p.endswith("UsagePage.xaml"):
        extra.append(('Value="#FBFCFA"', 'Value="{DynamicResource RowZebraBrush}"'))
    if p.endswith("SwitchPage.xaml"):
        extra.append(('Background="White"', 'Background="{DynamicResource SurfaceBrush}"'))
    if p.endswith(("ArchiveWizardWindow.xaml", "DialogWindow.xaml", "EulaWindow.xaml")):
        extra.append(('Background="White"', 'Background="{DynamicResource SurfaceBrush}"'))
    for a, b in extra:
        if a in s2:
            m = s2.count(a)
            s2 = s2.replace(a, b)
            print("  %s: %s x%d" % (os.path.basename(p), a, m))
    if s2 != s:
        io.open(p, "w", encoding="utf-8", newline="\n").write(s2)
    total += n
    print("%s: %d brush refs -> DynamicResource" % (os.path.basename(p), n))
print("TOTAL", total)
