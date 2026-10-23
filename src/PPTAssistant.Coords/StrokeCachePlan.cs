using System.Diagnostics;

namespace PPTAssistant.Coords;

public readonly record struct StrokeCachePlan
{
    /// <summary>默认瓦片边长（物理像素）。512 在 4K 下得到 8×5=40 块，单块 ≈1MB（Pbgra32）。</summary>
    public const int DefaultTileSize = 512;

    /// <summary>画布的物理像素宽度。</summary>
    public int PixelWidth { get; }

    /// <summary>画布的物理像素高度。</summary>
    public int PixelHeight { get; }

    /// <summary>瓦片边长（物理像素）。</summary>
    public int TileSize { get; }

    /// <summary>画布的 DPI 缩放（物理像素 = DIP × Scale）。</summary>
    public double Scale { get; }

    private StrokeCachePlan(int pixelWidth, int pixelHeight, int tileSize, double scale)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        TileSize = tileSize;
        Scale = scale;
    }

    /// <summary>
    /// 按 DIP 视口尺寸建立规划。
    /// </summary>
    /// <param name="viewportDips">视口（DIP）。</param>
    /// <param name="scale">DPI 缩放（<see cref="DisplayScale"/> 口径）。</param>
    /// <param name="tileSize">瓦片边长（物理像素）。</param>
    public static StrokeCachePlan ForViewport(DipRect viewportDips, double scale = 1.0, int tileSize = DefaultTileSize)
    {
        var effectiveScale = scale > 0 ? scale : 1.0;
        var effectiveTile = tileSize > 0 ? tileSize : DefaultTileSize;

        // 向上取整：视口右/下边残留的不足一格也要有瓦片可用，否则最后一列内容的脏区无处落笔。
        var pixelWidth = (int)Math.Ceiling(Math.Max(0, viewportDips.Width) * effectiveScale);
        var pixelHeight = (int)Math.Ceiling(Math.Max(0, viewportDips.Height) * effectiveScale);
        return new StrokeCachePlan(pixelWidth, pixelHeight, effectiveTile, effectiveScale);
    }

    /// <summary>瓦片列数。</summary>
    public int Columns => PixelWidth <= 0 ? 0 : (PixelWidth + TileSize - 1) / TileSize;

    /// <summary>瓦片行数。</summary>
    public int Rows => PixelHeight <= 0 ? 0 : (PixelHeight + TileSize - 1) / TileSize;

    /// <summary>瓦片总数（缓存位图的总容量上限）。</summary>
    public int TileCount => Columns * Rows;

    /// <summary>整张画布的 DIP 矩形。</summary>
    public DipRect FullCanvas => new(0, 0, PixelWidth / Scale, PixelHeight / Scale);

    /// <summary>某个瓦片在**物理像素**下的矩形。</summary>
    public DipRect TilePixelRect(int column, int row)
    {
        var x = column * TileSize;
        var y = row * TileSize;
        return new DipRect(x, y, Math.Min(TileSize, PixelWidth - x), Math.Min(TileSize, PixelHeight - y));
    }

    /// <summary>
    /// 返回与脏矩形（DIP）相交的**全部瓦片编号**（行优先，去重，稳定升序）。
    /// </summary>
    /// <param name="dirtyDips">脏矩形（DIP）。空矩形返回空列表。</param>
    /// <returns>瓦片编号（<c>row * Columns + column</c>）；脏矩形完全在画布外时返回空列表。</returns>
    public IReadOnlyList<int> TilesFor(DipRect dirtyDips)
    {
        if (dirtyDips.IsEmpty || Columns == 0 || Rows == 0)
        {
            return [];
        }

        // DIP → 物理像素。**左/上向下取整、右/下向上取整**：任何被脏矩形碰到一个像素的瓦片都要算进来。
        var left = (int)Math.Floor(dirtyDips.X * Scale);
        var top = (int)Math.Floor(dirtyDips.Y * Scale);
        var right = (int)Math.Ceiling(dirtyDips.Right * Scale);
        var bottom = (int)Math.Ceiling(dirtyDips.Bottom * Scale);

        var firstColumn = Math.Max(0, FloorDiv(left, TileSize));
        var lastColumn = Math.Min(Columns - 1, FloorDiv(right - 1, TileSize));
        var firstRow = Math.Max(0, FloorDiv(top, TileSize));
        var lastRow = Math.Min(Rows - 1, FloorDiv(bottom - 1, TileSize));

        if (lastColumn < firstColumn || lastRow < firstRow)
        {
            return [];
        }

        var tiles = new List<int>((lastColumn - firstColumn + 1) * (lastRow - firstRow + 1));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                tiles.Add((row * Columns) + column);
            }
        }

        return tiles;
    }

    /// <summary>瓦片编号 → （列, 行）。</summary>
    public (int Column, int Row) TileCoordinates(int tile)
        => Columns == 0 ? (-1, -1) : (tile % Columns, tile / Columns);

    /// <summary>
    /// 向下取整的整数除法（<c>-1 / 512</c> 在 C# 里等于 0，但我们要求 −1——负数坐标也要落到正确的瓦片）。
    /// </summary>
    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        if (value < 0 && quotient * divisor != value)
        {
            quotient--;
        }

        return quotient;
    }

    public override string ToString()
        => $"{PixelWidth}×{PixelHeight}px @{Scale:0.##}x 瓦片{TileSize}px → {Columns}×{Rows}={TileCount} 块";
}
