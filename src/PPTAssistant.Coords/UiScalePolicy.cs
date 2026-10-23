namespace PPTAssistant.Coords;

/// <summary>用户界面缩放的取值与屏内适配规则；不参与显示器 DPI 或墨迹坐标换算。</summary>
public static class UiScalePolicy
{
    public const double Minimum = 0.75;
    public const double Maximum = 2.0;
    public const double Default = 1.0;
    public const double Step = 0.25;

    /// <summary>非法设置恢复为默认值；有效范围内的任意小数都保留。</summary>
    public static double Normalize(double requested)
        => double.IsFinite(requested) && requested >= Minimum && requested <= Maximum
            ? requested
            : Default;

    /// <summary>在用户请求的比例以内缩小内容，使其同时适合可用宽高。</summary>
    /// <remarks>
    /// 所有长度使用同一种单位（UI 调用方传 DIP），不叠加显示器 DPI。
    /// 窄屏适配可以小于 <see cref="Minimum"/>，但不会改写用户设置。
    /// 尚未完成布局或无效的尺寸无法用于适配，此时保留规范化后的请求值。
    /// </remarks>
    public static double Fit(
        double requested,
        double availableWidth,
        double availableHeight,
        double contentWidth,
        double contentHeight)
    {
        var scale = Normalize(requested);
        if (!IsValidDimension(availableWidth)
            || !IsValidDimension(availableHeight)
            || !IsValidDimension(contentWidth)
            || !IsValidDimension(contentHeight))
        {
            return scale;
        }

        var fit = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
        return Math.Min(scale, Math.Max(double.Epsilon, fit));
    }

    private static bool IsValidDimension(double value) => double.IsFinite(value) && value > 0;
}
