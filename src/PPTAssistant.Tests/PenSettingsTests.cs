using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Overlay;
using PPTAssistant.Session;
using PPTAssistant.Session.WriteBack;

namespace PPTAssistant.Tests;

public class PenWidthRangeTests
{
    [Fact]
    public void FrozenRangeIsTwoToFifty() 
    {
        Assert.Equal(2.0, PenWidthRange.MinDips, 6);
        Assert.Equal(50.0, PenWidthRange.MaxDips, 6);

        Assert.True(PenWidthRange.IsValid(PenWidthRange.MinDips));
        Assert.True(PenWidthRange.IsValid(PenWidthRange.MaxDips));
    }

    [Theory]
    [InlineData(1.9, false)]
    [InlineData(2.0, true)]
    [InlineData(25.0, true)]
    [InlineData(50.0, true)]
    [InlineData(50.1, false)]
    [InlineData(-3.0, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void ValidityFollowsTheRange(double value, bool expected)
        => Assert.Equal(expected, PenWidthRange.IsValid(value));

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(8.0, 8.0)]
    [InlineData(100.0, 50.0)]
    [InlineData(double.NaN, 6.0)]
    public void ClampPullsIntoRangeAndDefaultsOnNonFinite(double input, double expected)
        => Assert.Equal(expected, PenWidthRange.Clamp(input), 6);

    [Theory]
    [InlineData(8.24, 8.0)]
    [InlineData(8.25, 8.5)]
    [InlineData(8.749, 8.5)]
    [InlineData(1.0, 2.0)]
    [InlineData(99.0, 50.0)]
    public void SnapRoundsToHalfStepThenClamps(double input, double expected)
        => Assert.Equal(expected, PenWidthRange.Snap(input), 6);

    [Fact]
    public void SnapAlwaysLandsOnAStepBoundary()
    {
        for (var raw = 0.0; raw <= 60.0; raw += 0.13)
        {
            var snapped = PenWidthRange.Snap(raw);
            Assert.True(PenWidthRange.IsValid(snapped));
            Assert.Equal(0.0, Math.Round(snapped / PenWidthRange.StepDips) * PenWidthRange.StepDips - snapped, 6);
        }
    }

    [Fact]
    public void FormatUsesOneDecimal() => Assert.Equal("6.0", PenWidthRange.Format(6.0));
}

public class ToolbarLayoutTests
{
    private static readonly DipRect Screen1080 = new(0, 0, 1920, 1080);
    private static readonly DipRect Screen4K = new(0, 0, 3840, 2160);

    /// <summary>多屏时非主屏的原点是负的（<see cref="DipRect"/> 的约定）——布局不能假设原点为 0。</summary>
    private static readonly DipRect ScreenLeftOfPrimary = new(-1920, 0, 1920, 1080);

    [Theory]
    [MemberData(nameof(Screens))]
    public void EveryRegionStaysInsideItsScreen(DipRect screen)
    {
        var plan = ToolbarLayout.Plan(screen, centerWidth: 700, centerHeight: 76, cornerWidth: 300, cornerHeight: 76);

        AssertInside(plan.CenterBar, screen);
        AssertInside(plan.LeftCorner, screen);
        AssertInside(plan.RightCorner, screen);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheThreeRegionsDoNotOverlap(DipRect screen)
    {
        // 1920 宽：300 + 700 + 300 + 两侧留边 = 1328 < 1920，三块应当完全分开。
        var plan = ToolbarLayout.Plan(screen, 700, 76, 300, 76);

        Assert.True(ToolbarLayout.AreDisjoint(plan),
            $"三块区域重叠了：中央={plan.CenterBar} 左角={plan.LeftCorner} 右角={plan.RightCorner}");
    }

    [Fact]
    public void CenterBarIsHorizontallyCenteredAndBottomAligned()
    {
        var plan = ToolbarLayout.Plan(Screen4K, 700, 76, 300, 76);

        Assert.Equal(Screen4K.CenterX, plan.CenterBar.CenterX, 3);

        Assert.Equal(plan.CenterBar.Bottom, plan.LeftCorner.Bottom, 3);
        Assert.Equal(plan.CenterBar.Bottom, plan.RightCorner.Bottom, 3);
        Assert.Equal(Screen4K.Bottom - ToolbarLayout.BottomMarginDips, plan.CenterBar.Bottom, 3);
    }

    [Fact]
    public void CornersAreSymmetricAboutTheScreen()
    {
        var plan = ToolbarLayout.Plan(Screen4K, 700, 76, 300, 76);

        // 左右角区到各自屏幕边的距离一致（讲台两侧手感的对称性）。
        var leftGap = plan.LeftCorner.X - Screen4K.X;
        var rightGap = Screen4K.Right - plan.RightCorner.Right;
        Assert.Equal(leftGap, rightGap, 3);
    }

    [Fact]
    public void CornersFollowTheScreenOriginOnASecondaryDisplay()
    {
        var plan = ToolbarLayout.Plan(ScreenLeftOfPrimary, 700, 76, 300, 76);

        Assert.True(plan.LeftCorner.X >= ScreenLeftOfPrimary.X);
        Assert.True(plan.RightCorner.Right <= ScreenLeftOfPrimary.Right);
    }

    [Fact]
    public void PageLabelFormatsIndexAndCount()
    {
        Assert.Equal("3 / 12", PageLabel.Format(3, 12));
        Assert.Equal("1 / 1", PageLabel.Format(1, 1));
        // 拿不到时就显示 ?，绝不暴露 0 / -1 这类内部值。
        Assert.Equal("? / 12", PageLabel.Format(0, 12));
        Assert.Equal("3 / ?", PageLabel.Format(3, 0));
        Assert.Equal("? / ?", PageLabel.Format(0, 0));
    }

    public static TheoryData<DipRect> Screens => new() { Screen1080, Screen4K, ScreenLeftOfPrimary };

    private static void AssertInside(DipRect region, DipRect screen)
    {
        Assert.True(region.X >= screen.X - 0.001, $"{region} 左越界（屏 {screen}）");
        Assert.True(region.Y >= screen.Y - 0.001, $"{region} 上越界（屏 {screen}）");
        Assert.True(region.Right <= screen.Right + 0.001, $"{region} 右越界（屏 {screen}）");
        Assert.True(region.Bottom <= screen.Bottom + 0.001, $"{region} 下越界（屏 {screen}）");
    }
}

public class InkPaletteTests
{
    [Fact]
    public void PresetsContainTheThreeLegacyColors()
    {
        foreach (var legacy in InkPalette.LegacyThreeColors)
        {
            Assert.Contains(legacy, InkPalette.Presets);
        }

        Assert.Equal(8, InkPalette.Presets.Count);
        Assert.Equal(5, InkPalette.MaxRecent);
    }

    [Fact]
    public void AddRecentPromotesTheNewColorToFront()
    {
        IReadOnlyList<InkColor> recent = [new(1, 2, 3), new(4, 5, 6)];

        var result = InkPalette.AddRecent(recent, new(9, 9, 9));

        Assert.Equal(new InkColor(9, 9, 9), result[0]);
        Assert.Equal(new InkColor(1, 2, 3), result[1]);
    }

    [Fact]
    public void AddRecentDeduplicatesAndKeepsCapacity()
    {
        IReadOnlyList<InkColor> recent = [new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4), new(5, 5, 5)];

        // 选中已有色：不重复、不增长，只是顶置。
        var promoted = InkPalette.AddRecent(recent, new(3, 3, 3));
        Assert.Equal(5, promoted.Count);
        Assert.Single(promoted, c => c == new InkColor(3, 3, 3));
        Assert.Equal(new InkColor(3, 3, 3), promoted[0]);

        // 选新色：超容量时挤掉最后一个。
        var added = InkPalette.AddRecent(recent, new(6, 6, 6));
        Assert.Equal(5, added.Count);
        Assert.DoesNotContain(new InkColor(5, 5, 5), added);
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(217, 27, 27)]
    [InlineData(0, 133, 205)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 128, 128)]
    [InlineData(0, 176, 80)]
    public void HsvRoundTripStaysWithinOneStep(byte r, byte g, byte b)
    {
        var original = new InkColor(r, g, b);

        var roundTripped = InkPalette.HsvToRgb(InkPalette.RgbToHsv(original));

        Assert.InRange(Math.Abs(roundTripped.R - r), 0, 1);
        Assert.InRange(Math.Abs(roundTripped.G - g), 0, 1);
        Assert.InRange(Math.Abs(roundTripped.B - b), 0, 1);
    }

    [Fact]
    public void RgbToHsvReportsExpectedHues()
    {
        Assert.Equal(0.0, InkPalette.RgbToHsv(new InkColor(255, 0, 0)).Hue, 3);
        Assert.Equal(120.0, InkPalette.RgbToHsv(new InkColor(0, 255, 0)).Hue, 3);
        Assert.Equal(240.0, InkPalette.RgbToHsv(new InkColor(0, 0, 255)).Hue, 3);
        // 灰度没有色相：定义为 0，避免除零。
        Assert.Equal(0.0, InkPalette.RgbToHsv(new InkColor(80, 80, 80)).Hue, 3);
        Assert.Equal(0.0, InkPalette.RgbToHsv(new InkColor(80, 80, 80)).Saturation, 3);
    }

    [Fact]
    public void HueWrapsAround()
    {
        var a = InkPalette.HsvToRgb(new HsvColor(0, 1, 1));
        var b = InkPalette.HsvToRgb(new HsvColor(360, 1, 1));
        var c = InkPalette.HsvToRgb(new HsvColor(-360 + 0, 1, 1));

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }
}

public class PenStyleStateTests
{
    [Fact]
    public void SnapshotIsNotAffectedByLaterWidthChanges()
    {
        var pen = new PenStyleState(6.0, new InkColor(255, 0, 0));

        var firstStroke = pen.Snapshot();       // 起笔（细）
        pen.SetWidth(30.0);                     // 放映中途调粗
        var secondStroke = pen.Snapshot();       // 下一笔（粗）

        // 「改宽前后的两笔各自落盘宽度正确」——正是这条不变量在保证。
        Assert.Equal(6.0, firstStroke.WidthDips, 6);
        Assert.Equal(30.0, secondStroke.WidthDips, 6);
    }

    [Fact]
    public void SnapshotCarriesColor()
    {
        var pen = new PenStyleState(6.0, new InkColor(255, 0, 0));
        var snapshot = pen.Snapshot();

        pen.SetColor(new InkColor(0, 133, 205));

        Assert.Equal(new InkColor(255, 0, 0), snapshot.Color);
        Assert.Equal(new InkColor(0, 133, 205), pen.Color);
    }

    [Fact]
    public void WidthIsClampedIntoTheSliderRange()
    {
        var pen = new PenStyleState(6.0, InkColor.Default);

        Assert.False(pen.SetWidth(6.0));       // 没变
        Assert.True(pen.SetWidth(999.0));
        Assert.Equal(PenWidthRange.MaxDips, pen.WidthDips, 6);
        Assert.True(pen.SetWidth(0.1));
        Assert.Equal(PenWidthRange.MinDips, pen.WidthDips, 6);
    }

    [Fact]
    public void SetColorPushesRecentAndDeduplicates()
    {
        var pen = new PenStyleState(6.0, InkColor.Default);

        pen.SetColor(new InkColor(1, 1, 1));
        pen.SetColor(new InkColor(2, 2, 2));
        pen.SetColor(new InkColor(1, 1, 1));

        Assert.Equal(new InkColor(1, 1, 1), pen.Recent[0]);
        Assert.Equal(2, pen.Recent.Count);
    }

    [Fact]
    public void InitialRecentIsNormalized()
    {
        IReadOnlyList<InkColor> raw = [new(1, 1, 1), new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4), new(5, 5, 5), new(6, 6, 6)];

        var pen = new PenStyleState(6.0, InkColor.Default, raw);

        Assert.Equal(InkPalette.MaxRecent, pen.Recent.Count);
        Assert.Equal(raw.Distinct().Take(InkPalette.MaxRecent).ToList(), pen.Recent);
    }

    [Fact]
    public void PerStrokeWidthsSurviveTheInkMlRoundTrip()
    {
        var geometry = new SlideShowGeometry(new DipRect(0, 0, 2560, 1440), SlideSize.Widescreen);
        var pen = new PenStyleState(6.0, InkColor.Default);

        var thin = pen.Snapshot();
        pen.SetWidth(24.0);
        var thick = pen.Snapshot();

        static List<StrokePoint> Line() =>
        [
            new(new DipPoint(100, 100), 0.5f, InputDeviceKind.Pen, 0),
            new(new DipPoint(160, 100), 0.5f, InputDeviceKind.Pen, 20),
        ];

        var thinStroke = ScreenStrokeConverter.Convert(geometry, Line(), thin.WidthDips, thin.Color)!;
        var thickStroke = ScreenStrokeConverter.Convert(geometry, Line(), thick.WidthDips, thick.Color)!;
        var document = InkMlWriter.Write(new InkDrawing(geometry.Slide, [thinStroke, thickStroke]), DateTime.UtcNow);

        var readBack = InkMlReader.Read(document.Xml, document.Bounds);

        // 6 DIP / 2.667 ≈ 2.25 pt；24 DIP / 2.667 ≈ 9 pt —— 两笔必须各自保持。
        Assert.Equal(2.25, readBack.Drawing.Strokes[0].WidthPoints, 2);
        Assert.Equal(9.0, readBack.Drawing.Strokes[1].WidthPoints, 2);
    }
}
