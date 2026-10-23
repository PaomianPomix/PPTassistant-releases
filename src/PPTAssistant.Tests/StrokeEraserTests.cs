using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokeEraserTests
{
    private const double Radius = StrokeEraser.DefaultRadiusDips;

    private static DipPoint P(double x, double y) => new(x, y);

    private static DipPoint[] Line(params (double X, double Y)[] points)
        => points.Select(p => new DipPoint(p.X, p.Y)).ToArray();

    private static double[] Widths(int count, double width)
        => Enumerable.Repeat(width, count).ToArray();

    [Fact]
    public void TapOnSinglePointStrokeErasesIt()
    {
        Assert.True(StrokeEraser.Intersects(Line((100, 100)), [6], Line((100, 100)), Radius));
    }

    [Fact]
    public void SinglePointStrokeIsNotErasedWhenFarAway()
    {
        Assert.False(StrokeEraser.Intersects(Line((100, 100)), [6], Line((100, 200)), Radius));
    }

    [Fact]
    public void EraserMustReachTheStrokeEdgeNotJustTheCenterline()
    {
        // 中心线在 20 DIP 外：半径 15 够不到中心线，但笔宽 12（半宽 6）的墨迹边缘在 14 DIP 处。
        var stroke = Line((0, 0), (100, 0));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 12), Line((50, 20)), Radius));

        // 22 DIP 外：21（15 + 6）再加 0.5 容差也够不到。
        Assert.False(StrokeEraser.Intersects(stroke, Widths(2, 12), Line((50, 22)), Radius));
    }

    [Fact]
    public void FastDragSweepsTheSegmentBetweenSamples()
    {
        // 两端点都离竖直笔画很远，但两点之间的线段横穿它——这是快速拖动必须命中的场景。
        var stroke = Line((50, 0), (50, 100));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 6), Line((-10, 50), (110, 50)), Radius));
    }

    [Fact]
    public void DiagonalSweepCrossesStroke()
    {
        var stroke = Line((0, 50), (100, 50));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 6), Line((0, 0), (100, 100)), Radius));
    }

    [Fact]
    public void ParallelPathNearMissDoesNotErase()
    {
        var stroke = Line((0, 0), (100, 0));

        // 15 DIP 正好在半径上（含 0.5 容差）——接触即算命中。
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 6), Line((0, 15), (100, 15)), Radius));

        // 16 DIP 已经超出半径 + 容差（笔宽 0 时）。
        Assert.False(StrokeEraser.Intersects(stroke, Widths(2, 0), Line((0, 16), (100, 16)), Radius));
    }

    [Fact]
    public void CrossingSegmentsHitEvenWhenAllEndpointsAreFar()
    {
        // 中心线十字交叉：四个端点都远超半径，但线段本身相交。
        var stroke = Line((-10, 0), (10, 0));
        var eraser = Line((0, -10), (0, 10));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 0), eraser, 1.0));
    }

    [Fact]
    public void ThinStrokeCanBeMissedByASinglePointButNotByItsSweep()
    {
        var stroke = Line((0, 0), (100, 0));
        Assert.False(StrokeEraser.Intersects(stroke, Widths(2, 0), Line((50, 40)), Radius));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(2, 0), Line((50, 40), (50, 0)), Radius));
    }

    [Fact]
    public void DegenerateSegmentsAreHandledAsPoints()
    {
        var stroke = Line((0, 0), (0, 0), (100, 0));
        Assert.True(StrokeEraser.Intersects(stroke, Widths(3, 6), Line((50, 5)), Radius));
        Assert.False(StrokeEraser.Intersects(stroke, Widths(3, 6), Line((50, 50)), Radius));
    }

    [Fact]
    public void EmptyInputsNeverErase()
    {
        Assert.False(StrokeEraser.Intersects([], [], Line((0, 0)), Radius));
        Assert.False(StrokeEraser.Intersects(Line((0, 0)), [6], [], Radius));
        Assert.False(StrokeEraser.Intersects([], [6], Line((0, 0)), Radius));
    }

    [Fact]
    public void WidthsCollectionMayBeShorterThanCenterline()
    {
        // 防御性：数据不齐时用最后一个宽度兜底，而不是抛异常或误判为零宽。
        Assert.True(StrokeEraser.Intersects(Line((0, 0), (100, 0)), [20], Line((50, 20)), Radius));
    }

    [Fact]
    public void DistanceHelpersExposeTheExpectedGeometry()
    {
        Assert.Equal(0, StrokeEraser.DistancePointToSegment(P(50, 0), P(0, 0), P(100, 0)), 6);
        Assert.Equal(10, StrokeEraser.DistancePointToSegment(P(50, 10), P(0, 0), P(100, 0)), 6);
        Assert.Equal(Math.Sqrt(34), StrokeEraser.DistancePointToSegment(P(-5, 3), P(0, 0), P(100, 0)), 6);
        Assert.Equal(0, StrokeEraser.DistanceSegmentToSegment(P(0, 0), P(10, 0), P(5, -5), P(5, 5)), 6);
        Assert.Equal(
            5,
            StrokeEraser.DistanceSegmentToSegment(P(0, 0), P(10, 0), P(5, 5), P(15, 5)),
            6);
    }

    [Fact]
    public void DistanceToPathUsesEveryLeg()
    {
        var path = Line((0, 0), (100, 0), (100, 100));
        Assert.Equal(5, StrokeEraser.DistanceToPath(P(105, 50), path), 6);
        Assert.True(double.IsPositiveInfinity(StrokeEraser.DistanceToPath(P(0, 0), [])));
    }

    [Fact]
    public void EraserDiameterMatchesTheOriginalProgram()
    {

        Assert.Equal(30.0, StrokeEraser.DefaultDiameterDips, 6);
        Assert.Equal(15.0, StrokeEraser.DefaultRadiusDips, 6);
    }

    [Fact]
    public void ManyPointsRemainAccurateAtTheEnds()
    {
        var stroke = new List<DipPoint>(1000);
        for (var i = 0; i < 1000; i++)
        {
            stroke.Add(new DipPoint(i, Math.Sin(i / 20.0) * 20));
        }

        // 末端 10 DIP 外：笔宽 0 时必然命中。
        var end = stroke[^1];
        Assert.True(StrokeEraser.Intersects(stroke, Widths(stroke.Count, 0), [new DipPoint(end.X + 10, end.Y)], Radius));

        // 末端 20 DIP 外：半径 15 + 0.5 够不到。
        Assert.False(StrokeEraser.Intersects(stroke, Widths(stroke.Count, 0), [new DipPoint(end.X + 20, end.Y)], Radius));
    }
}
