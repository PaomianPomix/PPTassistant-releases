using System.Runtime.InteropServices;

namespace PPTAssistant.Overlay.Native;

public static class PenInputChannel
{
    /// <summary><c>POINTER_INPUT_TYPE</c> 的 <c>PT_PEN</c>（主动式电容笔/电磁笔；不含触摸与鼠标）。</summary>
    private const uint PointerInputTypePen = 0x00000003;

    /// <summary>上次调用返回的 Win32 错误码（诊断用；成功时为 0）。</summary>
    public static int LastError { get; private set; }

    /// <summary>
    /// 把整机范围内的笔输入定向到 <paramref name="hwnd"/>。
    /// </summary>
    /// <returns>注册是否成功；失败时读 <see cref="LastError"/>（常见：API 不存在=系统不支持，或已被其它窗口占用）。调用方用自己的状态位防止重复注册。</returns>
    public static bool TryRegister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            LastError = 0;
            return false;
        }

        var ok = RegisterPointerInputTarget(hwnd, PointerInputTypePen);
        LastError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>
    /// 注销笔输入定向（整机的笔恢复「正常命中测试」）。
    /// </summary>
    /// <returns>注销是否成功。失败通常意味着「本来就没注册」——调用方用自己的状态位挡住这种情况。</returns>
    public static bool TryUnregister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            LastError = 0;
            return false;
        }

        var ok = UnregisterPointerInputTarget(hwnd, PointerInputTypePen);
        LastError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterPointerInputTarget(IntPtr hwnd, uint inputType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPointerInputTarget(IntPtr hwnd, uint inputType);
}
