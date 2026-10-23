using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

internal sealed class FloatingPanel
{
    /// <summary>面板与锚点（工具栏中央条）之间的间距（DIP）。</summary>
    public const double AnchorGap = 10;

    private readonly TextBlock _title;
    private readonly Button _close;
    private readonly StackPanel _root;
    private readonly Border _surface;

    /// <param name="closeTooltip">关闭按钮的悬停提示。</param>
    /// <param name="onClose">点关闭后要做什么（窗口自己决定「收起 + 抛事件」）。</param>
    public FloatingPanel(string closeTooltip, Action onClose)
    {
        _title = new TextBlock
        {
            Foreground = UiTheme.Text,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _close = UiCloseButton.Create(onClose, closeTooltip);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_close, Dock.Right);
        header.Children.Add(_close);
        header.Children.Add(_title);

        _root = new StackPanel { Orientation = Orientation.Vertical };
        _root.Children.Add(header);

        _surface = UiSurface.Panel(_root, UiMetrics.Standard, PaddingFor(UiMetrics.Standard));
    }

    /// <summary>面板根元素（交给 <c>Window.Content</c>）。</summary>
    public UIElement Root => _surface;

    /// <summary>标题文案。</summary>
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    /// <summary>关闭按钮（少数窗口如缩略图侧栏要单独接管）。</summary>
    public Button CloseButton => _close;

    /// <summary>加入主体内容（在标题栏下方）。</summary>
    public void SetBody(UIElement body)
    {
        while (_root.Children.Count > 1)
        {
            _root.Children.RemoveAt(1);
        }

        _root.Children.Add(body);
    }

    /// <summary>档位变化时更新圆角、内边距与关闭按钮尺寸（容器对象不变，事件接线全部有效）。</summary>
    public void ApplyMetrics(UiTierMetrics metrics)
    {
        _surface.CornerRadius = UiSurface.Radius(metrics);
        _surface.Padding = new Thickness(PaddingFor(metrics));
        _title.FontSize = metrics.IsCompact ? 13 : 15;
        UiCloseButton.Resize(_close, CloseSizeFor(metrics));
    }

    /// <summary>
    /// 把窗口摆在锚点（工具栏中央条）<b>正上方</b>；上方放不下改放下方；最后一律夹进屏内。
    /// </summary>
    /// <param name="window">要摆放的窗口（用其实际/期望尺寸）。</param>
    /// <param name="screen">目标屏。</param>
    /// <param name="anchor">锚点矩形（屏幕 DIP）。</param>
    public static void PlaceNear(Window window, ScreenInfo screen, DipRect anchor)
    {
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.DesiredSize.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.DesiredSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bounds = screen.Bounds;
        var left = anchor.CenterX - (width / 2);
        var top = anchor.Y - height - AnchorGap;
        if (top < bounds.Y)
        {
            top = Math.Min(anchor.Bottom + AnchorGap, bounds.Bottom - height);
        }

        window.Left = Math.Clamp(left, bounds.X, Math.Max(bounds.X, bounds.Right - width));
        window.Top = Math.Clamp(top, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - height));
    }

    private static double PaddingFor(UiTierMetrics metrics) => metrics.IsCompact ? 8 : 10;

    /// <summary>关闭按钮直径（跟着档位走，但面板本身不大，上限交给视觉判断）。</summary>
    private static double CloseSizeFor(UiTierMetrics metrics) => metrics.Tier switch
    {
        UiTier.Compact => 20,
        UiTier.Large => 26,
        _ => UiCloseButton.DefaultSize,
    };

    /// <summary>把一个普通 <see cref="TextBlock"/> 造成主题里的分组标题。</summary>
    public static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = UiTheme.TextMuted,
        FontSize = 12,
        Margin = new Thickness(0, 8, 0, 2),
    };

    public static TextBlock BodyText(string text, double fontSize = 13) => new()
    {
        Text = text,
        Foreground = UiTheme.Text,
        FontSize = fontSize,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>主题里的一句提示（浅灰）。</summary>
    public static TextBlock Hint(string text, double fontSize = 12) => new()
    {
        Text = text,
        Foreground = UiTheme.TextMuted,
        FontSize = fontSize,
        TextWrapping = TextWrapping.Wrap,
    };
}
