using System.Text.Json;
using PPTAssistant.App;

namespace PPTAssistant.Tests;

public sealed class UiScaleConfigTests
{
    [Fact]
    public void ExistingConfigWithoutScaleKeepsAutomaticSize()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{\"PenWidthDips\":8}")!;

        Assert.Equal(1, config.SanitizedUiScale);
        Assert.Equal(8, config.ToSessionOptions().PenWidthDips);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(2)]
    public void ScaleSurvivesJsonRoundTripWithoutChangingInk(double scale)
    {
        var config = new AppConfig { UiScale = scale, PenWidthDips = 8, PenColorHex = "#0085CD" };

        var json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.Equal(scale, restored.SanitizedUiScale);
        Assert.Equal(8, restored.ToSessionOptions().PenWidthDips);
        Assert.Equal("#0085CD", restored.ToSessionOptions().PenColor.ToHex());
        Assert.DoesNotContain("SanitizedUiScale", json);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(50)]
    public void InvalidStoredScaleFallsBackWithoutDiscardingOtherSettings(double scale)
    {
        var json = JsonSerializer.Serialize(new AppConfig { UiScale = scale, PenWidthDips = 12 });
        var config = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.Equal(1, config.SanitizedUiScale);
        Assert.Equal(12, config.SanitizedPenWidth);
    }
}
