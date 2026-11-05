# -*- coding: utf-8 -*-
"""头像三页改造（首字/色环 → 色环+真实头像双椭圆）+ 账号页学生认证徽标。
python -I patch_avatars.py"""
import io
import os

VIEWS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "TraeTool", "Views")


def replace_block(path, start_marker, end_marker, make_new):
    p = os.path.join(VIEWS, path)
    s = io.open(p, encoding="utf-8").read()
    i = s.index(start_marker)
    # 行首缩进
    line_start = s.rfind("\n", 0, i) + 1
    ind = s[line_start:i]
    j = s.index(end_marker, i) + len(end_marker)
    new = make_new(ind)
    s = s[:line_start] + new + s[j:]
    io.open(p, "w", encoding="utf-8", newline="\n").write(s)
    print("ok", path, "avatar block")


def avatar_block(ind, size, radius, font, has_grid_col):
    col = 'Grid.Column="0" ' if has_grid_col else ""
    return (
        f'{ind}<Border {col}Width="{size}" Height="{size}" CornerRadius="{radius}" VerticalAlignment="Center">\n'
        f'{ind}    <Grid>\n'
        f'{ind}        <Ellipse Fill="{{Binding AvatarHue, Converter={{x:Static views:HueToBrushConverter.Instance}}}}"\n'
        f'{ind}                 Visibility="{{Binding AvatarImage, Converter={{x:Static views:NullToVisConverter.Instance}}}}"/>\n'
        f'{ind}        <Ellipse Fill="{{Binding AvatarImage, Converter={{x:Static views:ImageToBrushConverter.Instance}}}}"\n'
        f'{ind}                 Visibility="{{Binding AvatarImage, Converter={{x:Static views:NotNullToVisConverter.Instance}}}}"/>\n'
        f'{ind}        <TextBlock Text="{{Binding Initial}}" FontSize="{font}" FontWeight="SemiBold" Foreground="White"\n'
        f'{ind}                   HorizontalAlignment="Center" VerticalAlignment="Center"\n'
        f'{ind}                   Visibility="{{Binding AvatarImage, Converter={{x:Static views:NullToVisConverter.Instance}}}}"/>\n'
        f'{ind}    </Grid>\n'
        f'{ind}></Border>'
    )


replace_block(
    "AccountsPage.xaml",
    '<Border Grid.Column="0" Width="36" Height="36" CornerRadius="18"',
    '</Border>',
    lambda ind: avatar_block(ind, 36, 18, "14", True),
)

replace_block(
    "SwitchPage.xaml",
    '<Border Grid.Column="0" Width="36" Height="36" CornerRadius="18"',
    '</Border>',
    lambda ind: avatar_block(ind, 36, 18, "14", True),
)

replace_block(
    "DashboardPage.xaml",
    '<Border Width="30" Height="30" CornerRadius="15"',
    '</Border>',
    lambda ind: avatar_block(ind, 30, 15, "12", False),
)

# 账号页：未登录徽标之后追加「学生认证」徽标
p = os.path.join(VIEWS, "AccountsPage.xaml")
s = io.open(p, encoding="utf-8").read()
anchor = ('<TextBlock Text="未登录" FontSize="10"\n'
          '                                                                   Foreground="{DynamicResource WarnBrush}"/>\n'
          '                                                    </Border>')
assert s.count(anchor) == 1, s.count(anchor)
badge = anchor + ('\n'
                  '                                                    <Border Style="{StaticResource Badge}"\n'
                  '                                                            Background="{DynamicResource SuccessSoftBrush}"\n'
                  '                                                            BorderBrush="{DynamicResource SuccessLineBrush}"\n'
                  '                                                            Visibility="{Binding StudentVerified, Converter={x:Static views:BoolToVisConverter.TrueInstance}}">\n'
                  '                                                        <TextBlock Text="学生认证" FontSize="10"\n'
                  '                                                                   Foreground="{DynamicResource SuccessBrush}"/>\n'
                  '                                                    </Border>')
s = s.replace(anchor, badge)
io.open(p, "w", encoding="utf-8", newline="\n").write(s)
print("ok AccountsPage.xaml student badge")
print("ALL AVATAR PATCHES DONE")
