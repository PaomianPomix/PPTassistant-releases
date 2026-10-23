using System.Windows;
using System.Windows.Media;
using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

/// <summary>在已有响应式档位之上统一缩放内容，并让完整面板保持在目标屏内。</summary>
internal static class UiScaling
{
    /// <summary>重新测量未缩放内容，避免反复打开时把上一次缩放再乘一次。</summary>
    public static double FitToScreen(FrameworkElement root, ScreenInfo screen, double requested, double margin = 8)
    {
        root.LayoutTransform = Transform.Identity;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = UiScalePolicy.Fit(
            requested,
            Math.Max(1, screen.Bounds.Width - (2 * margin)),
            Math.Max(1, screen.Bounds.Height - (2 * margin)),
            root.DesiredSize.Width,
            root.DesiredSize.Height);
        Apply(root, scale);
        return scale;
    }

    /// <summary>只缩放内容树一次：文字、图标、内外边距和命中区域同步变化。</summary>
    public static void Apply(FrameworkElement root, double scale)
    {
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        root.LayoutTransform = transform;
    }
}
