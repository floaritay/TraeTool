# -*- coding: utf-8 -*-
"""patch_dockpanels 续：SettingsPage / ArchiveWizardWindow / DialogWindow。python -I patch_dockpanels2.py"""
import io
import os

VIEWS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool", "Views")


def patch(rel, pairs):
    path = os.path.join(VIEWS, rel)
    s = io.open(path, encoding="utf-8").read()
    for old, new, cnt in pairs:
        n = s.count(old)
        assert n == cnt, "%s: expect %d, got %d: %r" % (rel, cnt, n, old[:70])
        s = s.replace(old, new)
    io.open(path, "w", encoding="utf-8", newline="\n").write(s)
    print("ok", rel)


# ---------- 设置页：盾牌提示 ----------
patch("SettingsPage.xaml", [
    ('<StackPanel Orientation="Horizontal" Margin="0,10,0,0">\n'
     '                        <Path Data="{x:Static icons:Icons.Shield}"',
     '<DockPanel Margin="0,10,0,0">\n'
     '                        <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Shield}"', 1),
    ('不会随档案上传。"\n'
     '                                   FontSize="10.5" Foreground="{StaticResource MuteBrush}"\n'
     '                                   TextWrapping="Wrap"/>\n'
     '                    </StackPanel>',
     '不会随档案上传。"\n'
     '                                   FontSize="10.5" Foreground="{StaticResource MuteBrush}"\n'
     '                                   TextWrapping="Wrap"/>\n'
     '                    </DockPanel>', 1),
])

# ---------- 向导：警告框 + bullet 行 + 状态行限宽 ----------
patch("ArchiveWizardWindow.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                            <Path Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                                  Width="14" Height="14" Stroke="{StaticResource WarnBrush}"\n'
     '                                  Margin="0,1,8,0"/>',
     '<DockPanel>\n'
     '                            <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                                  Width="14" Height="14" Stroke="{StaticResource WarnBrush}"\n'
     '                                  Margin="0,1,8,0"/>', 1),
    ('TextWrapping="Wrap"/>\n'
     '                        </StackPanel>\n'
     '                    </Border>',
     'TextWrapping="Wrap"/>\n'
     '                        </DockPanel>\n'
     '                    </Border>', 1),
    ('<StackPanel Orientation="Horizontal" Margin="0,0,0,5">\n'
     '                                    <Path Data="{x:Static icons:Icons.Check}" Style="{StaticResource Ico}"\n'
     '                                          Width="12" Height="12" Stroke="{StaticResource SuccessBrush}"\n'
     '                                          Margin="1,2,7,0"/>',
     '<DockPanel Margin="0,0,0,5">\n'
     '                                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Check}" Style="{StaticResource Ico}"\n'
     '                                          Width="12" Height="12" Stroke="{StaticResource SuccessBrush}"\n'
     '                                          Margin="1,2,7,0"/>', 1),
    ('TextWrapping="Wrap"/>\n'
     '                                </StackPanel>\n'
     '                            </DataTemplate>',
     'TextWrapping="Wrap"/>\n'
     '                                </DockPanel>\n'
     '                            </DataTemplate>', 1),
    ('<StackPanel DockPanel.Dock="Left" Orientation="Horizontal"\n'
     '                                    VerticalAlignment="Center">',
     '<StackPanel DockPanel.Dock="Left" Orientation="Horizontal"\n'
     '                                    VerticalAlignment="Center" MaxWidth="260">', 1),
])

# ---------- 对话框：标题行 ----------
patch("DialogWindow.xaml", [
    ('<StackPanel Orientation="Horizontal" Margin="0,0,0,12">\n'
     '                    <Border x:Name="IconCircle"',
     '<DockPanel Margin="0,0,0,12">\n'
     '                    <Border x:Name="IconCircle" DockPanel.Dock="Left"', 1),
    ('VerticalAlignment="Center"\n'
     '                               TextWrapping="Wrap"/>\n'
     '                </StackPanel>',
     'VerticalAlignment="Center"\n'
     '                               TextWrapping="Wrap"/>\n'
     '                </DockPanel>', 1),
])

print("REMAINING PATCHES DONE")
