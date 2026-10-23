using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class UiScalePolicyTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.749)]
    [InlineData(2.001)]
    public void InvalidStoredScaleFallsBackToDefault(double requested)
        => Assert.Equal(UiScalePolicy.Default, UiScalePolicy.Normalize(requested));

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.125)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ValidScaleIsPreservedWithoutRounding(double requested)
        => Assert.Equal(requested, UiScalePolicy.Normalize(requested));

    [Theory]
    [InlineData(1.5, 1200, 800, 400, 200, 1.5)]
    [InlineData(2.0, 600, 800, 400, 200, 1.5)]
    [InlineData(2.0, 1200, 300, 400, 200, 1.5)]
    [InlineData(0.75, 1200, 800, 400, 200, 0.75)]
    [InlineData(2.0, 200, 800, 400, 200, 0.5)]
    [InlineData(double.NaN, 1200, 800, 400, 200, 1.0)]
    public void FittingHonorsBothDimensionsAndDoesNotExceedRequestedScale(
        double requested, double availableWidth, double availableHeight,
        double contentWidth, double contentHeight, double expected)
    {
        var scale = UiScalePolicy.Fit(requested, availableWidth, availableHeight, contentWidth, contentHeight);

        Assert.Equal(expected, scale, 10);
        Assert.True(contentWidth * scale <= availableWidth);
        Assert.True(contentHeight * scale <= availableHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void UnmeasuredOrInvalidDimensionsKeepTheNormalizedRequest(double invalid)
    {
        Assert.Equal(1.5, UiScalePolicy.Fit(1.5, invalid, 800, 400, 200));
        Assert.Equal(1.5, UiScalePolicy.Fit(1.5, 1200, invalid, 400, 200));
        Assert.Equal(1.5, UiScalePolicy.Fit(1.5, 1200, 800, invalid, 200));
        Assert.Equal(1.5, UiScalePolicy.Fit(1.5, 1200, 800, 400, invalid));
        Assert.Equal(UiScalePolicy.Default, UiScalePolicy.Fit(double.NaN, invalid, 800, 400, 200));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void FitRatioIsIndependentOfTheCoordinateUnits(double displayScale)
    {
        // Converting every length from DIP to physical pixels must not multiply the UI scale again.
        var inDips = UiScalePolicy.Fit(2, 900, 600, 600, 300);
        var inPixels = UiScalePolicy.Fit(2,
            900 * displayScale, 600 * displayScale,
            600 * displayScale, 300 * displayScale);

        Assert.Equal(inDips, inPixels, 10);
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(1024, 768)]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void CombinedToolbarFitKeepsBothCornersClearAndInsideAnOffsetScreen(double width, double height)
    {
        var screen = new DipRect(-width, -height, width, height);
        var metrics = UiMetrics.For(width);
        var centerWidth = UiMetrics.RowWidth(metrics, [3, 2, 2, 1]);
        var cornerWidth = UiMetrics.CornerBarWidth(metrics);
        var contentHeight = metrics.ButtonSize + (2 * UiMetrics.PanelBorderThickness);
        const double gap = 8;
        var scale = UiScalePolicy.Fit(UiScalePolicy.Maximum,
            width - (2 * ToolbarLayout.CornerMarginDips) - (2 * gap),
            height - ToolbarLayout.BottomMarginDips,
            centerWidth + (2 * cornerWidth), contentHeight);
        var plan = ToolbarLayout.Plan(screen,
            centerWidth * scale, contentHeight * scale,
            cornerWidth * scale, metrics.ButtonSize * scale);

        Assert.True(ToolbarLayout.AreDisjoint(plan));
        Assert.True(plan.CenterBar.X - plan.LeftCorner.Right >= gap - 0.001);
        Assert.True(plan.RightCorner.X - plan.CenterBar.Right >= gap - 0.001);
        foreach (var region in new[] { plan.CenterBar, plan.LeftCorner, plan.RightCorner })
        {
            Assert.True(region.X >= screen.X - 0.001);
            Assert.True(region.Y >= screen.Y - 0.001);
            Assert.True(region.Right <= screen.Right + 0.001);
            Assert.True(region.Bottom <= screen.Bottom + 0.001);
        }
    }

    [Fact]
    public void CentralToolbarRespectsTheScreenTopWhenTheBottomMarginDoesNotFit()
    {
        var screen = new DipRect(-800, -100, 800, 100);
        var plan = ToolbarLayout.Plan(screen, 300, 95, 100, 40);

        Assert.Equal(screen.Y, plan.CenterBar.Y);
        Assert.True(plan.CenterBar.Bottom <= screen.Bottom);
    }
}
