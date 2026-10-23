namespace PPTAssistant.Coords;

public static class StrokeEraser
{

    public const double DefaultDiameterDips = 30.0;

    /// <summary>默认橡皮半径（DIP）。</summary>
    public const double DefaultRadiusDips = DefaultDiameterDips / 2;

    /// <summary>命中容差（DIP）：吸收采样点之间的弦与真实曲线之间的微小偏差。</summary>
    public const double HitToleranceDips = 0.5;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// 橡皮扫过 <paramref name="eraserPath"/> 时，是否擦除这条笔画。
    /// </summary>
    /// <param name="centerline">笔画的中心线（DIP，建议是渲染用的密集点）。</param>
    /// <param name="widths">逐点笔宽（DIP，与 <paramref name="centerline"/> 一一对应）。</param>
    /// <param name="eraserPath">橡皮经过的路径（DIP，至少 1 个点；连续两点之间按扫过的线段处理）。</param>
    /// <param name="eraserRadiusDips">橡皮半径（DIP）。</param>
    /// <returns>true = 命中，应整笔删除。</returns>
    public static bool Intersects(
        IReadOnlyList<DipPoint> centerline,
        IReadOnlyList<double> widths,
        IReadOnlyList<DipPoint> eraserPath,
        double eraserRadiusDips)
    {
        ArgumentNullException.ThrowIfNull(centerline);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(eraserPath);

        if (centerline.Count == 0 || eraserPath.Count == 0)
        {
            return false;
        }

        var radius = Math.Max(0, eraserRadiusDips) + HitToleranceDips;

        // ① 包围盒粗筛：绝大多数笔画离橡皮很远，O(1) 就能排除。
        if (!BoundsOverlap(centerline, widths, eraserPath, radius))
        {
            return false;
        }

        // ② 单点笔画（「点一下」）：点 × 橡皮路径。
        if (centerline.Count == 1 || IsSinglePoint(centerline))
        {
            var reach = radius + (HalfWidth(widths, 0));
            return DistanceToPath(centerline[0], eraserPath) <= reach;
        }

        // ③ 逐段判定：笔画的每一段（中心线 + 两端半宽）与橡皮扫过的每一段比较。
        for (var i = 0; i < centerline.Count - 1; i++)
        {
            var a = centerline[i];
            var b = centerline[i + 1];
            if (NearlyEqual(a, b))
            {
                continue;
            }

            var halfWidth = Math.Max(HalfWidth(widths, i), HalfWidth(widths, i + 1));
            var reach = radius + halfWidth;

            for (var j = 0; j < eraserPath.Count; j++)
            {
                if (j == eraserPath.Count - 1)
                {
                    // 橡皮路径的最后一个点：点 × 笔画段。
                    if (DistancePointToSegment(eraserPath[j], a, b) <= reach)
                    {
                        return true;
                    }

                    continue;
                }

                var c = eraserPath[j];
                var d = eraserPath[j + 1];
                if (NearlyEqual(c, d))
                {
                    if (DistancePointToSegment(c, a, b) <= reach)
                    {
                        return true;
                    }

                    continue;
                }

                if (DistanceSegmentToSegment(a, b, c, d) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>点到折线的最短距离（空路径返回 <see cref="double.PositiveInfinity"/>）。</summary>
    public static double DistanceToPath(DipPoint point, IReadOnlyList<DipPoint> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (path.Count == 1)
        {
            return Distance(point, path[0]);
        }

        var best = double.MaxValue;
        for (var i = 0; i < path.Count - 1; i++)
        {
            best = Math.Min(best, DistancePointToSegment(point, path[i], path[i + 1]));
        }

        // 单点路径段（连续重复点）也要算进去。
        return Math.Min(best, Distance(point, path[^1]));
    }

    /// <summary>点到线段的最短距离（退化线段按点处理）。</summary>
    public static double DistancePointToSegment(DipPoint point, DipPoint a, DipPoint b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSquared = (abx * abx) + (aby * aby);
        if (lengthSquared <= Epsilon)
        {
            return Distance(point, a);
        }

        var apx = point.X - a.X;
        var apy = point.Y - a.Y;
        var t = ((apx * abx) + (apy * aby)) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        var qx = a.X + (abx * t);
        var qy = a.Y + (aby * t);
        var dx = point.X - qx;
        var dy = point.Y - qy;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>两条线段之间的最短距离（相交为 0）。</summary>
    public static double DistanceSegmentToSegment(DipPoint a, DipPoint b, DipPoint c, DipPoint d)
    {
        if (SegmentsIntersect(a, b, c, d))
        {
            return 0;
        }

        return Math.Min(
            Math.Min(DistancePointToSegment(a, c, d), DistancePointToSegment(b, c, d)),
            Math.Min(DistancePointToSegment(c, a, b), DistancePointToSegment(d, a, b)));
    }

    private static bool SegmentsIntersect(DipPoint a, DipPoint b, DipPoint c, DipPoint d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 * o2 < -Epsilon && o3 * o4 < -Epsilon)
        {
            return true;
        }

        return (Math.Abs(o1) <= Epsilon && OnSegment(a, b, c))
               || (Math.Abs(o2) <= Epsilon && OnSegment(a, b, d))
               || (Math.Abs(o3) <= Epsilon && OnSegment(c, d, a))
               || (Math.Abs(o4) <= Epsilon && OnSegment(c, d, b));
    }

    /// <summary>叉积的符号（z 分量）。</summary>
    private static double Orientation(DipPoint a, DipPoint b, DipPoint c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool OnSegment(DipPoint a, DipPoint b, DipPoint p)
        => p.X >= Math.Min(a.X, b.X) - Epsilon
           && p.X <= Math.Max(a.X, b.X) + Epsilon
           && p.Y >= Math.Min(a.Y, b.Y) - Epsilon
           && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    private static bool BoundsOverlap(
        IReadOnlyList<DipPoint> centerline,
        IReadOnlyList<double> widths,
        IReadOnlyList<DipPoint> eraserPath,
        double radius)
    {
        var maxHalfWidth = 0.0;
        for (var i = 0; i < centerline.Count; i++)
        {
            maxHalfWidth = Math.Max(maxHalfWidth, HalfWidth(widths, i));
        }

        GetBounds(centerline, out var strokeLeft, out var strokeTop, out var strokeRight, out var strokeBottom);
        GetBounds(eraserPath, out var eraserLeft, out var eraserTop, out var eraserRight, out var eraserBottom);

        var strokeReach = maxHalfWidth;
        var eraserReach = radius;

        return strokeLeft - strokeReach <= eraserRight + eraserReach
               && eraserLeft - eraserReach <= strokeRight + strokeReach
               && strokeTop - strokeReach <= eraserBottom + eraserReach
               && eraserTop - eraserReach <= strokeBottom + strokeReach;
    }

    private static void GetBounds(
        IReadOnlyList<DipPoint> points,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = double.MaxValue;
        top = double.MaxValue;
        right = double.MinValue;
        bottom = double.MinValue;

        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
    }

    private static bool IsSinglePoint(IReadOnlyList<DipPoint> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            if (!NearlyEqual(points[0], points[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static double HalfWidth(IReadOnlyList<double> widths, int index)
    {
        if (widths.Count == 0)
        {
            return 0;
        }

        var width = index < widths.Count ? widths[index] : widths[^1];
        return Math.Max(0, width) / 2;
    }

    private static double Distance(DipPoint a, DipPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool NearlyEqual(DipPoint a, DipPoint b)
        => Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;
}
