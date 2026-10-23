using System.Runtime.InteropServices;

namespace PPTAssistant.Overlay.Native;

public static class ScreenCapture
{
    /// <summary>抓取一行像素所需的字节数（BGRA32）。</summary>
    public static int Stride(int width) => width * 4;

    /// <summary>抓取屏幕矩形到 32bpp BGRA 缓冲（自顶向下）。</summary>
    /// <param name="left">屏幕物理像素 X。</param>
    /// <param name="top">屏幕物理像素 Y。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="destination">目标缓冲，长度至少 <c>Stride(width) * height</c>。</param>
    /// <returns>是否成功。</returns>
    public static bool TryCaptureBgra32(int left, int top, int width, int height, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (width <= 0 || height <= 0 || destination.Length < Stride(width) * height)
        {
            return false;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            // SRCCOPY | CAPTUREBLT：CAPTUREBLT 让分层窗口（覆盖层、工具栏、镜片本身）也进入结果。
            const int Srccopy = 0x00CC0020;
            const int CaptureBlt = 0x40000000;
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, Srccopy | CaptureBlt))
            {
                return false;
            }

            var info = new BitmapInfo
            {
                biSize = Marshal.SizeOf<BitmapInfo>(),
                biWidth = width,
                biHeight = -height, // 负高度 = 自顶向下，省一次翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                return GetDIBits(memoryDc, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref info, 0) != 0;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int biSize;

        public int biWidth;

        public int biHeight;

        public short biPlanes;

        public short biBitCount;

        public int biCompression;

        public int biSizeImage;

        public int biXPelsPerMeter;

        public int biYPelsPerMeter;

        public int biClrUsed;

        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destDc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr bitmap,
        uint start,
        uint lines,
        IntPtr bits,
        ref BitmapInfo info,
        uint usage);
}
