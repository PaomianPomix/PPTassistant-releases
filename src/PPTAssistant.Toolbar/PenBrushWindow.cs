using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

public sealed class PenBrushWindow : Window, IPenBrushView
{
    private const double PreviewBoxWidth = 108;
    private const double PreviewBoxHeight = 76;

    private readonly Slider _slider;
    private readonly Ellipse _dot;
    private readonly Border _preview;
    private readonly TextBlock _valueLabel;
    private readonly FloatingPanel _panel;
    private ScreenInfo? _screen;
    private DipRect _anchor;
    private IntPtr _hwnd;
    private UiTierMetrics? _appliedTier;
    private bool _suppress;
    private bool _dirty;
    private double _value = PenWidthRange.DefaultDips;
    private bool _disposed;

    public PenBrushWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "PPTAssistant 笔刷";

        // 预览：主题的「纸」底（白）+ 居中的圆点（直径 = 当前值，DIP）。
        _dot = new Ellipse
        {
            Width = _value,
            Height = _value,
            Fill = UiTheme.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _preview = new Border
        {
            Width = PreviewBoxWidth,
            Height = PreviewBoxHeight,
            Background = UiTheme.ThumbnailItemBackground,
            BorderBrush = UiTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _dot,
        };

        _valueLabel = new TextBlock
        {
            Text = PenWidthRange.Format(_value),
            Foreground = UiTheme.Text,
            FontSize = 20,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
        };

        _slider = new Slider
        {
            Minimum = PenWidthRange.MinDips,
            Maximum = PenWidthRange.MaxDips,
            Value = _value,
            TickFrequency = PenWidthRange.StepDips,
            SmallChange = PenWidthRange.StepDips,
            LargeChange = 2.0,
            IsSnapToTickEnabled = true,
            Width = 232,
            IsMoveToPointEnabled = false,
            Focusable = false,
            Margin = new Thickness(0, 2, 0, 2),
        };
        UiSliderStyle.Apply(_slider);
        _slider.ValueChanged += OnSliderValueChanged;

        // 抬手 = 一次调节结束 → 落盘（拖动过程中的变化只即时生效、不写盘）。
        _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Commit()));
        _slider.PreviewMouseLeftButtonUp += (_, _) => Commit();

        var body = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(_preview);
        body.Children.Add(_valueLabel);
        body.Children.Add(_slider);

        _panel = new FloatingPanel("收起笔刷", () =>
        {
            HidePanel();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        _panel.Title = "笔刷大小";
        _panel.SetBody(body);
        Content = _panel.Root;
    }

    /// <inheritdoc />
    bool IPenBrushView.IsVisible => base.IsVisible;

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
    public double Value => _value;

    /// <summary>用户选择的 UI 比例；下次显示时在基础档位之上应用。</summary>
    public double UiScale { get; set; } = UiScalePolicy.Default;

    /// <summary>考虑屏幕可用空间后实际应用的 UI 比例。</summary>
    public double EffectiveUiScale { get; private set; } = UiScalePolicy.Default;

    /// <inheritdoc />
    public event EventHandler<double>? ValueChanged;

    /// <inheritdoc />
    public event EventHandler<double>? ValueCommitted;

    /// <inheritdoc />
    public event EventHandler? CloseRequested;

    /// <inheritdoc />
    public void ShowOn(ScreenInfo screen, DipRect anchorDips)
    {
        _screen = screen;
        _anchor = anchorDips;

        var metrics = UiMetrics.For(screen.Bounds.Width);
        if (_appliedTier != metrics)
        {
            _appliedTier = metrics;
            _panel.ApplyMetrics(metrics);
            _preview.CornerRadius = new CornerRadius(metrics.CornerRadius);
        }

        EffectiveUiScale = UiScaling.FitToScreen((FrameworkElement)_panel.Root, screen, UiScale);

        if (!base.IsVisible)
        {
            Show();
        }

        UpdateLayout();
        FloatingPanel.PlaceNear(this, screen, _anchor);

        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.ApplyOverlayBase(_hwnd);
            WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    /// <inheritdoc />
    void IPenBrushView.Hide()
    {
        // 收起也算一次调节结束：确保最后一次拖动值被持久化。
        Commit();
        HidePanel();
    }

    /// <summary>隐藏（幂等）。</summary>
    public void HidePanel()
    {
        if (base.IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void SetValue(double valueDips)
    {
        var clamped = PenWidthRange.Clamp(valueDips);
        _suppress = true;
        try
        {
            _value = clamped;
            _slider.Value = clamped;
            UpdatePreview();
        }
        finally
        {
            _suppress = false;
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
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowStyles.ApplyOverlayBase(_hwnd);
        WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _value = PenWidthRange.Clamp(e.NewValue);
        UpdatePreview();

        if (!_suppress)
        {
            _dirty = true;
            ValueChanged?.Invoke(this, _value);
        }
    }

    /// <summary>
    /// 一次调节结束（抬手 / 收起）→ 落盘。
    /// </summary>
    /// <remarks>只在**用户真的动过滑杆**时才抛：散场时 <see cref="IPenBrushView.Hide"/> 也会走到这里，
    /// 若无条件抛事件，每次退出放映都会白写一次 config、并在日志里留下假的「笔宽 → x DIP」。</remarks>
    private void Commit()
    {
        if (_suppress || !_dirty)
        {
            return;
        }

        _dirty = false;
        ValueCommitted?.Invoke(this, _value);
    }

    private void UpdatePreview()
    {
        _valueLabel.Text = PenWidthRange.Format(_value);
        _dot.Width = _value;
        _dot.Height = _value;
    }
}
