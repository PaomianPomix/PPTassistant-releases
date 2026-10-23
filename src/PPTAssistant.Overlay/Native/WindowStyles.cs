using System.Runtime.InteropServices;

namespace PPTAssistant.Overlay.Native;

public static class WindowStyles
{
    /// <summary><c>GWL_EXSTYLE</c>。</summary>
    public const int GwlExStyle = -20;

    /// <summary>命中测试跳过本窗口（输入穿透给下层窗口）。</summary>
    public const int WsExTransparent = 0x00000020;

    /// <summary>点击本窗口不激活它。</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>不出现在任务栏 / Alt+Tab。</summary>
    public const int WsExToolWindow = 0x00000080;

    /// <summary>分层窗口（WPF <c>AllowsTransparency=true</c> 会自动带上，这里显式确认）。</summary>
    public const int WsExLayered = 0x00080000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>给覆盖层/工具栏套上「不激活、不进任务栏」的基样式；幂等。</summary>
    public static void ApplyOverlayBase(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetExtendedStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        SetExtendedStyle(hwnd, style);
    }

    /// <summary>设置/取消「输入穿透」。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetExtendedStyle(hwnd);
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetExtendedStyle(hwnd, style);
    }

    /// <summary>置顶且不激活（覆盖层必须在放映窗口之上，但不能抢焦点）。</summary>
    public static void BringToTopWithoutActivation(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public static void HideWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = ShowWindow(hwnd, SwHide);
    }

    /// <summary>
    /// 窗口在**操作系统层面**是否可见（<c>IsWindowVisible</c>）——与 WPF 的 <c>IsVisible</c> 是两回事，
    /// 诊断「明明 Hide 了却还画在屏幕上」时必须用它。
    /// </summary>
    public static bool IsWindowVisibleOnScreen(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindowVisibleNative(hwnd);

    private const int SwHide = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleNative(IntPtr hwnd);

    /// <summary>
    /// 判定 <see cref="TryEnableBlurBehind"/> 的结果——覆盖层是否真的走上了「非分层 + DWM 合成」这条路。
    /// </summary>
    public enum BlurBehindResult
    {

        Enabled = 0,

        /// <summary>句柄还没建立（调用过早）。</summary>
        NoWindow = 1,

        /// <summary>系统不支持 / 调用失败（必须回落分层窗口）。</summary>
        Unsupported = 2,

        /// <summary>DWM 合成被关闭（旧系统、远程桌面、虚拟机、部分投屏驱动）——blur-behind 不会生效。</summary>
        CompositionDisabled = 3,
    }

    /// <summary><c>DWM_BB_ENABLE</c>：按 <c>hRgnBlur</c> 指定的区域启用/停用玻璃效果。</summary>
    private const int DwmBbEnable = 0x00000001;

    public static BlurBehindResult TryEnableBlurBehind(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return BlurBehindResult.NoWindow;
        }

        if (!IsCompositionEnabled())
        {
            // 合成关了：blur-behind 即使返回 S_OK 也不会有透明效果，直接判不可用更诚实。
            return BlurBehindResult.CompositionDisabled;
        }

        try
        {
            var data = new DwmBlurBehind
            {
                // hRgnBlur = NULL + DWM_BB_ENABLE：整窗玻璃，逐像素 alpha 生效。
                DwmFlags = DwmBbEnable,
                Enable = true,
                BlurRegion = IntPtr.Zero,
                TransitionOnMaximized = false,
            };

            var hr = DwmEnableBlurBehindWindow(hwnd, ref data);
            if (hr != 0)
            {
                return BlurBehindResult.Unsupported;
            }

            LastBlurBehindHResult = hr;
            return BlurBehindResult.Enabled;
        }
        catch (DllNotFoundException)
        {
            return BlurBehindResult.Unsupported;
        }
        catch (EntryPointNotFoundException)
        {
            return BlurBehindResult.Unsupported;
        }
    }

    /// <summary>最近一次 <see cref="TryEnableBlurBehind"/> 的 HRESULT（诊断用；失败时便于查证）。</summary>
    public static int LastBlurBehindHResult { get; private set; }

    /// <summary>关闭 blur-behind（回落到分层窗口前调用，避免留下半套状态）。</summary>
    public static void DisableBlurBehind(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var data = new DwmBlurBehind
            {
                DwmFlags = DwmBbEnable,
                Enable = false,
                BlurRegion = IntPtr.Zero,
                TransitionOnMaximized = false,
            };

            _ = DwmEnableBlurBehindWindow(hwnd, ref data);
        }
        catch (Exception)
        {
            // 系统不支持时忽略。
        }
    }

    /// <summary>DWM 合成当前是否开启（关掉时任何非分层透明方案都不会生效）。</summary>
    public static bool IsCompositionEnabled()
    {
        try
        {
            return DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>窗口当前是否带分层样式（<c>WS_EX_LAYERED</c>）——判断「现在到底走的是哪条路」的客观证据。</summary>
    public static bool IsLayered(IntPtr hwnd)
        => hwnd != IntPtr.Zero && (GetExtendedStyle(hwnd) & WsExLayered) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public int DwmFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;

        public IntPtr BlurRegion;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind data);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    public static double AverageScreenLuminance(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return -1;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return -1;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            ReleaseDc(IntPtr.Zero, screenDc);
            return -1;
        }

        var previous = SelectObject(memoryDc, bitmap);
        var luminance = -1.0;
        try
        {
            // CAPTURELAYEREDWINDOWS：必须包含分层窗口，否则测不到覆盖层本身。
            var copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Srccopy | CaptureLayeredWindows);            if (copied)
            {
                luminance = ReadAverageLuminance(memoryDc, bitmap, width, height);
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDc(IntPtr.Zero, screenDc);
        }

        return luminance;
    }

    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureLayeredWindows = 0x40000000;

    /// <summary>
    /// 抓屏判断一块区域里是否存在「明显偏红」的像素（用于验证明红笔色真的被画到了屏上）。
    /// </summary>
    /// <remarks>判据是「红通道显著高于绿蓝」而不是「精确等于某个 RGB」——抓屏会经过 DWM 合成，
    /// 精确比色在混合 DPI / HDR / 颜色管理下不可靠；「明显偏红」在浅背景上够用且稳。</remarks>
    public static bool RedPresence(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            ReleaseDc(IntPtr.Zero, screenDc);
            return false;
        }

        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Srccopy | CaptureLayeredWindows))
            {
                return false;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
            };

            var stride = width * 4;
            var buffer = Marshal.AllocHGlobal(stride * height);
            try
            {
                if (GetDIBits(memoryDc, bitmap, 0, (uint)height, buffer, ref info, 0) == 0)
                {
                    return false;
                }

                var pixels = width * height;
                for (var i = 0; i < pixels; i++)
                {
                    var offset = i * 4;
                    int b = Marshal.ReadByte(buffer, offset);
                    int g = Marshal.ReadByte(buffer, offset + 1);
                    int r = Marshal.ReadByte(buffer, offset + 2);
                    if (r > 90 && r - g > 45 && r - b > 45)
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDc(IntPtr.Zero, screenDc);
        }
    }

    private static double ReadAverageLuminance(IntPtr dc, IntPtr bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // 负高度 = 自上而下，省一次翻转。
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };

        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        try
        {
            var lines = GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref info, 0);
            if (lines == 0)
            {
                return -1;
            }

            long total = 0;
            var pixels = width * height;
            for (var i = 0; i < pixels; i++)
            {
                var offset = i * 4;
                var b = Marshal.ReadByte(buffer, offset);
                var g = Marshal.ReadByte(buffer, offset + 1);
                var r = Marshal.ReadByte(buffer, offset + 2);
                total += (299L * r) + (587L * g) + (114L * b);
            }

            return total / (double)pixels / 1000.0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    private static void ReleaseDc(IntPtr hwnd, IntPtr dc) => _ = ReleaseDC(hwnd, dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr destDc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint start,
        uint lines,
        IntPtr bits,
        ref BitmapInfo info,
        uint usage);

    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c>：窗口对所有抓屏 API（BitBlt/DXGI/GDI）隐形——窗口仍在屏幕上显示，但不出现在抓屏结果里。</summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    public static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    /// <summary>还原 <see cref="ExcludeFromCapture"/>（<c>WDA_NONE</c>）。</summary>
    public static void ClearDisplayAffinity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowDisplayAffinity(hwnd, 0);
    }

    public static int GetExtendedStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? unchecked((int)GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64())
            : GetWindowLong32(hwnd, GwlExStyle);

    private static void SetExtendedStyle(IntPtr hwnd, int style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(style));
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlExStyle, style);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
