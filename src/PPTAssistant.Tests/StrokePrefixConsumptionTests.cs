using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class StrokePrefixConsumptionTests
{

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ShortStrokeStillConsumesSomethingWhileWriting(int rawCount)
    {
        var (from, to, count) = StrokePrefixConsumption.Plan(
            rawCount, 0, StrokePrefixConsumption.LiveTailPoints);

        Assert.True(count > 0, $"{rawCount} 个点的笔画在书写中就应当有可消费区间（否则画不出来）");
        Assert.Equal(0, from);
        Assert.True(to <= rawCount - 1);
        Assert.True(to >= from);
    }

    /// <summary>
    /// 短于尾巴所需（≤ 5 点 = 尾巴 4 + 1）时，正常路径只会消费 1 个点；
    /// 这一条**单独钉住**「全部原样消费」这个退化分支，因为它才是「点一下」的生路。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DegenerateShortStrokeConsumesEveryPoint(int rawCount)
    {
        var (from, to, count) = StrokePrefixConsumption.Plan(
            rawCount, 0, StrokePrefixConsumption.LiveTailPoints);

        Assert.Equal(rawCount, count);
        Assert.Equal(0, from);
        Assert.Equal(rawCount - 1, to);
    }

    /// <summary>收笔冲刷：无论多少点，剩余的点必须全部消费掉（否则最后几个点永远不显示）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(100)]
    [InlineData(3000)]
    public void FlushConsumesEveryRemainingPoint(int rawCount)
    {
        var (from, to, count) = StrokePrefixConsumption.Plan(
            rawCount, 0, StrokePrefixConsumption.FlushTailPoints);

        Assert.Equal(rawCount, count);
        Assert.Equal(0, from);
        Assert.Equal(rawCount - 1, to);
    }

    /// <summary>长笔在书写中要保住尾巴——保留 4 个点不消费，供 Catmull-Rom 往后看邻居。</summary>
    [Fact]
    public void LongStrokeKeepsTailWhileWriting()
    {
        var (_, to, count) = StrokePrefixConsumption.Plan(
            100, 0, StrokePrefixConsumption.LiveTailPoints);

        // 可消费 96 个点（下标 0..95），最后 4 个（96..99）留给下一轮。
        Assert.Equal(96, count);
        Assert.Equal(95, to);
    }

    /// <summary>不变量②：穷举「点数 × 已消费位置 × 尾巴」的组合，区间上界绝不越界。</summary>
    [Fact]
    public void PlanNeverExceedsRawRange()
    {
        for (var rawCount = 0; rawCount <= 40; rawCount++)
        {
            for (var consumed = 0; consumed <= rawCount + 2; consumed++)
            {
                foreach (var tail in new[] { 0, 1, 4, 8 })
                {
                    var (from, to, count) = StrokePrefixConsumption.Plan(rawCount, consumed, tail);

                    if (count == 0)
                    {
                        continue;
                    }

                    Assert.True(to <= rawCount - 1,
                        $"越界：rawCount={rawCount} consumed={consumed} tail={tail} → ({from},{to},{count})");
                    Assert.True(from >= 0, $"起点为负：rawCount={rawCount} consumed={consumed} tail={tail}");
                    Assert.True(from <= to, $"区间倒置：rawCount={rawCount} consumed={consumed} tail={tail}");
                    Assert.Equal(to - from + 1, count);
                    Assert.True(to >= consumed,
                        $"区间没覆盖新点：rawCount={rawCount} consumed={consumed} tail={tail} → ({from},{to})");
                }
            }
        }
    }

    /// <summary>空输入（还没采到点）必须返回空区间，渲染端不应有任何动作。</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 4)]
    [InlineData(5, 5, 4)]
    [InlineData(5, 9, 4)]
    public void NoWorkReturnsEmpty(int rawCount, int consumed, int tail)
        => Assert.Equal(0, StrokePrefixConsumption.Plan(rawCount, consumed, tail).Count);

    /// <summary>
    /// 首尾相接：反复调用 Plan（模拟逐点追加）不应漏点也不应重复，
    /// 直到收笔冲刷时消费总数恰好等于原始点数。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(37)]
    [InlineData(500)]
    public void IncrementalCallsCoverEveryPointExactlyOnce(int total)
    {
        var consumed = 0;
        for (var rawCount = 1; rawCount <= total; rawCount++)
        {
            var (_, to, count) = StrokePrefixConsumption.Plan(
                rawCount, consumed, StrokePrefixConsumption.LiveTailPoints);
            if (count > 0)
            {
                Assert.Equal(consumed, StrokePrefixConsumption.Plan(
                    rawCount, consumed, StrokePrefixConsumption.LiveTailPoints).From);
                consumed = to + 1;
            }
        }

        // 收笔冲刷：把剩下的全消费掉，总数必须刚好是 total。
        var (flushFrom, flushTo, flushCount) = StrokePrefixConsumption.Plan(
            total, consumed, StrokePrefixConsumption.FlushTailPoints);

        var totalConsumed = consumed + flushCount;
        Assert.Equal(total, totalConsumed);
        if (flushCount > 0)
        {
            Assert.Equal(consumed, flushFrom);
            Assert.Equal(total - 1, flushTo);
        }
    }
}
