using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

public sealed class ToolbarWindow : Window, IToolbarView
{

    private static readonly int[] GroupSizes = [3, 1, 2, 1];

    internal const int EraserLongPressMilliseconds = 800;

    private readonly TextBlock _statusLabel;
    private readonly Border _statusBar;
    private readonly StackPanel _row;
    private readonly StackPanel _root;
    private readonly List<Border> _capsules = [];
    private readonly List<(Button Button, ToolIconKind Icon)> _iconButtons = [];
    private readonly Button _pointerButton;
    private readonly Button _penButton;
    private readonly Button _eraserButton;
    private readonly Button _magnifierButton;
    private readonly Button _paletteButton;
    private readonly Button _brushButton;
    private readonly CornerBarWindow _leftCorner;
    private readonly CornerBarWindow _rightCorner;
    private readonly DispatcherTimer _eraserHoldTimer;

    private ScreenInfo? _screen;
    private UiTierMetrics _metrics = UiMetrics.Standard;
    private IntPtr _hwnd;
    private bool _penMode;
    private bool _eraserMode;
    private bool _eraserLongPressFired;
    private bool _magnifierActive;
    private bool _paletteActive;
    private bool _brushActive;
    private bool _thumbnailActive;
    private bool _nativeInkSupported = true;
    private string? _status;
    private bool _disposed;
    private bool _applyingLayout;
    private double _uiScale = 1;
    private double _shownUiScale = 1;

    private bool _shown;

    public ToolbarWindow()
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
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "PPTAssistant 工具栏";
        SnapsToDevicePixels = true;

        _pointerButton = Add(ToolIconKind.Pointer, "指针（鼠标态：触摸直接操作 PPT）", () => PenModeRequested?.Invoke(this, false));
        _penButton = Add(ToolIconKind.Pen, "笔（书写模式：触摸不再穿透给 PPT）", () => PenModeRequested?.Invoke(this, true));
        _eraserButton = Add(ToolIconKind.Eraser, "橡皮（点击进入擦除；长按清空本页板书）", OnEraserClicked);
        _magnifierButton = Add(ToolIconKind.Magnifier, "放大镜（1.0–3.0×，滚轮调倍率）", () => MagnifierToggleRequested?.Invoke(this, EventArgs.Empty));
        _paletteButton = Add(ToolIconKind.Palette, "色板（预设 / 最近使用 / 任意 RGB）", () => PaletteToggleRequested?.Invoke(this, EventArgs.Empty));
        _brushButton = Add(ToolIconKind.Brush, "笔刷（无级调节笔宽 2–50）", () => PenBrushToggleRequested?.Invoke(this, EventArgs.Empty));
        Add(ToolIconKind.Exit, "退出放映", () => ExitShowRequested?.Invoke(this, EventArgs.Empty));

        var groups = new List<IReadOnlyList<UIElement>>();
        var index = 0;
        foreach (var size in GroupSizes)
        {
            var members = new List<UIElement>(size);
            for (var i = 0; i < size && index < _iconButtons.Count; i++, index++)
            {
                members.Add(_iconButtons[index].Button);
            }

            groups.Add(members);
        }

        _row = UiSurface.GroupedRow(_metrics, groups);
        _capsules.AddRange(_row.Children.OfType<Border>());

        _statusLabel = new TextBlock
        {
            Foreground = UiTheme.StatusText,
            FontSize = UiMetrics.StatusFontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _statusBar = new Border
        {
            Background = UiTheme.MessageBackground,
            CornerRadius = UiSurface.Radius(_metrics),
            Padding = new Thickness(30, 10, 30, 10),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _statusLabel,
            Visibility = Visibility.Collapsed,
        };

        _root = new StackPanel { Orientation = Orientation.Vertical };
        _root.Children.Add(_statusBar);
        _root.Children.Add(_row);
        Content = _root;

        // 左右下角翻页区（C4）：本窗口持有，事件转发给上层（Session 只认识一个入口）。
        _leftCorner = new CornerBarWindow(ThumbnailSide.Left);
        _rightCorner = new CornerBarWindow(ThumbnailSide.Right);
        foreach (var corner in new[] { _leftCorner, _rightCorner })
        {
            corner.PreviousRequested += (_, _) => PreviousSlideRequested?.Invoke(this, EventArgs.Empty);
            corner.NextRequested += (_, _) => NextSlideRequested?.Invoke(this, EventArgs.Empty);
            corner.ThumbnailToggleRequested += (_, side) => ThumbnailPanelToggleRequested?.Invoke(this, side);
        }

        _eraserHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(EraserLongPressMilliseconds),
        };
        _eraserHoldTimer.Tick += (_, _) => OnEraserHoldElapsed();
        _eraserButton.PreviewMouseLeftButtonDown += OnEraserButtonPressed;
        _eraserButton.PreviewMouseLeftButtonUp += OnEraserButtonReleased;
        _eraserButton.LostMouseCapture += (_, _) => _eraserHoldTimer.Stop();

        // 触摸长按不要弹系统上下文菜单/按压反馈；本按钮自己处理「长按」语义。
        Stylus.SetIsPressAndHoldEnabled(_eraserButton, false);
        Stylus.SetIsFlicksEnabled(_eraserButton, false);

        SizeChanged += (_, _) => RepositionAll();
    }

    /// <inheritdoc />
    bool IToolbarView.IsVisible => base.IsVisible;

    /// <inheritdoc />
    public DipRect Bounds
    {
        get
        {
            var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
            var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
            return new DipRect(Left, Top, width, height);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DipRect> InputExclusionRects
    {
        get
        {
            var rects = new List<DipRect>(3);
            if (base.IsVisible)
            {
                rects.Add(Bounds);
            }

            if (_leftCorner.IsVisible)
            {
                rects.Add(_leftCorner.Bounds);
            }

            if (_rightCorner.IsVisible)
            {
                rects.Add(_rightCorner.Bounds);
            }

            return rects;
        }
    }

    /// <inheritdoc />
    public bool IsPenMode => _penMode;

    /// <inheritdoc />
    public bool IsEraserMode => _eraserMode;

    /// <summary>当前档位（诊断用）。</summary>
    public UiTier Tier => _metrics.Tier;

    /// <summary>用户缩放倍率；下次显示时应用，自动档位仍按原始屏宽选择。</summary>
    public double UiScale
    {
        get => _uiScale;
        set => _uiScale = UiScalePolicy.Normalize(value);
    }

    /// <summary>经屏幕空间限制后的实际倍率，中央条与两个角区始终一致。</summary>
    public double EffectiveUiScale { get; private set; } = 1;

    /// <summary>
    /// 两个角区窗口（**诊断用**：外观快照探针要单独渲染角区——它不是中央条可视树的一部分）。
    /// </summary>
    internal IReadOnlyList<CornerBarWindow> Corners => [_leftCorner, _rightCorner];

    /// <summary>诊断日志回调（由 App 注入；会一并下发给两个角区）。</summary>
    public Action<string>? Trace
    {
        get => _trace;
        set
        {
            _trace = value;
            _leftCorner.Trace = value;
            _rightCorner.Trace = value;
        }
    }

    /// <inheritdoc />
    public event EventHandler? NextSlideRequested;

    /// <inheritdoc />
    public event EventHandler? PreviousSlideRequested;

    /// <inheritdoc />
    public event EventHandler<bool>? PenModeRequested;

    /// <inheritdoc />
    public event EventHandler? EraseModeRequested;

    /// <inheritdoc />
    public event EventHandler? ClearPageRequested;

    /// <inheritdoc />
    public event EventHandler? MagnifierToggleRequested;

    /// <inheritdoc />
    public event EventHandler<ThumbnailSide>? ThumbnailPanelToggleRequested;

    /// <inheritdoc />
    public event EventHandler? PaletteToggleRequested;

    /// <inheritdoc />
    public event EventHandler? PenBrushToggleRequested;

    /// <inheritdoc />
    public event EventHandler? ExitShowRequested;

    /// <inheritdoc />
    public void ShowOn(ScreenInfo screen)
    {
        _screen = screen;
        _shown = true;
        _shownUiScale = UiScale;
        _applyingLayout = true;
        try
        {
            ApplyMetrics(UiMetrics.For(screen.Bounds.Width));
        }
        finally
        {
            _applyingLayout = false;
        }

        ApplyScaleAndLayout();

        if (!base.IsVisible)
        {
            Show();
        }

        UpdateLayout();
        RepositionAll();
        ApplyWindowStyles();
    }

    /// <inheritdoc />
    void IToolbarView.Hide()
    {
        HideAll();
    }

    /// <summary>隐藏中央条与两个角区（幂等；把 <see cref="_shown"/> 一并置否）。</summary>
    public void HideAll()
    {
        _shown = false;

        // 收起时取消未完成的长按（否则窗口都收掉了，800ms 后仍会发一次清页请求）。
        _eraserHoldTimer.Stop();
        _eraserLongPressFired = false;

        _leftCorner.HidePanel();
        _rightCorner.HidePanel();

        var wpfVisible = base.IsVisible;
        if (wpfVisible)
        {
            base.Hide();
        }

        // ★ 中央条同样在 OS 层再压一次（理由见 CornerBarWindow.HidePanel）：
        //   WPF 说「已隐藏」不等于屏幕上真的没有了。
        var osVisible = false;
        if (_hwnd != IntPtr.Zero)
        {
            osVisible = WindowStyles.IsWindowVisibleOnScreen(_hwnd);
            WindowStyles.HideWindow(_hwnd);
        }

        if (wpfVisible || osVisible)
        {
            Trace?.Invoke(
                $"[toolbar] 中央条已收起（WPF IsVisible={wpfVisible}→{base.IsVisible} OS 可见（WPF Hide 之后）={osVisible}；"
                + $"左角 WPF={_leftCorner.IsVisible} 右角 WPF={_rightCorner.IsVisible}）");
        }
    }

    /// <inheritdoc />
    public void SetPenMode(bool penMode)
    {
        _penMode = penMode;
        _eraserMode = false;

        ToolButton.MarkActive(_eraserButton, false);
        ToolButton.MarkActive(_penButton, penMode);
        ToolButton.MarkActive(_pointerButton, !penMode);
    }

    /// <inheritdoc />
    public void SetEraserMode(bool eraserMode)
    {
        _eraserMode = eraserMode;
        ToolButton.MarkActive(_eraserButton, eraserMode);

        if (eraserMode)
        {
            // 橡皮驻留时不能同时显示指针或笔为激活（否则老师无法判断当前拖动会写字还是擦除）。
            _penMode = false;
            ToolButton.MarkActive(_penButton, false);
            ToolButton.MarkActive(_pointerButton, false);
        }
    }

    /// <inheritdoc />
    public void SetMagnifierActive(bool active)
    {
        _magnifierActive = active;
        ToolButton.MarkActive(_magnifierButton, active);
    }

    /// <inheritdoc />
    public void SetThumbnailPanelActive(bool active)
    {
        // 缩略图按钮在两侧角区上——两侧一起高亮（侧栏是同一个窗口，只是停在左或右）。
        _thumbnailActive = active;
        _leftCorner.SetThumbnailActive(active);
        _rightCorner.SetThumbnailActive(active);
    }

    /// <inheritdoc />
    public void SetPaletteActive(bool active)
    {
        _paletteActive = active;
        ToolButton.MarkActive(_paletteButton, active);
    }

    /// <inheritdoc />
    public void SetPenBrushActive(bool active)
    {
        _brushActive = active;
        ToolButton.MarkActive(_brushButton, active);
    }

    /// <inheritdoc />
    public void UpdateSlidePosition(int slideIndex, int slideCount)
    {
        var text = PageLabel.Format(slideIndex, slideCount);
        _leftCorner.SetPageLabel(text);
        _rightCorner.SetPageLabel(text);
    }

    /// <inheritdoc />
    public void SetNativeInkSupported(bool supported)
    {
        _nativeInkSupported = supported;
        RefreshStatus();
    }

    /// <inheritdoc />
    public void SetStatus(string? status)
    {
        _status = status;
        RefreshStatus();
    }

    /// <inheritdoc />
    public void EnsureOnTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }

        _leftCorner.EnsureOnTop();
        _rightCorner.EnsureOnTop();
    }

    /// <summary>释放窗口（产品里由 App 在退出时调用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eraserHoldTimer.Stop();
        _leftCorner.Dispose();
        _rightCorner.Dispose();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // 工具栏本身绝不能被点穿透（否则按钮点不到）。
        WindowStyles.ApplyOverlayBase(_hwnd);
        WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
    }

    private Button Add(ToolIconKind icon, string tooltip, Action onClick)
    {
        var button = ToolButton.Create(icon, tooltip, onClick, _metrics, standalone: false);
        _iconButtons.Add((button, icon));
        return button;
    }

    private void ApplyMetrics(UiTierMetrics metrics)
    {
        _metrics = metrics;

        foreach (var (button, icon) in _iconButtons)
        {
            ToolButton.ApplyMetrics(button, icon, metrics);
        }

        for (var i = 0; i < _capsules.Count; i++)
        {
            UiSurface.ApplyMetrics(_capsules[i], metrics);
            _capsules[i].Margin = i < _capsules.Count - 1
                ? new Thickness(0, 0, metrics.Gap, 0)
                : new Thickness(0);
        }

        _statusBar.CornerRadius = UiSurface.Radius(metrics);
        var compact = metrics.IsCompact;
        _statusBar.Padding = compact ? new Thickness(20, 6, 20, 6) : new Thickness(30, 10, 30, 10);
        _statusLabel.FontSize = compact ? 14 : UiMetrics.StatusFontSize;

        _leftCorner.ApplyMetrics(metrics);
        _rightCorner.ApplyMetrics(metrics);

        // 换档后重新声明一次激活状态（按钮对象没变，这里只是防御性重申，不改变语义）。
        _leftCorner.SetThumbnailActive(_thumbnailActive);
        _rightCorner.SetThumbnailActive(_thumbnailActive);
        ToolButton.MarkActive(_magnifierButton, _magnifierActive);
        ToolButton.MarkActive(_paletteButton, _paletteActive);
        ToolButton.MarkActive(_brushButton, _brushActive);
        ToolButton.MarkActive(_eraserButton, _eraserMode);
        ToolButton.MarkActive(_penButton, _penMode);
        ToolButton.MarkActive(_pointerButton, !_penMode && !_eraserMode);
    }

    /// <summary>橡皮按钮的点击（短按）：进入橡皮态；长按已触发清页时吞掉这次点击。</summary>
    private void OnEraserClicked()
    {
        if (_eraserLongPressFired)
        {
            _eraserLongPressFired = false;
            return;
        }

        EraseModeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEraserButtonPressed(object sender, MouseButtonEventArgs e)
    {
        _eraserLongPressFired = false;
        _eraserHoldTimer.Stop();
        _eraserHoldTimer.Start();
    }

    private void OnEraserButtonReleased(object sender, MouseButtonEventArgs e)
    {
        _eraserHoldTimer.Stop();

        // ★ 不在这里设 `e.Handled`：预览事件被吞掉会让 ButtonBase 收不到抬手，
        //   从而不释放鼠标捕获（工具栏后续点击可能全部被这个按钮吞掉）。
        //   长按后的「不进入橡皮态」由 Click 回调里的 `_eraserLongPressFired` 判定。
    }

    /// <summary>长按达到阈值：清空本页（破坏性动作，由 Session 在状态行给出反馈）。</summary>
    private void OnEraserHoldElapsed()
    {
        _eraserHoldTimer.Stop();
        if (_eraserLongPressFired)
        {
            return;
        }

        _eraserLongPressFired = true;
        Trace?.Invoke($"[toolbar] 橡皮长按 ≥{EraserLongPressMilliseconds}ms → 请求清空本页");
        ClearPageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshStatus()
    {
        var parts = new List<string>(2);
        if (!_nativeInkSupported)
        {
            parts.Add("宿主不支持原生墨迹写回");
        }

        if (!string.IsNullOrWhiteSpace(_status))
        {
            parts.Add(_status!);
        }

        if (parts.Count == 0)
        {
            _statusLabel.Text = string.Empty;
            _statusBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            _statusLabel.Text = string.Join(" · ", parts);
            _statusBar.Visibility = Visibility.Visible;
        }

        if (base.IsVisible)
        {
            ApplyScaleAndLayout();
            RepositionAll();
        }
    }

    /// <summary>先按原始档位测量，再对整个可视树统一缩放，避免字号与点击区域使用不同倍率。</summary>
    private void ApplyScaleAndLayout()
    {
        if (_screen is not { } screen || _applyingLayout)
        {
            return;
        }

        _applyingLayout = true;
        try
        {
            // 状态文案只增加高度，不把中央条撑宽至两侧翻页区。
            _row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var centerWidth = _row.DesiredSize.Width;
            _statusBar.MaxWidth = centerWidth;
            _statusBar.Measure(new Size(centerWidth, double.PositiveInfinity));
            var centerHeight = _row.DesiredSize.Height + _statusBar.DesiredSize.Height;
            var cornerWidth = UiMetrics.CornerBarWidth(_metrics);
            var cornerHeight = UiMetrics.CornerBarHeight(_metrics);
            const double separation = 8;
            EffectiveUiScale = UiScalePolicy.Fit(
                _shownUiScale,
                Math.Max(1, screen.Bounds.Width - (2 * ToolbarLayout.CornerMarginDips) - (2 * separation)),
                Math.Max(1, screen.Bounds.Height - (2 * ToolbarLayout.BottomMarginDips)),
                centerWidth + (2 * cornerWidth),
                Math.Max(centerHeight, cornerHeight));

            _root.LayoutTransform = EffectiveUiScale == 1
                ? Transform.Identity
                : new ScaleTransform(EffectiveUiScale, EffectiveUiScale);
            _leftCorner.ApplyUiScale(EffectiveUiScale);
            _rightCorner.ApplyUiScale(EffectiveUiScale);
            _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            UpdateLayout();
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    private void RepositionAll()
    {
        if (_screen is not { } screen || _applyingLayout)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var plan = ToolbarLayout.Plan(screen.Bounds, width, height, _leftCorner.Width, _leftCorner.Height);
        Left = plan.CenterBar.X;
        Top = plan.CenterBar.Y;
        _leftCorner.ShowAt(plan.LeftCorner, _shown);
        _rightCorner.ShowAt(plan.RightCorner, _shown);
    }

    private void ApplyWindowStyles()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.ApplyOverlayBase(_hwnd);
            WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }

        var style = _hwnd == IntPtr.Zero ? 0 : WindowStyles.GetExtendedStyle(_hwnd);
        Trace?.Invoke(
            $"[toolbar] hwnd=0x{_hwnd:X} exstyle=0x{style:X8} 档位={DescribeTier} 缩放={_shownUiScale:P0} 实际={EffectiveUiScale:P0} 中央条={Bounds} 左角={_leftCorner.Bounds} 右角={_rightCorner.Bounds}");
    }

    private string DescribeTier => _metrics.Tier switch
    {
        UiTier.Compact => "窄屏档（<1440）",
        UiTier.Large => "大屏档（≥3800）",
        _ => "标准档（1440–3799）",
    };

    private Action<string>? _trace;
}
