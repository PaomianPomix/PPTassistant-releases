using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Overlay;
using PPTAssistant.Session.WriteBack;

namespace PPTAssistant.Tests;

public class ScreenStrokeConverterTests
{
    /// <summary>造一笔：从 (x,y) 水平走 length 个 DIP。</summary>
    private static List<StrokePoint> HorizontalStroke(double x, double y, double length, int count = 3)
    {
        var points = new List<StrokePoint>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = length * i / (count - 1);
            points.Add(new StrokePoint(new DipPoint(x + offset, y), 0.5f, InputDeviceKind.Mouse, i * 10));
        }

        return points;
    }

    [Fact]
    public void PenWidthFollowsTheScreenScaleNotAPhysicalConstant()
    {
        // 本机的真实情形：2560×1440 DIP 的屏，960×540 pt 的 16:9 页面 → 缩放 2.667 DIP/pt。
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 2560, 1440), SlideSize.Widescreen);

        var stroke = ScreenStrokeConverter.Convert(geometry, HorizontalStroke(100, 100, 500), 8.0, InkColor.Default);

        Assert.NotNull(stroke);
        // 8 DIP ÷ 2.667 = 3 pt —— 这才是屏幕上看起来的那个粗细。
        Assert.Equal(3.0, stroke!.WidthPoints, 3);
        // 回归锁：绝不能再退回那个物理换算（8 × 96/72 = 10.67）。
        Assert.NotEqual(8.0 * 96.0 / 72.0, stroke.WidthPoints, 2);
    }

    [Fact]
    public void SameInkOnADifferentResolutionScreenMatchesThatScreen()
    {
        // 4K 屏（3840 DIP 宽）显示同一个页面 → 缩放 4 DIP/pt。
        var large = new SlideShowGeometry(new DipRect(0, 0, 3840, 2160), SlideSize.Widescreen);
        // 1080p 屏（1920 DIP 宽）→ 缩放 2 DIP/pt。
        var small = new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), SlideSize.Widescreen);

        // 同一个「屏幕上 8 DIP 粗」的笔，在不同屏幕上换算出的幻灯片宽度不同——
        // 这不是 bug，而是"所见即所得"的必然：各屏的观看比例本来就不同。
        var onLarge = ScreenStrokeConverter.Convert(large, HorizontalStroke(100, 100, 300), 8.0, InkColor.Default)!;
        var onSmall = ScreenStrokeConverter.Convert(small, HorizontalStroke(100, 100, 300), 8.0, InkColor.Default)!;

        Assert.Equal(2.0, onLarge.WidthPoints, 3);
        Assert.Equal(4.0, onSmall.WidthPoints, 3);

        // 关键不变量：**屏幕上看到的粗细与页面宽度的比例**一致。
        Assert.Equal(8.0 / 3840, onLarge.WidthPoints / large.Slide.Width, 6);
        Assert.Equal(8.0 / 1920, onSmall.WidthPoints / small.Slide.Width, 6);
    }

    [Fact]
    public void LetterboxDoesNotChangeTheWidth()
    {
        // 4:3 页面放 16:9 屏 → 缩放取较小者，内容区居中留黑边。
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 2560, 1440), SlideSize.Standard);

        Assert.True(geometry.HasLetterbox);
        var stroke = ScreenStrokeConverter.Convert(geometry, HorizontalStroke(1280, 720, 400), 12.0, InkColor.Default)!;

        // 黑边只影响位置，不影响缩放比例（等比缩放）。
        Assert.Equal(12.0 / geometry.Scale, stroke.WidthPoints, 6);
    }

    [Fact]
    public void PositionsAreMappedWithTheSameGeometry()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 2560, 1440), SlideSize.Widescreen);
        var points = new List<StrokePoint>
        {
            new(new DipPoint(600, 600), 0.25f, InputDeviceKind.Pen, 0),
            new(new DipPoint(1280, 720), 1.0f, InputDeviceKind.Pen, 40),
        };

        var stroke = ScreenStrokeConverter.Convert(geometry, points, 8.0, new InkColor(1, 2, 3))!;

        // 位置照旧：屏幕 (600,600) → 幻灯片 (225,225)；中心点 (1280,720) → (480,270)。
        Assert.Equal(225.0, stroke.Points[0].X, 3);
        Assert.Equal(225.0, stroke.Points[0].Y, 3);
        Assert.Equal(480.0, stroke.Points[1].X, 3);
        Assert.Equal(270.0, stroke.Points[1].Y, 3);

        // 压力与时间要原样带过去（InkML 的 F / T 通道靠它们）。
        Assert.Equal(0.25f, stroke.Points[0].Pressure, 3);
        Assert.Equal(40, stroke.Points[1].T);
        Assert.Equal(new InkColor(1, 2, 3), stroke.Color);
    }

    [Fact]
    public void EmptyInputYieldsNoStroke()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), SlideSize.Widescreen);

        Assert.Null(ScreenStrokeConverter.Convert(geometry, [], 8.0, InkColor.Default));
    }

    [Fact]
    public void WrittenInkWidthSurvivesTheInkMlRoundTrip()
    {
        // 端到端数量级：8 DIP 的一笔 → InkML 的 brushProperty width（cm）应当约 0.106 cm（= 3 pt）。
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 2560, 1440), SlideSize.Widescreen);
        var stroke = ScreenStrokeConverter.Convert(geometry, HorizontalStroke(100, 100, 400), 8.0, InkColor.Default)!;
        var drawing = new InkDrawing(geometry.Slide, [stroke]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);

        // 3 pt = 3 × 2.54/72 cm = 0.1058 cm
        Assert.Contains("value=\"0.10583\"", document.Xml);
        // 而那个错误公式会写 10.67 pt = 0.376 cm（用户看到的"变粗"就是这个）。
        Assert.DoesNotContain("value=\"0.37639\"", document.Xml);

        var readBack = InkMlReader.Read(document.Xml, document.Bounds);
        Assert.Equal(3.0, readBack.Drawing.Strokes[0].WidthPoints, 2);
    }
}
