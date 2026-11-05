# -*- coding: utf-8 -*-
"""App.xaml 主题化改造1：删除内联 SolidColorBrush 定义、挂接主题字典、
把组件内联 hex 语义化为主题键。python -I patch_theme1.py"""
import io
import os

APP = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool", "App.xaml")
s = io.open(APP, encoding="utf-8").read()

# 1) 删除内联刷子定义块（Bloom…NavActiveIcoBrush），换合并字典
start = s.index('<SolidColorBrush x:Key="BgBrush"')
end_marker = '<SolidColorBrush x:Key="NavActiveIcoBrush" Color="#9CB2FF"/>'
end = s.index(end_marker) + len(end_marker)
block = s[start:end]
assert block.count("<SolidColorBrush") == 28, block.count("<SolidColorBrush")
merged = ('<ResourceDictionary.MergedDictionaries>\n'
          '        <!-- 主题字典（Light/Dark 运行时整表替换，见 ThemeService；\n'
          '             所有主题敏感引用必须用 DynamicResource） -->\n'
          '        <ResourceDictionary Source="Themes/Light.xaml"/>\n'
          '    </ResourceDictionary.MergedDictionaries>\n\n'
          '    ')
s = s[:start] + merged + s[end:]

# 2) 组件内联 hex → 主题键（全部替换）
hex_map = [
    ('Value="#FCFCFA"', 'Value="{DynamicResource GhostHoverBgBrush}"'),      # ghost hover
    ('Value="#AFB8C1"', 'Value="{DynamicResource GhostHoverBorderBrush}"'),
    ('Value="#F6F7F4"', 'Value="{DynamicResource DisabledBgBrush}"'),
    ('Value="#B7C1CF"', 'Value="{DynamicResource AccentDisabledBgBrush}"'),
    ('Value="#F1F3EF"', 'Value="{DynamicResource ChipBgBrush}"'),
    ('Value="#F8F9F6"', 'Value="{DynamicResource LogBoxBgBrush}"'),
    ('Value="#ECEEE9"', 'Value="{DynamicResource LogBoxBorderBrush}"'),
    ('Value="#6A7684"', 'Value="{DynamicResource LogTextBrush}"'),
    ('Value="#111821"', 'Value="{DynamicResource TermBgBrush}"'),
    ('Background="White"', 'Background="{DynamicResource SurfaceBrush}"'),
    ('<Setter Property="Background" Value="White"/>',
     '<Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/>'),
    ('BorderBrush="#C4CAD2"', 'BorderBrush="{DynamicResource CheckBorderBrush}"'),
]
for a, b in hex_map:
    n = s.count(a)
    assert n >= 1, "missing: " + a
    s = s.replace(a, b)

io.open(APP, "w", encoding="utf-8", newline="\n").write(s)
print("patch_theme1 done")
