using PPTAssistant.Coords;

namespace PPTAssistant.Tests;

public class MagnifierLensTests
{
    /// <summary>浮窗宽高（DIP，与 <c>MagnifierWindow</c> 的常量一致）。</summary>
    private const double WinW = 480;

    private const double WinH = 320;

    /// <summary>典型屏尺寸（DIP）：1440p、4K、1080p 100%、1080p@150%（=720 DIP）、720p、以及一块偏小的 1024×768。</summary>
    public static TheoryData<double, double> Screens => new()
    {
        { 2560, 1440 },
        { 3840, 2160 },
        { 1920, 1080 },
        { 1280, 720 },
        { 1024, 768 },
    };

    public static TheoryData<double> Zooms => new() { 1.0, 1.25, 2.0, 2.5, 3.0 };

    /// <summary>镜下取景：源矩形以浮窗中心为中心、宽高 = 浮窗宽高 ÷ 倍率（倍率语义的锚点）。</summary>
    [Theory]
    [MemberData(nameof(Zooms))]
    public void SourceIsCenteredOnWindowAndScalesWithZoom(double zoom)
    {
        var screen = new DipRect(0, 0, 2560, 1440);
        var window = new DipRect(1040, 560, WinW, WinH); // 屏中央，不触发夹取

        var source = MagnifierLens.WindowToSource(window, zoom, screen);

        Assert.Equal(window.CenterX, source.CenterX, 6);
        Assert.Equal(window.CenterY, source.CenterY, 6);
        Assert.Equal(WinW / zoom, source.Width, 6);
        Assert.Equal(WinH / zoom, source.Height, 6);
    }

    /// <summary>核心不变量：任何屏、任何倍率、任何拖放位置（含半出屏与完全出屏）下，源矩形都完整落在屏内。</summary>
    /// <remarks>浮窗可以被拖到任意位置（DragMove 不限制），出屏时源区域跟着出去就抓到垃圾——必须夹回屏内。</remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public void SourceStaysOnScreenAtAnyWindowPosition(double screenWidth, double screenHeight)
    {
        var screen = new DipRect(0, 0, screenWidth, screenHeight);

        foreach (var zoom in new[] { 1.0, 2.0, 3.0 })
        {
            // 窗口位置扫过「屏内 + 出屏一圈」（步长取质数避免与网格共线），源都必须完整在屏内。
            for (var y = -WinH; y <= screenHeight + WinH; y += 37)
            {
                for (var x = -WinW; x <= screenWidth + WinW; x += 41)
                {
                    var window = new DipRect(x, y, WinW, WinH);
                    var source = MagnifierLens.WindowToSource(window, zoom, screen);

                    Assert.True(
                        source.X >= screen.X - 0.001 && source.Right <= screen.Right + 0.001 &&
                        source.Y >= screen.Y - 0.001 && source.Bottom <= screen.Bottom + 0.001,
                        $"屏 {screenWidth}×{screenHeight} 倍率 {zoom} 浮窗({x:0},{y:0})：源 {source} 出屏");
                }
            }
        }
    }

    /// <summary>屏内摆放时源与浮窗同心；半出屏时源被夹回屏内（中心不再对齐，属预期行为——出屏的一半本来就没有内容）。</summary>
    [Fact]
    public void HalfOffscreenWindowClampsSourceIntoScreen()
    {
        var screen = new DipRect(0, 0, 2560, 1440);

        // 右缘半出屏：窗口 [2400..2880]，2× 源应为 [2400..2640]——宽度方向放得下，被夹回 [2560-240, 2560]。
        var window = new DipRect(2400, 560, WinW, WinH);
        var source = MagnifierLens.WindowToSource(window, 2.0, screen);

        Assert.Equal(screen.Right - source.Width, source.X, 6);
        Assert.Equal(window.CenterY, source.CenterY, 6); // 高度方向没出屏，仍同心
    }

    /// <summary>回退模式（内容跟光标）：源以光标为中心、与浮窗同宽高比；光标贴边时夹回屏内。</summary>
    [Fact]
    public void FollowCursorSourceIsCenteredOnCursor()
    {
        var screen = new DipRect(0, 0, 2560, 1440);

        var source = MagnifierLens.PointToSource(1300, 800, WinW, WinH, 2.0, screen);
        Assert.Equal(1300, source.CenterX, 6);
        Assert.Equal(800, source.CenterY, 6);
        Assert.Equal(WinW / 2, source.Width, 6);
        Assert.Equal(WinH / 2, source.Height, 6);

        // 光标贴屏角：源完整在屏内、贴住角。
        var corner = MagnifierLens.PointToSource(0, 0, WinW, WinH, 2.0, screen);
        Assert.Equal(screen.X, corner.X, 6);
        Assert.Equal(screen.Y, corner.Y, 6);
    }

    /// <summary>
    /// 回退模式的冻结条件（几何事实）：光标在浮窗覆盖区内 → 源区域与浮窗相交 → 必须冻结画面；
    /// 光标远离浮窗 → 不相交 → 正常刷新。绝不显示「自己的放大像」。
    /// </summary>
    [Fact]
    public void FollowCursorFreezesExactlyWhenSourceOverlapsWindow()
    {
        var screen = new DipRect(0, 0, 2560, 1440);
        var window = new DipRect(1040, 560, WinW, WinH);

        // 光标在浮窗正中：源完全落进浮窗——冻结。
        var atCenter = MagnifierLens.PointToSource(window.CenterX, window.CenterY, WinW, WinH, 2.0, screen);
        Assert.True(atCenter.Intersect(window).Area > 0);

        // 光标在浮窗左缘外、但源半宽（120）以内：源伸进浮窗——冻结（「靠近即冻结」）。
        var nearEdge = MagnifierLens.PointToSource(window.X - 100, window.CenterY, WinW, WinH, 2.0, screen);
        Assert.True(nearEdge.Intersect(window).Area > 0);

        // 光标远离浮窗（距离 > 源半宽）：不相交——正常刷新。
        var far = MagnifierLens.PointToSource(window.X - 300, window.CenterY, WinW, WinH, 2.0, screen);
        Assert.Equal(0, far.Intersect(window).Area, 6);
    }

    /// <summary>源边长 = 浮窗边长 ÷ 倍率；上限 = 浮窗边长（1.0× 即「原样」），下限 <see cref="MagnifierLens.MinSourceDips"/>。</summary>
    [Theory]
    [InlineData(480, 1.0, 480)]
    [InlineData(480, 2.0, 240)]
    [InlineData(480, 3.0, 160)]
    [InlineData(320, 1.0, 320)]
    [InlineData(320, 2.5, 128)]
    public void SourceSizeIsWindowDividedByZoom(double lensSize, double zoom, double expected)
    {
        Assert.Equal(expected, MagnifierLens.SourceSize(lensSize, zoom), 6);
    }

    /// <summary>倍率越界时几何本身仍给出合法值（区间夹紧由调用方做）。</summary>
    [Fact]
    public void SourceSizeIsClampedIntoUsableRange()
    {
        Assert.Equal(MagnifierLens.MinSourceDips, MagnifierLens.SourceSize(WinW, 999), 6);
        Assert.Equal(WinW, MagnifierLens.SourceSize(WinW, 0.001), 6);
    }

    /// <summary>
    /// 屏比浮窗还小（极小屏）时的已知限制：源比屏大，无法完整在屏内——
    /// 夹取退化为「贴住屏幕左上」，与 <see cref="MagnifierLens.SourceSize"/> 的下限保护共同保证不崩溃、不抓屏外垃圾的主观恶化。
    /// 实际教室屏（≥ 720p = 1280×720 DIP）不会触发。
    /// </summary>
    [Fact]
    public void ScreenSmallerThanWindowPinsSourceToScreenOrigin()
    {
        var screen = new DipRect(0, 0, 400, 300);
        var window = new DipRect(100, 80, WinW, WinH);

        var source = MagnifierLens.WindowToSource(window, 1.0, screen);

        Assert.Equal(screen.X, source.X, 6); // ClampInto 区间倒置取 min
        Assert.Equal(screen.Y, source.Y, 6);
    }

    [Fact]
    public void LegacyFollowPlacementOverlappedSourceByFiftyNinePercent()
    {
        const double lens = 360;
        const double lensHeightLegacy = 360 + 8 + 32; // 镜片 + 提示条间距 + 提示条（旧版窗口 360×400）
        var sourceHalf = 360 / 2 / 2.0; // 源半边长 @2× = 90
        var source = new DipRect(1300 - sourceHalf, 800 - sourceHalf, 90 * 2, 90 * 2);

        var legacyWindow = new DipRect(1120, 416, lens, lensHeightLegacy); // top = 800 − 360 − 24
        var overlap = legacyWindow.Intersect(source);

        Assert.Equal(106, overlap.Height, 6);
        Assert.Equal(180, overlap.Width, 6);
        Assert.Equal(19080, overlap.Area, 6);
    }
}
