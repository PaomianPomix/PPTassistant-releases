using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

internal static class ToolButton
{
    /// <summary>「已开启 / 当前模式」的激活标记（附加属性，供模板触发器读取）。</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive",
            typeof(bool),
            typeof(ToolButton),
            new FrameworkPropertyMetadata(false));

    /// <summary>常态图标层的内容（模板里是 <c>PART_NormalIcon</c>）。</summary>
    public static readonly DependencyProperty NormalIconProperty =
        DependencyProperty.RegisterAttached(
            "NormalIcon",
            typeof(UIElement),
            typeof(ToolButton),
            new FrameworkPropertyMetadata(null));

    /// <summary>按压/选中图标层的内容（模板里是 <c>PART_SelectedIcon</c>，常态 Opacity=0）。</summary>
    public static readonly DependencyProperty SelectedIconProperty =
        DependencyProperty.RegisterAttached(
            "SelectedIcon",
            typeof(UIElement),
            typeof(ToolButton),
            new FrameworkPropertyMetadata(null));

    /// <summary>非图标内容（角区中间按钮的页码文本）。</summary>
    public static readonly DependencyProperty FaceContentProperty =
        DependencyProperty.RegisterAttached(
            "FaceContent",
            typeof(object),
            typeof(ToolButton),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(ToolButton),
            new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>
    /// 造一个图标按钮。
    /// </summary>
    /// <param name="icon">图标。</param>
    /// <param name="tooltip">悬停提示。</param>
    /// <param name="onClick">点击动作。</param>
    /// <param name="metrics">当前档位的度量（按钮边长、圆角、图标内边距）。</param>
    /// <param name="standalone">
    /// true = 按钮自己就是一整颗磨砂胶囊（角区、退出）；false = 与相邻按钮共用一个胶囊
    /// （中央条的分组，背景/描边由容器 <see cref="UiPanel"/> 提供）。
    /// </param>
    public static Button Create(
        ToolIconKind icon,
        string tooltip,
        Action onClick,
        UiTierMetrics metrics,
        bool standalone = true)
    {
        var button = CreateBase(tooltip, onClick, metrics, standalone);
        button.SetValue(NormalIconProperty, ScaledIcon(ToolIcons.Create(icon, ToolIconState.Normal), metrics.IconScale));
        button.SetValue(SelectedIconProperty, ScaledIcon(ToolIcons.Create(icon, ToolIconState.Selected), metrics.IconScale));
        return button;
    }

    public static Button CreateWithFace(
        UIElement face,
        string tooltip,
        Action onClick,
        double width,
        UiTierMetrics metrics,
        bool standalone = true)
    {
        var button = CreateBase(tooltip, onClick, metrics, standalone);
        button.Width = width;
        button.SetValue(FaceContentProperty, face);
        return button;
    }

    /// <summary>把按钮标成激活 / 取消激活（模板触发器据此换图标与淡绿底，鼠标悬停也不会盖掉）。</summary>
    public static void MarkActive(Button button, bool active) => SetIsActive(button, active);

    /// <summary>
    /// 档位变化时重建按钮的外观（尺寸、圆角、图标缩放）——按钮对象本身不变，
    /// 因此事件接线、激活状态都不用重新建立。
    /// </summary>
    public static void ApplyMetrics(Button button, ToolIconKind icon, UiTierMetrics metrics)
    {
        button.Width = metrics.ButtonSize;
        button.Height = metrics.ButtonSize;
        SetCornerRadius(button, new CornerRadius(metrics.CornerRadius));
        button.SetValue(NormalIconProperty, ScaledIcon(ToolIcons.Create(icon, ToolIconState.Normal), metrics.IconScale));
        button.SetValue(SelectedIconProperty, ScaledIcon(ToolIcons.Create(icon, ToolIconState.Selected), metrics.IconScale));
    }

    private static Button CreateBase(string tooltip, Action onClick, UiTierMetrics metrics, bool standalone)
    {
        var button = new Button
        {
            Width = metrics.ButtonSize,
            Height = metrics.ButtonSize,
            Margin = new Thickness(0),
            Focusable = false,
            ToolTip = tooltip,
            Template = ButtonTemplate,
            // 胶囊内部（同一组）的按钮自己不画底/边——底色与描边由容器那一圈给，组内才不会出现「双线」。
            Background = standalone ? UiTheme.Panel : Brushes.Transparent,
            BorderBrush = standalone ? UiTheme.Border : Brushes.Transparent,
            BorderThickness = standalone
                ? new Thickness(UiMetrics.PanelBorderThickness)
                : new Thickness(0),
            SnapsToDevicePixels = true,
        };

        SetCornerRadius(button, new CornerRadius(metrics.CornerRadius));
        button.Click += (_, _) => onClick();
        return button;
    }

    private static UIElement ScaledIcon(UIElement icon, double scale)
    {
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            icon.RenderTransform = new ScaleTransform(scale, scale, ToolIcons.Box / 2, ToolIcons.Box / 2);
        }

        return icon;
    }

    private static readonly ControlTemplate ButtonTemplate = BuildTemplate();

    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(CornerRadiusProperty));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(IconPresenter("PART_NormalIcon", NormalIconProperty, opacity: 1));
        grid.AppendChild(IconPresenter("PART_SelectedIcon", SelectedIconProperty, opacity: 0));

        var face = new FrameworkElementFactory(typeof(ContentPresenter));
        face.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(FaceContentProperty));
        face.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        face.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(face);

        border.AppendChild(grid);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        AddBackgroundTrigger(template, UIElement.IsMouseOverProperty, true, UiTheme.HoverTint);
        AddBackgroundTrigger(template, Button.IsPressedProperty, true, UiTheme.PressedTint);
        AddActiveTrigger(template);
        AddPressFadeTrigger(template);
        AddDisabledTrigger(template, 0.35);

        return template;
    }

    private static FrameworkElementFactory IconPresenter(string name, DependencyProperty content, double opacity)
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), name);
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(content));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(UIElement.OpacityProperty, opacity);
        presenter.SetValue(UIElement.IsHitTestVisibleProperty, false);
        return presenter;
    }

    private static void AddBackgroundTrigger(ControlTemplate template, DependencyProperty property, object value, Brush background)
    {
        var trigger = new Trigger { Property = property, Value = value };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, background) { TargetName = "Bd" });
        template.Triggers.Add(trigger);
    }

    private static void AddActiveTrigger(ControlTemplate template)
    {
        var trigger = new Trigger { Property = IsActiveProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, UiTheme.ActiveTint) { TargetName = "Bd" });
        trigger.Setters.Add(new Setter(Border.BorderBrushProperty, UiTheme.Accent) { TargetName = "Bd" });
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0d) { TargetName = "PART_NormalIcon" });
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1d) { TargetName = "PART_SelectedIcon" });
        template.Triggers.Add(trigger);
    }

    private static void AddPressFadeTrigger(ControlTemplate template)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(Fade("PART_NormalIcon", 1, 0));
        storyboard.Children.Add(Fade("PART_SelectedIcon", 0, 1));

        var trigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0d) { TargetName = "PART_NormalIcon" });
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1d) { TargetName = "PART_SelectedIcon" });
        trigger.EnterActions.Add(new BeginStoryboard { Storyboard = storyboard });
        template.Triggers.Add(trigger);
    }

    private static DoubleAnimationUsingKeyFrames Fade(string target, double from, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(PressFadeMilliseconds))));

        // ★ FillBehavior.Stop：动画播完就把属性还给 Setter（终态），不保留最后一帧。
        //   默认的 HoldEnd 正是缺陷的成因。
        animation.FillBehavior = FillBehavior.Stop;
        Storyboard.SetTargetName(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        return animation;
    }

    private const int PressFadeMilliseconds = 100;

    private static void AddDisabledTrigger(ControlTemplate template, double opacity)
    {
        var trigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, opacity) { TargetName = "Bd" });
        template.Triggers.Add(trigger);
    }
}
