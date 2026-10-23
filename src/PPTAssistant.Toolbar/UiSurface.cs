using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

internal static class UiSurface
{
    /// <summary>按档位取圆角（3/6/12）。</summary>
    public static CornerRadius Radius(UiTierMetrics metrics) => new(metrics.CornerRadius);

    public static Border Capsule(UIElement child, UiTierMetrics metrics) => new()
    {
        Background = UiTheme.Panel,
        BorderBrush = UiTheme.Border,
        BorderThickness = new Thickness(UiMetrics.PanelBorderThickness),
        CornerRadius = Radius(metrics),
        Child = child,
        SnapsToDevicePixels = true,
    };

    /// <summary>造一块带内边距的浮窗面板（色板/笔刷等独立窗口的容器）。</summary>
    public static Border Panel(UIElement child, UiTierMetrics metrics, double padding) => new()
    {
        Background = UiTheme.Panel,
        BorderBrush = UiTheme.Border,
        BorderThickness = new Thickness(UiMetrics.PanelBorderThickness),
        CornerRadius = Radius(metrics),
        Padding = new Thickness(padding),
        Child = child,
        SnapsToDevicePixels = true,
    };

    /// <summary>
    /// 把按钮按组排成一行：每个组一个胶囊，组间留档位空隙（2/4/8）。
    /// </summary>
    /// <param name="metrics">档位度量。</param>
    /// <param name="groups">每组里的按钮（组内按钮共享同一个胶囊）。</param>
    public static StackPanel GroupedRow(UiTierMetrics metrics, IReadOnlyList<IReadOnlyList<UIElement>> groups)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (var i = 0; i < groups.Count; i++)
        {
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var element in groups[i])
            {
                inner.Children.Add(element);
            }

            var capsule = Capsule(inner, metrics);
            if (i < groups.Count - 1)
            {
                capsule.Margin = new Thickness(0, 0, metrics.Gap, 0);
            }

            row.Children.Add(capsule);
        }

        return row;
    }

    /// <summary>档位变化时更新胶囊圆角（胶囊对象保持不重建，事件接线不受影响）。</summary>
    public static void ApplyMetrics(Border capsule, UiTierMetrics metrics)
        => capsule.CornerRadius = Radius(metrics);

    public static Border MessageBar(
        string text,
        UiTierMetrics metrics,
        double horizontalPadding = 30,
        double verticalPadding = 10,
        double fontSize = UiMetrics.StatusFontSize,
        Brush? foreground = null)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground ?? UiTheme.Text,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };

        return new Border
        {
            Background = UiTheme.MessageBackground,
            CornerRadius = Radius(metrics),
            Padding = new Thickness(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding),
            Child = label,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}
