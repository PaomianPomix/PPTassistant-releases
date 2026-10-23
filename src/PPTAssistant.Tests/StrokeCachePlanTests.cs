using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokeCachePlanTests
{
    private static StrokeCachePlan Plan(double width = 3840, double height = 2160, double scale = 1.0, int tile = 512)
        => StrokeCachePlan.ForViewport(new DipRect(0, 0, width, height), scale, tile);

    /// <summary>4K @100% 且瓦片 512 → 8×5 = 40 块（与文档里的口径一致）。</summary>
    [Fact]
    public void FourKGridIsEightByFive()
    {
        var plan = Plan();

        Assert.Equal(8, plan.Columns);
        Assert.Equal(5, plan.Rows);
        Assert.Equal(40, plan.TileCount);
        Assert.Contains("3840×2160px", plan.ToString());
    }

    /// <summary>DIP 视口在 200% 缩放下覆盖 2 倍物理像素 → 瓦片数按比例增加（坐标系必须真的换算了）。</summary>
    [Fact]
    public void ScaleChangesGrid()
    {
        // 用 2048×1024 这种「整除」尺寸，隔离出「缩放」这一个变量。
        var hundred = Plan(width: 2048, height: 1024, scale: 1.0);
        var doubleScale = Plan(width: 2048, height: 1024, scale: 2.0);

        Assert.Equal(4, hundred.Columns);
        Assert.Equal(2, hundred.Rows);
        Assert.Equal(hundred.Columns * 2, doubleScale.Columns);
        Assert.Equal(hundred.Rows * 2, doubleScale.Rows);
    }

    /// <summary>不足一格的残留也要有瓦片（否则右/下边缘的脏区无处落笔）。</summary>
    [Fact]
    public void PartialEdgeGetsTile()
    {
        var plan = Plan(width: 513, height: 513);

        Assert.Equal(2, plan.Columns);
        Assert.Equal(2, plan.Rows);
        // 第二列只有 1 像素宽。
        Assert.Equal(1, plan.TilePixelRect(1, 0).Width);
        Assert.Equal(1, plan.TilePixelRect(0, 1).Height);
    }

    /// <summary>空脏矩形 → 无瓦片（调用方跳过刷新）。</summary>
    [Fact]
    public void EmptyDirtyRectYieldsNoTiles()
    {
        Assert.Empty(Plan().TilesFor(new DipRect(10, 10, 0, 0)));
        Assert.Empty(Plan().TilesFor(new DipRect(10, 10, 5, -1)));
    }

    /// <summary>完全在画布外的脏矩形 → 无瓦片。</summary>
    [Fact]
    public void OffscreenDirtyRectYieldsNoTiles()
    {
        Assert.Empty(Plan().TilesFor(new DipRect(-100, -100, 50, 50)));
        Assert.Empty(Plan().TilesFor(new DipRect(5000, 5000, 50, 50)));
    }

    /// <summary>瓦片内的一个小矩形只命中一块。</summary>
    [Fact]
    public void SingleTileHit()
    {
        var tiles = Plan().TilesFor(new DipRect(600, 600, 50, 50));

        var tile = Assert.Single(tiles);
        Assert.Equal(1 * 8 + 1, tile);
    }

    /// <summary>跨格边界时命中相邻的瓦片（不能只算起点所在的那一块）。</summary>
    [Fact]
    public void CrossingTileBoundaryHitsBoth()
    {
        // x 从 500 到 520，横跨 512 这条瓦片边界。
        var tiles = Plan().TilesFor(new DipRect(500, 10, 20, 10));

        Assert.Equal(2, tiles.Count);
        Assert.Contains(0, tiles);
        Assert.Contains(1, tiles);
    }

    /// <summary>恰好落在边界上的脏矩形：右/下边界所在的那一块也要更新（否则边界像素漏刷）。</summary>
    [Fact]
    public void BoundaryExactlyOnEdgeIncludesRightTile()
    {
        // 右边界正好等于 512 = 第一块瓦片的终点，说明第 0 列的最后一个像素变了。
        var touching = Plan().TilesFor(new DipRect(500, 10, 12, 10));
        Assert.Contains(0, touching);
        Assert.DoesNotContain(1, touching);

        // 再多一个像素就进入第二块。
        var crossing = Plan().TilesFor(new DipRect(500, 10, 13, 10));
        Assert.Contains(1, crossing);
    }

    /// <summary>整张画布 → 全部瓦片，且不重复（增量缓存清空时要能一次刷全）。</summary>
    [Fact]
    public void FullCanvasHitsEveryTileOnce()
    {
        var plan = Plan();
        var tiles = plan.TilesFor(plan.FullCanvas);

        Assert.Equal(plan.TileCount, tiles.Count);
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, plan.TileCount), tiles.OrderBy(static t => t));
    }

    /// <summary>★★ 「增量」的量化定义：一笔的脏区触发的瓦片数必须远小于总瓦片数。</summary>
    [Fact]
    public void OneStrokeTouchesFewTiles()
    {
        var plan = Plan();

        // 一条 200 DIP 长的横笔（4K 屏上很短的一划），笔宽 10。
        var dirty = DirtyRect.FromPoints(
            [new DipPoint(1000, 1000), new DipPoint(1200, 1000)],
            maxWidthDips: 10.0);
        var tiles = plan.TilesFor(dirty);

        Assert.True(
            tiles.Count <= 4,
            $"一小段笔迹不应触发多于 4 块瓦片，实际 {tiles.Count}（总 {plan.TileCount} 块）");
        Assert.True(
            tiles.Count < plan.TileCount / 8,
            "增量更新的瓦片数应远小于总瓦片数，否则等于整张重烘");
    }

    /// <summary>★★ 瓦片数与书写时长无关：把笔迹拉长到横跨整个屏，触发的仍只是它经过的那一行。</summary>
    [Fact]
    public void LongStrokeStaysOneRow()
    {
        var plan = Plan();

        var tiles = plan.TilesFor(new DipRect(0, 1000, 3840, 20));

        // 横向铺满 → 8 块；纵向只跨 1 行 → 共 8 块，而不是 40。
        Assert.Equal(plan.Columns, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(1, t / plan.Columns));
    }

    /// <summary>穷举：网格内移动的小矩形，命中的瓦片必须真的与之有像素交集（不漏、不误报）。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void EveryReportedTileActuallyIntersects(double scale)
    {
        var plan = Plan(scale: scale);

        for (var x = 0.0; x < 3840; x += 137.0)
        {
            for (var y = 0.0; y < 2160; y += 91.0)
            {
                var dirty = DirtyRect.SnapOutward(new DipRect(x, y, 30, 30), 6, 6, scale);
                var tiles = plan.TilesFor(dirty);
                Assert.NotEmpty(tiles);

                foreach (var tile in tiles)
                {
                    Assert.InRange(tile, 0, plan.TileCount - 1);

                    // 瓦片物理矩形换算回 DIP 与脏矩形比。
                    var (column, row) = plan.TileCoordinates(tile);
                    var pixelRect = plan.TilePixelRect(column, row);
                    var tileDips = new DipRect(
                        pixelRect.X / scale,
                        pixelRect.Y / scale,
                        pixelRect.Width / scale,
                        pixelRect.Height / scale);

                    Assert.True(
                        DirtyRect.Intersects(dirty, tileDips),
                        $"瓦片 {tile} 并不与脏矩形 {dirty} 相交（scale={scale}）");
                }
            }
        }
    }

    /// <summary>穷举：与脏矩形真的相交的瓦片，一个都不能漏（这是「不漏刷」的正面证明）。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void NoIntersectingTileIsMissed(double scale)
    {
        var plan = Plan(scale: scale);

        for (var x = 0.0; x < 3840; x += 211.0)
        {
            for (var y = 0.0; y < 2160; y += 173.0)
            {
                var dirty = DirtyRect.SnapOutward(new DipRect(x, y, 40, 25), 6, 6, scale);
                var reported = plan.TilesFor(dirty).ToHashSet();

                for (var tile = 0; tile < plan.TileCount; tile++)
                {
                    var (column, row) = plan.TileCoordinates(tile);
                    var pixelRect = plan.TilePixelRect(column, row);
                    var tileDips = new DipRect(
                        pixelRect.X / scale,
                        pixelRect.Y / scale,
                        pixelRect.Width / scale,
                        pixelRect.Height / scale);

                    if (DirtyRect.Intersects(dirty, tileDips))
                    {
                        Assert.Contains(tile, reported);
                    }
                }
            }
        }
    }

    /// <summary>返回顺序稳定（行优先升序）——调用方用它做去重与批量刷新，顺序不稳定会掩盖漏刷的 bug。</summary>
    [Fact]
    public void TileOrderIsStable()
    {
        var tiles = Plan().TilesFor(new DipRect(0, 0, 3840, 2160));

        Assert.Equal(tiles.OrderBy(static t => t), tiles);
    }

    /// <summary>非法的瓦片尺寸/缩放退化为默认值，不抛异常。</summary>
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-8, 1.0)]
    [InlineData(512, 0.0)]
    public void InvalidParametersFallBack(int tileSize, double scale)
    {
        var plan = StrokeCachePlan.ForViewport(new DipRect(0, 0, 1024, 768), scale, tileSize);

        Assert.True(plan.TileCount > 0);
        Assert.NotEmpty(plan.TilesFor(new DipRect(10, 10, 10, 10)));
    }

    /// <summary>零尺寸视口 → 无瓦片，不抛除零。</summary>
    [Fact]
    public void EmptyViewportYieldsNoTiles()
    {
        var plan = StrokeCachePlan.ForViewport(new DipRect(0, 0, 0, 0));

        Assert.Equal(0, plan.TileCount);
        Assert.Empty(plan.TilesFor(new DipRect(0, 0, 100, 100)));
    }
}
