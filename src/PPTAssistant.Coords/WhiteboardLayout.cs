namespace PPTAssistant.Coords;

/// <summary>白板的屏幕适配：触摸操作尺寸与矢量画布坐标分别计算。</summary>
public static class WhiteboardLayout
{
    public const double MinimumTouchTarget = 44;
    public const double ToolbarBaseHeight = 160;
    public const double MinimumCanvasHeight = 180;

    /// <summary>保持触摸按钮至少 44 DIP，4K 默认约 88 物理像素，避免重复叠加 Windows DPI。</summary>
    public static double ToolbarScale(double viewportWidthDips, double viewportHeightDips,
        double physicalWidth, double requestedUiScale)
    {
        if (!Valid(viewportWidthDips) || !Valid(viewportHeightDips)) return 1;
        var displayScale = Valid(physicalWidth) ? physicalWidth / viewportWidthDips : 1;
        var longestPhysicalSide = Math.Max(viewportWidthDips, viewportHeightDips) * displayScale;
        var physicalTargetScale = Math.Clamp(longestPhysicalSide / 1920, 1, 2);
        var automaticScale = Math.Max(1, physicalTargetScale / displayScale);
        var requested = automaticScale * UiScalePolicy.Normalize(requestedUiScale);
        var reserve = Math.Min(MinimumCanvasHeight, viewportHeightDips * .55);
        var maximumForHeight = (viewportHeightDips - reserve) / ToolbarBaseHeight;
        // 窄屏保留触摸基准尺寸，不让用户的 200% 设置把多数工具及画布挤出视口。
        var maximumForWidth = Math.Max(1, viewportWidthDips / 960);
        return Math.Max(1, Math.Min(requested, Math.Min(maximumForWidth, maximumForHeight)));
    }

    /// <summary>
    /// 将已存笔迹等比例装入可用区域；额外空白仍是画布，坐标从左上角连续延伸。
    /// 只改变视图，不修改笔迹，来回切换宽高比不会产生累计缩放误差。
    /// </summary>
    public static WhiteboardViewport Fit(double referenceWidth, double referenceHeight,
        double availableWidth, double availableHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableHeight);
        if (!Valid(referenceWidth) || !Valid(referenceHeight) || !Valid(availableWidth) || !Valid(availableHeight))
            throw new ArgumentOutOfRangeException(nameof(availableWidth), "白板尺寸必须是有限正数。");
        var scale = Math.Min(availableWidth / referenceWidth, availableHeight / referenceHeight);
        return new WhiteboardViewport(scale, availableWidth / scale, availableHeight / scale);
    }

    private static bool Valid(double value) => double.IsFinite(value) && value > 0;
}

public readonly record struct WhiteboardViewport(double Scale, double LogicalWidth, double LogicalHeight);
