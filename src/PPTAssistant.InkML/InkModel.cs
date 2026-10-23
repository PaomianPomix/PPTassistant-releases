using PPTAssistant.Coords;

namespace PPTAssistant.InkML;

public readonly record struct InkPoint(double X, double Y, float Pressure, int T);

public sealed record InkStroke(IReadOnlyList<InkPoint> Points, double WidthPoints, InkColor Color);

/// <summary>一次板书（一页的墨迹集合）。</summary>
/// <param name="SlideSize">所在幻灯片的页面尺寸（pt）——用于校验落位是否越界。</param>
/// <param name="Strokes">笔画集合。</param>
public sealed record InkDrawing(SlideSize SlideSize, IReadOnlyList<InkStroke> Strokes)
{
    /// <summary>笔画总点数（诊断 / 旁路日志用）。</summary>
    public int PointCount => Strokes.Sum(static s => s.Points.Count);

    /// <summary>所有笔画的包围盒（pt）；没有笔画时为 null。</summary>
    public InkBounds? ComputeBounds()
    {
        double? minX = null, minY = null, maxX = null, maxY = null;
        foreach (var stroke in Strokes)
        {
            foreach (var point in stroke.Points)
            {
                minX = minX is null ? point.X : Math.Min(minX.Value, point.X);
                minY = minY is null ? point.Y : Math.Min(minY.Value, point.Y);
                maxX = maxX is null ? point.X : Math.Max(maxX.Value, point.X);
                maxY = maxY is null ? point.Y : Math.Max(maxY.Value, point.Y);
            }
        }

        return minX is null
            ? null
            : new InkBounds(minX.Value, minY!.Value, maxX!.Value - minX.Value, maxY!.Value - minY!.Value);
    }

    /// <summary>是否所有笔画都带可用压力数据（决定要不要写 F 通道）。</summary>
    public bool HasPressure => Strokes.Any(static s => s.Points.Any(static p => p.Pressure > 0f));
}

/// <summary>墨迹包围盒（pt）。</summary>
public readonly record struct InkBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public InkBounds WithMinimumSize(double minimum)
        => new(X, Y, Math.Max(Width, minimum), Math.Max(Height, minimum));

    public override string ToString() => $"({X:0.##},{Y:0.##} {Width:0.##}×{Height:0.##} pt)";
}
