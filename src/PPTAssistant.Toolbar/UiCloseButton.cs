using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PPTAssistant.Toolbar;

internal static class UiCloseButton
{
    /// <summary>面板 Header 里的默认尺寸（DIP）。</summary>
    public const double DefaultSize = 22;

    /// <summary>造一个圆形 ✕ 按钮。</summary>
    /// <param name="onClick">点击动作。</param>
    /// <param name="tooltip">悬停提示。</param>
    /// <param name="size">直径（DIP）。</param>
    public static Button Create(Action onClick, string tooltip, double size = DefaultSize)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = tooltip,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildTemplate(size),
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>再造一个同款、不同尺寸的（档位变化时用；模板随尺寸重算笔画）。</summary>
    public static void Resize(Button button, double size)
    {
        button.Width = size;
        button.Height = size;
        button.Template = BuildTemplate(size);
    }

    private static ControlTemplate BuildTemplate(double size)
    {
        var disc = new FrameworkElementFactory(typeof(Ellipse), "Disc");
        disc.SetValue(Shape.FillProperty, UiTheme.CloseDisc);
        disc.SetValue(FrameworkElement.WidthProperty, size);
        disc.SetValue(FrameworkElement.HeightProperty, size);

        var glyph = new FrameworkElementFactory(typeof(Path), "Glyph");
        glyph.SetValue(Path.DataProperty, GlyphGeometry(size));
        glyph.SetValue(Shape.StrokeProperty, UiTheme.CloseGlyph);
        glyph.SetValue(Shape.StrokeThicknessProperty, Math.Max(1.1, size * 0.055));
        glyph.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        glyph.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        // ★ 对齐必须是 Stretch（默认），**不能写 Center**：几何坐标从 inset 起笔，
        //   而 Stretch=None 的 Path 在 Center 对齐时按「几何包围盒」参与布局 ⇒ 整条 ✕ 被
        //   推向右下 inset（22px 按钮上偏 ~7px），看起来就是「关闭键错位」。
        //   Stretch 让 Path 铺满整个 size×size 的格子，几何按自己的坐标画在正中间。
        glyph.SetValue(Shape.StretchProperty, Stretch.None);

        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.WidthProperty, size);
        grid.SetValue(FrameworkElement.HeightProperty, size);
        grid.AppendChild(disc);
        grid.AppendChild(glyph);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = grid };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Shape.FillProperty, UiTheme.CloseDiscHover) { TargetName = "Disc" });
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Shape.FillProperty, UiTheme.CloseDiscPressed) { TargetName = "Disc" });
        pressed.Setters.Add(new Setter(Shape.StrokeProperty, UiTheme.CloseGlyphPressed) { TargetName = "Glyph" });
        template.Triggers.Add(pressed);

        return template;
    }

    /// <summary>✕ 的两道斜线（起止点按直径内缩 32%，与 44px 原图的比例接近）。</summary>
    private static Geometry GlyphGeometry(double size)
    {
        var inset = size * 0.32;
        var far = size - inset;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            AddLine(context, inset, inset, far, far);
            AddLine(context, far, inset, inset, far);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void AddLine(StreamGeometryContext context, double x1, double y1, double x2, double y2)
    {
        context.BeginFigure(Point(x1, y1), isFilled: false, isClosed: false);
        context.LineTo(Point(x2, y2), isStroked: true, isSmoothJoin: false);
    }

    private static Point Point(double x, double y) => new(x, y);
}
