# -*- coding: utf-8 -*-
"""切换页四按钮小字缩短（860 窄窗 actions 省宽，根治信息列被挤）。python -I patch_btnsubs.py"""
import io
import os

p = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool", "Views", "SwitchPage.xaml")
s = io.open(p, encoding="utf-8").read()
pairs = [
    # 建档小字是 Style trigger 的 Setter（两分支），都缩
    ('Value="备份客户端登录态"', 'Value="备份登录态"'),
    ('Value="重新备份登录态"', 'Value="重新备份"'),
    ('Text="重启客户端并切换"', 'Text="重启并切换"'),
    ('Text="校验档案（低频）"', 'Text="校验档案"'),
    ('Text="删除该账号档案"', 'Text="删除该档案"'),
]
for a, b in pairs:
    n = s.count(a)
    assert n == 1, "expect 1, got %d: %r" % (n, a)
    s = s.replace(a, b)
io.open(p, "w", encoding="utf-8", newline="\n").write(s)
print("sub captions shortened x5")
