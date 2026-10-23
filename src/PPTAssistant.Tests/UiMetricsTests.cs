using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class UiMetricsTests
{
    [Theory]
    [InlineData(double.NaN, UiTier.Compact)]
    [InlineData(-1920, UiTier.Compact)]   // 多屏时左上原点是负的
    [InlineData(0, UiTier.Compact)]
    [InlineData(1366, UiTier.Compact)]
    [InlineData(1439, UiTier.Compact)]
    [InlineData(1439.99, UiTier.Compact)]
    [InlineData(1440, UiTier.Standard)]   
    [InlineData(1920, UiTier.Standard)]
    [InlineData(3799, UiTier.Standard)]
    [InlineData(3799.99, UiTier.Standard)]
    [InlineData(3800, UiTier.Large)]      // 边界：MinWindowWidth="3800"
    [InlineData(3840, UiTier.Large)]
    public void TierFollowsTheSourceBreakpoints(double width, UiTier expected)
        => Assert.Equal(expected, UiMetrics.TierFor(width));

    [Fact]
    public void ThereAreExactlyThreeTiers()
        => Assert.Equal(3, Enum.GetValues<UiTier>().Length);

    [Theory]
    [InlineData(UiTier.Compact, 30, 3, 8, 2)]      // <1440 档
    [InlineData(UiTier.Standard, 50, 6, 11, 4)]    
    [InlineData(UiTier.Large, 100, 12, 25, 8)]     // ≥3800 档
    public void CoreTableMatchesTheEvidence(UiTier tier, double buttonSize, double cornerRadius, double iconPadding, double gap)
    {
        var metrics = UiMetrics.ForTier(tier);

        Assert.Equal(tier, metrics.Tier);
        Assert.Equal(buttonSize, metrics.ButtonSize, 6);
        Assert.Equal(cornerRadius, metrics.CornerRadius, 6);
        Assert.Equal(iconPadding, metrics.IconPadding, 6);
        Assert.Equal(gap, metrics.Gap, 6);
    }

    [Theory]
    [InlineData(UiTier.Compact, 20, 13, 6)]
    [InlineData(UiTier.Standard, 40, 19, 12)]
    [InlineData(UiTier.Large, 60, 29, 21)]
    public void ThumbnailBadgeMatchesTheEvidence(UiTier tier, double width, double height, double fontSize)
    {
        var metrics = UiMetrics.ForTier(tier);

        Assert.Equal(width, metrics.ThumbnailBadgeWidth, 6);
        Assert.Equal(height, metrics.ThumbnailBadgeHeight, 6);
        Assert.Equal(fontSize, metrics.ThumbnailBadgeFontSize, 6);
    }

    [Theory]
    [InlineData(UiTier.Compact, 90, 65)]
    [InlineData(UiTier.Standard, 160, 110)]
    [InlineData(UiTier.Large, 300, 210)]
    public void ThumbnailItemSizeMatchesTheEvidence(UiTier tier, double width, double height)
    {
        var metrics = UiMetrics.ForTier(tier);

        Assert.Equal(width, metrics.ThumbnailWidth, 6);
        Assert.Equal(height, metrics.ThumbnailHeight, 6);
    }

    [Theory]
    [InlineData(UiTier.Compact, 14)]
    [InlineData(UiTier.Standard, 28)]
    [InlineData(UiTier.Large, 50)]
    public void IconSizeIsButtonMinusPadding(UiTier tier, double expected)
    {
        var metrics = UiMetrics.ForTier(tier);

        Assert.Equal(expected, metrics.IconSize, 6);
        Assert.Equal(expected / UiMetrics.IconDesignBox, metrics.IconScale, 6);
    }

    /// <summary>圆角/内边距/空隙必须随档位单调不降（否则「大屏档看起来更紧凑」的怪现象）。</summary>
    [Fact]
    public void MetricsGrowWithTheTier()
    {
        var tiers = new[] { UiMetrics.Compact, UiMetrics.Standard, UiMetrics.Large };

        for (var i = 1; i < tiers.Length; i++)
        {
            Assert.True(tiers[i].ButtonSize > tiers[i - 1].ButtonSize);
            Assert.True(tiers[i].CornerRadius > tiers[i - 1].CornerRadius);
            Assert.True(tiers[i].IconPadding > tiers[i - 1].IconPadding);
            Assert.True(tiers[i].Gap > tiers[i - 1].Gap);
            Assert.True(tiers[i].PageFontSize > tiers[i - 1].PageFontSize);
            Assert.True(UiMetrics.CornerBarWidth(tiers[i]) > UiMetrics.CornerBarWidth(tiers[i - 1]));
        }
    }

    /// <summary>
    /// 中央条宽度 = 各胶囊内按钮宽之和 + 每个胶囊 2px 描边 + 胶囊间空隙。
    /// </summary>
    [Fact]
    public void RowWidthAddsBordersAndGaps()
    {
        var standard = UiMetrics.Standard;

        // 我们的中央条分四组：书写 3 + 放大镜 1 + 外观 2 + 退出 1。
        var width = UiMetrics.RowWidth(standard, [3, 1, 2, 1]);

        Assert.Equal((7 * 50) + (4 * 2) + (3 * 4), width, 6);
        Assert.Equal(370, width, 6);

        // 只有一个胶囊时没有空隙、只有自身描边。
        Assert.Equal(50 + 2, UiMetrics.RowWidth(standard, [1]), 6);

        // 空行宽为零（构造期还没填按钮时不能抛，也不能算出负数）。
        Assert.Equal(0, UiMetrics.RowWidth(standard, []), 6);
    }

    [Fact]
    public void CornerBarWidthIsTwoButtonsPlusThePageButtonAndGaps()
    {
        Assert.Equal((2 * 30) + 48 + (2 * 2), UiMetrics.CornerBarWidth(UiMetrics.Compact), 6);
        Assert.Equal((2 * 50) + 84 + (2 * 4), UiMetrics.CornerBarWidth(UiMetrics.Standard), 6);
        Assert.Equal((2 * 100) + 168 + (2 * 8), UiMetrics.CornerBarWidth(UiMetrics.Large), 6);

        Assert.Equal(50, UiMetrics.CornerBarHeight(UiMetrics.Standard), 6);
    }

    /// <summary>
    /// 最窄的常见课室屏（1366×768）上，新度量给出的三块区域仍互不重叠且在屏内。
    /// </summary>
    /// <remarks>页码嵌进中间按钮后角区变窄，这条断言是 K3（挖洞同源）的算术前提。</remarks>
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void NewMetricsKeepTheThreeRegionsDisjointOnRealScreens(double width, double height)
    {
        var screen = new DipRect(0, 0, width, height);
        var metrics = UiMetrics.For(width);
        var centerWidth = UiMetrics.RowWidth(metrics, [3, 1, 2, 1]);
        var cornerWidth = UiMetrics.CornerBarWidth(metrics);
        var cornerHeight = UiMetrics.CornerBarHeight(metrics);

        var plan = ToolbarLayout.Plan(screen, centerWidth, cornerHeight, cornerWidth, cornerHeight);

        Assert.True(ToolbarLayout.AreDisjoint(plan),
            $"三块区域重叠了：中央={plan.CenterBar} 左角={plan.LeftCorner} 右角={plan.RightCorner}（屏 {screen}）");
        Assert.True(plan.LeftCorner.X >= screen.X - 0.001);
        Assert.True(plan.RightCorner.Right <= screen.Right + 0.001);
        Assert.True(plan.CenterBar.Y >= screen.Y - 0.001);
    }
}
