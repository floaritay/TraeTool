using System.Windows.Media;

namespace TraeTool.Resources;

/// <summary>Lucide 线性图标几何（stroke 1.75，24 网格）。
/// 用法: Data="{x:Static icons:Icons.Xxx}"，配 Path Stretch=Uniform + Stroke。</summary>
public static class Icons
{
    private static Geometry G(string d)
    {
        var g = Geometry.Parse(d);
        g.Freeze(); // 跨线程安全（静态初始化可能不在 UI 线程）
        return g;
    }

    public static readonly Geometry Dashboard = G("M4 3 H9 A1 1 0 0 1 10 4 V11 A1 1 0 0 1 9 12 H4 A1 1 0 0 1 3 11 V4 A1 1 0 0 1 4 3 Z M15 3 H20 A1 1 0 0 1 21 4 V7 A1 1 0 0 1 20 8 H15 A1 1 0 0 1 14 7 V4 A1 1 0 0 1 15 3 Z M15 12 H20 A1 1 0 0 1 21 13 V20 A1 1 0 0 1 20 21 H15 A1 1 0 0 1 14 20 V13 A1 1 0 0 1 15 12 Z M4 16 H9 A1 1 0 0 1 10 17 V20 A1 1 0 0 1 9 21 H4 A1 1 0 0 1 3 20 V17 A1 1 0 0 1 4 16 Z");
    public static readonly Geometry CalendarCheck = G("M8 2 v4 M16 2 v4 M5 4 H19 A2 2 0 0 1 21 6 V20 A2 2 0 0 1 19 22 H5 A2 2 0 0 1 3 20 V6 A2 2 0 0 1 5 4 Z M3 10 h18 M9 16 L11 18 L15 14");
    public static readonly Geometry Users = G("M16 21 v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M5 7 A4 4 0 1 0 13 7 A4 4 0 1 0 5 7 Z M22 21 v-2a4 4 0 0 0-3-3.87 M16 3.13 a4 4 0 0 1 0 7.75");
    public static readonly Geometry Switch = G("M8 3 L4 7 l4 4 M4 7 h16 M16 21 L20 17 L16 13 M20 17 H4");
    public static readonly Geometry Chart = G("M3 3 v18h18 M18 17 V9 M13 17 V5 M8 17 v-3");
    public static readonly Geometry Settings = G("M12.22 2 h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12 Z");
    public static readonly Geometry Refresh = G("M3 12 a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3 v5h-5 M21 12 a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16 H3v5");
    public static readonly Geometry Zap = G("M4 14 a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z");
    public static readonly Geometry Plus = G("M5 12 h14 M12 5 v14");
    public static readonly Geometry Shield = G("M20 13 c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z M9 12 L11 14 L15 10");
    public static readonly Geometry Activity = G("M22 12 h-4l-3 9L9 3l-3 9H2");
    public static readonly Geometry Alert = G("M21.73 18 L13.73 4 a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3 M12 9 v4 M12 17 h.01");
    public static readonly Geometry Info = G("M2 12 A10 10 0 1 0 22 12 A10 10 0 1 0 2 12 Z M12 16 v-4 M12 8 h.01");
    public static readonly Geometry X = G("M18 6 L6 18 M6 6 L18 18");
    public static readonly Geometry Check = G("M20 6 L9 17 l-5-5");
    public static readonly Geometry CheckCheck = G("M18 6 L7 17 l-5-5 M22 10 L14.5 17.5 L13 16");
    public static readonly Geometry Globe = G("M12 2 a10 10 0 1 0 0 20 a10 10 0 0 0 0-20 M2 12 h20 M12 2 a15.3 15.3 0 0 1 4 10 a15.3 15.3 0 0 1-4 10 a15.3 15.3 0 0 1-4-10 a15.3 15.3 0 0 1 4-10");
    public static readonly Geometry Server = G("M2 2 H22 V10 H2 Z M2 14 H22 V22 H2 Z M6 6 h.01 M6 18 h.01");
    public static readonly Geometry Monitor = G("M4 3 H20 A2 2 0 0 1 22 5 V15 A2 2 0 0 1 20 17 H4 A2 2 0 0 1 2 15 V5 A2 2 0 0 1 4 3 Z M8 21 L16 21 M12 17 L12 21");
    public static readonly Geometry Database = G("M3 5 V19A9 3 0 0 0 21 19V5 M3 12 A9 3 0 0 0 21 12");
    public static readonly Geometry Minus = G("M5 12 h14");
    public static readonly Geometry Square = G("M6.5 5.5 H17.5 A1 1 0 0 1 18.5 6.5 V17.5 A1 1 0 0 1 17.5 18.5 H6.5 A1 1 0 0 1 5.5 17.5 V6.5 A1 1 0 0 1 6.5 5.5 Z");
    public static readonly Geometry Tray = G("M22 12 h-6l-2 3h-4l-2-3H2 M5.45 5.11 L2 12 v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z");
    public static readonly Geometry Wallet = G("M19 7 V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1 M3 5 v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4");
    public static readonly Geometry Flame = G("M8.5 14.5 A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z");
    public static readonly Geometry Calendar = G("M8 2 v4 M16 2 v4 M5 4 H19 A2 2 0 0 1 21 6 V20 A2 2 0 0 1 19 22 H5 A2 2 0 0 1 3 20 V6 A2 2 0 0 1 5 4 Z M3 10 h18");
}
