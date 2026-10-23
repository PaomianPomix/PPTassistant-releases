using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PPTAssistant.Toolbar;

public static class UiToggleStyles
{
    /// <summary>标记方框/圆点的边长（DIP）。</summary>
    private const double BoxSize = 16;

    /// <summary>复选标记（对勾）的几何：画在 16 格里。</summary>
    private static readonly Geometry CheckGeometry = BuildCheck();

    private static readonly ControlTemplate CheckBoxControlTemplate = BuildCheckBox();
    private static readonly ControlTemplate RadioButtonControlTemplate = BuildRadioButton();

    /// <summary>取复选按钮模板（同一个模板实例可被多个控件共用）。</summary>
    public static ControlTemplate CheckBoxTemplate() => CheckBoxControlTemplate;

    /// <summary>取单选按钮模板。</summary>
    public static ControlTemplate RadioButtonTemplate() => RadioButtonControlTemplate;

    private static ControlTemplate BuildCheckBox()
    {
        var box = new FrameworkElementFactory(typeof(Border), "Box");
        box.SetValue(FrameworkElement.WidthProperty, BoxSize);
        box.SetValue(FrameworkElement.HeightProperty, BoxSize);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetValue(Border.BackgroundProperty, UiTheme.White);
        box.SetValue(Border.BorderBrushProperty, UiTheme.Border);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));

        var check = new FrameworkElementFactory(typeof(Path), "Check");
        check.SetValue(Path.DataProperty, CheckGeometry);
        check.SetValue(Shape.StrokeProperty, UiTheme.White);
        check.SetValue(Shape.StrokeThicknessProperty, 1.8);
        check.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        check.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.OpacityProperty, 0d);

        var glyph = new FrameworkElementFactory(typeof(Grid));
        glyph.AppendChild(box);
        glyph.AppendChild(check);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));

        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        layout.AppendChild(glyph);
        layout.AppendChild(content);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = layout };

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, UiTheme.Accent) { TargetName = "Box" });
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, UiTheme.Accent) { TargetName = "Box" });
        checkedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1d) { TargetName = "Check" });
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, UiTheme.Accent) { TargetName = "Box" });
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    private static ControlTemplate BuildRadioButton()
    {
        var ring = new FrameworkElementFactory(typeof(Ellipse), "Ring");
        ring.SetValue(FrameworkElement.WidthProperty, BoxSize);
        ring.SetValue(FrameworkElement.HeightProperty, BoxSize);
        ring.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        ring.SetValue(Shape.FillProperty, UiTheme.White);
        ring.SetValue(Shape.StrokeProperty, UiTheme.Border);
        ring.SetValue(Shape.StrokeThicknessProperty, 1d);

        var dot = new FrameworkElementFactory(typeof(Ellipse), "Dot");
        dot.SetValue(FrameworkElement.WidthProperty, BoxSize - 8);
        dot.SetValue(FrameworkElement.HeightProperty, BoxSize - 8);
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetValue(Shape.FillProperty, UiTheme.Accent);
        dot.SetValue(UIElement.OpacityProperty, 0d);

        var glyph = new FrameworkElementFactory(typeof(Grid));
        glyph.AppendChild(ring);
        glyph.AppendChild(dot);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));

        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        layout.AppendChild(glyph);
        layout.AppendChild(content);

        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = layout };

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Shape.StrokeProperty, UiTheme.Accent) { TargetName = "Ring" });
        checkedTrigger.Setters.Add(new Setter(Shape.StrokeThicknessProperty, 2d) { TargetName = "Ring" });
        checkedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1d) { TargetName = "Dot" });
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Shape.StrokeProperty, UiTheme.Accent) { TargetName = "Ring" });
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    /// <summary>16 格里的对勾（口径与 <c>ToolIcons</c> 的线型语言一致）。</summary>
    private static Geometry BuildCheck()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(4, 8.4), isFilled: false, isClosed: false);
            context.LineTo(new Point(6.8, 11.2), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(12.2, 5), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }
}
