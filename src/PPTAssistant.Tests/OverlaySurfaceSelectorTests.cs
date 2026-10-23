using PPTAssistant.Overlay;

namespace PPTAssistant.Tests;

public class OverlaySurfaceSelectorTests
{
    /// <summary>请求分层 → 分层（不冒险）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LayeredRequestStaysLayered(bool compositionEnabled)
    {
        var decision = OverlaySurfaceSelector.DecideForConstruction(OverlaySurfaceMode.Layered, compositionEnabled);

        Assert.Equal(OverlaySurfaceMode.Layered, decision.Effective);
        Assert.False(decision.FellBack);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlurBehindRequestAlwaysFallsBackToLayered(bool compositionEnabled)
    {
        var decision = OverlaySurfaceSelector.DecideForConstruction(OverlaySurfaceMode.BlurBehind, compositionEnabled);

        Assert.Equal(OverlaySurfaceMode.Layered, decision.Effective);
        Assert.True(decision.FellBack, "回落必须被显式标记（真机排障时要从日志里一眼看出来）");
    }

    /// <summary>回落理由要说明「为什么不能非分层」，而不只是「合成不可用」。</summary>
    [Fact]
    public void BlurBehindFallbackReasonExplainsWhy()
    {
        var decision = OverlaySurfaceSelector.DecideForConstruction(OverlaySurfaceMode.BlurBehind, compositionEnabled: true);

        Assert.Contains("非分层", decision.Reason);
        Assert.Contains("透明", decision.Reason);
    }

    /// <summary>ToString 要能一眼读出「请求 / 生效 / 是否回落」。</summary>
    [Fact]
    public void ToStringIsDiagnosable()
    {
        var decision = OverlaySurfaceSelector.DecideForConstruction(OverlaySurfaceMode.BlurBehind, compositionEnabled: true);
        var text = decision.ToString();

        Assert.Contains("BlurBehind", text);
        Assert.Contains("Layered", text);
        Assert.Contains("回落", text);
    }

    /// <summary>回落标志与模式必须自洽（不会出现「回落了但模式没变」这种自相矛盾的状态）。</summary>
    [Theory]
    [InlineData(OverlaySurfaceMode.Layered, true)]
    [InlineData(OverlaySurfaceMode.Layered, false)]
    [InlineData(OverlaySurfaceMode.BlurBehind, true)]
    [InlineData(OverlaySurfaceMode.BlurBehind, false)]
    public void FellBackIsConsistent(OverlaySurfaceMode requested, bool compositionEnabled)
    {
        var decision = OverlaySurfaceSelector.DecideForConstruction(requested, compositionEnabled);

        Assert.Equal(decision.Requested != decision.Effective, decision.FellBack);
        Assert.NotEmpty(decision.Reason);
    }

    /// <summary>
    /// ★ 安全不变量：**无论输入什么，生效的模式永远是分层**。
    /// </summary>
    /// <remarks>穷举当前枚举的全部取值 × 合成开关的全部取值。这条断言就是「不能让老师看到黑屏」的
    /// 代码化表达——它一旦失败，说明有人放行了非分层路径。</remarks>
    [Fact]
    public void EffectiveModeIsAlwaysLayered()
    {
        foreach (var requested in Enum.GetValues<OverlaySurfaceMode>())
        {
            foreach (var composition in new[] { true, false })
            {
                var decision = OverlaySurfaceSelector.DecideForConstruction(requested, composition);
                Assert.Equal(OverlaySurfaceMode.Layered, decision.Effective);
            }
        }
    }
}
