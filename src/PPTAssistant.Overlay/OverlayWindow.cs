using PPTAssistant.InkML;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Overlay;

public sealed class OverlayWindow : Window, IInkSurface
{
    private readonly Canvas _inkCanvas;
    private readonly InkRenderer _renderer;
    private readonly Path _hitSurface;
    private IReadOnlyList<DipRect> _inputExclusions = [];
    private IntPtr _hwnd;
    private InputMode _mode = InputMode.Mouse;
    private int _nextStrokeId = 1;
    private int _activeStrokeId;
    private bool _penDeviceSeen;
    private bool _disposed;
    private InkColor _penColor = InkColor.Default;
    private int _lastLoggedStyle;
    private bool _alwaysRoutePenToInk = true;

    private WindowStyles.BlurBehindResult _blurProbe = WindowStyles.BlurBehindResult.NoWindow;

    public OverlaySurfaceMode RequestedSurfaceMode { get; set; } = OverlaySurfaceMode.Layered;

    public SurfaceModeDecision SurfaceMode { get; private set; }

    /// <summary>是否处于「非分层」路径（当前恒为 false；保留用于命中面机制的分支判断）。</summary>
    private bool IsNonLayered => SurfaceMode.Effective == OverlaySurfaceMode.BlurBehind;

    /// <summary>正在书写的那支笔/那根手指的设备 id（-1 = 无）。用于吃掉并发触点的 move/up。</summary>
    private int _activeStylusId = -1;

    /// <summary>当前这一笔来自哪条事件路径（见 <see cref="StrokeSource"/>）。</summary>
    private StrokeSource _activeSource = StrokeSource.None;

    private bool _erasing;

    /// <summary>橡皮拖动来自哪条事件路径。</summary>
    private StrokeSource _eraseSource = StrokeSource.None;

    /// <summary>正在拖动橡皮的设备 id（-1 = 鼠标 / 未知）。</summary>
    private int _eraseStylusId = -1;

    /// <summary>上一次橡皮采样位置——每次移动用「上一点 → 当前点」的线段做命中判定，快速拖动不漏擦。</summary>
    private DipPoint _lastErasePoint;
    private bool _mouseDiagnosticLogged;
    private bool _mouseRejectedLogged;
    private bool _mouseMoveDiagnosticLogged;
    private bool _stylusDiagnosticLogged;
    private int _strokeStartTimestamp;
    private bool _stylusRejectedLogged;

    private bool _penChannelActive;

    private bool _pointerMessageLogged;

    private bool _penChannelRejectedLogged;

    /// <inheritdoc />
    public void SetInputExclusion(IReadOnlyList<DipRect>? excludedAreasDips)
    {
        _inputExclusions = excludedAreasDips is null ? [] : [.. excludedAreasDips];
        ApplyInputSurface();

        if (_inputExclusions.Count > 0)
        {
            Trace?.Invoke(
                $"[overlay] 命中面挖洞 ×{_inputExclusions.Count}：{string.Join(" ", _inputExclusions.Select(static r => r.ToString()))}"
                + "（这些区域交给悬浮工具窗口，覆盖层不再抢点击）");
        }
    }

    /// <summary>
    /// 重建命中面：笔态时铺满视口（alpha = 1/255），并挖掉 <see cref="SetInputExclusion"/> 指定的区域；
    /// 鼠标态则完全撤掉（那时靠 <c>WS_EX_TRANSPARENT</c> 让点击穿透）。
    /// </summary>
    private void ApplyInputSurface()
    {
        // 鼠标态不需要命中面（点穿透）；笔态与橡皮态都要「接住」输入。
        if (_mode == InputMode.Mouse || Width <= 0 || Height <= 0)
        {
            _hitSurface.Fill = null;
            _hitSurface.Data = null;
            return;
        }

        if (IsNonLayered)
        {
            _hitSurface.Fill = Brushes.Transparent;
            _hitSurface.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
            return;
        }

        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        Geometry geometry = full;
        foreach (var exclusion in _inputExclusions)
        {
            if (exclusion.Width <= 0 || exclusion.Height <= 0)
            {
                continue;
            }

            // 排除矩形是屏幕坐标（多屏时原点可能是负的），换算到窗口客户区坐标。
            var local = new Rect(
                exclusion.X - Left,
                exclusion.Y - Top,
                exclusion.Width,
                exclusion.Height);

            // 稍微外扩几像素：工具栏边缘的圆角/阴影不该由覆盖层接管点击。
            local.Inflate(6, 6);

            geometry = new CombinedGeometry(GeometryCombineMode.Exclude, geometry, new RectangleGeometry(local));
        }

        _hitSurface.Data = geometry;
        _hitSurface.Fill = InputSurfaceBrush;
    }

    private static readonly Brush InputSurfaceBrush = CreateInputSurfaceBrush();

    private static Brush CreateInputSurfaceBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "PPTAssistant 覆盖层";

        SurfaceMode = OverlaySurfaceSelector.DecideForConstruction(
            RequestedSurfaceMode,
            WindowStyles.IsCompositionEnabled());

        AllowsTransparency = SurfaceMode.Effective == OverlaySurfaceMode.Layered;

        Background = Brushes.Transparent;

        // 坐标要精确：不开像素吸附与布局取整（否则笔迹会有半像素级偏移）。
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;

        _inkCanvas = new Canvas
        {
            // 命中测试交给窗口背景（透明但非 null），图元不参与。
            IsHitTestVisible = false,
            Background = null,
            ClipToBounds = true,
        };        _renderer = new InkRenderer(_inkCanvas);

        // 命中面（笔态用）：全屏 alpha=1/255，但把「不参与命中」的区域挖掉（见 SetInputExclusion）。
        _hitSurface = new Path
        {
            Fill = null,
            IsHitTestVisible = true,
        };

        // 根容器：**必须是 Transparent 画刷而不是 null**——理由同窗口背景（null = 不透明黑）。
        var root = new Grid { IsHitTestVisible = true, Background = Brushes.Transparent };
        root.Children.Add(_hitSurface);
        root.Children.Add(_inkCanvas);
        Content = root;

        // 关掉系统手势：书写时不该被「按住右键 / 轻扫 / 触摸反馈」打断。
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

        SizeChanged += (_, _) => RaiseViewportChanged();
        LocationChanged += (_, _) => RaiseViewportChanged();
    }

    /// <inheritdoc />
    public InputMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            EndActiveStroke();
            EndErase();
            ApplyWindowMode();
            ModeChanged?.Invoke(this, _mode);
        }
    }

    /// <inheritdoc />
    /// <remarks>显式实现：<see cref="Window"/> 已有同名成员（且语义完全一致），
    /// 显式实现可以让「接口调用」与「直接用窗口」两条路都不产生 CS0108 隐藏警告。</remarks>
    bool IInkSurface.IsVisible => base.IsVisible;

    /// <inheritdoc />
    public DipRect Viewport => new(Left, Top, Width, Height);

    /// <inheritdoc />
    /// <remarks>以渲染器的「仍保留在屏上的笔画」为准——擦除后计数必须下降。</remarks>
    public int StrokeCount => _renderer.StrokeCount;

    /// <inheritdoc />
    public int PointCount { get; private set; }

    public double PenWidthDips
    {
        get => _renderer.PenWidthDips;
        set => _renderer.PenWidthDips = value;
    }

    public InkColor PenColor
    {
        get => _penColor;
        set
        {
            _penColor = value;
            _renderer.PenColor = Color.FromRgb(value.R, value.G, value.B);
        }
    }

    public bool IgnoreTouchWhenPenPresent { get; set; } = true;

    /// <summary>是否允许鼠标绘制（仅开发期方便在无触摸机器上验证链路）。</summary>
    public bool AllowMouseDrawing { get; set; } = true;

    public bool AlwaysRoutePenToInk
    {
        get => _alwaysRoutePenToInk;
        set
        {
            if (_alwaysRoutePenToInk == value)
            {
                return;
            }

            _alwaysRoutePenToInk = value;
            UpdatePenChannel(InputRouting.WantsPenChannelRedirect(_mode, value));
            Trace?.Invoke($"[overlay] 逃生门 → {(value ? "关（笔永远走我们的引擎）" : "开（笔穿透、可当教鞭）")}");
        }
    }

    /// <summary>诊断日志回调（由 App 注入；覆盖层不自己决定往哪写日志）。</summary>
    public Action<string>? Trace { get; set; }

    /// <inheritdoc />
    public event EventHandler<InkStrokeEventArgs>? StrokeBegan;

    /// <inheritdoc />
    public event EventHandler<InkStrokeEventArgs>? StrokeUpdated;

    /// <inheritdoc />
    public event EventHandler<InkStrokeEventArgs>? StrokeEnded;

    /// <inheritdoc />
    public event EventHandler<InkStrokesErasedEventArgs>? StrokesErased;

    /// <inheritdoc />
    public event EventHandler? ViewportChanged;

    /// <inheritdoc />
    public event EventHandler<InputMode>? ModeChanged;

    /// <inheritdoc />
    public void ShowOn(ScreenInfo screen)
    {
        var bounds = screen.Bounds;
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;

        // 缓存位图按视口的**物理像素**预分配；缩放取窗口的实际 DPI（多屏混合 DPI 时以本屏为准）。
        _renderer.SetViewport(new DipRect(0, 0, Width, Height), GetDpiScale());

        if (!base.IsVisible)
        {
            Show();
        }

        ApplyWindowMode();
        RaiseViewportChanged();
    }

    /// <summary>本窗口所在显示器的 DPI 缩放（物理像素 / DIP）。窗口未上屏时用系统主屏的口径。</summary>
    private double GetDpiScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformFromDevice;
            if (transform is { } matrix && matrix.M11 > 0)
            {
                return 1.0 / matrix.M11;
            }
        }
        catch (Exception)
        {
            // 窗口还没接上合成目标时会抛；回落系统口径。
        }

        // 窗口还没上屏时无法从合成目标取缩放——回落 1.0（首帧按 100%，上屏后 ShowOn 会重设）。
        return 1.0;
    }
    /// <inheritdoc />
    /// <remarks>显式实现（理由同 <see cref="IInkSurface.IsVisible"/>）；差别只在于先给未完结的笔画收尾。</remarks>
    void IInkSurface.Hide()
    {
        EndActiveStroke();
        EndErase();

        UpdatePenChannel(wanted: false);

        if (base.IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void ClearInk()
    {
        EndActiveStroke();
        EndErase();
        _renderer.Clear();
        PointCount = 0;
    }

    /// <inheritdoc />
    public void EnsureOnTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    /// <summary>释放窗口（产品里由 App 在退出时调用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndActiveStroke();
        EndErase();

        UpdatePenChannel(wanted: false);

        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            source.AddHook(OnWndProcObserved);
        }

        ApplySurfaceMode();

        ApplyWindowMode();
    }

    private IntPtr OnWndProcObserved(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmPointerDown = 0x0246;
        const int WmPointerEnter = 0x0249;

        if (_penChannelActive && (msg == WmPointerDown || msg == WmPointerEnter) && !_pointerMessageLogged)
        {
            _pointerMessageLogged = true;
            // lParam 是打包的屏幕坐标（signed short × 2）。
            var x = unchecked((short)((long)lParam & 0xFFFF));
            var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            Trace?.Invoke(
                $"[overlay] 笔通道收到 WM_POINTER{(msg == WmPointerDown ? "DOWN" : "ENTER")}"
                + $"：pointerId={(long)wParam} 屏幕坐标=({x}, {y})——重定向已到达本窗口；"
                + "若此后始终没有 Stylus 事件，则 WPF 未提升重定向输入");
        }

        return IntPtr.Zero;
    }

    private void ApplySurfaceMode()
    {
        if (SurfaceMode.Effective == OverlaySurfaceMode.BlurBehind)
        {
            _blurProbe = WindowStyles.TryEnableBlurBehind(_hwnd);
        }

        Trace?.Invoke(
            $"[overlay] 窗口表面模式：请求={SurfaceMode.Requested} 生效={SurfaceMode.Effective}"
            + $"（{SurfaceMode.Reason}）；blur-behind 调用结果={_blurProbe}"
            + $" HRESULT=0x{WindowStyles.LastBlurBehindHResult:X8}"
            + $" AllowsTransparency={AllowsTransparency}"
            + $" WS_EX_LAYERED={(WindowStyles.IsLayered(_hwnd) ? "1" : "0")}"
            + $" DWM合成={(WindowStyles.IsCompositionEnabled() ? "开" : "关")}");

        // 表面模式决定了命中面机制（分层才需要 alpha=1/255）。
        ApplyInputSurface();
    }

    /// <inheritdoc />
    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);

        if (_activeStrokeId > 0 || _erasing)
        {
            return;
        }

        var device = ClassifyDevice(e.StylusDevice);

        if (device == InputDeviceKind.Pen && _mode == InputMode.Mouse)
        {
            var position = e.GetPosition(this);
            var outsideViewport = position.X < 0 || position.Y < 0 || position.X >= Width || position.Y >= Height;
            var screenX = Left + position.X;
            var screenY = Top + position.Y;
            var insideExclusion = false;
            foreach (var exclusion in _inputExclusions)
            {
                if (new Rect(exclusion.X, exclusion.Y, exclusion.Width, exclusion.Height).Contains(screenX, screenY))
                {
                    insideExclusion = true;
                    break;
                }
            }

            if (outsideViewport || insideExclusion)
            {
                LogPenChannelRejectedOnce(position, outsideViewport, insideExclusion);
                return;
            }
        }

        if (!ShouldCapture(device))
        {
            LogStylusRejectedOnce(device);
            return;
        }

        LogStylusDiagnosticOnce(device, e);

        e.Handled = true;
        CaptureStylus();

        if (_mode == InputMode.Erase)
        {
            var position = e.GetPosition(this);
            BeginErase(StrokeSource.Stylus, new DipPoint(position.X, position.Y), e.StylusDevice?.Id ?? -1);
            return;
        }

        var points = ReadPoints(e);
        if (points.Count == 0)
        {
            return;
        }

        BeginStroke(StrokeSource.Stylus, device, points, e.StylusDevice?.Id ?? -1);
    }

    /// <inheritdoc />
    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);

        if (_erasing)
        {
            if (IsActiveEraseDevice(e.StylusDevice))
            {
                e.Handled = true;
                ContinueErase(e.GetPosition(this));
            }

            return;
        }

        if (!IsActiveDevice(e.StylusDevice))
        {
            return;
        }

        var points = ReadPoints(e);
        if (points.Count == 0)
        {
            return;
        }

        e.Handled = true;
        PointCount += points.Count;
        _renderer.AddPoints(points);
        Raise(StrokeUpdated, _activeStrokeId, points[0].Device, points);
    }

    /// <inheritdoc />
    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);

        if (_erasing)
        {
            if (IsActiveEraseDevice(e.StylusDevice))
            {
                e.Handled = true;
                ContinueErase(e.GetPosition(this));
                EndErase();
            }

            return;
        }

        // 只让「正在书写的那支笔/那根手指」的抬起结束本笔；否则并发的另一触点抬手会误收笔。
        if (!IsActiveDevice(e.StylusDevice))
        {
            return;
        }

        e.Handled = true;
        var device = ClassifyDevice(e.StylusDevice);
        var points = ReadPoints(e);
        if (points.Count > 0)
        {
            PointCount += points.Count;
            _renderer.AddPoints(points);
            Raise(StrokeUpdated, _activeStrokeId, device, points);
        }

        EndActiveStroke();
    }

    /// <inheritdoc />
    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);

        // 丢失捕获（例如系统弹窗抢走输入）时必须收尾，否则笔迹会「挂」在屏幕上永不完结。
        if (_activeSource == StrokeSource.Stylus)
        {
            EndActiveStroke();
        }

        if (_eraseSource == StrokeSource.Stylus)
        {
            EndErase();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_activeStrokeId > 0 || _erasing)
        {
            return;
        }

        LogMouseDiagnosticOnce(e);

        if (!InputRouting.IsRealMouse(e.StylusDevice is not null))        {
            LogMouseRejectedOnce($"被判定为提升事件（StylusDevice 非空：{Describe(e.StylusDevice)}）");
            return;
        }

        if (!ShouldCapture(InputDeviceKind.Mouse))
        {
            LogMouseRejectedOnce(
                $"放行规则拒绝（模式={_mode} 允许鼠标绘制={AllowMouseDrawing} 已见笔设备={_penDeviceSeen}）");
            return;
        }

        e.Handled = true;
        CaptureMouse();

        if (_mode == InputMode.Erase)
        {
            var position = e.GetPosition(this);
            BeginErase(StrokeSource.Mouse, new DipPoint(position.X, position.Y), -1);
            return;
        }

        var point = ToMouseStrokePoint(e.GetPosition(this), ToStrokeTime(e.Timestamp));
        BeginStroke(StrokeSource.Mouse, InputDeviceKind.Mouse, [point], arrowDeviceId: -1);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 诊断：第一帧鼠标移动就到（用来回答「这个窗口到底收不收得到鼠标事件」这个是非题）。
        if (!_mouseMoveDiagnosticLogged)
        {
            _mouseMoveDiagnosticLogged = true;
            Trace?.Invoke(
                $"[overlay] 收到鼠标移动：位置={e.GetPosition(this)} 模式={_mode} 鼠标在窗口上={IsMouseOver} "
                + $"直接命中={IsMouseDirectlyOver} 已捕获={IsMouseCaptured}");
        }

        if (_erasing)
        {
            if (_eraseSource == StrokeSource.Mouse && InputRouting.IsRealMouse(e.StylusDevice is not null))
            {
                e.Handled = true;
                ContinueErase(e.GetPosition(this));
            }

            return;
        }

        if (_activeSource != StrokeSource.Mouse || !InputRouting.IsRealMouse(e.StylusDevice is not null))
        {
            return;
        }

        e.Handled = true;
        var point = ToMouseStrokePoint(e.GetPosition(this), ToStrokeTime(e.Timestamp));
        PointCount++;
        _renderer.AddPoints([point]);
        Raise(StrokeUpdated, _activeStrokeId, InputDeviceKind.Mouse, [point]);
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_erasing)
        {
            if (_eraseSource == StrokeSource.Mouse &&
                InputRouting.IsRealMouse(e.StylusDevice is not null) &&
                e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                ContinueErase(e.GetPosition(this));
                EndErase();
            }

            return;
        }

        if (_activeSource != StrokeSource.Mouse ||
            !InputRouting.IsRealMouse(e.StylusDevice is not null) ||
            e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        var point = ToMouseStrokePoint(e.GetPosition(this), ToStrokeTime(e.Timestamp));
        PointCount++;
        _renderer.AddPoints([point]);
        Raise(StrokeUpdated, _activeStrokeId, InputDeviceKind.Mouse, [point]);

        EndActiveStroke();
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_activeSource == StrokeSource.Mouse)
        {
            EndActiveStroke();
        }

        if (_eraseSource == StrokeSource.Mouse)
        {
            EndErase();
        }
    }

    /// <summary>开一笔（Stylus 与 Mouse 两条路径共用，保证计数、渲染、事件三者一致）。</summary>
    private void BeginStroke(StrokeSource source, InputDeviceKind device, List<StrokePoint> points, int arrowDeviceId)
    {
        _activeStrokeId = _nextStrokeId++;
        _activeSource = source;
        _activeStylusId = arrowDeviceId;
        PointCount += points.Count;
        _renderer.BeginStroke(_activeStrokeId);
        _renderer.AddPoints(points);
        Raise(StrokeBegan, _activeStrokeId, device, points);
    }

    /// <summary>鼠标点 → 采样点（鼠标没有压力数据，一律记 0）。</summary>
    private static StrokePoint ToMouseStrokePoint(Point position, int timeMilliseconds = 0)
        => new(new DipPoint(position.X, position.Y), StrokePoint.NoPressure, InputDeviceKind.Mouse);

    /// <summary>描述一个 StylusDevice（诊断用）。</summary>
    private static string Describe(StylusDevice? device)
    {
        if (device is null)
        {
            return "null";
        }

        try
        {
            var tablet = device.TabletDevice;
            return $"Id={device.Id} TabletType={(tablet is null ? "?" : tablet.Type.ToString())} Name={device.Name ?? "?"}";
        }
        catch (Exception ex)
        {
            return $"<读取失败：{ex.GetType().Name}>";
        }
    }

    private void LogMouseDiagnosticOnce(MouseEventArgs e)
    {
        if (_mouseDiagnosticLogged)
        {
            return;
        }

        _mouseDiagnosticLogged = true;
        Trace?.Invoke(
            $"[overlay] 鼠标输入诊断：StylusDevice={Describe(e.StylusDevice)} 模式={_mode} "
            + $"允许鼠标绘制={AllowMouseDrawing} 已见笔设备={_penDeviceSeen} 原样式={e.StylusDevice?.TabletDevice?.Type.ToString() ?? "-"}");
    }

    private void LogMouseRejectedOnce(string reason)
    {
        if (_mouseRejectedLogged)
        {
            return;
        }

        _mouseRejectedLogged = true;
        Trace?.Invoke($"[overlay] 鼠标笔迹被拒：{reason}（本次放映内只记一次）");
    }

    private void LogStylusDiagnosticOnce(InputDeviceKind device, StylusDownEventArgs e)
    {
        if (_stylusDiagnosticLogged)
        {
            return;
        }

        _stylusDiagnosticLogged = true;
        var pressure = 0f;
        try
        {
            var points = e.GetStylusPoints(this);
            if (points.Count > 0)
            {
                pressure = points[0].PressureFactor;
            }
        }
        catch (Exception)
        {
        }

        Trace?.Invoke(
            $"[overlay] 受理笔/触摸输入：判定设备={device} 设备详情=[{Describe(e.StylusDevice)}] "
            + $"压力={pressure:0.00} 模式={_mode} 已见笔设备={_penDeviceSeen}");
    }

    private void LogStylusRejectedOnce(InputDeviceKind device)
    {
        if (_stylusRejectedLogged)
        {
            return;
        }

        _stylusRejectedLogged = true;
        Trace?.Invoke(
            $"[overlay] 笔/触摸输入被拒：判定设备={device} 模式={_mode} 允许鼠标绘制={AllowMouseDrawing} "
            + $"已见笔设备={_penDeviceSeen} 忽略触摸={IgnoreTouchWhenPenPresent}"
            + "（若是压感笔，说明它被放行给了宿主，PowerPoint 会自己画自己的墨迹）");
    }

    private void EndActiveStroke()
    {
        if (_activeStrokeId <= 0)
        {
            return;
        }

        var strokeId = _activeStrokeId;
        _activeStrokeId = 0;
        _activeSource = StrokeSource.None;
        _activeStylusId = -1;
        _renderer.EndStroke();

        try
        {
            if (IsStylusCaptured)
            {
                ReleaseStylusCapture();
            }

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        catch (Exception)
        {
            // 捕获可能已因设备消失而失效，忽略。
        }

        StrokeEnded?.Invoke(this, new InkStrokeEventArgs(strokeId, InputDeviceKind.Unknown, []));
    }

    /// <summary>开始一次橡皮拖动（按下即算一次「点擦」，所以立刻用单点判定一次）。</summary>
    private void BeginErase(StrokeSource source, DipPoint position, int stylusId)
    {
        _erasing = true;
        _eraseSource = source;
        _eraseStylusId = stylusId;
        _lastErasePoint = position;
        EraseAlong([position]);
    }

    /// <summary>橡皮移动到新位置：用「上一点 → 当前点」的线段判定，快速拖动也不会漏擦。</summary>
    private void ContinueErase(Point position)
    {
        var point = new DipPoint(position.X, position.Y);
        if (Math.Abs(point.X - _lastErasePoint.X) < 0.001 && Math.Abs(point.Y - _lastErasePoint.Y) < 0.001)
        {
            return;
        }

        EraseAlong([_lastErasePoint, point]);
        _lastErasePoint = point;
    }

    /// <summary>把一段橡皮路径交给渲染器，并在真的删了笔画时向上抛事件。</summary>
    private void EraseAlong(IReadOnlyList<DipPoint> path)
    {
        var erased = _renderer.Erase(path, StrokeEraser.DefaultRadiusDips);
        if (erased.Count == 0)
        {
            return;
        }

        Trace?.Invoke(
            $"[overlay] 橡皮擦除 {erased.Count} 笔（编号 {string.Join(",", erased)}；"
            + $"本页剩余 {_renderer.StrokeCount} 笔）");

        // 会话层据此把删除同步进旁路日志（否则「屏上擦了、写回又出现」）。
        StrokesErased?.Invoke(this, new InkStrokesErasedEventArgs(erased, _renderer.StrokeCount));
    }

    /// <summary>结束橡皮拖动（幂等）：清状态并释放输入捕获。</summary>
    private void EndErase()
    {
        if (!_erasing)
        {
            return;
        }

        _erasing = false;
        _eraseSource = StrokeSource.None;
        _eraseStylusId = -1;

        try
        {
            if (IsStylusCaptured)
            {
                ReleaseStylusCapture();
            }

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        catch (Exception)
        {
            // 捕获可能已因设备消失而失效，忽略。
        }
    }

    /// <summary>本次事件是否来自「正在拖橡皮的那支笔/那根手指」。</summary>
    private bool IsActiveEraseDevice(StylusDevice? device)
    {
        if (!_erasing || _eraseSource != StrokeSource.Stylus)
        {
            return false;
        }

        var deviceId = device?.Id ?? -1;
        return _eraseStylusId < 0 || deviceId < 0 || deviceId == _eraseStylusId;
    }

    /// <summary>本次事件是否来自「正在书写的那支笔/那根手指」（并发触点的 move/up 必须被吃掉）。</summary>
    private bool IsActiveDevice(StylusDevice? device)
    {
        if (_activeStrokeId <= 0 || _activeSource != StrokeSource.Stylus)
        {
            return false;
        }

        // 设备 id 拿不到时（极少数实现）退化为「只看有没有在写」。
        var deviceId = device?.Id ?? -1;
        return _activeStylusId < 0 || deviceId < 0 || deviceId == _activeStylusId;
    }

    /// <summary>是否应当捕获本次输入（规则见 <see cref="InputRouting"/>）。</summary>
    private bool ShouldCapture(InputDeviceKind device)
    {
        if (device == InputDeviceKind.Pen)
        {

            _penDeviceSeen = true;
        }

        var penAlwaysOurs = AlwaysRoutePenToInk && _penChannelActive;
        return InputRouting.ShouldCapture(_mode, device, _penDeviceSeen, IgnoreTouchWhenPenPresent, AllowMouseDrawing, penAlwaysOurs);
    }

    /// <summary>一笔的输入来源（把 Stylus 与 Mouse 两条事件路径分开，避免互相误收笔）。</summary>
    private enum StrokeSource
    {
        None = 0,
        Stylus = 1,
        Mouse = 2,
    }

    /// <summary>
    /// 把输入事件的时间戳（毫秒，系统启动起算）换算成「相对本笔起笔的毫秒数」——InkML 的 T 通道要用它。
    /// </summary>
    /// <remarks>本笔第一个采样点负责设定基准（返回 0），之后返回增量；异常（时间倒流）压到 0，不写负数。</remarks>
    private int ToStrokeTime(int timestamp)
    {
        if (_strokeStartTimestamp == 0)
        {
            _strokeStartTimestamp = timestamp;
            return 0;
        }

        var delta = timestamp - _strokeStartTimestamp;
        return delta < 0 ? 0 : delta;
    }

    private List<StrokePoint> ReadPoints(StylusEventArgs e)
    {
        var result = new List<StrokePoint>(8);
        var device = ClassifyDevice(e.StylusDevice);
        // WPF 的输入事件时间戳（毫秒，系统启动起算）→ 相对本笔起笔的毫秒数，供 InkML 的 T 通道使用。
        var time = ToStrokeTime(e.Timestamp);
        try
        {
            foreach (var point in e.GetStylusPoints(this))
            {

                var pressure = device == InputDeviceKind.Pen ? point.PressureFactor : StrokePoint.NoPressure;
                result.Add(new StrokePoint(new DipPoint(point.X, point.Y), pressure, device, time));
            }
        }
        catch (Exception)
        {
            // 设备在读取途中消失会抛；丢掉本次采样即可（下一笔会重新开始）。
        }

        if (result.Count == 0)
        {
            // 极少数情况下事件里没有采样点，退回单点位置。
            try
            {
                var position = e.GetPosition(this);
                var pressure = device == InputDeviceKind.Pen && e.StylusDevice is not null
                    ? e.StylusDevice.GetStylusPoints(this)[0].PressureFactor
                    : StrokePoint.NoPressure;
                result.Add(new StrokePoint(new DipPoint(position.X, position.Y), pressure, device, time));
            }
            catch (Exception)
            {
            }
        }

        return result;
    }

    private static InputDeviceKind ClassifyDevice(StylusDevice? device)
    {
        if (device is null)
        {
            return InputDeviceKind.Mouse;
        }

        return device.TabletDevice?.Type switch
        {
            TabletDeviceType.Stylus => InputDeviceKind.Pen,
            TabletDeviceType.Touch => InputDeviceKind.Touch,
            _ => InputDeviceKind.Unknown,
        };
    }

    private void ApplyWindowMode()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        WindowStyles.ApplyOverlayBase(_hwnd);
        WindowStyles.SetClickThrough(_hwnd, _mode == InputMode.Mouse);
        WindowStyles.BringToTopWithoutActivation(_hwnd);

        UpdatePenChannel(InputRouting.WantsPenChannelRedirect(_mode, AlwaysRoutePenToInk));

        ApplyInputSurface();

        var style = WindowStyles.GetExtendedStyle(_hwnd);
        if (style != _lastLoggedStyle)
        {
            _lastLoggedStyle = style;
            Trace?.Invoke(
                $"[overlay] hwnd=0x{_hwnd:X} exstyle=0x{style:X8} 穿透={(style & WindowStyles.WsExTransparent) != 0} 不激活={(style & WindowStyles.WsExNoActivate) != 0} 模式={_mode}");
        }
    }

    private void RaiseViewportChanged() => ViewportChanged?.Invoke(this, EventArgs.Empty);

    private void UpdatePenChannel(bool wanted)
    {
        if (_hwnd == IntPtr.Zero || wanted == _penChannelActive)
        {
            return;
        }

        if (wanted)
        {
            _penChannelActive = PenInputChannel.TryRegister(_hwnd);
            _pointerMessageLogged = false;

            if (_penChannelActive)
            {
                Trace?.Invoke("[overlay] 笔通道已注册（RegisterPointerInputTarget PT_PEN）：鼠标态下笔输入定向到覆盖层，触摸/鼠标照旧穿透给宿主");
            }
            else
            {
                Trace?.Invoke($"[overlay] 笔通道注册失败（GetLastError={PenInputChannel.LastError}）：回落笔穿透，指针模式下笔输入交给宿主");
            }
        }
        else
        {
            if (PenInputChannel.TryUnregister(_hwnd))
            {
                _penChannelActive = false;
                Trace?.Invoke("[overlay] 笔通道已注销：笔输入恢复系统默认命中测试");
            }
            else
            {
                Trace?.Invoke($"[overlay] 笔通道注销失败（GetLastError={PenInputChannel.LastError}）！整机笔输入仍被定向到本窗口——下次窗口状态变化时重试；窗口销毁后由系统清理。若笔持续失灵，结束放映或重启应用可恢复");
            }
        }
    }

    private void LogPenChannelRejectedOnce(Point position, bool outsideViewport, bool insideExclusion)
    {
        if (_penChannelRejectedLogged)
        {
            return;
        }

        _penChannelRejectedLogged = true;
        var reason = outsideViewport ? "视口之外（其它屏幕）" : insideExclusion ? "工具栏区域" : "?";
        Trace?.Invoke($"[overlay] 笔通道拒收落点：位置={position} 原因={reason}（指针模式下工具栏请用鼠标或手指操作）");
    }

    private void Raise(
        EventHandler<InkStrokeEventArgs>? handler,
        int strokeId,
        InputDeviceKind device,
        IReadOnlyList<StrokePoint> points)
        => handler?.Invoke(this, new InkStrokeEventArgs(strokeId, device, points));
}
