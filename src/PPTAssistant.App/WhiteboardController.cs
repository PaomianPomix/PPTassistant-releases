using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using PPTAssistant.Coords;
using PPTAssistant.Session;

namespace PPTAssistant.App;

/// <summary>独立白板的生命周期与本地自动保存，不经过 PPT 板书写回。</summary>
internal sealed class WhiteboardController : IDisposable
{
    private readonly PresentationSession _session;
    private readonly Func<double> _uiScale;
    private readonly Action<string> _notify;
    private readonly DispatcherTimer _saveTimer;
    private readonly string _path;
    private WhiteboardDocument? _document;
    private WhiteboardWindow? _window;
    private bool _dirty;
    private bool _fromPresentation;
    private bool _disposed;
    private bool _saveFailureReported;
    private bool _transitioning;
    private IntPtr _returnWindow;

    internal WhiteboardController(PresentationSession session, Func<double> uiScale,
        Action<string> notify, string? path = null)
    {
        _session = session;
        _uiScale = uiScale;
        _notify = notify;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PPTAssistant", "Whiteboard", "board.pptboard");
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _saveTimer.Tick += (_, _) => Save();
    }

    internal bool IsVisible => _window?.IsVisible == true;
    internal event EventHandler? VisibilityChanged;

    internal void Open()
    {
        if (_disposed || _transitioning) return;
        if (IsVisible)
        {
            _window!.Activate();
            return;
        }

        _transitioning = true;
        try
        {
            EnsureWindow();
            _returnWindow = GetForegroundWindow();
            _fromPresentation = _session.SuspendForWhiteboard();
            ShowWindow();
        }
        catch (Exception ex)
        {
            RecoverFailedOpen();
            AppLog.Error("打开白板失败", ex);
            _notify("白板未能打开，原有内容已保留。请查看日志。\n" + ex.Message);
        }
        finally
        {
            _transitioning = false;
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void OnSessionStateChanged()
    {
        if (_disposed || _transitioning || !IsVisible) return;
        if (_session.State == SessionState.Presenting)
        {
            // 独立白板打开期间开始放映：接管刚出现的覆盖层，仍留在白板。
            _transitioning = true;
            try
            {
                _fromPresentation = _session.SuspendForWhiteboard();
                ShowWindow();
            }
            catch (Exception ex)
            {
                RecoverFailedOpen();
                AppLog.Error("白板接管放映失败", ex);
                _notify("白板未能切换到放映屏幕，内容已保留。可从托盘重新打开后重试。");
            }
            finally
            {
                _transitioning = false;
                VisibilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_fromPresentation)
        {
            // 宿主真实退场后先隐藏白板，让既有 PPT 板书保留提示可见。
            Return(restoreFocus: false);
        }
    }

    private void ShowWindow()
    {
        var screens = ScreenEnumerator.Enumerate();
        var screen = _session.ShowScreen ?? screens.FirstOrDefault(s => s.IsPrimary);
        if (screen.Bounds.Width <= 0)
            screen = new ScreenInfo("Primary", new DipRect(0, 0,
                SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), true, 1);
        _window!.ShowOn(screen, _fromPresentation, _uiScale());
    }

    private void RecoverFailedOpen()
    {
        try { _window?.Hide(); }
        catch (Exception ex) { AppLog.Error("收起白板失败", ex); }
        if (!TryResumePresentation()) ShowForRetry();
    }

    private bool TryResumePresentation()
    {
        try
        {
            // Suspend 可能已修改会话状态后才抛出；不能只依赖其尚未返回的 bool。
            if (_session.IsWhiteboardOpen) _session.ResumeFromWhiteboard();
        }
        catch (Exception ex) { AppLog.Error("恢复放映工具失败，可再次返回重试", ex); }
        _fromPresentation = _session.IsWhiteboardOpen;
        return !_fromPresentation;
    }

    private void ShowForRetry()
    {
        if (_window is null) return;
        // ShowOn 同时重置窗口的返回防重入标记，失败后仍可再次点击返回。
        try { ShowWindow(); }
        catch (Exception ex) { AppLog.Error("重新显示白板失败，可从托盘再次打开", ex); }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        string? recoveryNotice = null;
        try
        {
            _document = WhiteboardStore.Load(_path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                                   or System.Text.Json.JsonException or NotSupportedException)
        {
            // 保留无法读取的文件；备份失败则拒绝继续，绝不覆盖唯一的原件。
            var backup = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            File.Copy(_path, backup);
            _document = new WhiteboardDocument();
            recoveryNotice = "上次白板未能读取，已保留原文件副本：" + backup;
            AppLog.Error(recoveryNotice, ex);
        }

        _window = new WhiteboardWindow(_document);
        _window.ReturnRequested += (_, _) => Return();
        _document.Changed += OnDocumentChanged;
        if (recoveryNotice is not null)
        {
            _window.ShowSaveStatus(recoveryNotice);
            _notify(recoveryNotice);
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool Save()
    {
        _saveTimer.Stop();
        if (!_dirty || _document is null) return true;
        try
        {
            WhiteboardStore.Save(_path, _document);
            _dirty = false;
            _saveFailureReported = false;
            _window?.ShowSaveStatus(null);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("白板自动保存失败", ex);
            _window?.ShowSaveStatus("自动保存失败，内容仍在当前窗口；请导出图片并检查磁盘空间。" );
            if (!_saveFailureReported)
                _notify("白板自动保存失败，内容仍保留在内存中。可先导出图片；再次修改或返回时会重试。");
            _saveFailureReported = true;
            return false;
        }
    }

    private void Return(bool restoreFocus = true)
    {
        if (_disposed || _transitioning) return;
        _transitioning = true;
        try
        {
            _window?.CompleteActiveStroke();
            Save();
            var wasPresenting = _fromPresentation || _session.IsWhiteboardOpen;
            _window?.Hide();
            // 先恢复宿主焦点，再显示工具栏，避免宿主置顶时把工具栏盖住。
            if (restoreFocus)
            {
                var target = wasPresenting ? FindPresentationWindow() : _returnWindow;
                if (target != IntPtr.Zero && IsWindow(target) && IsWindowVisible(target))
                    SetForegroundWindow(target);
            }
            if (TryResumePresentation()) _returnWindow = IntPtr.Zero;
            else
            {
                ShowForRetry();
                _notify("放映工具暂未恢复，白板内容已保留。请再次点击返回，或从托盘重新打开后重试。");
            }
        }
        catch (Exception ex)
        {
            _fromPresentation = _session.IsWhiteboardOpen;
            ShowForRetry();
            AppLog.Error("返回白板前的窗口失败", ex);
            _notify("暂时无法返回，白板内容已保留。请再次点击返回，或从托盘重新打开后重试。");
        }
        finally
        {
            _transitioning = false;
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _window?.CompleteActiveStroke(); }
        catch (Exception ex) { AppLog.Error("关闭白板时收尾当前笔画失败", ex); }
        if (!Save() && _document is not null)
        {
            // 正常存储位置不可写时，在临时目录留一份可恢复的原生笔迹。
            var recoveryPath = Path.Combine(Path.GetTempPath(),
                "PPTAssistant-whiteboard-recovery-" + Guid.NewGuid().ToString("N") + ".pptboard");
            try
            {
                WhiteboardStore.Save(recoveryPath, _document);
                AppLog.Warn("白板已应急保存到：" + recoveryPath);
                _notify("白板已应急保存到：" + recoveryPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("白板应急保存失败", ex);
            }
        }
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        try { _window?.ClosePermanently(); }
        finally
        {
            _window = null;
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private IntPtr FindPresentationWindow()
    {
        var info = _session.Host?.Info;
        if (info is null || info.ProcessId <= 0 || _session.ShowScreen is not { } screen) return IntPtr.Zero;
        var bounds = screen.PhysicalBounds;
        var screenArea = bounds.Width * bounds.Height;
        if (screenArea <= 0) return IntPtr.Zero;
        var best = IntPtr.Zero;
        var bestScore = 0d;
        var foreground = GetForegroundWindow();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId != info.ProcessId || !GetWindowRect(window, out var rect)) return true;
            // 主编辑窗即使最大化也不是返回目标；无标题栏的全屏宿主可复用主 HWND。
            if (window == info.MainWindowHandle && (GetWindowLong(window, -16) & 0x00C00000) != 0) return true;
            var width = Math.Max(0, Math.Min(rect.Right, bounds.X + bounds.Width) - Math.Max(rect.Left, bounds.X));
            var height = Math.Max(0, Math.Min(rect.Bottom, bounds.Y + bounds.Height) - Math.Max(rect.Top, bounds.Y));
            var coverage = width * height / screenArea;
            if (coverage < 0.8) return true;
            // 几何匹配优先；同等覆盖时保留从工具栏进入时的宿主窗口或当前前台宿主。
            var score = coverage + (window == _returnWindow ? 0.001 : window == foreground ? 0.0005 : 0);
            if (score > bestScore) { bestScore = score; best = window; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
