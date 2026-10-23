namespace PPTAssistant.Coords;

public sealed class SlideShowGeometry
{
    /// <summary>创建映射。</summary>
    /// <param name="viewportDips">覆盖层窗口矩形（DIP）。</param>
    /// <param name="slide">幻灯片页面尺寸（pt）。</param>
    /// <param name="clipToContent">映射是否把结果夹在 <see cref="ContentBounds"/> 内（墨迹采集用 true）。</param>
    public SlideShowGeometry(DipRect viewportDips, SlideSize slide, bool clipToContent = false)
    {
        if (!slide.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(slide), slide, "幻灯片尺寸必须为正。");
        }

        if (viewportDips.Width <= 0 || viewportDips.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportDips), viewportDips, "视口尺寸必须为正。");
        }

        Viewport = viewportDips;
        Slide = slide;

        // 等比缩放，取能同时容纳两个方向的较小系数。
        var scaleX = viewportDips.Width / slide.Width;
        var scaleY = viewportDips.Height / slide.Height;
        Scale = Math.Min(scaleX, scaleY);

        var contentWidth = slide.Width * Scale;
        var contentHeight = slide.Height * Scale;
        ContentBounds = new DipRect(
            viewportDips.X + ((viewportDips.Width - contentWidth) / 2),
            viewportDips.Y + ((viewportDips.Height - contentHeight) / 2),
            contentWidth,
            contentHeight);

        ClipToContent = clipToContent;
    }

    /// <summary>覆盖层窗口矩形（DIP）。</summary>
    public DipRect Viewport { get; }

    /// <summary>幻灯片页面尺寸（pt）。</summary>
    public SlideSize Slide { get; }

    /// <summary>缩放系数：每 1 pt 对应多少 DIP。</summary>
    public double Scale { get; }

    /// <summary>去掉黑边后的实际内容区（DIP）。</summary>
    public DipRect ContentBounds { get; }

    /// <summary>是否把越界坐标夹回内容区。</summary>
    public bool ClipToContent { get; }

    /// <summary>letterbox 左右黑边宽度（DIP）：左 = 右。</summary>
    public double HorizontalLetterbox => ContentBounds.X - Viewport.X;

    /// <summary>letterbox 上下黑边高度（DIP）：上 = 下。</summary>
    public double VerticalLetterbox => ContentBounds.Y - Viewport.Y;

    /// <summary>是否存在黑边（比例不一致时有）。</summary>
    public bool HasLetterbox => HorizontalLetterbox > 0.5 || VerticalLetterbox > 0.5;

    /// <summary>幻灯片坐标（pt）→ 屏幕坐标（DIP）。</summary>
    public DipPoint SlideToScreen(SlidePoint point)
    {
        var x = ContentBounds.X + (point.X * Scale);
        var y = ContentBounds.Y + (point.Y * Scale);
        return ClipToContent ? ClampToContent(x, y) : new DipPoint(x, y);
    }

    /// <summary>屏幕坐标（DIP）→ 幻灯片坐标（pt）。</summary>
    public SlidePoint ScreenToSlide(DipPoint point)
    {
        var x = point.X;
        var y = point.Y;
        if (ClipToContent)
        {
            var clamped = ClampToContent(x, y);
            x = clamped.X;
            y = clamped.Y;
        }

        return new SlidePoint((x - ContentBounds.X) / Scale, (y - ContentBounds.Y) / Scale);
    }

    /// <summary>点是否落在内容区（黑边上的点不应产生墨迹）。</summary>
    public bool IsInsideContent(DipPoint point) => ContentBounds.Contains(point);

    /// <summary>把 DIP 长度换算成幻灯片点长度（笔宽换算用）。</summary>
    public double DipsToSlideLength(double dips) => dips / Scale;

    /// <summary>把幻灯片点长度换算成 DIP 长度。</summary>
    public double SlideLengthToDips(double slideLength) => slideLength * Scale;

    private DipPoint ClampToContent(double x, double y)
    {
        var cx = Math.Clamp(x, ContentBounds.X, ContentBounds.Right);
        var cy = Math.Clamp(y, ContentBounds.Y, ContentBounds.Bottom);
        return new DipPoint(cx, cy);
    }

    public override string ToString()
        => $"viewport={Viewport} slide={Slide} scale={Scale:0.####} content={ContentBounds}";
}
