using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

public sealed class PenPaletteWindow : Window, IPenPaletteView
{
    private readonly PenPaletteControl _palette = new();
    private readonly FloatingPanel _panel;
    private ScreenInfo? _screen;
    private DipRect _anchor;
    private IntPtr _hwnd;
    private UiTierMetrics? _appliedTier;
    private bool _disposed;

    public PenPaletteWindow()
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
        Title = "PPTAssistant 色板";

        _panel = new FloatingPanel("收起色板", () =>
        {
            HidePanel();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        _panel.Title = "色板";
        _panel.SetBody(_palette);
        Content = _panel.Root;

        _palette.ColorPicked += (_, e) =>
        {
            ColorPicked?.Invoke(this, e.Color);

            if (e.Source != PalettePickSource.Swatch)
            {
                return;
            }

            HidePanel();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <inheritdoc />
    bool IPenPaletteView.IsVisible => base.IsVisible;

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
    public InkColor CurrentColor => _palette.CurrentColor;

    /// <summary>用户选择的 UI 比例；下次显示时在基础档位之上应用。</summary>
    public double UiScale { get; set; } = UiScalePolicy.Default;

    /// <summary>考虑屏幕可用空间后实际应用的 UI 比例。</summary>
    public double EffectiveUiScale { get; private set; } = UiScalePolicy.Default;

    /// <inheritdoc />
    public event EventHandler<InkColor>? ColorPicked;

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
            _palette.ApplyMetrics(metrics);
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
    void IPenPaletteView.Hide() => HidePanel();

    /// <summary>隐藏（幂等）。</summary>
    public void HidePanel()
    {
        if (base.IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void SetCurrentColor(InkColor color) => _palette.SetCurrentColor(color);

    /// <inheritdoc />
    public void SetRecentColors(IReadOnlyList<InkColor> recent) => _palette.SetRecentColors(recent);

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
}
