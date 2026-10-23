using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PPTAssistant.Coords;
using PPTAssistant.Host.Native;

namespace PPTAssistant.Host;

/// <summary>
/// 已挂接的宿主会话：持有 COM 引用、事件订阅与对象模型查询。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：进程外挂接。宿主可能先于本工具启动、也可能中途退出，
/// 因此不存在「长期有效」的实例——上层每次使用前先问 <see cref="IsAlive"/>，
/// 失效就丢掉重挂（<see cref="PptHostWatcher"/> 负责重试）。</para>
/// <para><b>线程</b>：所有 COM 调用都从调用方线程发起；本项目约定统一在 UI 线程调用本类，
/// 以免跨线程封送（STA 亲和）。事件回调来自 COM 传入线程，由 sink 抛给上层去 marshal。</para>
/// </remarks>
public sealed class PptHost : IDisposable
{

    public const string ProgId = "PowerPoint.Application";

    private static readonly Guid ApplicationEventsDiid = new("914934C2-5A91-11CF-8700-00AA0060263B");

    private readonly object _application;
    private readonly PptApplicationEventSink _sink;
    private readonly Action<string>? _trace;
    private IConnectionPoint? _connectionPoint;
    private int _adviseCookie = -1;
    private bool _disposed;

    private PptHost(object application, PptHostInfo info, Action<string>? trace)
    {
        _application = application;
        Info = info;
        _trace = trace;
        _sink = new PptApplicationEventSink(trace);
    }

    public PptHostInfo Info { get; private set; }

    /// <summary>放映开始（已过门控判断前，由上层调用 <see cref="ReadShowState"/> 决定是否受理）。</summary>
    public event EventHandler<SlideShowBeginEventArgs>? SlideShowBegin;

    /// <summary>翻页 / 跳页。</summary>
    public event EventHandler<SlideChangedEventArgs>? SlideChanged;

    /// <summary>放映结束。</summary>
    public event EventHandler<SlideShowEndEventArgs>? SlideShowEnd;

    /// <summary>演示文稿关闭（释放 COM 引用的时机）。</summary>
    public event EventHandler<SlideShowEndEventArgs>? PresentationCloseFinal;

    /// <summary>底层 Application COM 对象（供上层做对象模型调用；不要长期持有跨宿主生命周期）。</summary>
    public object ApplicationObject => _application;

    /// <summary>尝试挂接当前活动宿主。</summary>
    public static bool TryAttach(Action<string>? trace, out PptHost? host, out string? error)
    {
        host = null;
        error = null;

        object app;
        try
        {
            app = RotInterop.GetActiveObject(ProgId);
        }
        catch (Exception ex)
        {
            error = $"ROT 未找到宿主：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            var info = ProbeIdentity(app, trace);
            var instance = new PptHost(app, info, trace);
            if (!instance.HookEvents(out var hookError))
            {
                trace?.Invoke($"[host] 事件挂接失败（对象模型查询仍可用）：{hookError}");
            }

            host = instance;
            return true;
        }
        catch (Exception ex)
        {
            error = $"探测宿主身份失败：{ex.GetType().Name} {ex.Message}";
            SafeRelease(app);
            return false;
        }
    }

    public bool IsAlive
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            var pid = Info.ProcessId;
            if (pid <= 0)
            {

                return true;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // 进程不存在 —— **这是正常情况**（宿主关掉了），不该刷日志：
                // 监视器每秒轮询一次，记日志会把整个日志文件冲掉。
                return false;
            }
            catch (Exception ex)
            {
                // Win32Exception = 权限不足（pid 0 / 受保护进程）等：无法判断 → 宁可当作还活着。
                _trace?.Invoke($"[host] 存活判定无法确定（pid={pid}）：{ex.GetType().Name} {ex.Message}");
                return true;
            }
        }
    }

    /// <summary>
    /// 重新探测宿主身份（进程 / 类型 / 可执行路径 / 窗口句柄）。
    /// </summary>
    /// <returns>身份是否已解析（<see cref="PptHostInfo.IsResolved"/>）。</returns>
    /// <remarks>宿主刚被拉起时（无窗口）拿不到 <c>HWND</c>，身份会退化为 Unknown；
    /// 监视器在身份未解析期间反复调用本方法，直到能按进程名甄别出宿主。</remarks>
    public bool RefreshIdentity()
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            var info = ProbeIdentity(_application, _trace);
            if (info.IsResolved || !Info.IsResolved)
            {
                Info = info;
            }

            return Info.IsResolved;
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[host] 身份刷新失败：{ex.GetType().Name} {ex.Message}");
            return Info.IsResolved;
        }
    }

    /// <summary>读取放映状态快照（容错；宿主的任何属性异常都不会抛出）。</summary>
    public SlideShowState ReadShowState()
    {
        var windows = ComLateBinder.TryRead<object>(_application, a => a.SlideShowWindows);
        var count = ComLateBinder.TryRead<int>(windows, w => w.Count);
        if (windows is null || count <= 0)
        {
            return SlideShowState.Empty;
        }

        var window = ComLateBinder.Item(windows, 1);
        var presentation = ComLateBinder.TryRead<object>(_application, a => a.ActivePresentation);
        var settings = ComLateBinder.TryRead<object>(presentation, p => p.SlideShowSettings);
        var showType = ComLateBinder.TryRead<int>(settings, s => s.ShowType);
        var path = ComLateBinder.TryRead<string>(presentation, p => p.FullName);
        var (index, slideCount) = ComLateBinder.ReadSlidePosition(window);

        return new SlideShowState(true, showType, index, slideCount, path);
    }

    /// <summary>读取幻灯片页面尺寸（pt）。读不到时返回 null（上层用 16:9 兜底并告警）。</summary>
    public SlideSize? ReadSlideSize()
    {
        var presentation = ComLateBinder.TryRead<object>(_application, a => a.ActivePresentation);
        var pageSetup = ComLateBinder.TryRead<object>(presentation, p => p.PageSetup);
        var width = ComLateBinder.TryRead<double>(pageSetup, p => p.SlideWidth);
        var height = ComLateBinder.TryRead<double>(pageSetup, p => p.SlideHeight);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new SlideSize(width, height);
    }

    public DipRect? ReadShowWindowBoundsDips()
    {
        var window = ComLateBinder.Item(ComLateBinder.TryRead<object>(_application, a => a.SlideShowWindows), 1);
        if (window is null)
        {
            return null;
        }

        var left = ComLateBinder.TryRead<double>(window, w => w.Left);
        var top = ComLateBinder.TryRead<double>(window, w => w.Top);
        var width = ComLateBinder.TryRead<double>(window, w => w.Width);
        var height = ComLateBinder.TryRead<double>(window, w => w.Height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return UnitConverter.PointsToDips(left, top, width, height);
    }

    /// <summary>下一页。</summary>
    public bool NextSlide() => WithShowView(v => v.Next());

    /// <summary>上一页。</summary>
    public bool PreviousSlide() => WithShowView(v => v.Previous());

    /// <summary>跳到指定页（1 起）。</summary>
    public bool GotoSlide(int oneBasedIndex)
        => oneBasedIndex >= 1 && WithShowView(v => v.GotoSlide(oneBasedIndex));

    /// <summary>退出放映。</summary>
    public bool EndShow() => WithShowView(v => v.Exit());

    public bool ExportSlideThumbnail(int oneBasedIndex, string filePath, int width, int height)
    {
        if (oneBasedIndex < 1 || string.IsNullOrWhiteSpace(filePath) || width <= 0 || height <= 0)
        {
            return false;
        }

        var presentation = ComLateBinder.TryRead<object>(_application, a => a.ActivePresentation);
        var slides = ComLateBinder.TryRead<object>(presentation, p => p.Slides);
        var slide = ComLateBinder.Item(slides, oneBasedIndex);
        if (slide is null)
        {
            return false;
        }

        // Export(FileName, Filter, ScaleWidth, ScaleHeight)
        var ok = ComLateBinder.TryInvoke(slide, s => s.Export(filePath, "PNG", width, height));
        if (!ok)
        {
            _trace?.Invoke($"[host] 导出第 {oneBasedIndex} 页缩略图失败：{ComLateBinder.LastError}");
        }

        return ok;
    }

    public bool SaveAndClosePresentation(string documentPath, out bool wasOpen, out string? error)
    {
        wasOpen = false;
        error = null;

        if (string.IsNullOrWhiteSpace(documentPath))
        {
            error = "课件路径为空";
            return false;
        }

        try
        {
            var presentations = ComLateBinder.TryRead<object>(_application, a => a.Presentations);
            var count = ComLateBinder.TryRead<int>(presentations, p => p.Count);
            for (var i = 1; i <= count; i++)
            {
                var presentation = ComLateBinder.Item(presentations, i);
                var fullName = ComLateBinder.TryRead<string>(presentation, p => p.FullName);
                if (presentation is null || !PathEquals(fullName, documentPath))
                {
                    continue;
                }

                wasOpen = true;

                // 先保存（失败也要继续尝试关闭，否则文件仍被占用，写回必然失败）。
                if (!ComLateBinder.TryInvoke(presentation, p => p.Save()))
                {
                    _trace?.Invoke($"[host] 保存失败（继续关闭）：{ComLateBinder.LastError}");
                }

                if (!ComLateBinder.TryInvoke(presentation, p => p.Close()))
                {
                    error = $"关闭失败：{ComLateBinder.LastError}";
                    return false;
                }

                ComRelease(presentation);
                _trace?.Invoke($"[host] 已保存并关闭课件：{documentPath}");
                return true;
            }

            // 没开着：那就不需要 COM 介入，直接让上层改文件。
            _trace?.Invoke($"[host] 课件当前未打开（无需关闭）：{documentPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>重新打开课件，把界面还给老师。</summary>
    public bool OpenPresentation(string documentPath, out string? error)
    {
        error = null;
        try
        {
            var presentations = ComLateBinder.TryRead<object>(_application, a => a.Presentations);

            var ok = ComLateBinder.TryInvoke(presentations, p => p.Open(documentPath, 0, 0, -1));
            if (!ok)
            {
                error = ComLateBinder.LastError;
                return false;
            }

            _trace?.Invoke($"[host] 已重新打开课件：{documentPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>课件是否被别的进程独占（崩溃恢复路径用它决定「能不能安全写」）。</summary>
    public static bool IsFileLockedByAnotherProcess(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(documentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool PathEquals(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ComRelease(object comObject)
    {
        try
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(comObject))
            {
                _ = System.Runtime.InteropServices.Marshal.ReleaseComObject(comObject);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>取消订阅并释放 COM 引用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_connectionPoint is not null && _adviseCookie >= 0)
            {
                _connectionPoint.Unadvise(_adviseCookie);
                _adviseCookie = -1;
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[host] Unadvise 失败：{ex.Message}");
        }
        finally
        {
            if (_connectionPoint is not null)
            {
                SafeRelease(_connectionPoint);
                _connectionPoint = null;
            }
        }

        _sink.Dispose();
        SafeRelease(_application);
    }

    private bool HookEvents(out string? error)
    {
        error = null;
        try
        {
            _sink.SlideShowBegin += (_, e) => SlideShowBegin?.Invoke(this, e);
            _sink.SlideChanged += (_, e) => SlideChanged?.Invoke(this, e);
            _sink.SlideShowEnd += (_, e) => SlideShowEnd?.Invoke(this, e);
            _sink.PresentationCloseFinal += (_, e) => PresentationCloseFinal?.Invoke(this, e);

            var container = (IConnectionPointContainer)_application;
            var diid = ApplicationEventsDiid;
            container.FindConnectionPoint(ref diid, out var connectionPoint);
            _connectionPoint = connectionPoint;
            connectionPoint.Advise(_sink, out _adviseCookie);
            _trace?.Invoke($"[host] 已订阅 Application 事件（cookie={_adviseCookie}）");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    private bool WithShowView(Action<dynamic> action)
    {
        var windows = ComLateBinder.TryRead<object>(_application, a => a.SlideShowWindows);
        var window = ComLateBinder.Item(windows, 1);
        var view = ComLateBinder.TryRead<object>(window, w => w.View);
        return view is not null && ComLateBinder.TryInvoke(view, v => action(v));
    }

    private static PptHostInfo ProbeIdentity(object app, Action<string>? trace)
    {
        var hwndRaw = ComLateBinder.TryRead<int>(app, a => a.HWND);
        var hwnd = new IntPtr(hwndRaw);

        var processId = 0;
        var processName = string.Empty;
        var executablePath = string.Empty;
        if (hwnd != IntPtr.Zero)
        {
            _ = RotInterop.GetWindowThreadProcessId(hwnd, out var pid);
            processId = (int)pid;
            if (processId > 0)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    processName = process.ProcessName;
                    executablePath = SafeMainModulePath(process);
                }
                catch (Exception ex)
                {
                    trace?.Invoke($"[host] 读取宿主进程失败：{ex.Message}");
                }
            }
        }

        var version = ComLateBinder.TryRead<string>(app, a => a.Version) ?? string.Empty;
        var appPath = ComLateBinder.TryRead<string>(app, a => a.Path) ?? string.Empty;
        if (string.IsNullOrEmpty(executablePath))
        {
            executablePath = appPath;
        }

        if (processId <= 0)
        {
            var fallback = ResolveHostProcessByName(appPath, trace);
            if (fallback is not null)
            {
                processId = fallback.ProcessId;
                processName = fallback.ProcessName;
                executablePath = fallback.ExecutablePath;
                hwnd = fallback.MainWindowHandle;
            }
        }

        var kind = processName.ToLowerInvariant() switch
        {
            "powerpnt" => HostKind.PowerPoint,
            "wpp" => HostKind.WpsPresentation,
            _ => HostKind.Unknown,
        };

        return new PptHostInfo(kind, processId, processName, executablePath, version, hwnd);
    }

    /// <summary>按进程名找宿主进程（HWND 不可用时的兜底，见 <see cref="ProbeIdentity"/>）。</summary>
    private static HostProcess? ResolveHostProcessByName(string appPath, Action<string>? trace)
    {
        var candidates = new List<Process>();
        try
        {
            foreach (var name in HostProcessNames)
            {
                try
                {
                    candidates.AddRange(Process.GetProcessesByName(name));
                }
                catch (Exception ex)
                {
                    trace?.Invoke($"[host] 枚举进程 {name} 失败：{ex.Message}");
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            Process? chosen = null;

            // ① Application.Path 精确匹配（最可靠）。
            if (!string.IsNullOrEmpty(appPath))
            {
                chosen = candidates.FirstOrDefault(p =>
                    string.Equals(SafeMainModulePath(p), appPath, StringComparison.OrdinalIgnoreCase));
            }

            // ② 只有一个候选就直接用。
            chosen ??= candidates.Count == 1 ? candidates[0] : null;

            // ③ 多个候选时取最晚启动的（老师通常是刚打开的那一个）。
            chosen ??= candidates
                .OrderByDescending(p => SafeStartTime(p))
                .First();

            var result = new HostProcess(
                chosen.Id,
                chosen.ProcessName,
                SafeMainModulePath(chosen),
                SafeMainWindowHandle(chosen));
            trace?.Invoke($"[host] 窗口句柄不可用，按进程名兜底匹配到 {result.ProcessName} (pid={result.ProcessId})");
            return result;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    private static string SafeMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            // 跨位数 / 权限不足时 MainModule 会抛。
            return string.Empty;
        }
    }

    private static IntPtr SafeMainWindowHandle(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static DateTime SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>兜底匹配到的宿主进程信息。</summary>
    private sealed record HostProcess(int ProcessId, string ProcessName, string ExecutablePath, IntPtr MainWindowHandle);

    /// <summary>宿主进程名（按进程甄别宿主用，见 <see cref="ProbeIdentity"/>）。</summary>
    private static readonly string[] HostProcessNames = ["POWERPNT", "wpp"];

    private static void SafeRelease(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                _ = Marshal.ReleaseComObject(comObject);
            }
        }
        catch (Exception)
        {
            // 释放失败无所谓，交给 GC / RPC 清理。
        }
    }

    [ComImport]
    [Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPointContainer
    {
        void EnumConnectionPoints(out IntPtr ppEnum);

        void FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP);
    }

    [ComImport]
    [Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPoint
    {
        void GetConnectionInterface(out Guid pIID);

        void GetConnectionPointContainer(out IntPtr ppCPC);

        void Advise([MarshalAs(UnmanagedType.IUnknown)] object punkSink, out int pdwCookie);

        void Unadvise(int dwCookie);

        void EnumConnections(out IntPtr ppEnum);
    }
}
