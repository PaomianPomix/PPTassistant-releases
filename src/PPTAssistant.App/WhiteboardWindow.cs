using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using PPTAssistant.Coords;

namespace PPTAssistant.App;

/// <summary>独立于课件的全屏白板；窗口可反复隐藏，文档和保存由 AppHost 管理。</summary>
internal sealed class WhiteboardWindow : Window
{
    private static readonly Color BlackInk = Color.FromRgb(0x21, 0x25, 0x29);
    private readonly WhiteboardDocument _document;
    private readonly InkCanvas _canvas;
    private readonly Canvas _viewport;
    private readonly FrameworkElement _dock;
    private readonly TextBlock _pageNumber = Label(string.Empty, 14, true);
    private readonly TextBlock _saveStatus = Label("更改自动保存到本机", 12);
    private readonly TextBlock _hint = Label("独立画布 · 灵感随手写", 12);
    private readonly Dictionary<Color, Button> _colors = [];
    private readonly Dictionary<double, Button> _widths = [];
    private readonly Button _pen;
    private readonly Button _eraser;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _clear;
    private readonly Button _whiteBackground;
    private readonly Button _blackBackground;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _add;
    private readonly Button _delete;
    private readonly Button _return;
    private Color _penColor = BlackInk;
    private double _penWidth = 6;
    private bool _isErasing;
    private bool? _lastDarkBackground;
    private bool _returning;
    private bool _disposed;
    private bool _updatingViewport;
    private double _toolbarScale = 1;
    private double _canvasScale = 1;
    private double _requestedUiScale = 1;
    private ScreenInfo? _screen;

    internal WhiteboardWindow(WhiteboardDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        Title = "PPTAssistant 白板";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.White;
        Foreground = SettingsUi.Text;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Resources = SettingsUi.CreateResources();
        Identify(this, "WhiteboardWindow", "PPTAssistant 白板");

        _canvas = new InkCanvas
        {
            Width = document.Width,
            Height = document.Height,
            Background = Brushes.White,
            ClipToBounds = true,
            EditingMode = InkCanvasEditingMode.Ink,
            EditingModeInverted = InkCanvasEditingMode.EraseByStroke,
            EraserShape = new EllipseStylusShape(32, 32),
            Focusable = true,
        };
        Identify(_canvas, "WhiteboardCanvas", "白板书写区域");
        // 保留 WPF 原生墨迹管线，使鼠标、触摸和压感笔共用相同坐标与压力数据。
        Stylus.SetIsPressAndHoldEnabled(_canvas, false);
        Stylus.SetIsFlicksEnabled(_canvas, false);
        Stylus.SetIsTapFeedbackEnabled(_canvas, false);
        Stylus.SetIsTouchFeedbackEnabled(_canvas, false);

        _pen = ActionButton("画笔", "WhiteboardPen", "画笔（P）", () => SetTool(false), PencilIcon);
        _eraser = ActionButton("橡皮", "WhiteboardEraser", "橡皮：擦除整条笔迹（E）", () => SetTool(true), EraserIcon);
        _undo = ActionButton("撤销", "WhiteboardUndo", "撤销（Ctrl+Z）", document.Undo, UndoIcon);
        _redo = ActionButton("重做", "WhiteboardRedo", "重做（Ctrl+Y / Ctrl+Shift+Z）", document.Redo, RedoIcon);
        _clear = ActionButton("清空", "WhiteboardClear", "清空本页，可撤销恢复", document.ClearCurrentPage, ClearIcon);
        _whiteBackground = ActionButton("白底", "WhiteboardWhiteBackground", "切换白色画布", () => document.SetDarkBackground(false));
        _blackBackground = ActionButton("黑底", "WhiteboardBlackBackground", "切换黑色画布", () => document.SetDarkBackground(true));
        _previous = ActionButton("上一页", "WhiteboardPrevious", "上一页", () => document.GoToPage(document.CurrentPageIndex - 1), PreviousIcon);
        _next = ActionButton("下一页", "WhiteboardNext", "下一页", () => document.GoToPage(document.CurrentPageIndex + 1), NextIcon);
        _add = ActionButton("新增一页", "WhiteboardAdd", $"新增一页（最多 {WhiteboardDocument.MaxPages} 页）", AddPage, PlusIcon);
        _delete = ActionButton("删除本页", "WhiteboardDelete", "删除本页，至少保留一页", DeletePage, DeleteIcon);
        _return = ActionButton("返回", "WhiteboardReturn", "返回（Esc）", RequestReturn, PreviousIcon);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Canvas 父容器不按子项逻辑尺寸参与测量，书写区域可以完整铺满剩余屏幕。
        _viewport = new Canvas { Background = Brushes.White, ClipToBounds = true };
        Identify(_viewport, "WhiteboardViewport", "白板可写区域");
        _viewport.Children.Add(_canvas);
        _viewport.SizeChanged += (_, _) => UpdateViewport();
        root.Children.Add(_viewport);
        _dock = BuildDock();
        Identify(_dock, "WhiteboardDock", "白板底部控制区");
        Grid.SetRow(_dock, 1);
        root.Children.Add(_dock);
        Content = root;
        root.SizeChanged += (_, _) => UpdateToolbarScale();

        _document.Changed += OnDocumentChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => DetachDocument();
        RefreshDocument();
        ApplyDrawingAttributes();
    }

    internal event EventHandler? ReturnRequested;

    internal void ShowOn(ScreenInfo screen, bool returnToPresentation, double uiScale = 1)
    {
        CompleteActiveStroke();
        _screen = screen;
        _requestedUiScale = uiScale;
        _returning = false;
        _return.Content = ButtonContent(returnToPresentation ? "返回放映" : "返回桌面", PreviousIcon);
        AutomationProperties.SetName(_return, returnToPresentation ? "返回放映" : "返回桌面");
        var bounds = screen.Bounds;
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowState = WindowState.Normal;
        UpdateToolbarScale(bounds.Width, bounds.Height);
        if (!IsVisible) Show();
        // 混合 DPI 的屏幕绝对坐标必须来自物理矩形，不按主屏比例换算。
        var physical = screen.PhysicalBounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            _ = SetWindowPos(hwnd, new IntPtr(-1), (int)Math.Round(physical.X), (int)Math.Round(physical.Y),
                (int)Math.Round(physical.Width), (int)Math.Round(physical.Height), 0x0040);
        UpdateLayout();
        UpdateViewport();
        Activate();
        _canvas.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            Keyboard.Focus(_canvas);
        }));
    }

    internal void ShowSaveStatus(string? error)
    {
        _saveStatus.Text = error is null ? "已自动保存到本机" : "自动保存未完成 · 请导出备份";
        _saveStatus.Foreground = error is null ? SettingsUi.Muted : SettingsUi.Error;
        _saveStatus.ToolTip = error is null ? "下次打开白板时继续书写" : error;
        AutomationProperties.SetName(_saveStatus, _saveStatus.Text);
    }

    internal int ExportPixelWidth => ExportPixels(_viewport.ActualWidth, horizontal: true);
    internal int ExportPixelHeight => ExportPixels(_viewport.ActualHeight, horizontal: false);

    /// <summary>导出当前完整可写区域，按目标屏幕物理像素渲染，不包含底部控制栏。</summary>
    internal void ExportCurrentPage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CompleteActiveStroke();
        UpdateLayout();
        UpdateViewport();
        var page = _document.CurrentPage;
        var pixelWidth = ExportPixelWidth;
        var pixelHeight = ExportPixelHeight;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, pixelWidth, pixelHeight);
            drawing.PushClip(new RectangleGeometry(rect));
            drawing.DrawRectangle(page.IsDark ? Brushes.Black : Brushes.White, null, rect);
            drawing.PushTransform(new ScaleTransform(pixelWidth / _canvas.Width, pixelHeight / _canvas.Height));
            page.Strokes.Draw(drawing);
            drawing.Pop();
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        // 编码和写入都完成后才替换，磁盘写满或文件被占用时保留已有图片。
        var destination = System.IO.Path.GetFullPath(path);
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!,
            $".{System.IO.Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, output.ToArray());
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>宿主退出时释放事件并销毁窗口；日常返回应使用 Hide。</summary>
    internal void ClosePermanently()
    {
        DetachDocument();
        Closing -= OnClosing;
        Close();
    }

    private FrameworkElement BuildDock()
    {
        var rows = new StackPanel();
        rows.Children.Add(BuildToolRows());
        rows.Children.Add(BuildPageRow());
        return new Border
        {
            BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Background = SettingsUi.Subtle, Child = rows,
        };
    }

    private FrameworkElement BuildToolRows()
    {
        var header = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };
        var heading = new Grid { Height = 20, Margin = new Thickness(4, 0, 4, 2) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label("白板", 14, true);
        heading.Children.Add(title);
        _hint.Foreground = SettingsUi.Muted;
        _hint.Margin = new Thickness(16, 0, 16, 0);
        _hint.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_hint, 1);
        heading.Children.Add(_hint);
        _saveStatus.Foreground = SettingsUi.Muted;
        _saveStatus.MaxWidth = 220;
        _saveStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        Identify(_saveStatus, "WhiteboardSaveStatus", _saveStatus.Text);
        Grid.SetColumn(_saveStatus, 2);
        heading.Children.Add(_saveStatus);
        header.Children.Add(heading);

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        Identify(tools, "WhiteboardTools", "白板工具栏");
        tools.Children.Add(Group(_pen, _eraser));
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, titleText, color) in new[]
        {
            ("Black", "黑色", BlackInk), ("White", "白色", Colors.White),
            ("Red", "红色", Color.FromRgb(0xD9, 0x3F, 0x4B)), ("Blue", "蓝色", Color.FromRgb(0x28, 0x64, 0xD7)),
            ("Green", "绿色", Color.FromRgb(0x26, 0x8B, 0x65)), ("Yellow", "黄色", Color.FromRgb(0xEF, 0xBC, 0x37)),
        })
        {
            var button = ActionButton(titleText, $"WhiteboardColor{name}", $"{titleText}画笔", () =>
            {
                _penColor = color;
                SetTool(false);
            });
            button.Width = 44;
            button.Padding = new Thickness(10);
            button.Content = new Ellipse
            {
                Width = 19, Height = 19,
                Fill = new SolidColorBrush(color),
                Stroke = SettingsUi.Border, StrokeThickness = 1,
            };
            _colors.Add(color, button);
            colors.Children.Add(button);
        }
        tools.Children.Add(Group(colors));
        var widths = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, titleText, width) in new[] { ("Fine", "细", 3d), ("Medium", "中", 6d), ("Thick", "粗", 12d) })
        {
            var button = ActionButton(titleText, $"WhiteboardWidth{name}", $"{titleText}笔：{width:0} 点", () =>
            {
                _penWidth = width;
                SetTool(false);
            });
            button.MinWidth = 44;
            button.Padding = new Thickness(9, 8, 9, 8);
            _widths.Add(width, button);
            widths.Children.Add(button);
        }
        tools.Children.Add(Group(widths));
        tools.Children.Add(Group(_undo, _redo, _clear));
        tools.Children.Add(Group(_whiteBackground, _blackBackground));
        tools.Children.Add(Group(ActionButton("导出 PNG", "WhiteboardExport", "导出当前页图片（Ctrl+S）", ExportWithDialog, ExportIcon)));
        header.Children.Add(HorizontalScroll(tools, "WhiteboardToolsScroll", "左右滑动可查看全部书写工具"));
        return header;
    }

    private FrameworkElement BuildPageRow()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var paging = new StackPanel { Orientation = Orientation.Horizontal };
        _return.Background = SettingsUi.Selected;
        _return.BorderBrush = SettingsUi.Border;
        _return.Margin = new Thickness(2, 2, 12, 2);
        row.Children.Add(_return);
        paging.Children.Add(_previous);
        _pageNumber.MinWidth = 106;
        _pageNumber.Margin = new Thickness(10, 0, 10, 0);
        _pageNumber.TextAlignment = TextAlignment.Center;
        Identify(_pageNumber, "WhiteboardPageNumber", "白板页码");
        paging.Children.Add(_pageNumber);
        paging.Children.Add(_next);
        paging.Children.Add(new Border { Width = 16 });
        paging.Children.Add(_add);
        paging.Children.Add(_delete);
        var scroll = HorizontalScroll(paging, "WhiteboardPagesScroll", "左右滑动可查看全部分页操作");
        Grid.SetColumn(scroll, 1);
        row.Children.Add(scroll);
        return new Border
        {
            Margin = new Thickness(12, 0, 12, 4),
            Child = row,
        };
    }

    private static ScrollViewer HorizontalScroll(FrameworkElement content, string id, string name)
    {
        var scroll = new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false,
            PanningMode = PanningMode.HorizontalOnly, IsTabStop = false,
        };
        Identify(scroll, id, name);
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableWidth <= 0 || Keyboard.Modifiers != ModifierKeys.None) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        return scroll;
    }

    private void SetTool(bool erasing)
    {
        _isErasing = erasing;
        _canvas.EditingMode = erasing ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
        ApplyDrawingAttributes();
        RefreshSelections();
    }

    private void UpdateToolbarScale(double? availableWidth = null, double? availableHeight = null)
    {
        var root = Content as FrameworkElement;
        var width = availableWidth ?? root?.ActualWidth ?? ActualWidth;
        var height = availableHeight ?? root?.ActualHeight ?? ActualHeight;
        if (width <= 0 || height <= 0) return;
        var physicalWidth = _screen?.PhysicalBounds.Width ?? width;
        var scale = WhiteboardLayout.ToolbarScale(width, height, physicalWidth, _requestedUiScale);
        // 滚动条只在溢出时出现。用真实内容高度再约束一次，避免小屏被控制区挤满。
        if (_dock is Border { Child: FrameworkElement rows })
        {
            rows.Measure(new Size(width / scale, double.PositiveInfinity));
            var dockHeight = rows.DesiredSize.Height + 1;
            // 为布局取整留 2 DIP 余量，使 640×480 等小屏也保留至少 180 DIP 画布。
            var availableDockHeight = height - Math.Min(WhiteboardLayout.MinimumCanvasHeight + 2, height * 0.55);
            if (dockHeight > 0) scale = Math.Max(1, Math.Min(scale, availableDockHeight / dockHeight));
        }
        if (Math.Abs(scale - _toolbarScale) < 0.001) return;
        _toolbarScale = scale;
        _dock.LayoutTransform = new ScaleTransform(scale, scale);
        ApplyDrawingAttributes();
    }

    private void UpdateViewport()
    {
        if (_updatingViewport || _viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0) return;
        _updatingViewport = true;
        try
        {
            var page = _document.CurrentPage;
            if (!page.IsViewportInitialized)
                page.InitializeViewport(_viewport.ActualWidth, _viewport.ActualHeight);
            var fit = WhiteboardLayout.Fit(page.Width, page.Height, _viewport.ActualWidth, _viewport.ActualHeight);
            _canvasScale = fit.Scale;
            _canvas.Width = fit.LogicalWidth;
            _canvas.Height = fit.LogicalHeight;
            _canvas.RenderTransform = new ScaleTransform(fit.Scale, fit.Scale);
            ApplyDrawingAttributes();
        }
        finally
        {
            _updatingViewport = false;
        }
    }

    private int ExportPixels(double viewportDips, bool horizontal)
    {
        if (viewportDips <= 0) return Math.Max(1, (int)Math.Round(horizontal ? _canvas.Width : _canvas.Height));
        var content = _viewport.Parent as FrameworkElement;
        var fullDips = horizontal ? content?.ActualWidth ?? ActualWidth : content?.ActualHeight ?? ActualHeight;
        var physical = horizontal ? _screen?.PhysicalBounds.Width : _screen?.PhysicalBounds.Height;
        var pixelScale = physical.HasValue && fullDips > 0 ? physical.Value / fullDips : 1;
        return Math.Max(1, (int)Math.Round(viewportDips * pixelScale));
    }

    private void ApplyDrawingAttributes()
    {
        var logicalWidth = Math.Clamp(_penWidth * _toolbarScale / _canvasScale, 0.1, 1000);
        _canvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor, Width = logicalWidth, Height = logicalWidth,
            FitToCurve = true, IgnorePressure = false, StylusTip = StylusTip.Ellipse,
        };
        var eraserWidth = Math.Clamp(32 * _toolbarScale / _canvasScale, 1, 1000);
        _canvas.EraserShape = new EllipseStylusShape(eraserWidth, eraserWidth);
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => RefreshDocument();

    private void RefreshDocument()
    {
        var page = _document.CurrentPage;
        if (!ReferenceEquals(_canvas.Strokes, page.Strokes)) _canvas.Strokes = page.Strokes;
        _canvas.Background = page.IsDark ? Brushes.Black : Brushes.White;
        _viewport.Background = _canvas.Background;
        if (_lastDarkBackground != page.IsDark)
        {
            if (page.IsDark && _penColor == BlackInk) _penColor = Colors.White;
            else if (!page.IsDark && _penColor == Colors.White) _penColor = BlackInk;
            _lastDarkBackground = page.IsDark;
            ApplyDrawingAttributes();
        }
        _pageNumber.Text = $"第 {_document.CurrentPageIndex + 1} / {_document.Pages.Count} 页";
        AutomationProperties.SetName(_pageNumber, _pageNumber.Text);
        _previous.IsEnabled = _document.CurrentPageIndex > 0;
        _next.IsEnabled = _document.CurrentPageIndex < _document.Pages.Count - 1;
        _add.IsEnabled = _document.Pages.Count < WhiteboardDocument.MaxPages;
        _delete.IsEnabled = _document.Pages.Count > 1;
        _undo.IsEnabled = _document.CanUndo;
        _redo.IsEnabled = _document.CanRedo;
        _clear.IsEnabled = page.Strokes.Count > 0;
        UpdateViewport();
        RefreshSelections();
    }

    private void RefreshSelections()
    {
        Select(_pen, !_isErasing);
        Select(_eraser, _isErasing);
        Select(_whiteBackground, !_document.CurrentPage.IsDark);
        Select(_blackBackground, _document.CurrentPage.IsDark);
        foreach (var (color, button) in _colors) Select(button, color == _penColor);
        foreach (var (width, button) in _widths) Select(button, width == _penWidth);
    }

    private static void Select(Button button, bool selected)
    {
        button.Background = selected ? SettingsUi.Selected : Brushes.Transparent;
        button.BorderBrush = selected ? SettingsUi.Border : Brushes.Transparent;
        AutomationProperties.SetItemStatus(button, selected ? "已选择" : string.Empty);
    }

    private void AddPage()
    {
        if (!_document.AddPage())
            _hint.Text = $"已达 {WhiteboardDocument.MaxPages} 页，可删除不再需要的页面后继续";
    }

    private void DeletePage()
    {
        if (_document.Pages.Count <= 1) return;
        var pageNumber = _document.CurrentPageIndex + 1;
        if (MessageBox.Show(this, $"删除第 {pageNumber} 页及其全部笔迹？此操作无法撤销。", "删除白板页",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _document.DeleteCurrentPage();
    }

    private void ExportWithDialog()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出白板当前页", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png",
            AddExtension = true, OverwritePrompt = true,
            FileName = $"白板-{DateTime.Now:yyyyMMdd-HHmmss}-第{_document.CurrentPageIndex + 1}页.png",
        };
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            ExportCurrentPage(dialog.FileName);
            _hint.Text = $"已导出：{System.IO.Path.GetFileName(dialog.FileName)}";
            _hint.ToolTip = dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"无法导出图片，请更换保存位置后重试。\n\n{ex.Message}", "白板导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape) { RequestReturn(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control)
        {
            if (e.Key is not (Key.Z or Key.Y or Key.S)) return;
            CompleteActiveStroke();
            if (e.Key == Key.Z) _document.Undo();
            else if (e.Key == Key.Y) _document.Redo();
            else if (e.Key == Key.S) ExportWithDialog();
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
        {
            CompleteActiveStroke(); _document.Redo(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.None && (e.Key == Key.P || e.Key == Key.E))
        {
            CompleteActiveStroke(); SetTool(e.Key == Key.E); e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        RequestReturn();
    }

    private void RequestReturn()
    {
        if (_returning) return;
        CompleteActiveStroke();
        _returning = true;
        if (ReturnRequested is { } handler) handler(this, EventArgs.Empty);
        else Hide();
    }

    internal void CompleteActiveStroke()
    {
        if (!_canvas.IsMouseCaptured && !_canvas.IsStylusCaptured && !_canvas.AreAnyTouchesCaptured) return;
        // 原生 InkCanvas 在 LostDeviceCapture 中 Commit(true)，先收尾再换页/保存/隐藏。
        // 触控屏上可以一只手仍在书写、另一只手点工具栏，因此不能只等 PointerUp。
        // 不先切换 EditingMode：WPF 在笔画中途切到 None 会 Commit(false)，丢弃末笔。
        _canvas.ReleaseStylusCapture();
        _canvas.ReleaseMouseCapture();
        _canvas.ReleaseAllTouchCaptures();
    }

    private void DetachDocument()
    {
        if (_disposed) return;
        _disposed = true;
        _document.Changed -= OnDocumentChanged;
    }

    private Button ActionButton(string title, string id, string tooltip, Action action, string? icon = null)
    {
        var button = new Button
        {
            Content = ButtonContent(title, icon), ToolTip = tooltip,
            Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44, MinHeight = 44, Padding = new Thickness(12, 9, 12, 9),
        };
        Identify(button, id, title);
        button.Click += (_, _) => { CompleteActiveStroke(); action(); };
        return button;
    }

    private static FrameworkElement ButtonContent(string title, string? icon)
    {
        if (icon is null) return Label(title, 13);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(icon), Stroke = SettingsUi.Text, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform,
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0),
        });
        content.Children.Add(Label(title, 13));
        return content;
    }

    private static Border Group(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return new Border
        {
            Child = panel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 0, 8, 0),
            BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    private static TextBlock Label(string text, double fontSize, bool bold = false) => new()
    {
        Text = text, FontSize = fontSize, Foreground = SettingsUi.Text,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void Identify(DependencyObject target, string id, string name)
    {
        AutomationProperties.SetAutomationId(target, id);
        AutomationProperties.SetName(target, name);
    }

    private const string PencilIcon = "M3,13 L13,3 17,7 7,17 2,18 Z M11,5 L15,9 M3,13 L7,17";
    private const string EraserIcon = "M2,12 L11,3 18,10 10,18 7,18 Z M6,8 L13,15 M9,18 L18,18";
    private const string UndoIcon = "M7,4 L2,9 7,14 M2,9 L12,9 C19,9 19,18 12,18";
    private const string RedoIcon = "M13,4 L18,9 13,14 M18,9 L8,9 C1,9 1,18 8,18";
    private const string ClearIcon = "M3,3 L17,3 17,17 3,17 Z M7,7 L13,13 M13,7 L7,13";
    private const string PreviousIcon = "M13,3 L6,10 13,17";
    private const string NextIcon = "M7,3 L14,10 7,17";
    private const string PlusIcon = "M10,3 L10,17 M3,10 L17,10";
    private const string DeleteIcon = "M3,5 L17,5 M7,5 L7,2 13,2 13,5 M5,5 L6,18 14,18 15,5 M8,9 L8,14 M12,9 L12,14";
    private const string ExportIcon = "M10,2 L10,13 M6,9 L10,13 14,9 M3,12 L3,18 17,18 17,12";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
