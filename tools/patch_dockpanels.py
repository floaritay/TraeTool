# -*- coding: utf-8 -*-
"""横向 StackPanel(含换行文本) -> DockPanel 批量修丁。用法: python -I patch_dockpanels.py"""
import io
import os

VIEWS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool", "Views")


def patch(rel, pairs, closers=()):
    path = os.path.join(VIEWS, rel)
    s = io.open(path, encoding="utf-8").read()
    for old, new, cnt in pairs:
        n = s.count(old)
        assert n == cnt, "%s: expect %d, got %d: %r" % (rel, cnt, n, old[:70])
        s = s.replace(old, new)
    for old, new, cnt in closers:
        n = s.count(old)
        assert n == cnt, "%s closer: expect %d, got %d: %r" % (rel, cnt, n, old[:70])
        s = s.replace(old, new)
    io.open(path, "w", encoding="utf-8", newline="\n").write(s)
    print("ok", rel)


# ---------- 1) 用量页：状态横幅 + 临期横幅 ----------
patch("UsagePage.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                    <Path Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding StatusMessage}" FontSize="11.5"',
     '<DockPanel>\n'
     '                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding StatusMessage}" FontSize="11.5"', 1),
    ('<StackPanel Orientation="Horizontal">\n'
     '                    <Path Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource WarnBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding ExpiredText}" FontSize="11.5"',
     '<DockPanel>\n'
     '                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource WarnBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding ExpiredText}" FontSize="11.5"', 1),
], closers=[
    ('VerticalAlignment="Center"/>\n                </StackPanel>\n            </Border>',
     'VerticalAlignment="Center"/>\n                </DockPanel>\n            </Border>', 2),
])

# ---------- 2) 切换页：状态横幅 + notes 回滚行 ----------
patch("SwitchPage.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                    <Path Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding StatusMessage}"',
     '<DockPanel>\n'
     '                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>\n'
     '                    <TextBlock Text="{Binding StatusMessage}"', 1),
    ('<StackPanel Orientation="Horizontal">\n'
     '                        <Path Data="{x:Static icons:Icons.Shield}" Style="{StaticResource Ico}"\n'
     '                              Width="13" Height="13" Stroke="{StaticResource MuteBrush}" Margin="0,2,8,0"/>\n'
     '                        <TextBlock FontSize="10.5" Foreground="{StaticResource MuteBrush}" TextWrapping="Wrap"',
     '<DockPanel>\n'
     '                        <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Shield}" Style="{StaticResource Ico}"\n'
     '                              Width="13" Height="13" Stroke="{StaticResource MuteBrush}" Margin="0,2,8,0"/>\n'
     '                        <TextBlock FontSize="10.5" Foreground="{StaticResource MuteBrush}" TextWrapping="Wrap"', 1),
], closers=[
    ('rollback_*。"/>\n                    </StackPanel>',
     'rollback_*。"/>\n                    </DockPanel>', 1),
    ('TextWrapping="Wrap"\n                               VerticalAlignment="Center"/>\n                </StackPanel>',
     'TextWrapping="Wrap"\n                               VerticalAlignment="Center"/>\n                </DockPanel>', 1),
])

# ---------- 3) 账号页：状态横幅 + 底部引导 ----------
patch("AccountsPage.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                    <Path Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>',
     '<DockPanel>\n'
     '                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource PrimaryBrush}" Margin="0,0,8,0"/>', 1),
    ('<StackPanel Orientation="Horizontal">\n'
     '                <Path Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                      Width="13" Height="13" Stroke="{StaticResource MuteBrush}" Margin="2,1,8,0"/>',
     '<DockPanel>\n'
     '                <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                      Width="13" Height="13" Stroke="{StaticResource MuteBrush}" Margin="2,1,8,0"/>', 1),
], closers=[
    ('VerticalAlignment="Center"/>\n                </StackPanel>\n            </Border>',
     'VerticalAlignment="Center"/>\n                </DockPanel>\n            </Border>', 1),
    ('不会进入批量签到。"/>\n            </StackPanel>',
     '不会进入批量签到。"/>\n            </DockPanel>', 1),
])

# ---------- 4) 仪表盘：状态横幅（样式换行，扫描器漏报项） ----------
patch("DashboardPage.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                    <Path Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource DangerBrush}" Margin="0,0,8,0"/>',
     '<DockPanel>\n'
     '                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                          Width="14" Height="14" Stroke="{StaticResource DangerBrush}" Margin="0,0,8,0"/>', 1),
], closers=[
    ('Style="{StaticResource BannerText}"/>\n                </StackPanel>',
     'Style="{StaticResource BannerText}"/>\n                </DockPanel>', 1),
])

# ---------- 5) 签到页：说明条 + 9074 提示 + ±2s 提示 ----------
patch("CheckinPage.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                            <Path Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                                  Width="13" Height="13" Stroke="{StaticResource PrimaryBrush}"\n'
     '                                  Margin="0,1,8,0"/>',
     '<DockPanel>\n'
     '                            <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}" Style="{StaticResource Ico}"\n'
     '                                  Width="13" Height="13" Stroke="{StaticResource PrimaryBrush}"\n'
     '                                  Margin="0,1,8,0"/>', 1),
    ('<StackPanel Orientation="Horizontal" Margin="0,9,0,0">\n'
     '                            <Path Data="{x:Static icons:Icons.Info}"',
     '<DockPanel Margin="0,9,0,0">\n'
     '                            <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Info}"', 1),
    ('<StackPanel Orientation="Horizontal" Margin="0,8,0,0">\n'
     '                            <Path Data="{x:Static icons:Icons.Shield}"',
     '<DockPanel Margin="0,8,0,0">\n'
     '                            <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Shield}"', 1),
], closers=[
    ('勾选「参与签到」的账号。"/>\n                        </StackPanel>',
     '勾选「参与签到」的账号。"/>\n                        </DockPanel>', 1),
    ('冷却期间请勿重复点击。"/>\n                        </StackPanel>',
     '冷却期间请勿重复点击。"/>\n                        </DockPanel>', 1),
    ('规避风控。"/>\n                        </StackPanel>',
     '规避风控。"/>\n                        </DockPanel>', 1),
])

# ---------- 6) 设置页：盾牌提示 ----------
patch("SettingsPage.xaml", [
    ('<StackPanel Orientation="Horizontal" Margin="0,10,0,0">\n'
     '                        <Path Data="{x:Static icons:Icons.Shield}"',
     '<DockPanel Margin="0,10,0,0">\n'
     '                        <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Shield}"', 1),
    ('不会随档案上传。"/>\n                    </StackPanel>',
     '不会随档案上传。"/>\n                    </DockPanel>', 1),
])

# ---------- 7) 向导：警告框 + bullet 行 + 状态行限宽 ----------
patch("ArchiveWizardWindow.xaml", [
    ('<StackPanel Orientation="Horizontal">\n'
     '                            <Path Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                                  Width="14" Height="14" Stroke="{StaticResource WarnBrush}"\n'
     '                                  Margin="0,1,8,0"/>',
     '<DockPanel>\n'
     '                            <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Alert}" Style="{StaticResource Ico}"\n'
     '                                  Width="14" Height="14" Stroke="{StaticResource WarnBrush}"\n'
     '                                  Margin="0,1,8,0"/>', 1),
    ('<StackPanel Orientation="Horizontal" Margin="0,0,0,5">\n'
     '                                    <Path Data="{x:Static icons:Icons.Check}" Style="{StaticResource Ico}"\n'
     '                                          Width="12" Height="12" Stroke="{StaticResource SuccessBrush}"\n'
     '                                          Margin="1,2,7,0"/>',
     '<DockPanel Margin="0,0,0,5">\n'
     '                                    <Path DockPanel.Dock="Left" Data="{x:Static icons:Icons.Check}" Style="{StaticResource Ico}"\n'
     '                                          Width="12" Height="12" Stroke="{StaticResource SuccessBrush}"\n'
     '                                          Margin="1,2,7,0"/>', 1),
    ('<StackPanel DockPanel.Dock="Left" Orientation="Horizontal"\n'
     '                                    VerticalAlignment="Center">',
     '<StackPanel DockPanel.Dock="Left" Orientation="Horizontal"\n'
     '                                    VerticalAlignment="Center" MaxWidth="260">', 1),
], closers=[
    ('TextWrapping="Wrap"/>\n                        </StackPanel>\n                    </Border>',
     'TextWrapping="Wrap"/>\n                        </DockPanel>\n                    </Border>', 1),
    ('TextWrapping="Wrap"/>\n                                </StackPanel>\n                            </DataTemplate>',
     'TextWrapping="Wrap"/>\n                                </DockPanel>\n                            </DataTemplate>', 1),
])

# ---------- 8) 对话框：标题行（图标+标题可换行） ----------
patch("DialogWindow.xaml", [
    ('<StackPanel Orientation="Horizontal" Margin="0,0,0,12">\n'
     '                    <Border x:Name="IconCircle"',
     '<DockPanel Margin="0,0,0,12">\n'
     '                    <Border x:Name="IconCircle" DockPanel.Dock="Left"', 1),
], closers=[
    ('TextWrapping="Wrap"/>\n                </StackPanel>',
     'TextWrapping="Wrap"/>\n                </DockPanel>', 1),
])

print("ALL DOCKPANEL PATCHES DONE")
