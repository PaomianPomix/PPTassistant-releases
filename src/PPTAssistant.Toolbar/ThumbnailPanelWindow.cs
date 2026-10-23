using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PPTAssistant.Coords;
using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Toolbar;

public sealed class ThumbnailPanelWindow : Window, IThumbnailPanelView
{
    private const double ThumbnailDecodeMargin = 8;

    private readonly StackPanel _items;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _title;
    private readonly Border _frame;
    private readonly Button _close;
    private readonly DockPanel _header;
    private readonly Dictionary<int, Border> _itemBorders = [];
    private ScreenInfo? _screen;
    private IntPtr _hwnd;
    private UiTierMetrics _metrics = UiMetrics.Standard;
    private int _currentSlide;
    private ThumbnailSide _side = ThumbnailSide.Right;
    private bool _disposed;

    public ThumbnailPanelWindow()
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
        Title = "PPTAssistant 缩略图";
        Width = _metrics.ThumbnailPanelWidth;

        _title = new TextBlock
        {
            Text = "幻灯片",
            Foreground = UiTheme.Text,
            FontSize = 16,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _close = UiCloseButton.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty), "收起缩略图");

        _header = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 8, 8, 6) };
        DockPanel.SetDock(_close, Dock.Right);
        _header.Children.Add(_close);
        _header.Children.Add(_title);

        _items = new StackPanel { Orientation = Orientation.Vertical };
        _scroll = new ScrollViewer
        {

            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _items,
            Margin = new Thickness(0, 0, 2, 0),
        };

        var root = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        root.Children.Add(_header);
        root.Children.Add(_scroll);

        Content = _frame = new Border
        {
            Background = UiTheme.SidebarBackground,
            BorderBrush = UiTheme.Border,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = root,
        };

        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        SizeChanged += (_, _) => Reposition();
    }

    /// <inheritdoc />
    bool IThumbnailPanelView.IsVisible => base.IsVisible;

    /// <inheritdoc />
    public event EventHandler<int>? SlideActivated;

    /// <inheritdoc />
    public event EventHandler? CloseRequested;

    public DipRect Bounds => new(Left, Top, Width, Height);

    /// <summary>当前停靠的一侧（C4：左角区呼左栏、右角区呼右栏）。</summary>
    public ThumbnailSide Side => _side;

    /// <summary>当前档位（诊断用）。</summary>
    public UiTier Tier => _metrics.Tier;

    /// <summary>用户选择的 UI 比例；下次显示时在基础档位之上应用。</summary>
    public double UiScale { get; set; } = UiScalePolicy.Default;

    /// <summary>考虑屏幕可用空间后实际应用的 UI 比例。</summary>
    public double EffectiveUiScale { get; private set; } = UiScalePolicy.Default;

    /// <inheritdoc />
    public void ShowOn(ScreenInfo screen, ThumbnailSide side)
    {
        _screen = screen;
        _side = side;

        var metrics = UiMetrics.For(screen.Bounds.Width);
        if (metrics != _metrics)
        {
            _metrics = metrics;
            ApplyMetrics(metrics);
        }

        // 靠内的一侧画边框（贴右时左边框、贴左时右边框），让面板与屏幕边缘区分开。
        _frame.BorderThickness = side == ThumbnailSide.Right
            ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 0, 1, 0);
        _title.Text = metrics.IsCompact
            ? "幻灯片"
            : side == ThumbnailSide.Right ? "幻灯片（右侧）" : "幻灯片（左侧）";

        // 全高侧栏只放大内部内容；逻辑高度反向换算，确保底部仍有可滚动的视口。
        // 极矮屏至少留得下标题和一张完整缩略图。
        _header.Measure(new Size(metrics.ThumbnailPanelWidth, double.PositiveInfinity));
        var minimumHeight = _header.DesiredSize.Height + metrics.ThumbnailHeight
            + (2 * UiMetrics.PanelBorderThickness) + UiMetrics.ThumbnailItemGap;
        EffectiveUiScale = UiScalePolicy.Fit(UiScale, screen.Bounds.Width, screen.Bounds.Height,
            metrics.ThumbnailPanelWidth, minimumHeight);
        _frame.Width = metrics.ThumbnailPanelWidth;
        _frame.Height = screen.Bounds.Height / EffectiveUiScale;
        UiScaling.Apply(_frame, EffectiveUiScale);
        Width = metrics.ThumbnailPanelWidth * EffectiveUiScale;
        Top = screen.Bounds.Y;
        Height = screen.Bounds.Height;
        Reposition();

        if (!base.IsVisible)
        {
            Show();
        }

        UpdateLayout();
        Reposition();

        if (_hwnd != IntPtr.Zero)
        {
            WindowStyles.ApplyOverlayBase(_hwnd);
            WindowStyles.SetClickThrough(_hwnd, clickThrough: false);
            WindowStyles.BringToTopWithoutActivation(_hwnd);
        }
    }

    /// <inheritdoc />
    void IThumbnailPanelView.Hide()
    {
        if (base.IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void SetThumbnails(IReadOnlyList<SlideThumbnail> thumbnails, int currentSlideIndex)
    {
        _items.Children.Clear();
        _itemBorders.Clear();
        _currentSlide = currentSlideIndex;

        foreach (var thumbnail in thumbnails)
        {
            var item = CreateItem(thumbnail);
            _itemBorders[thumbnail.SlideIndex] = item;
            _items.Children.Add(item);
        }

        UpdateHighlights();

        // 打开时把当前页滚进视野（老师最关心的是「我现在在哪」）。
        if (_itemBorders.TryGetValue(currentSlideIndex, out _))
        {
            _scroll.ScrollToVerticalOffset(Math.Max(0, (currentSlideIndex - 2) * (_metrics.ThumbnailHeight + UiMetrics.ThumbnailItemGap)));
        }
    }

    /// <inheritdoc />
    public void SetCurrentSlide(int oneBasedIndex)
    {
        _currentSlide = oneBasedIndex;
        UpdateHighlights();
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

    /// <summary>档位变化时更新面板宽、关闭按钮与已建项（列表内容不重建，避免闪一下）。</summary>
    private void ApplyMetrics(UiTierMetrics metrics)
    {
        _title.FontSize = metrics.IsCompact ? 14 : 16;
        UiCloseButton.Resize(_close, metrics.IsCompact ? 20 : 24);

        foreach (var item in _itemBorders.Values)
        {
            ApplyItemMetrics(item, metrics);
        }
    }

    private void Reposition()
    {
        if (_screen is not { } screen)
        {
            return;
        }

        // C4：贴所在一侧的屏边（左角区呼出的侧栏贴左边，右角区呼出的贴右边）。
        Left = _side == ThumbnailSide.Right
            ? screen.Bounds.Right - Width
            : screen.Bounds.X;
        Top = screen.Bounds.Y;
        Height = screen.Bounds.Height;
    }

    private Border CreateItem(SlideThumbnail thumbnail)
    {
        var index = thumbnail.SlideIndex;
        var metrics = _metrics;
        var content = new Grid { Width = metrics.ThumbnailWidth - ThumbnailDecodeMargin, Height = metrics.ThumbnailHeight };

        if (thumbnail.ImagePath is { Length: > 0 } path && TryLoadImage(path, metrics) is { } image)
        {
            content.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
        }
        else
        {

            content.Children.Add(CreatePlaceholder(metrics));
        }

        content.Children.Add(CreateBadge(index, metrics));
        content.Children.Add(CreateSelectionFrame(metrics));

        var item = new Border
        {
            BorderThickness = new Thickness(0),
            Margin = new Thickness(3, 0, 3, UiMetrics.ThumbnailItemGap),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = $"跳到第 {index} 页",
            Tag = index,
        };

        ApplyItemMetrics(item, metrics);

        // 触摸与鼠标都会走到这里（WPF 把触摸提升为鼠标事件）。
        item.MouseLeftButtonUp += (_, _) => SlideActivated?.Invoke(this, index);
        return item;
    }

    private static void ApplyItemMetrics(Border item, UiTierMetrics metrics)
    {
        item.Background = UiTheme.ThumbnailItemBackground;
        item.BorderBrush = UiTheme.Border;
        item.BorderThickness = new Thickness(UiMetrics.PanelBorderThickness);
        item.CornerRadius = new CornerRadius(Math.Min(metrics.CornerRadius, 6));
        item.Margin = new Thickness(3, 0, 3, UiMetrics.ThumbnailItemGap);
        item.Width = metrics.ThumbnailWidth - 6;

        if (item.Child is Grid grid)
        {
            grid.Width = metrics.ThumbnailWidth - ThumbnailDecodeMargin;
            grid.Height = metrics.ThumbnailHeight;
        }
    }

    private static Grid CreateBadge(int index, UiTierMetrics metrics)
    {
        var width = metrics.ThumbnailBadgeWidth;
        var height = metrics.ThumbnailBadgeHeight;
        var background = new Path
        {
            Data = FoldedCornerGeometry(width, height),
            Fill = UiTheme.White,
            Stroke = UiTheme.BadgeBorder,
            StrokeThickness = 1,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Tag = "BadgeBackground",
        };

        var label = new TextBlock
        {
            Text = index.ToString(),
            Foreground = UiTheme.Text,
            FontSize = metrics.ThumbnailBadgeFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var host = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Tag = "Badge",
        };
        host.Children.Add(background);
        host.Children.Add(label);
        return host;
    }

    private static Border CreateSelectionFrame(UiTierMetrics metrics) => new()
    {
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(Math.Min(metrics.CornerRadius, 6)),
        IsHitTestVisible = false,
        Tag = "SelectionFrame",
    };

    private static UIElement CreatePlaceholder(UiTierMetrics metrics)
    {
        var box = new Grid
        {
            Background = UiTheme.ThumbnailItemBackground,
            IsHitTestVisible = false,
        };

        var height = Math.Max(18, metrics.ThumbnailHeight - metrics.ThumbnailBadgeHeight - 16);
        var width = Math.Max(24, height * 36d / 28d);
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        canvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Stroke = UiTheme.PlaceholderLine,
            StrokeThickness = 1,
        });

        for (var i = 0; i < 3; i++)
        {
            var line = new Rectangle
            {
                Width = width * (0.5 - (i * 0.09)),
                Height = 1,
                Fill = UiTheme.PlaceholderLine,
            };
            Canvas.SetLeft(line, width * 0.11);
            Canvas.SetTop(line, height * (0.3 + (i * 0.16)));
            canvas.Children.Add(line);
        }

        var sun = new Ellipse
        {
            Width = Math.Max(2, height * 0.16),
            Height = Math.Max(2, height * 0.16),
            Fill = UiTheme.PlaceholderLine,
        };
        Canvas.SetLeft(sun, width * 0.55);
        Canvas.SetTop(sun, height * 0.5);
        canvas.Children.Add(sun);

        box.Children.Add(canvas);
        return box;
    }

    private static Geometry FoldedCornerGeometry(double width, double height)
    {
        var cut = width * 0.5;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - cut, 0), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(width, height), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(0, height), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void UpdateHighlights()
    {
        foreach (var (index, item) in _itemBorders)
        {
            var selected = index == _currentSlide;
            if (item.Child is not Grid grid)
            {
                continue;
            }

            foreach (var child in grid.Children)
            {
                switch (child)
                {
                    case Border { Tag: "SelectionFrame" } frame:
                        frame.BorderBrush = selected ? UiTheme.AccentCoral : Brushes.Transparent;
                        frame.BorderThickness = new Thickness(selected ? 2 : 0);
                        frame.Margin = new Thickness(selected ? 1 : 0);
                        break;

                    case Grid { Tag: "Badge" } host:
                        if (host.Children[0] is Path badge)
                        {
                            badge.Fill = selected ? UiTheme.AccentCoral : UiTheme.White;
                            badge.Stroke = selected ? UiTheme.AccentCoral : UiTheme.BadgeBorder;
                            badge.StrokeThickness = selected ? 2 : 1;
                        }

                        if (host.Children.Count > 1 && host.Children[1] is TextBlock text)
                        {
                            text.Foreground = selected ? UiTheme.White : UiTheme.Text;
                        }

                        break;
                }
            }
        }
    }

    private static BitmapImage? TryLoadImage(string path, UiTierMetrics metrics)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();

            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = (int)Math.Max(32, metrics.ThumbnailWidth - ThumbnailDecodeMargin);
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // 文件被删/损坏都只是「这一页没缩略图」，不该影响面板。
            return null;
        }
    }
}
