using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

public sealed class MagnifierWindow : Window, IMagnifierView
{
    /// <summary>浮窗宽度（DIP）。</summary>
    private const double WindowWidthDips = 480;

    /// <summary>浮窗高度（DIP）。宽高比 3:2——镜下取景的内容以文字为主，宽一点看得舒服。</summary>
    private const double WindowHeightDips = 320;

    /// <summary>浮窗与屏幕边缘的初始边距（DIP）。</summary>
    private const double ScreenEdgeMarginDips = 24;

    /// <summary>重采样间隔（30fps）——镜片内容不需要更高，省 CPU。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(33);

    private readonly Border _frame;
    private readonly Border _hintHost;
    private readonly Image _lensImage;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private byte[] _buffer = [];
    private ScreenInfo? _screen;
    private double _zoom = 2.0;
    private IntPtr _hwnd;
    private bool _isActive;
    private bool _disposed;

    /// <summary>镜下取景是否可用（<c>WDA_EXCLUDEFROMCAPTURE</c> 生效）。false = 回退「内容跟光标」。</summary>
    private bool _lensBelowWindow;

    /// <summary>本会话内记住的上次浮窗位置（DIP）。再开时回原处——老师挪过的地方就是它该在的地方。</summary>
    private Point? _lastPosition;

    private DateTime _lastLogUtc = DateTime.MinValue;

    public MagnifierWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "PPTAssistant 放大镜";
        SnapsToDevicePixels = false;

        Width = WindowWidthDips;
        Height = WindowHeightDips;

        _lensImage = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        // 放大后要能看清笔画：用高质量重采样而不是最近邻（后者会出锯齿方块）。
        RenderOptions.SetBitmapScalingMode(_lensImage, BitmapScalingMode.HighQuality);

        _hint = new TextBlock
        {
            Text = HintTextFor(_zoom),
            Foreground = UiTheme.Text,
            FontSize = 13,
        };

        _hintHost = new Border
        {
            Background = UiTheme.Panel,
            BorderBrush = UiTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiMetrics.Standard.CornerRadius),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 8),
            IsHitTestVisible = false,
            Child = _hint,
        };

        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_lensImage);

        content.Children.Add(CreateInnerScrim());
        content.Children.Add(_hintHost);

        content.Children.Add(CreateDragGrip());

        _frame = new Border
        {
            CornerRadius = UiSurface.Radius(UiMetrics.Standard),
            BorderThickness = new Thickness(1),
            BorderBrush = UiTheme.Border,
            Background = UiTheme.LensBackground,
            Child = content,
        };
        Content = _frame;

        MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove 在没有按下态时抛（竞态：按钮已松开）——忽略即可，位置不受影响。
            }
        };

        // 滚轮调倍率：光标放浮窗上滚动（不抢焦点也能收到 WM_MOUSEWHEEL）。
        MouseWheel += (_, e) => Zoom += e.Delta > 0 ? 0.25 : -0.25;

        // 拖动/挪动留痕：教学排查需要知道浮窗在哪（高频事件，节流）。
        LocationChanged += (_, _) =>
            LogThrottled($"[magnifier] 浮窗位置 → {Left:0},{Top:0}（{_zoom:0.0}×，{(_lensBelowWindow ? "镜下取景" : "内容跟光标")}）");

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();

        Closed += OnClosed;
    }

    /// <summary>提示条文案（倍率 + 操作方式）。</summary>
    private static string HintTextFor(double zoom) => $"{zoom:0.0}× · 拖动 · 滚轮调倍率";

    /// <summary>诊断日志回调（由 App 注入）。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>用户选择的 UI 比例；改变镜框尺寸，不改变光学倍率。</summary>
    public double UiScale { get; set; } = UiScalePolicy.Default;

    /// <summary>考虑屏幕可用空间后实际应用的 UI 比例。</summary>
    public double EffectiveUiScale { get; private set; } = UiScalePolicy.Default;

    /// <inheritdoc />
    /// <remarks>显式实现：<see cref="Window"/> 已有同名成员（<c>IsActive</c> 在 <see cref="Window"/> 上指「窗口是否有焦点」，
    /// 与本接口的「放大镜是否开启」不是一回事），显式实现避免 CS0108 隐藏警告与语义混淆。</remarks>
    bool IMagnifierView.IsActive => _isActive;

    /// <inheritdoc />
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);
            if (Math.Abs(clamped - _zoom) < 0.001)
            {
                return;
            }

            _zoom = clamped;
            _hint.Text = HintTextFor(_zoom);
            // 滚轮是低频离散事件，逐次记录（不节流）；「跟随/位置」类高频日志才节流。
            Trace?.Invoke($"[magnifier] 倍率 → {_zoom:0.0}×");
            ZoomChanged?.Invoke(this, _zoom);
        }
    }

    /// <summary>最小倍率。</summary>
    public const double MinZoom = 1.0;

    public const double MaxZoom = 3.0;

    /// <inheritdoc />
    public event EventHandler<double>? ZoomChanged;

    /// <inheritdoc />
    public void ShowOn(ScreenInfo screen)
    {
        _screen = screen;
        _isActive = true;
        _hint.Text = HintTextFor(_zoom);
        ApplyMetrics(UiMetrics.For(screen.Bounds.Width));

        EffectiveUiScale = UiScalePolicy.Fit(UiScale,
            Math.Max(1, screen.Bounds.Width - (2 * ScreenEdgeMarginDips)),
            Math.Max(1, screen.Bounds.Height - (2 * ScreenEdgeMarginDips)),
            WindowWidthDips, WindowHeightDips);
        _frame.Width = WindowWidthDips;
        _frame.Height = WindowHeightDips;
        UiScaling.Apply(_frame, EffectiveUiScale);
        Width = WindowWidthDips * EffectiveUiScale;
        Height = WindowHeightDips * EffectiveUiScale;

        // 位置：本会话挪过就用上次的位置（夹回屏内），否则默认屏幕右下角（远离顶部中央的工具栏）。
        var screenBounds = screen.Bounds;
        var left = _lastPosition?.X ?? screenBounds.Right - Width - ScreenEdgeMarginDips;
        var top = _lastPosition?.Y ?? screenBounds.Bottom - Height - ScreenEdgeMarginDips;
        left = Math.Clamp(left, screenBounds.X, Math.Max(screenBounds.X, screenBounds.Right - Width));
        top = Math.Clamp(top, screenBounds.Y, Math.Max(screenBounds.Y, screenBounds.Bottom - Height));
        Left = left;
        Top = top;

        // 窗口还没上屏：此刻抓屏绝不可能抓到自己。先抓再显示，第一帧就是正确内容，
        // 也就不会先闪一下「黑镜片」。位置已定，源区域随之确定。
        // 注意：首帧要按「镜下取景」算几何（此时 _lensBelowWindow 尚未由 WDA 结果决定，
        // 乐观置 true——窗口未上屏，抓它正下方是绝对安全的）；
        // 否则首帧会走跟光标路径、抓光标周围的区域，闪一下错误内容。
        if (!base.IsVisible)
        {
            _lensBelowWindow = true;
            if (TryComputeGeometry(screen, out var geometry, out _, out _))
            {
                CaptureInto(geometry);
            }
        }

        if (!base.IsVisible)
        {
            Show();
        }

        if (_hwnd != IntPtr.Zero)
        {
            // 镜下取景的关键一步：把浮窗从抓屏里排除。失败（老系统）→ 回退内容跟光标。
            WindowStyles.ApplyOverlayBase(_hwnd);
            _lensBelowWindow = WindowStyles.ExcludeFromCapture(_hwnd);
            WindowStyles.BringToTopWithoutActivation(_hwnd);
            LogWindowStyle();
        }

        _timer.Start();
    }

    /// <inheritdoc />
    /// <remarks>显式实现（理由同 <see cref="IMagnifierView.IsActive"/>）：停定时器、还原显示亲和、藏窗口。</remarks>
    void IMagnifierView.Hide()
    {
        _timer.Stop();
        _isActive = false;
        _lastPosition = new Point(Left, Top);
        // 还原抓屏可见性：关闭放大镜后浮窗不存在于屏幕，亲和性不该残留（幂等无害，防御性还原）。
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.ClearDisplayAffinity(_hwnd);
        }

        if (base.IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void EnsureOnTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    /// <summary>释放窗口（App 退出时调用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        WindowStyles.ApplyOverlayBase(_hwnd);
    }

    private void ApplyMetrics(UiTierMetrics metrics)
    {
        _frame.CornerRadius = UiSurface.Radius(metrics);
        _hintHost.CornerRadius = UiSurface.Radius(metrics);
    }

    private static UIElement CreateInnerScrim()
    {
        const double thickness = 6;
        var grid = new Grid { IsHitTestVisible = false, Margin = new Thickness(UiMetrics.PanelBorderThickness) };

        grid.Children.Add(ScrimBar(thickness, vertical: true, fromTop: true));
        grid.Children.Add(ScrimBar(thickness, vertical: true, fromTop: false));
        grid.Children.Add(ScrimBar(thickness, vertical: false, fromTop: true));
        grid.Children.Add(ScrimBar(thickness, vertical: false, fromTop: false));
        return grid;
    }

    private static Rectangle ScrimBar(double thickness, bool vertical, bool fromTop)
    {

        var scrimColor = UiTheme.ScrimColor;
        var brush = new LinearGradientBrush
        {
            StartPoint = vertical ? new Point(0.5, fromTop ? 0 : 1) : new Point(fromTop ? 0 : 1, 0.5),
            EndPoint = vertical ? new Point(0.5, fromTop ? 1 : 0) : new Point(fromTop ? 1 : 0, 0.5),
            GradientStops =
            {
                new GradientStop(scrimColor, 0),
                new GradientStop(Color.FromArgb(0x00, scrimColor.R, scrimColor.G, scrimColor.B), 1),
            },
        };
        brush.Freeze();

        return new Rectangle
        {
            Fill = brush,
            IsHitTestVisible = false,
            Width = vertical ? double.NaN : thickness,
            Height = vertical ? thickness : double.NaN,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : (fromTop ? HorizontalAlignment.Left : HorizontalAlignment.Right),
            VerticalAlignment = vertical ? (fromTop ? VerticalAlignment.Top : VerticalAlignment.Bottom) : VerticalAlignment.Stretch,
        };
    }

    private static UIElement CreateDragGrip()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 8),
            IsHitTestVisible = false,
        };

        for (var i = 0; i < 3; i++)
        {
            panel.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                Fill = UiTheme.White,
                Stroke = UiTheme.ThumbDotBorder,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }

        return panel;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    /// <summary>一帧：按浮窗当前位置取景（或回退模式按光标）→ 抓屏 → 更新镜片内容。</summary>
    private void Refresh()
    {
        if (!_isActive || _screen is not { } screen)
        {
            return;
        }

        if (!TryComputeGeometry(screen, out var geometry, out var frozen, out var cursorDip))
        {
            return;
        }

        if (frozen)
        {
            // 回退模式专用：光标在浮窗内 → 源区域与浮窗相交，抓到的会是自己。冻结画面（保留上一帧）。
            var c = cursorDip!.Value; // frozen 只可能发生在回退模式，那里 cursorDip 一定有值
            LogThrottled(
                $"[magnifier] 内容跟光标模式：光标({c.X:0},{c.Y:0})进入浮窗，画面冻结（源区域与浮窗相交）");
            return;
        }

        CaptureInto(geometry);
    }

    /// <summary>一帧的几何（物理像素，供 BitBlt）。</summary>
    private readonly record struct FrameGeometry(int SourceLeft, int SourceTop, int SourceWidth, int SourceHeight);

    /// <summary>
    /// 取景几何：镜下取景模式以浮窗中心为中心；回退模式以光标为中心。
    /// 全部算术都在 <see cref="MagnifierLens"/>（纯逻辑，有单元测试）。
    /// </summary>
    /// <param name="frozen">回退模式下源区域与浮窗相交（光标进浮窗），调用方必须跳过抓屏。</param>
    /// <param name="cursorDip">回退模式下的光标位置（DIP）；镜下取景模式为 <c>null</c>。</param>
    private bool TryComputeGeometry(ScreenInfo screen, out FrameGeometry geometry, out bool frozen, out DipPoint? cursorDip)
    {
        geometry = default;
        frozen = false;
        cursorDip = null;

        var scale = screen.NormalizedScale;
        var windowDips = new DipRect(Left, Top, Width, Height);

        DipRect sourceDips;
        if (_lensBelowWindow)
        {
            sourceDips = MagnifierLens.WindowToSource(windowDips, _zoom, screen.Bounds);
        }
        else
        {
            if (!TryGetCursorPos(out var cursor))
            {
                return false;
            }

            var cx = screen.Bounds.X + ((cursor.X - screen.PhysicalBounds.X) / scale);
            var cy = screen.Bounds.Y + ((cursor.Y - screen.PhysicalBounds.Y) / scale);
            cursorDip = new DipPoint(cx, cy);
            sourceDips = MagnifierLens.PointToSource(cx, cy, Width, Height, _zoom, screen.Bounds);
            frozen = sourceDips.Intersect(windowDips).Area > 0;
        }

        // DIP → 物理像素（BitBlt 用）。
        geometry = new FrameGeometry(
            (int)Math.Round(screen.PhysicalBounds.X + ((sourceDips.X - screen.Bounds.X) * scale)),
            (int)Math.Round(screen.PhysicalBounds.Y + ((sourceDips.Y - screen.Bounds.Y) * scale)),
            Math.Max(8, (int)Math.Round(sourceDips.Width * scale)),
            Math.Max(8, (int)Math.Round(sourceDips.Height * scale)));
        return true;
    }

    /// <summary>抓源区域并送进镜片位图。</summary>
    private void CaptureInto(FrameGeometry geometry)
    {
        var width = geometry.SourceWidth;
        var height = geometry.SourceHeight;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _buffer = new byte[ScreenCapture.Stride(width) * height];
        }

        if (!ReferenceEquals(_lensImage.Source, _bitmap))
        {
            _lensImage.Source = _bitmap;
        }

        if (!ScreenCapture.TryCaptureBgra32(geometry.SourceLeft, geometry.SourceTop, width, height, _buffer))
        {
            // 抓屏失败必须留痕：否则镜片会安静地停在上一帧，看起来像「卡住」。
            LogThrottled($"[magnifier] 抓屏失败：矩形 {geometry.SourceLeft},{geometry.SourceTop} {width}×{height}");
            return;
        }

        _bitmap.WritePixels(
            new Int32Rect(0, 0, width, height),
            _buffer,
            ScreenCapture.Stride(width),
            0);

        LogFollow(geometry);
    }

    private int _lastLoggedLeft = int.MinValue;
    private int _lastLoggedTop = int.MinValue;

    private void LogFollow(FrameGeometry geometry)
    {
        if (Math.Abs(geometry.SourceLeft - _lastLoggedLeft) <= 40 &&
            Math.Abs(geometry.SourceTop - _lastLoggedTop) <= 40)
        {
            return;
        }

        _lastLoggedLeft = geometry.SourceLeft;
        _lastLoggedTop = geometry.SourceTop;
        LogThrottled(
            $"[magnifier] 取景：源 {geometry.SourceWidth}×{geometry.SourceHeight}@{geometry.SourceLeft},{geometry.SourceTop}" +
            $" 浮窗={Left:0},{Top:0} 倍率={_zoom:0.0}（{(_lensBelowWindow ? "镜下取景" : "内容跟光标")}）");
    }

    private void LogWindowStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = WindowStyles.GetExtendedStyle(_hwnd);
        Trace?.Invoke(
            $"[magnifier] hwnd=0x{_hwnd:X} exstyle=0x{style:X8} 不激活={(style & WindowStyles.WsExNoActivate) != 0}" +
            $" 抓屏排除(WDA)={_lensBelowWindow} 模式={(_lensBelowWindow ? "镜下取景" : "内容跟光标")}");
    }

    /// <summary>节流日志（同类消息 2 秒内只记一次）——高频路径（取景/位置/冻结）必须节流。</summary>
    private void LogThrottled(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLogUtc).TotalMilliseconds < 2000)
        {
            return;
        }

        _lastLogUtc = now;
        Trace?.Invoke(message);
    }

    // ――――――――― 指针位置（仅回退模式需要） ―――――――――

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private bool TryGetCursorPos(out NativePoint point)
    {
        try
        {
            return GetCursorPos(out point);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            point = default;
            return false;
        }
    }
}
