namespace PPTAssistant.Coords;

public static class StrokeSmoothing
{
    /// <summary>默认扁平化容差（DIP）：0.2 ≈ 亚像素，肉眼不可能看出折线。</summary>
    public const double DefaultToleranceDips = 0.2;

    /// <summary>去重阈值（DIP）：相邻点近于此值视为重复（设备在极慢书写时会报同一个点）。</summary>
    public const double MinInputSpacingDips = 0.6;

    /// <summary>判定「共线」的容差（DIP）：小于它直接返回首末两点，直线原样保留。</summary>
    public const double StraightnessEpsilonDips = 0.08;

    /// <summary>细分递归深度上限（2^8 = 256 段/曲线段的上限，防病态输入炸内存）。</summary>
    private const int MaxSubdivisionDepth = 8;

    /// <summary>输出点数上限（安全阀：病态输入不能把 UI 拖死）。</summary>
    public const int MaxOutputPoints = 8192;

    /// <summary>
    /// 把原始采样点平滑成密集折线（点足够密时，肉眼与真实曲线无异）。
    /// </summary>
    /// <param name="raw">原始采样点（窗口 DIP，按书写顺序）。</param>
    /// <param name="toleranceDips">扁平化容差（DIP）；越小越贴近原曲线、点越多。</param>
    /// <returns>平滑后的折线（含首末点）。点数 ≤ <see cref="MaxOutputPoints"/>。</returns>
    public static IReadOnlyList<DipPoint> Flatten(
        IReadOnlyList<DipPoint> raw,
        double toleranceDips = DefaultToleranceDips)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var points = Dedupe(raw);
        if (points.Count <= 2)
        {
            // 两点（或一点）本来就是直线/点，无需平滑。
            return points;
        }

        if (IsStraight(points))
        {
            // 直线原样保留：既省点，也避免「鼠标画直线被平滑出波纹」。
            return [points[0], points[^1]];
        }

        var tolerance = Math.Max(0.01, toleranceDips);
        var result = new List<DipPoint>(points.Count * 4) { points[0] };

        for (var i = 0; i < points.Count - 1; i++)
        {
            // 端点用镜像邻居，保证起笔/收笔的切线自然（不强行拉直）。
            var p0 = i > 0 ? points[i - 1] : Mirror(points[0], points[1]);
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Count ? points[i + 2] : Mirror(points[^1], points[^2]);

            var (c1, c2) = ControlPoints(p0, p1, p2, p3);
            FlattenCubic(p1, c1, c2, p2, tolerance, 0, result);

            if (result.Count >= MaxOutputPoints)
            {
                break;
            }
        }

        // 收尾点必须精确落位（自适应细分有时会在容差内提前收敛，不保证恰好落在 Pn）。
        if (result[^1] != points[^1])
        {
            result.Add(points[^1]);
        }

        return result;
    }

    /// <summary>
    /// 把「原始点的宽度」重采样到平滑后的密集折线上（按弧长线性插值）。
    /// </summary>
    /// <remarks>
    /// 变宽笔画要求每个渲染点都有自己的宽度；而平滑后的点与原采样点不再一一对应，
    /// 所以按**弧长**在相邻原采样点之间线性插值——压力变化在几何上是连续的。
    /// </remarks>
    public static IReadOnlyList<double> SampleWidths(
        IReadOnlyList<DipPoint> rawPath,
        IReadOnlyList<double> rawWidths,
        IReadOnlyList<DipPoint> densePath)
    {
        ArgumentNullException.ThrowIfNull(rawPath);
        ArgumentNullException.ThrowIfNull(rawWidths);
        ArgumentNullException.ThrowIfNull(densePath);

        if (rawWidths.Count != rawPath.Count)
        {
            throw new ArgumentException("宽度数组必须与原始点一一对应", nameof(rawWidths));
        }

        if (densePath.Count == 0)
        {
            return [];
        }

        if (rawPath.Count == 0)
        {
            return new double[densePath.Count];
        }

        if (rawPath.Count == 1 || densePath.Count == 1)
        {
            return Enumerable.Repeat(rawWidths.Count > 0 ? rawWidths[0] : 0, densePath.Count).ToArray();
        }

        // 原始点累计弧长
        var cumulative = new double[rawPath.Count];
        for (var i = 1; i < rawPath.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] + Distance(rawPath[i - 1], rawPath[i]);
        }

        var widths = new double[densePath.Count];
        var j = 0;
        var walked = 0.0;
        for (var i = 0; i < densePath.Count; i++)
        {
            if (i > 0)
            {
                walked += Distance(densePath[i - 1], densePath[i]);
            }

            // 推进到包含当前弧长的原采样区间
            while (j < rawPath.Count - 2 && cumulative[j + 1] < walked)
            {
                j++;
            }

            var spanStart = cumulative[j];
            var spanEnd = cumulative[j + 1];
            var span = spanEnd - spanStart;
            var t = span <= double.Epsilon ? 0 : Math.Clamp((walked - spanStart) / span, 0, 1);
            widths[i] = (rawWidths[j] * (1 - t)) + (rawWidths[j + 1] * t);
        }

        return widths;
    }

    /// <summary>去掉重复/过密的点（保留首末点）。</summary>
    private static List<DipPoint> Dedupe(IReadOnlyList<DipPoint> raw)
    {
        var result = new List<DipPoint>(raw.Count);
        foreach (var point in raw)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            if (result.Count > 0 && Distance(result[^1], point) < MinInputSpacingDips)
            {
                continue;
            }

            result.Add(point);
        }

        // 末点必须保留（去重可能把末点吃掉，导致笔画短一截）。
        if (raw.Count > 0 && result.Count > 0)
        {
            var last = raw[^1];
            if (double.IsFinite(last.X) && double.IsFinite(last.Y) && result[^1] != last)
            {
                if (result.Count > 1 || Distance(result[^1], last) >= MinInputSpacingDips)
                {
                    result.Add(last);
                }
                else
                {
                    result[^1] = last;
                }
            }
        }

        return result;
    }

    /// <summary>所有点到「首末点连线」的距离都不超过阈值吗（直线判定）。</summary>
    private static bool IsStraight(List<DipPoint> points)
    {
        var a = points[0];
        var b = points[^1];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < double.Epsilon)
        {
            return false;
        }

        foreach (var point in points)
        {
            var cross = Math.Abs(((point.X - a.X) * dy) - ((point.Y - a.Y) * dx));
            if (cross / length > StraightnessEpsilonDips)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 <paramref name="edge"/> 关于 <paramref name="pivot"/> 镜像（端点补虚拟邻居用）。</summary>
    private static DipPoint Mirror(DipPoint pivot, DipPoint edge)
        => new((2 * pivot.X) - edge.X, (2 * pivot.Y) - edge.Y);

    private static (DipPoint C1, DipPoint C2) ControlPoints(DipPoint p0, DipPoint p1, DipPoint p2, DipPoint p3)
    {
        var dPrev = Distance(p0, p1);
        var dCur = Distance(p1, p2);
        var dNext = Distance(p2, p3);

        var scaleIn = dPrev + dCur <= double.Epsilon ? 0 : dCur / (dPrev + dCur);
        var scaleOut = dCur + dNext <= double.Epsilon ? 0 : dCur / (dCur + dNext);

        var c1 = new DipPoint(
            p1.X + ((p2.X - p0.X) * scaleIn / 3),
            p1.Y + ((p2.Y - p0.Y) * scaleIn / 3));

        var c2 = new DipPoint(
            p2.X - ((p3.X - p1.X) * scaleOut / 3),
            p2.Y - ((p3.Y - p1.Y) * scaleOut / 3));

        return (c1, c2);
    }

    /// <summary>自适应细分三次贝塞尔：控制点离弦足够近就收敛，否则对半分（de Casteljau）。</summary>
    private static void FlattenCubic(
        DipPoint p0,
        DipPoint c1,
        DipPoint c2,
        DipPoint p3,
        double tolerance,
        int depth,
        List<DipPoint> output)
    {
        if (depth >= MaxSubdivisionDepth || output.Count >= MaxOutputPoints)
        {
            return;
        }

        if (Flatness(p0, c1, c2, p3) <= tolerance)
        {
            output.Add(p3);
            return;
        }

        // de Casteljau 在 t = 0.5 处对分。
        var p01 = Mid(p0, c1);
        var p12 = Mid(c1, c2);
        var p23 = Mid(c2, p3);
        var p012 = Mid(p01, p12);
        var p123 = Mid(p12, p23);
        var mid = Mid(p012, p123);

        FlattenCubic(p0, p01, p012, mid, tolerance, depth + 1, output);
        FlattenCubic(mid, p123, p23, p3, tolerance, depth + 1, output);
    }

    private static double Flatness(DipPoint p0, DipPoint c1, DipPoint c2, DipPoint p3)
    {
        var dx = p3.X - p0.X;
        var dy = p3.Y - p0.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < double.Epsilon)
        {
            // 弦退化为点：用控制点到端点的距离衡量。
            return Math.Max(Distance(p0, c1), Distance(p0, c2));
        }

        var d1 = Math.Abs(((c1.X - p0.X) * dy) - ((c1.Y - p0.Y) * dx)) / length;
        var d2 = Math.Abs(((c2.X - p0.X) * dy) - ((c2.Y - p0.Y) * dx)) / length;
        return 0.75 * Math.Max(d1, d2);
    }

    private static DipPoint Mid(DipPoint a, DipPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance(DipPoint a, DipPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
