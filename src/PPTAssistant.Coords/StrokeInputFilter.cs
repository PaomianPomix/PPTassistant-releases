namespace PPTAssistant.Coords;

public static class StrokeInputFilter
{
    /// <summary>
    /// 居中二项式滤波（核 (1,2,1)/4）：<c>out[i] = (p[i-1] + 2·p[i] + p[i+1]) / 4</c>。
    /// </summary>
    /// <param name="points">原始采样点（窗口 DIP，按书写顺序）。</param>
    /// <returns>滤波后的点（与输入等长、同序）。首末点保留原值。</returns>
    public static IReadOnlyList<DipPoint> Binomial3(IReadOnlyList<DipPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count <= 2)
        {
            // 两点以内没有可滤的中间点；单点/空输入原样返回。
            return points as IReadOnlyList<DipPoint> ?? points.ToList();
        }

        var result = new DipPoint[points.Count];
        result[0] = points[0];
        result[^1] = points[^1];

        for (var i = 1; i < points.Count - 1; i++)
        {
            var prev = points[i - 1];
            var cur = points[i];
            var next = points[i + 1];
            result[i] = new DipPoint(
                (prev.X + (2 * cur.X) + next.X) / 4,
                (prev.Y + (2 * cur.Y) + next.Y) / 4);
        }

        return result;
    }
}
