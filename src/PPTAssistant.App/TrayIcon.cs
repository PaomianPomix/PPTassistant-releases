using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PPTAssistant.App;

/// <summary>托盘菜单命令。</summary>
public enum TrayCommand
{
    None = 0,

    /// <summary>打开日志目录（老师/支持人员排查用）。</summary>
    OpenLogDirectory = 1,

    /// <summary>打开配置目录（<c>config.json</c> 所在处）。</summary>
    OpenConfigDirectory = 2,

    OpenSettings = 4,

    /// <summary>主动处理自启时延后的残留板书恢复。</summary>
    RecoverPendingInk = 5,

    /// <summary>退出程序。</summary>
    Exit = 3,
}

public sealed class TrayIcon : IDisposable
{
    private const int TrayCallbackMessage = 0x0400 + 1; // WM_APP + 1
    private const int WmDestroy = 0x0002;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;

    /// <summary>NIF_INFO：本次 Shell_NotifyIcon 带气泡信息。</summary>
    private const int NifInfo = 0x00000010;
    private const int NimDelete = 0x00000002;

    private const int MfString = 0x00000000;
    private const int MfGrayed = 0x00000001;
    private const int MfSeparator = 0x00000800;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Func<string> _statusProvider;
    private readonly Action<TrayCommand> _onCommand;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _data;
    private readonly IconHandle _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(IntPtr hwnd, Func<string> statusProvider, Action<TrayCommand> onCommand)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("托盘图标需要一个有效窗口句柄。", nameof(hwnd));
        }

        _hwnd = hwnd;
        _statusProvider = statusProvider;
        _onCommand = onCommand;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("该窗口句柄不是 WPF 窗口（取不到 HwndSource）。");
        _source = source;
        _source.AddHook(WndProc);

        _icon = ApplicationIcon.CreateTrayIcon();
        Add();
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed || !_added)
        {
            return;
        }

        try
        {
            var data = _data;
            data.uFlags = NifInfo;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            _ = ShellNotifyIcon(NimModify, ref data);
        }
        catch (Exception)
        {
            // 气泡失败无所谓：日志里已经有同样的信息。
        }
    }

    /// <summary>更新托盘提示文本（状态变化时调用）。</summary>
    public void RefreshTooltip()
    {
        if (_disposed || !_added)
        {
            return;
        }

        _data.szTip = Truncate(_statusProvider(), 63);
        _ = ShellNotifyIcon(NimModify, ref _data);
    }

    /// <summary>移除托盘图标。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_added)
            {
                _ = ShellNotifyIcon(NimDelete, ref _data);
                _added = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"移除托盘图标失败：{ex.Message}");
        }
        finally
        {
            _source.RemoveHook(WndProc);

            _icon.Dispose();
        }
    }

    private void Add()
    {
        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon.DangerousGetHandle(),
            szTip = Truncate(_statusProvider(), 63),
        };

        if (ShellNotifyIcon(NimAdd, ref _data))
        {
            _added = true;
            AppLog.Info("托盘图标已就位");
            return;
        }

        AppLog.Warn($"托盘图标添加失败（Win32 错误 {Marshal.GetLastWin32Error()}），本程序将不显示托盘入口");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        // 资源管理器重启后托盘会被清空，需要重新添加（否则图标永久消失）。
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            _added = false;
            Add();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == TrayCallbackMessage)
        {
            var notification = (int)(lParam.ToInt64() & 0xFFFF);
            if (notification is WmRButtonUp or WmLButtonDblClk or WmContextMenu)
            {
                ShowMenu();
                handled = true;
            }
        }
        else if (msg == WmDestroy)
        {
            Dispose();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MfString | MfGrayed, 0, Truncate(_statusProvider(), 60));
            _ = AppendMenu(menu, MfSeparator, 0, null);
            _ = AppendMenu(menu, MfString, (int)TrayCommand.OpenSettings, "设置…");
            _ = AppendMenu(menu, MfString, (int)TrayCommand.RecoverPendingInk, "恢复上次板书…");
            _ = AppendMenu(menu, MfString, (int)TrayCommand.OpenLogDirectory, "打开日志目录");
            _ = AppendMenu(menu, MfString, (int)TrayCommand.OpenConfigDirectory, "打开配置目录");
            _ = AppendMenu(menu, MfSeparator, 0, null);
            _ = AppendMenu(menu, MfString, (int)TrayCommand.Exit, "退出 PPTAssistant");

            if (!GetCursorPos(out var point))
            {
                return;
            }

            // 经典要求：弹菜单前把自己设为前台窗口，否则菜单不会随点击别处而消失；
            // 弹完再补一条 WM_NULL（MSDN 的 trackPopupMenu 备注）。
            _ = SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _hwnd, IntPtr.Zero);
            _ = PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command != 0)
            {
                _onCommand((TrayCommand)command);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("托盘菜单弹出失败", ex);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static string Truncate(string text, int maxLength)
        => string.IsNullOrEmpty(text) ? "PPTAssistant"
            : text.Length <= maxLength ? text
            : text[..maxLength];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;

        public IntPtr hWnd;

        public int uID;

        public int uFlags;

        public int uCallbackMessage;

        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szTip;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;

        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
