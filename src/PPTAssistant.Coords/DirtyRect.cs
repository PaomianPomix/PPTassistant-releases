using System.Diagnostics;

namespace PPTAssistant.Coords;

public static class DirtyRect
{
    /// <summary>默认余量（DIP）：覆盖抗锯齿羽化与 DIP→物理像素取整的误差。</summary>
    public const double DefaultPaddingDips = 2.0;

    /// <summary>
    /// 由中心线算脏矩形。
    /// </summary>
    /// <param name="points">中心线采样点（DIP）；空输入返回空矩形。</param>
    /// <param name="maxWidthDips">本条笔画的最粗笔宽（DIP）。取整条的最大值而不是末点，
    /// 是因为压感笔的宽度在书写过程中会变化，若只按末点外扩，前面较粗的段落会漏刷。</param>
    /// <param name="scale">DPI 缩放（<see cref="DisplayScale"/> 的口径：物理像素 = DIP × scale）。</param>
    /// <param name="paddingDips">额外余量（DIP）。</param>
    /// <returns>对齐到物理像素网格、并已按笔宽外扩的矩形。</returns>
    public static DipRect FromPoints(
        IReadOnlyList<DipPoint> points,
        double maxWidthDips,
        double scale = 1.0,
        double paddingDips = DefaultPaddingDips)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return new DipRect(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        // 半笔宽 + 余量：笔迹的实际足迹（不是采样点本身）。
        var reach = Math.Max(0, maxWidthDips) / 2 + Math.Max(0, paddingDips);

        return SnapOutward(DipRect.FromLtrb(minX, minY, maxX, maxY), reach, reach, scale);
    }

    /// <summary>
    /// 把一个矩形按「四周外扩 + 物理像素对齐」处理。
    /// </summary>
    /// <param name="rect">原始矩形（DIP）。</param>
    /// <param name="padX">水平外扩（DIP）。</param>
    /// <param name="padY">垂直外扩（DIP）。</param>
    /// <param name="scale">DPI 缩放。</param>
    public static DipRect SnapOutward(DipRect rect, double padX, double padY, double scale = 1.0)
    {
        var effectiveScale = scale > 0 ? scale : 1.0;
        var left = rect.X - Math.Max(0, padX);
        var top = rect.Y - Math.Max(0, padY);
        var right = rect.Right + Math.Max(0, padX);
        var bottom = rect.Bottom + Math.Max(0, padY);

        // 向外对齐到物理像素：左/上向下取整，右/下向上取整。
        left = Math.Floor(left * effectiveScale) / effectiveScale;
        top = Math.Floor(top * effectiveScale) / effectiveScale;
        right = Math.Ceiling(right * effectiveScale) / effectiveScale;
        bottom = Math.Ceiling(bottom * effectiveScale) / effectiveScale;

        return DipRect.FromLtrb(left, top, right, bottom);
    }

    /// <summary>
    /// 两个矩形是否重叠（含边界接触算重叠——边界像素也要重绘）。
    /// </summary>
    public static bool Intersects(DipRect a, DipRect b)
        => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
           && a.X <= b.Right && b.X <= a.Right
           && a.Y <= b.Bottom && b.Y <= a.Bottom;

    /// <summary>
    /// 把 <paramref name="rect"/> 并进 <paramref name="accumulated"/>（用于「自上次刷新以来累计的脏区」）。
    /// </summary>
    /// <remarks>
    /// 每来一个采样点就刷新一次屏幕是浪费——同一帧内可能有多个点。
    /// 把一帧内的脏区并成一个包围盒，一帧只刷一次（本方法只做并集，不做多矩形合并）。
    /// </remarks>
    public static DipRect Union(DipRect accumulated, DipRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return accumulated;
        }

        if (accumulated.Width <= 0 || accumulated.Height <= 0)
        {
            return rect;
        }

        return DipRect.FromLtrb(
            Math.Min(accumulated.X, rect.X),
            Math.Min(accumulated.Y, rect.Y),
            Math.Max(accumulated.Right, rect.Right),
            Math.Max(accumulated.Bottom, rect.Bottom));
    }

    /// <summary>与视口求交（屏幕外的部分不参与刷新）。</summary>
    public static DipRect ClipTo(DipRect rect, DipRect viewport) => rect.Intersect(viewport);
}
