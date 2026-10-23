namespace PPTAssistant.Coords;

public static class MagnifierLens
{
    /// <summary>源区域边长下限（DIP）——再小就没有放大意义了。</summary>
    public const double MinSourceDips = 16;

    /// <summary>
    /// 源区域边长（DIP）：浮窗边长 ÷ 倍率；夹到 <c>[MinSourceDips, 浮窗边长]</c>。
    /// </summary>
    /// <remarks>上限 = 浮窗边长：1.0× 时源与浮窗等大（「原样」），倍率语义即由它锚定。</remarks>
    public static double SourceSize(double lensSizeDips, double zoom)
        => Math.Clamp(lensSizeDips / Math.Max(zoom, 0.01), MinSourceDips, lensSizeDips);

    /// <summary>
    /// 镜下取景：以浮窗中心为中心、边长 = 浮窗边长 ÷ 倍率 的源矩形，并夹取到目标屏内。
    /// </summary>
    /// <param name="window">浮窗矩形（DIP，屏幕坐标系）。</param>
    /// <param name="zoom">倍率（1.0–3.0×）。</param>
    /// <param name="screenBounds">目标屏矩形（DIP）。</param>
    public static DipRect WindowToSource(DipRect window, double zoom, DipRect screenBounds)
        => PointToSource(window.CenterX, window.CenterY, window.Width, window.Height, zoom, screenBounds);

    /// <summary>
    /// 「内容跟光标」模式（<c>WDA_EXCLUDEFROMCAPTURE</c> 不可用时的回退）：以给定点为中心、
    /// 与浮窗同宽高比的源矩形，并夹取到目标屏内。
    /// </summary>
    /// <param name="centerX">中心 X（DIP，屏幕坐标系）——回退模式下是光标位置。</param>
    /// <param name="centerY">中心 Y（DIP，屏幕坐标系）。</param>
    /// <param name="windowWidth">浮窗宽（DIP）——决定源矩形的宽高比与 1.0× 时的尺寸。</param>
    /// <param name="windowHeight">浮窗高（DIP）。</param>
    /// <param name="zoom">倍率（1.0–3.0×）。</param>
    /// <param name="screenBounds">目标屏矩形（DIP）。</param>
    public static DipRect PointToSource(
        double centerX,
        double centerY,
        double windowWidth,
        double windowHeight,
        double zoom,
        DipRect screenBounds)
    {
        var sw = SourceSize(windowWidth, zoom);
        var sh = SourceSize(windowHeight, zoom);
        var left = ClampInto(centerX - (sw / 2), screenBounds.X, screenBounds.Right - sw);
        var top = ClampInto(centerY - (sh / 2), screenBounds.Y, screenBounds.Bottom - sh);
        return new DipRect(left, top, sw, sh);
    }

    /// <summary>把 <paramref name="value"/> 夹进 <c>[min, max]</c>；区间倒置（屏比源还小）时取 <paramref name="min"/>。</summary>
    private static double ClampInto(double value, double min, double max)
        => max <= min ? min : Math.Clamp(value, min, max);
}
