namespace PPTAssistant.Coords;

public static class StrokeOutline
{
    /// <summary>圆（圆头 / 圆角）的细分段数：16 段在 6–20 DIP 的笔宽下已看不出多边形。</summary>
    public const int CircleSegments = 16;

    /// <summary>转角小于此值（度）不补顶点圆——相邻梯形的重叠已覆盖，省一半图元。</summary>
    public const double RoundJoinThresholdDegrees = 10.0;

    private const double MinRadiusDips = 0.05;

    /// <summary>
    /// 构造填充轮廓。
    /// </summary>
    /// <param name="path">中心线（通常是 <see cref="StrokeSmoothing.Flatten"/> 的输出，已足够密）。</param>
    /// <param name="widths">逐点宽度（DIP，与 <paramref name="path"/> 一一对应）。</param>
    /// <returns>多边形列表（每个多边形是顶点数组）；单点输入返回一个圆（「点一下」的圆点）。</returns>
    public static IReadOnlyList<DipPoint[]> Build(IReadOnlyList<DipPoint> path, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(widths);

        if (path.Count == 0 || widths.Count != path.Count)
        {
            return [];
        }

        var polygons = new List<DipPoint[]>(Math.Max(1, path.Count));

        if (path.Count == 1)
        {
            var radius = Math.Max(MinRadiusDips, widths[0] / 2);
            polygons.Add(Circle(path[0], radius));
            return polygons;
        }

        for (var i = 0; i < path.Count - 1; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < double.Epsilon)
            {
                continue;
            }

            // 单位法向：把宽度摊到中心线两侧。
            var nx = -dy / length;
            var ny = dx / length;
            var ha = Math.Max(MinRadiusDips, widths[i] / 2);
            var hb = Math.Max(MinRadiusDips, widths[i + 1] / 2);

            polygons.Add(
            [
                new DipPoint(a.X + (nx * ha), a.Y + (ny * ha)),
                new DipPoint(b.X + (nx * hb), b.Y + (ny * hb)),
                new DipPoint(b.X - (nx * hb), b.Y - (ny * hb)),
                new DipPoint(a.X - (nx * ha), a.Y - (ny * ha)),
            ]);

            // 顶点圆：覆盖梯形在转角外的缺口（转角越大缺口越大）。
            if (i > 0 && TurnDegrees(path[i - 1], a, b) > RoundJoinThresholdDegrees)
            {
                polygons.Add(Circle(a, ha));
            }
        }

        // 两端必须圆头：起笔/收笔各补一个圆。
        polygons.Add(Circle(path[0], Math.Max(MinRadiusDips, widths[0] / 2)));
        polygons.Add(Circle(path[^1], Math.Max(MinRadiusDips, widths[^1] / 2)));

        return polygons;
    }

    

    public static DipPoint[] Circle(DipPoint center, double radius, int segments = CircleSegments)
    {
        var count = Math.Max(3, segments);
        var points = new DipPoint[count];
        for (var i = 0; i < count; i++)
        {
            // 负角度：与梯形环绕方向对齐（Nonzero 填充的前提）。不要改回正角度。
            var angle = -2.0 * Math.PI * i / count;
            points[i] = new DipPoint(
                center.X + (radius * Math.Cos(angle)),
                center.Y + (radius * Math.Sin(angle)));
        }

        return points;
    }

    /// <summary>轮廓的包围盒（含笔宽）——调用方用它算脏矩形。</summary>
    public static DipRect ComputeBounds(IReadOnlyList<DipPoint[]> polygons)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var polygon in polygons)
        {
            foreach (var point in polygon)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        return minX > maxX ? new DipRect(0, 0, 0, 0) : DipRect.FromLtrb(minX, minY, maxX, maxY);
    }

    /// <summary>中心线在 <paramref name="b"/> 处的转角（度，0 = 直行）。</summary>
    private static double TurnDegrees(DipPoint a, DipPoint b, DipPoint c)
    {
        var ax = b.X - a.X;
        var ay = b.Y - a.Y;
        var bx = c.X - b.X;
        var by = c.Y - b.Y;
        var la = Math.Sqrt((ax * ax) + (ay * ay));
        var lb = Math.Sqrt((bx * bx) + (by * by));
        if (la < double.Epsilon || lb < double.Epsilon)
        {
            return 0;
        }

        var cos = ((ax * bx) + (ay * by)) / (la * lb);
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}
