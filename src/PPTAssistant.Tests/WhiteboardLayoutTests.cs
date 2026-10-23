using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class WhiteboardLayoutTests
{
    [Fact]
    public void SmallScreenKeepsTouchTargetsUsableWithoutDoublingTheDock()
    {
        var scale = WhiteboardLayout.ToolbarScale(640, 480, 640, 2);
        Assert.Equal(44, WhiteboardLayout.MinimumTouchTarget * scale);
        Assert.True(480 - WhiteboardLayout.ToolbarBaseHeight * scale >= 300);
    }

    [Theory]
    [InlineData(3840, 2160, 3840, 1)]
    [InlineData(2560, 1440, 3840, 1.5)]
    [InlineData(1920, 1080, 3840, 2)]
    [InlineData(2160, 3840, 2160, 1)]
    public void FourKHasConsistentPhysicalTouchTargetsAcrossDpiAndOrientation(
        double widthDips, double heightDips, double physicalWidth, double dpi)
    {
        var scale = WhiteboardLayout.ToolbarScale(widthDips, heightDips, physicalWidth, 1);
        Assert.Equal(88, WhiteboardLayout.MinimumTouchTarget * scale * dpi, 8);
        Assert.True(scale >= 1);
    }

    [Theory]
    [InlineData(640, 480, .75)]
    [InlineData(640, 480, 2)]
    [InlineData(768, 1024, 2)]
    [InlineData(1280, 800, 1.5)]
    [InlineData(1920, 1080, 1)]
    [InlineData(3840, 2160, 2)]
    public void ScreenFitPreservesTouchSizeAndWritingSpace(double width, double height, double requested)
    {
        var scale = WhiteboardLayout.ToolbarScale(width, height, width, requested);
        Assert.True(scale >= 1);
        Assert.True(height - WhiteboardLayout.ToolbarBaseHeight * scale >= WhiteboardLayout.MinimumCanvasHeight - 0.001);
    }

    [Theory]
    [InlineData(1920, 920)]
    [InlineData(3840, 1840)]
    [InlineData(1280, 640)]
    [InlineData(900, 1440)]
    [InlineData(640, 180)]
    public void ViewportFillsAvailableAreaWithoutCroppingOrDistortingStoredPage(double width, double height)
    {
        var fit = WhiteboardLayout.Fit(1600, 900, width, height);
        Assert.Equal(width, fit.LogicalWidth * fit.Scale, 8);
        Assert.Equal(height, fit.LogicalHeight * fit.Scale, 8);
        Assert.True(fit.LogicalWidth >= 1600 - .001 && fit.LogicalHeight >= 900 - .001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidViewportsAreRejectedBeforeTheyReachTransforms(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WhiteboardLayout.Fit(value, 900, 1920, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => WhiteboardLayout.Fit(1600, 900, value, 1080));
    }
}
