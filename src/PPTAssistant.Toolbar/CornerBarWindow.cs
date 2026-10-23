using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

internal sealed class CornerBarWindow : Window
{
    private readonly StackPanel _row;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Button _pageButton;
    private readonly TextBlock _pageLabel;
    private readonly Viewbox _pageFace;
    private readonly ThumbnailSide _side;
    private UiTierMetrics _metrics = UiMetrics.Standard;
    private IntPtr _hwnd;
    private int _showCount;
    private int _hideCount;
    private bool _disposed;

    /// <param name="side">本角区所在的一侧（决定缩略图侧栏方位）。</param>
    public CornerBarWindow(ThumbnailSide side)
    {
        _side = side;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Title = side == ThumbnailSide.Left ? "PPTAssistant 翻页区（左）" : "PPTAssistant 翻页区（右）";

        _previousButton = ToolButton.Create(ToolIconKind.ChevronLeft, "上一页", () => PreviousRequested?.Invoke(this, EventArgs.Empty), _metrics);
        _nextButton = ToolButton.Create(ToolIconKind.ChevronRight, "下一页", () => NextRequested?.Invoke(this, EventArgs.Empty), _metrics);

        _pageLabel = new TextBlock
        {
            Text = PageLabel.Format(0, 0),
            FontFamily = UiTheme.PageFont,
            Foreground = UiTheme.Text,
            FontSize = _metrics.PageFontSize,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _pageFace = new Viewbox
        {
            Child = _pageLabel,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _pageButton = ToolButton.CreateWithFace(
            _pageFace,
            side == ThumbnailSide.Left ? "幻灯片导航（左侧）" : "幻灯片导航（右侧）",
            () => ThumbnailToggleRequested?.Invoke(this, _side),
            _metrics.PageButtonWidth,
            _metrics);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _row.Children.Add(_previousButton);
        _row.Children.Add(_pageButton);
        _row.Children.Add(_nextButton);

        Content = _row;
        ApplyMetrics(_metrics);
    }

    /// <summary>上一页。</summary>
    public event EventHandler? PreviousRequested;

    /// <summary>下一页。</summary>
    public event EventHandler? NextRequested;

    /// <summary>缩略图侧栏开关（带上是哪一侧）。</summary>
    public event EventHandler<ThumbnailSide>? ThumbnailToggleRequested;

    /// <summary>本角区所在的一侧。</summary>
    public ThumbnailSide Side => _side;

    /// <summary>诊断日志回调（由 <see cref="ToolbarWindow"/> 注入）。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>屏幕矩形（DIP）。</summary>
    public DipRect Bounds => new(Left, Top, Width, Height);

    /// <summary>当前档位（诊断用）。</summary>
    public UiTier Tier => _metrics.Tier;

    /// <summary>与中央条共用的实际缩放倍率。</summary>
    public double EffectiveUiScale { get; private set; } = 1;

    /// <summary>同步页码（两处角区由 <see cref="ToolbarWindow"/> 同时写入）。</summary>
    public void SetPageLabel(string text) => _pageLabel.Text = text;

    /// <summary>同步缩略图侧栏的开启高亮（中间那颗页码按钮）。</summary>
    public void SetThumbnailActive(bool active) => ToolButton.MarkActive(_pageButton, active);

    public void ApplyMetrics(UiTierMetrics metrics)
    {
        _metrics = metrics;

        ToolButton.ApplyMetrics(_previousButton, ToolIconKind.ChevronLeft, metrics);
        ToolButton.ApplyMetrics(_nextButton, ToolIconKind.ChevronRight, metrics);

        _pageButton.Width = metrics.PageButtonWidth;
        _pageButton.Height = metrics.ButtonSize;
        ToolButton.SetCornerRadius(_pageButton, new CornerRadius(metrics.CornerRadius));
        _pageLabel.FontSize = metrics.PageFontSize;
        // 三位数及更长页码按需收小，保留完整数字，且不改变角区宽度或命中区域。
        var inset = Math.Max(2, metrics.ButtonSize * 0.06);
        _pageFace.Width = metrics.PageButtonWidth - (2 * inset);
        _pageFace.Height = metrics.ButtonSize - (2 * inset);

        var gap = new Thickness(0, 0, metrics.Gap, 0);
        _previousButton.Margin = gap;
        _pageButton.Margin = gap;
        _nextButton.Margin = new Thickness(0);

        ApplyUiScale(EffectiveUiScale);
    }

    /// <summary>缩放整个角区内容；窗口外框同步使用相同倍率，保证覆盖层挖洞与按钮一致。</summary>
    public void ApplyUiScale(double scale)
    {
        // 中央条的适屏计算可能低于用户选项的最小倍率，不能在此重新按选项范围钳制。
        EffectiveUiScale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        _row.LayoutTransform = EffectiveUiScale == 1
            ? Transform.Identity
            : new ScaleTransform(EffectiveUiScale, EffectiveUiScale);
        Width = UiMetrics.CornerBarWidth(_metrics) * EffectiveUiScale;
        Height = UiMetrics.CornerBarHeight(_metrics) * EffectiveUiScale;
        if (base.IsVisible)
        {
            UpdateLayout();
        }
    }

    public void ShowAt(DipRect rect, bool visible)
    {
        Left = rect.X;
        Top = rect.Y;
        Width = rect.Width;
        Height = rect.Height;

        if (!visible)
        {
            HidePanel();
            return;
        }

        if (!base.IsVisible)
        {
            Show();
            _showCount++;
            Trace?.Invoke($"[corner] {(Side == ThumbnailSide.Left ? "左角" : "右角")}已显示（第 {_showCount} 次）位置={rect}");
        }

        UpdateLayout();

        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.ApplyOverlayBase(_hwnd);
            WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    public void HidePanel()
    {
        var name = Side == ThumbnailSide.Left ? "左角" : "右角";
        var wpfVisible = base.IsVisible;
        if (wpfVisible)
        {
            base.Hide();
        }

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // ★ 在 OS 层再压一次（幂等）。WPF 的 IsVisible 与操作系统可见性是两回事：
        //   只要出现一次失配，屏幕上就会留下「退不掉」的角区窗口，而日志一切正常。
        var osVisibleBefore = WindowStyles.IsWindowVisibleOnScreen(_hwnd);
        WindowStyles.HideWindow(_hwnd);

        // 只在**真的发生过一次隐藏**时记账（重排也会走到这里，静默通过以免刷屏）。
        if (!wpfVisible && !osVisibleBefore)
        {
            return;
        }

        var osVisibleAfter = WindowStyles.IsWindowVisibleOnScreen(_hwnd);
        _hideCount++;
        Trace?.Invoke(
            $"[corner] {name}已收起（第 {_hideCount} 次；WPF IsVisible={wpfVisible}→{base.IsVisible}"
            + $" OS 可见（WPF Hide 之后）={osVisibleBefore}→{osVisibleAfter}）");

        if (osVisibleAfter)
        {
            Trace?.Invoke($"[corner] ⚠ {name}在 ShowWindow(SW_HIDE) 之后 OS 仍报告可见——需现场排查（可能是别的进程在同位置画东西）");
        }
    }

    /// <summary>置顶且不激活。</summary>
    public void EnsureOnTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    /// <summary>释放窗口（幂等）。</summary>
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
