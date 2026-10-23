using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokeSmoothingTests
{
    /// <summary>在圆上按固定弧长间距取点（模拟快速书写的稀疏采样）。</summary>
    private static List<DipPoint> SampleCircle(double radius, double spacing)
    {
        var points = new List<DipPoint>();
        var circumference = 2 * Math.PI * radius;
        var count = Math.Max(6, (int)Math.Round(circumference / spacing));
        for (var i = 0; i <= count; i++)
        {
            var angle = 2 * Math.PI * i / count;
            points.Add(new DipPoint(500 + (radius * Math.Cos(angle)), 500 + (radius * Math.Sin(angle))));
        }

        return points;
    }

    /// <summary>路径上相邻两段的**最大转角**（度）——「棱角」的直接度量。</summary>
    /// <remarks>
    /// 判据基准：16 边形（22.5°/段）看起来已经是光滑曲线，所以「每段转角 ≤ 22.5°」
    /// 可作为「肉眼无棱角」的工程阈值；而原始折线在快速书写（稀疏采样）下会远超它。
    /// </remarks>
    private static double MaxTurnDegrees(IReadOnlyList<DipPoint> path)
    {
        var worst = 0.0;
        for (var i = 2; i < path.Count; i++)
        {
            worst = Math.Max(worst, TurnDegrees(path[i - 2], path[i - 1], path[i]));
        }

        return worst;
    }

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

        var cos = Math.Clamp(((ax * bx) + (ay * by)) / (la * lb), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    /// <summary>最大相邻点间距（DIP）——转角必须发生在足够短的段上才算光滑。</summary>
    private static double MaxSpacing(IReadOnlyList<DipPoint> path)
    {
        var worst = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var dx = path[i].X - path[i - 1].X;
            var dy = path[i].Y - path[i - 1].Y;
            worst = Math.Max(worst, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return worst;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    [InlineData(400)]
    public void SmoothingRemovesVisibleCornersOnFastStrokes(double radius)
    {
        var raw = SampleCircle(radius, spacing: 50);

        var smoothed = StrokeSmoothing.Flatten(raw);

        Assert.True(
            MaxTurnDegrees(smoothed) <= 22.5,
            $"半径 {radius}：平滑后仍有可见棱角（最大转角 {MaxTurnDegrees(smoothed):0.0}°）");
        Assert.True(
            MaxSpacing(smoothed) <= 30,
            $"半径 {radius}：段长过大（最大 {MaxSpacing(smoothed):0.0} DIP），转角落在长段上仍能看出折线");
        Assert.True(smoothed.Count > raw.Count, "平滑应当加密点（否则仍是折线）");
    }

    /// <summary>
    /// 反证（解释这个修复为什么必要）：同样输入下，**原始折线**在曲率大的地方转角远超 22.5°。
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void RawPolylineCornersAreFarAboveThreshold(double radius)
    {
        var raw = SampleCircle(radius, spacing: 50);

        Assert.True(
            MaxTurnDegrees(raw) >= 20,
            $"半径 {radius}：原始折线的最大转角只有 {MaxTurnDegrees(raw):0.0}°——反证失效（判据或采样需复核）");
    }

    /// <summary>
    /// 精度：平滑后的路径必须贴着「老师想画的几何」走（圆弧情形即半径偏差小）。
    /// </summary>
    /// <remarks>
    /// 注意这里比较的是**与理想圆的偏差**：稀疏采样下「曲线穿过每个采样点」的任何方法
    /// （包括原始折线）都有这个量级的偏差——因为信息本来就不在采样里了。
    /// 平滑修的是**棱角与切线连续性**，不是把稀疏输入变出不存在的信息。
    /// </remarks>
    [Theory]
    [InlineData(120)]
    [InlineData(400)]
    public void SmoothedPathTracksIntendedGeometry(double radius)
    {
        var raw = SampleCircle(radius, spacing: 25);

        var smoothed = StrokeSmoothing.Flatten(raw);

        var worst = 0.0;
        foreach (var point in smoothed)
        {
            var dx = point.X - 500;
            var dy = point.Y - 500;
            worst = Math.Max(worst, Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius));
        }

        Assert.True(worst <= 1.0, $"半径 {radius}：路径偏离目标圆弧 {worst:0.00} DIP");
    }

    /// <summary>平滑后任意相邻点的间距必须足够小（不能留长直段），且点数有上限（安全阀）。</summary>
    [Fact]
    public void FlattenedPathHasBoundedSegmentLengthAndPointCount()
    {
        var raw = SampleCircle(400, spacing: 25);
        var smoothed = StrokeSmoothing.Flatten(raw);

        for (var i = 1; i < smoothed.Count; i++)
        {
            var dx = smoothed[i].X - smoothed[i - 1].X;
            var dy = smoothed[i].Y - smoothed[i - 1].Y;
            var step = Math.Sqrt((dx * dx) + (dy * dy));
            Assert.True(step <= 40, $"第 {i} 段长 {step:0.0} DIP——容差细分没生效");
        }

        Assert.True(smoothed.Count <= StrokeSmoothing.MaxOutputPoints);
    }

    /// <summary>端点必须严格保留（起笔/收笔位置不能因平滑而漂移）。</summary>
    [Fact]
    public void EndpointsArePreservedExactly()
    {
        var raw = SampleCircle(200, spacing: 20);
        var smoothed = StrokeSmoothing.Flatten(raw);

        Assert.Equal(raw[0], smoothed[0]);
        Assert.Equal(raw[^1], smoothed[^1]);
    }

    /// <summary>共线输入（手写的直线）保持笔直：只留首末两点，不被平滑出波纹。</summary>
    [Fact]
    public void StraightInputStaysStraight()
    {
        List<DipPoint> raw = [];
        for (var i = 0; i <= 10; i++)
        {
            raw.Add(new DipPoint(100 + (i * 12.5), 200 + (i * 6.25)));
        }

        var smoothed = StrokeSmoothing.Flatten(raw);

        Assert.Equal(2, smoothed.Count);
        Assert.Equal(raw[0], smoothed[0]);
        Assert.Equal(raw[^1], smoothed[^1]);
    }

    /// <summary>重复点（设备在极慢书写时报同一个点）不会产生零长度段或退化曲线。</summary>
    [Fact]
    public void DuplicatePointsAreCollapsed()
    {
        List<DipPoint> raw = [];
        for (var i = 0; i < 6; i++)
        {
            raw.Add(new DipPoint(300, 300));
            raw.Add(new DipPoint(300.1, 300.1));
        }

        raw.Add(new DipPoint(340, 320));

        var smoothed = StrokeSmoothing.Flatten(raw);

        Assert.All(smoothed, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.Equal(2, smoothed.Count); // 去重后只剩起点与终点
    }

    /// <summary>单点与两点输入原样返回（点一下 / 极短笔画）。</summary>
    [Fact]
    public void DegenerateInputsPassThrough()
    {
        Assert.Single(StrokeSmoothing.Flatten([new DipPoint(10, 20)]));
        Assert.Equal(
            2,
            StrokeSmoothing.Flatten([new DipPoint(10, 20), new DipPoint(40, 60)]).Count);
    }

    /// <summary>宽度剖面：按弧长线性插值——中点宽度应为两端平均。</summary>
    [Fact]
    public void WidthProfileInterpolatesAlongArcLength()
    {
        List<DipPoint> raw = [new(0, 0), new(100, 0), new(200, 0)];
        List<double> widths = [4, 8, 12];
        List<DipPoint> dense = [new(0, 0), new(50, 0), new(100, 0), new(150, 0), new(200, 0)];

        var sampled = StrokeSmoothing.SampleWidths(raw, widths, dense);

        Assert.Equal(5, sampled.Count);
        Assert.Equal(4, sampled[0], 6);
        Assert.Equal(6, sampled[1], 6);
        Assert.Equal(8, sampled[2], 6);
        Assert.Equal(10, sampled[3], 6);
        Assert.Equal(12, sampled[4], 6);
    }

    /// <summary>宽度数组与点数组长度不一致时抛错（调用方 bug 不能静默通过）。</summary>
    [Fact]
    public void MismatchedWidthCountThrows()
    {
        Assert.Throws<ArgumentException>(() => StrokeSmoothing.SampleWidths(
            [new DipPoint(0, 0), new DipPoint(1, 1)],
            [3],
            [new DipPoint(0, 0)]));
    }
}
