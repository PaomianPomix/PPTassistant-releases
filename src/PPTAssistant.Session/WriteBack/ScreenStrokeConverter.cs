using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Overlay;

namespace PPTAssistant.Session.WriteBack;

public static class ScreenStrokeConverter
{
    /// <summary>
    /// 换算一笔。返回 null 表示没有可落盘的点。
    /// </summary>
    /// <param name="geometry">当前视口与页面的映射（落盘时必须与书写时同一个）。</param>
    /// <param name="points">覆盖层给的采样点（窗口 DIP，含压力与时间）。</param>
    /// <param name="penWidthDips">屏幕上的笔宽（DIP，即 <c>StrokeThickness</c>）。</param>
    /// <param name="color">笔色。</param>
    public static InkStroke? Convert(
        SlideShowGeometry geometry,
        IReadOnlyList<StrokePoint> points,
        double penWidthDips,
        InkColor color)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return null;
        }

        var converted = new List<InkPoint>(points.Count);
        foreach (var point in points)
        {
            var slide = geometry.ScreenToSlide(point.Position);
            converted.Add(new InkPoint(slide.X, slide.Y, point.Pressure, point.TimeMilliseconds));
        }

        return new InkStroke(converted, geometry.DipsToSlideLength(penWidthDips), color);
    }
}
