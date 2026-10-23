namespace PPTAssistant.Coords;

public readonly record struct DipPoint(double X, double Y);

/// <summary>DIP 坐标下的矩形。</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public static DipRect FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>与另一个矩形求交。无交集时返回面积为零的矩形。</summary>
    public DipRect Intersect(DipRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        if (right <= left || bottom <= top)
        {
            return new DipRect(left, top, 0, 0);
        }

        return DipRect.FromLtrb(left, top, right, bottom);
    }

    /// <summary>点（含边界）是否落在矩形内。</summary>
    public bool Contains(DipPoint point)
        => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    /// <summary>是否为「有面积」的矩形（宽高均为正）。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString()
        => $"({X:0.##},{Y:0.##} {Width:0.##}×{Height:0.##})";
}

/// <summary>幻灯片坐标（点，1 pt = 1/72 英寸），来自 PowerPoint 对象模型的 PageSetup / InkML 坐标系。</summary>
public readonly record struct SlidePoint(double X, double Y);

/// <summary>幻灯片页面尺寸（点）。</summary>
public readonly record struct SlideSize(double Width, double Height)
{
    /// <summary>16:9 默认页面：960 × 540 pt（13.333in × 7.5in）。</summary>
    public static readonly SlideSize Widescreen = new(960, 540);

    /// <summary>4:3 传统页面：720 × 540 pt（10in × 7.5in）。</summary>
    public static readonly SlideSize Standard = new(720, 540);

    public double Aspect => Height <= 0 ? 1 : Width / Height;

    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString() => $"{Width:0.##}×{Height:0.##} pt";
}
