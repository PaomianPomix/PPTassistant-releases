using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class SlideShowGeometryTests
{
    /// <summary>16:9 幻灯片铺在 16:9 屏上：无黑边，缩放 = 视口宽 / 页宽。</summary>
    [Fact]
    public void WidescreenSlideOnWidescreenViewportHasNoLetterbox()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), SlideSize.Widescreen);

        Assert.False(geometry.HasLetterbox);
        Assert.Equal(2.0, geometry.Scale, 6);
        Assert.Equal(new DipRect(0, 0, 1920, 1080), geometry.ContentBounds);
        Assert.Equal(0, geometry.HorizontalLetterbox, 6);
        Assert.Equal(0, geometry.VerticalLetterbox, 6);
    }

    /// <summary>4:3 幻灯片铺在 16:9 屏上：左右留黑边，内容区 1440×1080（比例 4:3）。</summary>
    [Fact]
    public void StandardSlideOnWidescreenViewportLetterboxesHorizontally()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), SlideSize.Standard);

        Assert.True(geometry.HasLetterbox);
        Assert.Equal(2.0, geometry.Scale, 6); // min(1920/720, 1080/540)
        Assert.Equal(1440, geometry.ContentBounds.Width, 6);
        Assert.Equal(1080, geometry.ContentBounds.Height, 6);
        Assert.Equal(240, geometry.HorizontalLetterbox, 6); // (1920-1440)/2
        Assert.Equal(0, geometry.VerticalLetterbox, 6);
    }

    /// <summary>视口非原点（多屏、负坐标）时内容区随之偏移，黑边仍对称。</summary>
    [Fact]
    public void ViewportOffsetIsPropagatedToContentBounds()
    {
        var geometry = new SlideShowGeometry(new DipRect(-1920, 0, 1920, 1080), SlideSize.Standard);

        Assert.Equal(-1920 + 240, geometry.ContentBounds.X, 6);
        Assert.Equal(240, geometry.HorizontalLetterbox, 6);
        Assert.Equal(1440, geometry.ContentBounds.Width, 6);
    }

    /// <summary>幻灯片坐标 → 屏幕坐标 → 幻灯片坐标 必须回到原点（墨迹落位的往返一致性）。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(480, 270)]
    [InlineData(960, 540)]
    [InlineData(123.5, 456.25)]
    public void SlideToScreenAndBackIsLossless(double x, double y)
    {
        var geometry = new SlideShowGeometry(new DipRect(100, 50, 1600, 900), SlideSize.Widescreen);
        var source = new SlidePoint(x, y);

        var roundTrip = geometry.ScreenToSlide(geometry.SlideToScreen(source));

        Assert.Equal(source.X, roundTrip.X, 6);
        Assert.Equal(source.Y, roundTrip.Y, 6);
    }

    /// <summary>开启裁剪后，落在黑边上的点被夹回内容区（黑边不该产生墨迹）。</summary>
    [Fact]
    public void ClipToContentClampsPointsOutsideContent()
    {
        var viewport = new DipRect(0, 0, 1920, 1080);
        var unclipped = new SlideShowGeometry(viewport, SlideSize.Standard);
        var clipped = new SlideShowGeometry(viewport, SlideSize.Standard, clipToContent: true);

        var onLetterbox = new DipPoint(10, 540); // 左侧黑边内

        Assert.False(unclipped.IsInsideContent(onLetterbox));
        Assert.True(unclipped.ScreenToSlide(onLetterbox).X < 0);

        // 夹回内容区后，黑边上的点落在幻灯片左边缘（pt 坐标 0），而不是负数。
        Assert.Equal(0, clipped.ScreenToSlide(onLetterbox).X, 6);
        Assert.True(clipped.IsInsideContent(clipped.SlideToScreen(clipped.ScreenToSlide(onLetterbox))));
    }

    /// <summary>笔宽换算：DIP ↔ 幻灯片点长度互为逆运算。</summary>
    [Fact]
    public void LengthConversionIsInvertible()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), SlideSize.Widescreen);

        Assert.Equal(3.0, geometry.DipsToSlideLength(6.0), 6);
        Assert.Equal(6.0, geometry.SlideLengthToDips(3.0), 6);
    }

    /// <summary>非法尺寸要尽早抛，而不是让 0 缩放渗到整条链路（除零会在几层之后才爆）。</summary>
    [Fact]
    public void InvalidInputsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlideShowGeometry(new DipRect(0, 0, 1920, 1080), new SlideSize(0, 540)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlideShowGeometry(new DipRect(0, 0, 0, 1080), SlideSize.Widescreen));
    }
}

/// <summary><see cref="DipRect"/> 的基本几何（被映射与挑屏共同依赖）。</summary>
public class DipRectTests
{
    [Fact]
    public void IntersectComputesOverlap()
    {
        var a = new DipRect(0, 0, 100, 100);
        var b = new DipRect(50, 50, 100, 100);

        var intersection = a.Intersect(b);

        Assert.Equal(new DipRect(50, 50, 50, 50), intersection);
        Assert.Equal(2500, intersection.Area, 6);
    }

    [Fact]
    public void IntersectOfDisjointRectsIsEmpty()
    {
        var intersection = new DipRect(0, 0, 10, 10).Intersect(new DipRect(100, 100, 10, 10));

        Assert.Equal(0, intersection.Area, 6);
    }

    [Fact]
    public void ContainsIncludesEdges()
    {
        var rect = new DipRect(10, 20, 100, 200);

        Assert.True(rect.Contains(new DipPoint(10, 20)));
        Assert.True(rect.Contains(new DipPoint(110, 220)));
        Assert.False(rect.Contains(new DipPoint(110.5, 220)));
    }

    [Fact]
    public void FromLtrbMatchesWidthHeightConstructor()
    {
        Assert.Equal(new DipRect(5, 6, 7, 8), DipRect.FromLtrb(5, 6, 12, 14));
    }
}

/// <summary>点 / DIP / 物理像素换算（DPI 缩放的坑集中在这里）。</summary>
public class UnitConverterTests
{
    [Fact]
    public void PointToDipUsesFourThirdsAndIgnoresDisplayScale()
    {
        // 1 pt = 1/72 in，1 DIP = 1/96 in ⇒ ×4/3。这一步**不能**乘显示缩放：
        // WPF 的 DIP 已经吸收过一次缩放，再乘就会出现「坐标随 DPI 漂移」。
        Assert.Equal(96, UnitConverter.PointsToDips(72), 6);
        Assert.Equal(72, UnitConverter.DipsToPoints(96), 6);
    }

    [Fact]
    public void PixelsAndDipsConvertWithDisplayScale()
    {
        Assert.Equal(2880, UnitConverter.DipsToPixels(1920, 1.5), 6);
        Assert.Equal(1920, UnitConverter.PixelsToDips(2880, 1.5), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NormalizeDisplayScaleFallsBackToOne(double invalid)
        => Assert.Equal(1.0, UnitConverter.NormalizeDisplayScale(invalid), 6);
}

public class ScreenSelectorTests
{
    private static readonly IReadOnlyList<ScreenInfo> TwoScreens =
    [
        new ScreenInfo(@"\\.\DISPLAY1", new DipRect(0, 0, 1920, 1080), isPrimary: true, displayScale: 1.0),
        new ScreenInfo(@"\\.\DISPLAY2", new DipRect(1920, 0, 1920, 1080), isPrimary: false, displayScale: 1.0),
    ];

    /// <summary>宿主报告放映窗口在副屏上（笔记本 + 投影仪的典型教室布局）——必须挑副屏。</summary>
    [Fact]
    public void ComRectPicksSecondaryScreenWhenShowIsThere()
    {
        var picked = ScreenSelector.PickBestForComRect(TwoScreens, new DipRect(1920, 0, 1920, 1080));

        Assert.NotNull(picked);
        Assert.Equal(@"\\.\DISPLAY2", picked!.Value.DeviceName);
    }

    /// <summary>窗口横跨两块屏时按归一化覆盖率取占得多的那块。</summary>
    [Fact]
    public void ComRectUsesNormalizedCoverageWhenWindowSpansScreens()
    {
        // 窗口从 x=1700 延伸到 3840：主屏装下 220px，副屏装下 1920px。
        var picked = ScreenSelector.PickBestForComRect(TwoScreens, new DipRect(1700, 0, 2140, 1080));

        Assert.Equal(@"\\.\DISPLAY2", picked!.Value.DeviceName);
    }

    /// <summary>单屏时不猜：直接返回那唯一一块。</summary>
    [Fact]
    public void SingleScreenShortCircuits()
    {
        IReadOnlyList<ScreenInfo> screens =
        [
            new ScreenInfo(@"\\.\DISPLAY1", new DipRect(0, 0, 2560, 1440), isPrimary: true, displayScale: 1.0),
        ];

        Assert.Equal(@"\\.\DISPLAY1", ScreenSelector.PickBestForComRect(screens, new DipRect(0, 0, 1, 1))!.Value.DeviceName);
        Assert.Equal(@"\\.\DISPLAY1", ScreenSelector.PickBestForComRect(screens, new DipRect(0, 0, 0, 0))!.Value.DeviceName);
    }

    /// <summary>退化矩形（读不到窗口尺寸）→ 主屏优先，绝不返回 null。</summary>
    [Fact]
    public void DegenerateComRectFallsBackToPrimary()
    {
        var picked = ScreenSelector.PickBestForComRect(TwoScreens, new DipRect(0, 0, 0, 0));

        Assert.NotNull(picked);
        Assert.True(picked!.Value.IsPrimary);
    }

    [Fact]
    public void EmptyScreenListYieldsNull()
    {
        Assert.Null(ScreenSelector.PickBestForComRect([], new DipRect(0, 0, 100, 100)));
        Assert.Null(ScreenSelector.PickBest([], new DipRect(0, 0, 100, 100)));
    }

    [Fact]
    public void PicksPrimaryScreenByDeviceName()
    {
        Assert.Equal(@"\\.\DISPLAY1", ScreenSelector.PickByDeviceName(TwoScreens, @"\\.\display1")!.Value.DeviceName);
        Assert.Null(ScreenSelector.PickByDeviceName(TwoScreens, null));
    }

    /// <summary>物理像素入口（手里有精确窗口矩形时用）：混合 DPI 也判别正确。</summary>
    [Fact]
    public void PhysicalPixelEntryDiscriminatesMixedDpiScreens()
    {
        IReadOnlyList<ScreenInfo> screens =
        [
            // 主屏 100%：物理 1920 宽。
            new ScreenInfo(@"\\.\DISPLAY1", new DipRect(0, 0, 1920, 1080), isPrimary: true, displayScale: 1.0),

            // 副屏 200%：物理 3840 宽，DIP 只有 1920。
            new ScreenInfo(@"\\.\DISPLAY2", new DipRect(1920, 0, 1920, 1080), isPrimary: false, displayScale: 2.0),
        ];

        // GetWindowRect 给的是物理像素：窗口落在第二块屏的物理区间（3840–7680）里。
        var picked = ScreenSelector.PickBest(screens, new DipRect(3900, 0, 3840, 2160));

        Assert.Equal(@"\\.\DISPLAY2", picked!.Value.DeviceName);
    }

    /// <summary>物理像素入口完全不相交时按物理中心就近。</summary>
    [Fact]
    public void PhysicalPixelEntryFallsBackToNearest()
    {
        var picked = ScreenSelector.PickBest(TwoScreens, new DipRect(4000, 4000, 0, 0));

        Assert.NotNull(picked);
        Assert.Equal(@"\\.\DISPLAY2", picked!.Value.DeviceName);
    }
}
