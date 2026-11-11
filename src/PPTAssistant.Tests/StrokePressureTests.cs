using PPTAssistant.Coords;
using PPTAssistant.Overlay;

namespace PPTAssistant.Tests;

public class StrokePressureTests
{
    [Fact]
    public void PenEndpointsAtZeroPressureDoNotBulge()
    {
        float[] pressures = [0f, 0.05f, 0.2f, 0.05f, 0f];
        var widths = pressures.Select(pressure =>
        {
            var point = new StrokePoint(new DipPoint(0, 0), pressure, InputDeviceKind.Pen);
            return PenWidthModel.WidthAt(6, point.Pressure, point.HasPressure);
        }).ToArray();

        Assert.Equal(3.0, widths[0], 6);
        Assert.Equal(3.0, widths[^1], 6);
        Assert.True(widths[0] <= widths[1]);
        Assert.True(widths[^1] <= widths[^2]);
    }

    [Theory]
    [InlineData(InputDeviceKind.Touch, 0f)]
    [InlineData(InputDeviceKind.Touch, 0.5f)]
    [InlineData(InputDeviceKind.Mouse, 0f)]
    [InlineData(InputDeviceKind.Unknown, 0f)]
    public void NonPenInputKeepsConstantWidth(InputDeviceKind device, float pressure)
    {
        var point = new StrokePoint(new DipPoint(0, 0), pressure, device);
        Assert.False(point.HasPressure);
        Assert.Equal(6, PenWidthModel.WidthAt(6, point.Pressure, point.HasPressure));
    }
}
