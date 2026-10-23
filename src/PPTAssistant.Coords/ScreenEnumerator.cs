using System.Runtime.InteropServices;

namespace PPTAssistant.Coords;

/// <summary>
/// 枚举本机显示器（Win32 <c>EnumDisplayMonitors</c> + <c>GetDpiForMonitor</c>）。
/// </summary>
/// <remarks>
/// <b>已知限制（混合 DPI 多屏）</b>：<see cref="ScreenInfo.Bounds"/> 的 DIP 绝对坐标在多屏
/// 缩放不一致时不可靠（Windows 的虚拟桌面在物理像素上布局，而 DIP 是按各屏缩放换算的）。
/// 本场景（教室单块大屏 / 双屏同规格）不受影响：
/// <list type="bullet">
///   <item>宽度与高度（铺满目标屏所需的 DIP 尺寸）始终正确；</item>
///   <item>绝对位置仅用于「哪块屏」的判别，而判别已改用物理像素（<see cref="ScreenInfo.PhysicalBounds"/>）。</item>
/// </list>
/// 若将来真的遇到混合 DPI 场景，再引入 <c>WM_DPICHANGED</c> + 逐屏窗口放置。
/// </remarks>
public static class ScreenEnumerator
{
    /// <summary>枚举全部显示器；失败时返回空列表（调用方需能容忍）。</summary>
    public static IReadOnlyList<ScreenInfo> Enumerate()
    {
        var list = new List<ScreenInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = MONITORINFOEX.Create();
                if (!GetMonitorInfo(hMonitor, ref info))
                {
                    return true;
                }

                var scale = QueryScale(hMonitor);
                var physical = DipRect.FromLtrb(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right,
                    info.rcMonitor.Bottom);

                var normalized = UnitConverter.NormalizeDisplayScale(scale);
                var dips = new DipRect(
                    physical.X / normalized,
                    physical.Y / normalized,
                    physical.Width / normalized,
                    physical.Height / normalized);

                list.Add(new ScreenInfo(
                    info.szDevice ?? string.Empty,
                    dips,
                    physical,
                    (info.dwFlags & MonitorInfoPrimary) != 0,
                    normalized));
                return true;
            }, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            // 非 Windows / 极端裁剪环境：让上层走「无屏信息」的降级路径。
        }
        catch (EntryPointNotFoundException)
        {
        }

        return list;
    }

    /// <summary>取显示器物理像素矩形（px），用于与窗口矩形做归属判别（挑屏）。</summary>
    /// <remarks>等价于 <see cref="Enumerate"/> 的投影；保留独立签名是为了让调用点意图更直白
    /// （「我这次只想按物理像素比较」），实现上不复制查询逻辑。</remarks>
    public static IReadOnlyList<(string DeviceName, DipRect PhysicalBounds, double Scale, bool IsPrimary)> EnumeratePhysical()
    {
        var result = new List<(string, DipRect, double, bool)>();
        foreach (var screen in Enumerate())
        {
            result.Add((screen.DeviceName, screen.PhysicalBounds, screen.DisplayScale, screen.IsPrimary));
        }

        return result;
    }

    private static double QueryScale(IntPtr hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll 不可用（极老系统）→ 落回 1.0
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1.0;
    }

    private const int MonitorInfoPrimary = 0x1;

    private const int MdtEffectiveDpi = 0;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? szDevice;

        public static MONITORINFOEX Create() => new()
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
