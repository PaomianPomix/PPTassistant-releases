using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class DirtyRectTests
{
    /// <summary>空输入返回空矩形（调用方据此跳过刷新）。</summary>
    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        Assert.True(DirtyRect.FromPoints([], 6.0).IsEmpty);
    }

    /// <summary>单个采样点也必须得到一个有面积的矩形（「点一下」的圆点不能漏刷）。</summary>
    [Fact]
    public void SinglePointGetsArea()
    {
        var rect = DirtyRect.FromPoints([new DipPoint(100, 200)], 8.0);

        Assert.False(rect.IsEmpty);
        // 半径 4 + 余量 2 = 6，再被像素对齐（scale=1）包到整数 → 宽度至少 12。
        Assert.True(rect.Width >= 12, $"宽度应至少 12，实际 {rect.Width}");
        Assert.True(rect.Height >= 12, $"高度应至少 12，实际 {rect.Height}");
        Assert.True(rect.Contains(new DipPoint(100, 200)));
    }

    [Fact]
    public void HorizontalStrokeHasThickness()
    {
        var rect = DirtyRect.FromPoints(
            [new DipPoint(50, 300), new DipPoint(250, 300)],
            maxWidthDips: 10.0);

        // 高度 = 笔宽 10 + 两侧余量共 4 = 14 → 对齐后至少 14。
        Assert.True(rect.Height >= 14, $"水平笔的高度应为笔宽量级，实际 {rect.Height}");
        Assert.True(rect.Width >= 200, $"水平笔的宽度应覆盖整条笔，实际 {rect.Width}");
    }

    /// <summary>竖直笔的脏矩形必须有宽度（同上）。</summary>
    [Fact]
    public void VerticalStrokeHasWidth()
    {
        var rect = DirtyRect.FromPoints(
            [new DipPoint(300, 50), new DipPoint(300, 250)],
            maxWidthDips: 10.0);

        Assert.True(rect.Width >= 14, $"竖直笔的宽度应为笔宽量级，实际 {rect.Width}");
        Assert.True(rect.Height >= 200, $"竖直笔的高度应覆盖整条笔，实际 {rect.Height}");
    }

    /// <summary>笔越粗，脏矩形越大（宽笔不能被裁掉边缘）。</summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(6.0)]
    [InlineData(20.0)]
    [InlineData(48.0)]
    public void ThickerPenGetsLargerRect(double width)
    {
        var thin = DirtyRect.FromPoints([new DipPoint(100, 100)], 1.0);
        var thick = DirtyRect.FromPoints([new DipPoint(100, 100)], width);

        Assert.True(thick.Width >= thin.Width, $"{width} DIP 的脏矩形不应比 1 DIP 的更小");
        Assert.True(thick.Width >= width, $"单点笔的脏矩形应至少容纳整个笔宽 {width}，实际 {thick.Width}");
    }

    /// <summary>脏矩形必须包住笔迹的全部足迹（沿笔宽方向两侧都要够）。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CoversFullStrokeFootprint(double scale)
    {
        const double width = 12.0;
        var rect = DirtyRect.FromPoints(
            [new DipPoint(100, 100), new DipPoint(200, 100)],
            width,
            scale);

        // 采样点两侧各半个笔宽都必须落在脏矩形里。
        Assert.True(rect.X <= 100, "左边界应盖住起笔点");
        Assert.True(rect.Right >= 200, "右边界应盖住收笔点");
        Assert.True(rect.Y <= 100 - (width / 2), $"上边界应盖住上侧笔迹边缘（Y={100 - (width / 2)}），实际 {rect.Y}");
        Assert.True(rect.Bottom >= 100 + (width / 2), $"下边界应盖住下侧笔迹边缘，实际 {rect.Bottom}");
    }

    /// <summary>像素对齐必须**向外**：任何缩放比例下，边界都要盖住原矩形。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void PixelSnappingOnlyGrows(double scale)
    {
        var raw = new DipRect(10.3, 20.7, 100.4, 50.2);
        var snapped = DirtyRect.SnapOutward(raw, 0, 0, scale);

        Assert.True(snapped.X <= raw.X, $"左边界不得内缩（scale={scale}）");
        Assert.True(snapped.Y <= raw.Y, $"上边界不得内缩（scale={scale}）");
        Assert.True(snapped.Right >= raw.Right, $"右边界不得内缩（scale={scale}）");
        Assert.True(snapped.Bottom >= raw.Bottom, $"下边界不得内缩（scale={scale}）");
    }

    /// <summary>对齐后的边界必须落在物理像素网格上。</summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SnappedEdgesLandOnPixelGrid(double scale)
    {
        var snapped = DirtyRect.SnapOutward(new DipRect(10.3, 20.7, 100.4, 50.2), 0, 0, scale);

        Assert.Equal(Math.Floor(snapped.X * scale), snapped.X * scale, 6);
        Assert.Equal(Math.Floor(snapped.Y * scale), snapped.Y * scale, 6);
        Assert.Equal(Math.Ceiling(snapped.Right * scale), snapped.Right * scale, 6);
        Assert.Equal(Math.Ceiling(snapped.Bottom * scale), snapped.Bottom * scale, 6);
    }

    /// <summary>非法缩放（0 / 负数）退化为 1.0，不产生 NaN / 除零。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidScaleFallsBackToOne(double scale)
    {
        var rect = DirtyRect.FromPoints([new DipPoint(10, 10)], 6.0, scale);

        Assert.False(rect.IsEmpty);
        Assert.False(double.IsNaN(rect.X) || double.IsNaN(rect.Width));
    }

    /// <summary>相交判定：接触边界算相交（边界像素也要重绘）。</summary>
    [Fact]
    public void IntersectsCountsTouchingEdges()
    {
        Assert.True(DirtyRect.Intersects(new DipRect(0, 0, 10, 10), new DipRect(10, 0, 10, 10)));
        Assert.False(DirtyRect.Intersects(new DipRect(0, 0, 10, 10), new DipRect(10.5, 0, 10, 10)));
        Assert.False(DirtyRect.Intersects(new DipRect(0, 0, 10, 10), new DipRect(10, 0, 0, 10)));
    }

    /// <summary>并集：累计时不做多矩形合并，但要覆盖两者的全部范围。</summary>
    [Fact]
    public void UnionCoversBoth()
    {
        var a = new DipRect(0, 0, 10, 10);
        var b = new DipRect(50, 60, 5, 5);

        var union = DirtyRect.Union(a, b);
        Assert.Equal(0, union.X);
        Assert.Equal(0, union.Y);
        Assert.True(union.Right >= 55);
        Assert.True(union.Bottom >= 65);
    }

    /// <summary>并集：空矩形是并集的单位元（第一帧的累计从空开始）。</summary>
    [Fact]
    public void UnionWithEmptyReturnsOther()
    {
        var rect = new DipRect(1, 2, 3, 4);

        Assert.Equal(rect, DirtyRect.Union(new DipRect(0, 0, 0, 0), rect));
        Assert.Equal(rect, DirtyRect.Union(rect, new DipRect(0, 0, 0, 0)));
    }

    /// <summary>裁剪：脏矩形超出视口的部分必须被切掉（屏幕外像素不该进刷新队列）。</summary>
    [Fact]
    public void ClipToViewport()
    {
        var clipped = DirtyRect.ClipTo(new DipRect(-50, -50, 200, 200), new DipRect(0, 0, 100, 100));

        Assert.Equal(0, clipped.X);
        Assert.Equal(0, clipped.Y);
        Assert.Equal(100, clipped.Right);
        Assert.Equal(100, clipped.Bottom);
    }

    /// <summary>完全在视口外的脏矩形被裁成空（调用方跳过刷新）。</summary>
    [Fact]
    public void ClipRemovesOffscreen()
    {
        Assert.True(DirtyRect.ClipTo(new DipRect(500, 500, 50, 50), new DipRect(0, 0, 100, 100)).IsEmpty);
    }
}
