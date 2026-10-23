using PPTAssistant.InkML;
using System.Diagnostics;
using PPTAssistant.Host;
using PPTAssistant.Overlay;
using PPTAssistant.Session;

namespace PPTAssistant.Tests;

public class ShowGateTests
{

    [Fact]
    public void AcceptsFullScreenSpeakerShow() => Assert.True(ShowGate.IsAccepted(SlideShowState.ShowTypeSpeaker));

    /// <summary>窗口模式（2）、展台模式（3）与「没有放映」（0）一律不介入。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    public void RejectsEverythingElse(int showType) => Assert.False(ShowGate.IsAccepted(showType));

    /// <summary>门控常量值本身也要锁住：它来自 PowerPoint 对象模型，写错一个数字就全盘皆错。</summary>
    [Fact]
    public void SpeakerShowTypeIsOne() => Assert.Equal(1, SlideShowState.ShowTypeSpeaker);
}

public class LeadingEdgeDebouncerTests
{
    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;

    [Fact]
    public void AcceptsFirstRequest() => Assert.True(CreateClockTimed(0).TryAccept());

    [Fact]
    public void RejectsRequestsInsideTheWindow()
    {
        var now = 0L;
        var debouncer = new LeadingEdgeDebouncer(TimeSpan.FromMilliseconds(30), () => now);

        Assert.True(debouncer.TryAccept());   // t=0
        now = 10 * TicksPerMs;
        Assert.False(debouncer.TryAccept());  // t=10ms，窗口内
        now = 29 * TicksPerMs;
        Assert.False(debouncer.TryAccept());  // t=29ms，仍在窗口内
    }

    [Fact]
    public void AcceptsAgainAfterTheWindow()
    {
        var now = 0L;
        var debouncer = new LeadingEdgeDebouncer(TimeSpan.FromMilliseconds(30), () => now);

        Assert.True(debouncer.TryAccept());
        now = 30 * TicksPerMs;
        Assert.True(debouncer.TryAccept());   // 正好到期即可再次受理
    }

    [Fact]
    public void ZeroWindowAcceptsEverything()
    {
        var debouncer = new LeadingEdgeDebouncer(TimeSpan.Zero, () => 0);

        Assert.True(debouncer.TryAccept());
        Assert.True(debouncer.TryAccept());
    }

    [Fact]
    public void ResetClearsHistory()
    {
        var now = 0L;
        var debouncer = new LeadingEdgeDebouncer(TimeSpan.FromMilliseconds(30), () => now);

        Assert.True(debouncer.TryAccept());
        now = 5 * TicksPerMs;
        Assert.False(debouncer.TryAccept());

        debouncer.Reset();
        Assert.True(debouncer.TryAccept());   // 换课件/重新开场后不该被上一次的时间基准挡住
    }

    [Fact]
    public void NegativeWindowIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LeadingEdgeDebouncer(TimeSpan.FromMilliseconds(-1)));

    private static LeadingEdgeDebouncer CreateClockTimed(long now)
        => new(TimeSpan.FromMilliseconds(30), () => now);
}

/// <summary>会话参数默认值（配置缺省时的兜底行为）。</summary>
public class SessionOptionsTests
{
    [Fact]
    public void DefaultsMatchFrozenDecisions()
    {
        var options = SessionOptions.Default;

        Assert.Equal(TimeSpan.FromMilliseconds(30), options.PageTurnDebounce);

        Assert.InRange(options.PenWidthDips, 1.0, 200.0);

        Assert.True(options.IgnoreTouchWhenPenPresent);

        Assert.Equal(InkColor.Default, options.PenColor);
    }
}

/// <summary>墨迹模型里与 WPF 无关的部分（<see cref="InkColor"/> / <see cref="StrokePoint"/>）。</summary>
public class InkModelTests
{
    [Theory]
    [InlineData("#D91B1B", 0xD9, 0x1B, 0x1B)]
    [InlineData("D91B1B", 0xD9, 0x1B, 0x1B)]
    [InlineData("  #0085CD ", 0x00, 0x85, 0xCD)]
    public void ParsesHexColors(string text, byte r, byte g, byte b)
        => Assert.Equal(new InkColor(r, g, b), InkColor.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("rot")]
    public void FallsBackToDefaultOnBadInput(string? text)
        => Assert.Equal(InkColor.Default, InkColor.Parse(text));

    [Fact]
    public void ToHexRoundTrips()
    {
        var color = new InkColor(0x12, 0xAB, 0xEF);

        Assert.Equal("#12ABEF", color.ToHex());
        Assert.Equal(color, InkColor.Parse(color.ToHex()));
    }

    /// <summary>无压感硬件（红外/电容屏）必须表达成「没有压力数据」，而不是伪装成 0.5。</summary>
    [Fact]
    public void PressurelessPointsReportNoPressure()
    {
        var touch = new StrokePoint(new Coords.DipPoint(1, 2), StrokePoint.NoPressure, InputDeviceKind.Touch);
        var pen = new StrokePoint(new Coords.DipPoint(1, 2), 0.75f, InputDeviceKind.Pen);

        Assert.False(touch.HasPressure);
        Assert.True(pen.HasPressure);
    }
}

public class SettingsModelTests
{
    [Theory]
    [InlineData(WriteBackMode.Ask)]
    [InlineData(WriteBackMode.AutoRetain)]
    [InlineData(WriteBackMode.AutoDiscard)]
    public void WriteBackModeRoundTripsThroughItsName(WriteBackMode mode)
        => Assert.Equal(mode, WriteBackModes.Parse(WriteBackModes.Format(mode)));

    [Theory]
    [InlineData("ask", WriteBackMode.Ask)]
    [InlineData("AutoRetain", WriteBackMode.AutoRetain)]
    [InlineData("autodiscard", WriteBackMode.AutoDiscard)]
    public void WriteBackModeParsesCaseInsensitively(string text, WriteBackMode expected)
        => Assert.Equal(expected, WriteBackModes.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Retain")]
    [InlineData("Keep")]
    public void InvalidWriteBackModeFallsBackToAsk(string? text)
        => Assert.Equal(WriteBackMode.Ask, WriteBackModes.Parse(text));

    [Fact]
    public void SessionOptionsKeepEverySetting()
    {
        // 模拟「存盘后再读回」：两套独立构造的参数，每个设置项都必须一致。
        // （不用记录类型的整体相等——RecentPenColors 是集合，默认比较是引用相等，会给出假阴性。）
        var saved = new SessionOptions
        {
            PenWidthDips = 12.5,
            PenColor = new InkColor(0x00, 0x85, 0xCD),
            RecentPenColors = [new InkColor(1, 1, 1), new InkColor(2, 2, 2)],
            WriteBackMode = WriteBackMode.AutoDiscard,
            IgnoreTouchWhenPenPresent = false,
            AllowMouseDrawing = false,
        };

        var loaded = new SessionOptions
        {
            PenWidthDips = 12.5,
            PenColor = new InkColor(0x00, 0x85, 0xCD),
            RecentPenColors = [new InkColor(1, 1, 1), new InkColor(2, 2, 2)],
            WriteBackMode = WriteBackMode.AutoDiscard,
            IgnoreTouchWhenPenPresent = false,
            AllowMouseDrawing = false,
        };

        Assert.Equal(saved.PenWidthDips, loaded.PenWidthDips, 6);
        Assert.Equal(saved.PenColor, loaded.PenColor);
        Assert.Equal(saved.RecentPenColors, loaded.RecentPenColors);
        Assert.Equal(saved.WriteBackMode, loaded.WriteBackMode);
        Assert.Equal(saved.IgnoreTouchWhenPenPresent, loaded.IgnoreTouchWhenPenPresent);
        Assert.Equal(saved.AllowMouseDrawing, loaded.AllowMouseDrawing);
    }
}
