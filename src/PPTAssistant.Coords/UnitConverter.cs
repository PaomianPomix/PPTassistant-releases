namespace PPTAssistant.Coords;

public static class UnitConverter
{
    public const double PointsPerInch = 72.0;

    public const double DipsPerInch = 96.0;

    /// <summary>pt → DIP 的固定系数（4/3）。</summary>
    public const double DipsPerPoint = DipsPerInch / PointsPerInch;

    /// <summary>点转 DIP。</summary>
    public static double PointsToDips(double points) => points * DipsPerPoint;

    /// <summary>DIP 转点。</summary>
    public static double DipsToPoints(double dips) => dips / DipsPerPoint;

    public static DipPoint PointsToDips(SlidePoint point)
        => new(PointsToDips(point.X), PointsToDips(point.Y));

    /// <summary>把 COM 返回的「点」矩形（Left/Top/Width/Height）转成 DIP 矩形。</summary>
    public static DipRect PointsToDips(double left, double top, double width, double height)
        => new(PointsToDips(left), PointsToDips(top), PointsToDips(width), PointsToDips(height));

    /// <summary>DIP 转物理像素所需的缩放系数（1.0 = 100%）。</summary>
    public static double DipsToPixels(double dips, double displayScale) => dips * displayScale;

    /// <summary>物理像素转 DIP。</summary>
    public static double PixelsToDips(double pixels, double displayScale)
        => displayScale <= 0 ? pixels : pixels / displayScale;

    /// <summary>把非法（0 / 负数 / NaN）显示缩放兜底为 1.0，避免全链路塌成 0。</summary>
    public static double NormalizeDisplayScale(double displayScale)
        => displayScale > 0 && !double.IsNaN(displayScale) && !double.IsInfinity(displayScale)
            ? displayScale
            : 1.0;
}
