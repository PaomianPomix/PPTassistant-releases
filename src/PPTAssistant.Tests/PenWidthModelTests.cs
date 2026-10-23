using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class PenWidthModelTests
{
    /// <summary>中位压力 → 恰好基准宽度（与 InkML 的 brushWidth 语义对齐）。</summary>
    [Fact]
    public void NeutralPressureProducesBaseWidth()
    {
        Assert.Equal(8.0, PenWidthModel.WidthAt(8.0, 0.5f, hasPressure: true), 6);
    }

    /// <summary>压力两端：0 → 基准的 (1-gain)，1 → 基准的 (1+gain)。</summary>
    [Theory]
    [InlineData(0.0f, 0.5)]
    [InlineData(1.0f, 1.5)]
    [InlineData(0.75f, 1.25)]
    [InlineData(0.25f, 0.75)]
    public void PressureModulatesAroundBaseWidth(float pressure, double expectedFactor)
    {
        const double baseWidth = 10.0;
        Assert.Equal(baseWidth * expectedFactor, PenWidthModel.WidthAt(baseWidth, pressure, true), 6);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void NoPressureDataKeepsConstantWidth(float pressure)
    {
        Assert.Equal(6.0, PenWidthModel.WidthAt(6.0, pressure, hasPressure: false), 6);
    }

    /// <summary>压力极小时宽度被下限兜住（不能细到像断笔）。</summary>
    [Fact]
    public void WidthNeverFallsBelowMinimum()
    {
        Assert.Equal(PenWidthModel.MinWidthDips, PenWidthModel.WidthAt(2.0, 0f, true), 6);
        Assert.Equal(PenWidthModel.MinWidthDips, PenWidthModel.WidthAt(0.1, 0.5f, true), 6);
        Assert.Equal(PenWidthModel.MinWidthDips, PenWidthModel.WidthAt(-5, 0.5f, false), 6);
    }

    /// <summary>越界压力按 0–1 夹紧（设备驱动偶尔报超范围值，不能放大成异常粗细）。</summary>
    [Fact]
    public void OutOfRangePressureIsClamped()
    {
        Assert.Equal(PenWidthModel.WidthAt(10, 1.0f, true), PenWidthModel.WidthAt(10, 5f, true), 6);
        Assert.Equal(PenWidthModel.WidthAt(10, 0.0f, true), PenWidthModel.WidthAt(10, -3f, true), 6);
    }
}
