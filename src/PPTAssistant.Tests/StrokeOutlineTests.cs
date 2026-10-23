using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokeOutlineTests
{
    /// <summary>单点输入 = 一个圆点（「点一下」必须看得见）。</summary>
    [Fact]
    public void SinglePointBecomesDot()
    {
        var polygons = StrokeOutline.Build([new DipPoint(100, 100)], [8.0]);

        var polygon = Assert.Single(polygons);
        Assert.Equal(StrokeOutline.CircleSegments, polygon.Length);
        foreach (var point in polygon)
        {
            var distance = Math.Sqrt(((point.X - 100) * (point.X - 100)) + ((point.Y - 100) * (point.Y - 100)));
            Assert.Equal(4.0, distance, 6); // 半径 = 宽度/2
        }
    }

    /// <summary>两点笔 = 一个梯形 + 两端圆头。</summary>
    [Fact]
    public void TwoPointStrokeHasQuadAndTwoRoundCaps()
    {
        var polygons = StrokeOutline.Build(
            [new DipPoint(0, 0), new DipPoint(100, 0)],
            [10.0, 10.0]);

        Assert.Equal(3, polygons.Count);
        // 圆头半径 = 5：圆心分别落在两个端点上
        Assert.All(polygons[1], p => Assert.Equal(5.0, Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 6));
        Assert.All(polygons[2], p => Assert.Equal(5.0, Math.Sqrt(((p.X - 100) * (p.X - 100)) + (p.Y * p.Y)), 6));
    }

    /// <summary>直线中心线不补顶点圆（省图元），只有首末两个圆头。</summary>
    [Fact]
    public void StraightPathHasNoVertexCircles()
    {
        List<DipPoint> path = [new(0, 0), new(50, 0), new(100, 0), new(150, 0)];

        var polygons = StrokeOutline.Build(path, [6.0, 6.0, 6.0, 6.0]);

        Assert.Equal(5, polygons.Count); // 3 梯形 + 2 圆头
    }

    /// <summary>直角转角必须补顶点圆，否则外侧（转角外角）会缺一块。</summary>
    [Fact]
    public void RightAngleTurnGetsVertexCircle()
    {
        List<DipPoint> path = [new(0, 0), new(50, 0), new(50, 50)];

        var polygons = StrokeOutline.Build(path, [8.0, 8.0, 8.0]);

        // 2 梯形 + 1 顶点圆（转角 90°）+ 2 圆头
        Assert.Equal(5, polygons.Count);
    }

    /// <summary>包围盒必须覆盖笔宽（宽笔不能被当成细线裁剪）。</summary>
    [Fact]
    public void BoundsIncludeStrokeWidth()
    {
        var thin = StrokeOutline.ComputeBounds(
            StrokeOutline.Build([new DipPoint(0, 0), new DipPoint(100, 0)], [2.0, 2.0]));
        var thick = StrokeOutline.ComputeBounds(
            StrokeOutline.Build([new DipPoint(0, 0), new DipPoint(100, 0)], [20.0, 20.0]));

        Assert.Equal(102.0, thin.Width, 6);
        Assert.Equal(2.0, thin.Height, 6);
        Assert.Equal(120.0, thick.Width, 6);
        Assert.Equal(20.0, thick.Height, 6);
    }

    /// <summary>空输入与长度不匹配不产生多边形（不抛异常，静默返回空）。</summary>
    [Fact]
    public void MismatchedOrEmptyInputYieldsNoPolygons()
    {
        Assert.Empty(StrokeOutline.Build([], []));
        Assert.Empty(StrokeOutline.Build([new DipPoint(1, 1)], [4, 4]));
    }

    /// <summary>圆按指定段数细分（段数不足会看出多边形）。</summary>
    [Fact]
    public void CircleIsSubdividedWithRequestedSegments()
    {
        var circle = StrokeOutline.Circle(new DipPoint(0, 0), 10, segments: 8);

        Assert.Equal(8, circle.Length);
        Assert.All(circle, p => Assert.Equal(10.0, Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 6));
    }

    [Fact]
    public void AllPolygonsShareTheSameWindingDirection()
    {
        // 「弯道 + 逐点变宽」：既要产生梯形、也要产生顶点圆，还要有首末圆头。
        // 取一条 90° 转弯的中心线（触发顶点圆）并让宽度逐点变化（覆盖所有分支）。
        List<DipPoint> path =
        [
            new(0, 0), new(30, 0), new(60, 0), new(80, 10), new(80, 40), new(80, 70), new(80, 100),
        ];
        double[] widths = [4.0, 6.0, 8.0, 10.0, 9.0, 7.0, 5.0];

        var polygons = StrokeOutline.Build(path, widths);

        Assert.NotEmpty(polygons);

        var areas = polygons
            .Where(static p => p.Length >= 3)
            .Select(SignedArea)
            .ToList();

        Assert.NotEmpty(areas);
        // 全部 > 0 或全部 < 0（同号）。这里只要求「同号」，不锁死正负的具体取值。
        Assert.True(
            areas.All(static a => a > 0) || areas.All(static a => a < 0),
            "轮廓多边形的环绕方向不一致——Nonzero 填充会在重叠处挖洞");
    }

    [Fact]
    public void CircleWindsNegativeToMatchQuads()
    {
        var circle = StrokeOutline.Circle(new DipPoint(0, 0), 10);

        Assert.True(SignedArea(circle) < 0, "圆的环绕方向必须为负，才能与梯形在 Nonzero 下正确并集");
    }

    /// <summary>鞋带公式：顶点逆时针为正，顺时针为负（坐标系 Y 向下时符号相反）。</summary>
    private static double SignedArea(IReadOnlyList<DipPoint> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2.0;
    }
}
