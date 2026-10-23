using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Toolbar;

public readonly record struct SlideThumbnail(int SlideIndex, string? ImagePath);

/// <summary>缩略图侧栏停靠在哪一侧（**C4**：左右下角区各自呼出**所在一侧**的侧栏）。</summary>
public enum ThumbnailSide
{
    /// <summary>左角区呼出的左侧栏（贴屏左边）。</summary>
    Left = 0,

    /// <summary>右角区呼出的右侧栏（贴屏右边）。</summary>
    Right = 1,
}

public interface IMagnifierView
{
    /// <summary>当前是否处于放大状态。</summary>
    bool IsActive { get; }

    /// <summary>放大倍率（1.0–3.0，越界会被夹紧）。</summary>
    double Zoom { get; set; }

    void ShowOn(ScreenInfo screen);

    /// <summary>关闭放大镜（停刷新、还原抓屏可见性、记住浮窗位置）。</summary>
    void Hide();

    /// <summary>置顶且不激活（可能与其它置顶浮层交叠时用它保证工具栏在浮窗之上）。</summary>
    void EnsureOnTop();

    /// <summary>倍率变化（工具栏显示用）。</summary>
    event EventHandler<double>? ZoomChanged;
}

public interface IThumbnailPanelView
{
    /// <summary>当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>当前停靠的一侧（Session 据此判断「同一个角区再点一次 = 收起」）。</summary>
    ThumbnailSide Side { get; }

    /// <summary>侧栏在屏幕上的矩形（DIP）——覆盖层据此挖洞（笔态下侧栏也要点得到）。</summary>
    DipRect Bounds { get; }

    /// <summary>把侧栏摆到指定屏幕的指定一侧并显示（左角区 → 左，右角区 → 右）。</summary>
    void ShowOn(ScreenInfo screen, ThumbnailSide side);

    /// <summary>隐藏侧栏。</summary>
    void Hide();

    /// <summary>更新缩略图集合（未导出的页用 <see cref="SlideThumbnail.ImagePath"/> = null 显示占位）。</summary>
    void SetThumbnails(IReadOnlyList<SlideThumbnail> thumbnails, int currentSlideIndex);

    /// <summary>高亮当前页（不重建整个列表）。</summary>
    void SetCurrentSlide(int oneBasedIndex);

    /// <summary>置顶且不激活。</summary>
    void EnsureOnTop();

    /// <summary>用户点选了某一页（1 起）。</summary>
    event EventHandler<int>? SlideActivated;

    event EventHandler? CloseRequested;
}

public interface IPenPaletteView
{
    /// <summary>当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>悬浮窗在屏幕上的矩形（DIP）——覆盖层据此挖洞。</summary>
    DipRect Bounds { get; }

    /// <summary>当前选中的颜色。</summary>
    InkColor CurrentColor { get; }

    /// <summary>在指定屏幕上显示（<paramref name="anchorDips"/> = 工具栏中央条矩形，用于就近摆放）。</summary>
    void ShowOn(ScreenInfo screen, DipRect anchorDips);

    /// <summary>收起悬浮窗。</summary>
    void Hide();

    /// <summary>同步当前色（不改动事件语义：只更新显示）。</summary>
    void SetCurrentColor(InkColor color);

    /// <summary>同步最近使用色（新色顶置）。</summary>
    void SetRecentColors(IReadOnlyList<InkColor> recent);

    /// <summary>置顶且不激活。</summary>
    void EnsureOnTop();

    /// <summary>用户选了颜色（预设 / 最近使用 / 取色器）。</summary>
    event EventHandler<InkColor>? ColorPicked;

    event EventHandler? CloseRequested;
}

public interface IPenBrushView
{
    /// <summary>当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>悬浮窗在屏幕上的矩形（DIP）——覆盖层据此挖洞。</summary>
    DipRect Bounds { get; }

    /// <summary>当前笔宽（DIP）。</summary>
    double Value { get; }

    /// <summary>在指定屏幕上显示（<paramref name="anchorDips"/> = 工具栏中央条矩形，用于就近摆放）。</summary>
    void ShowOn(ScreenInfo screen, DipRect anchorDips);

    /// <summary>收起悬浮窗。</summary>
    void Hide();

    /// <summary>同步滑杆与预览（不触发 <see cref="ValueChanged"/>，避免回环）。</summary>
    void SetValue(double valueDips);

    /// <summary>置顶且不激活。</summary>
    void EnsureOnTop();

    /// <summary>拖动中实时变化（**下一笔生效**；不落盘）。</summary>
    event EventHandler<double>? ValueChanged;

    /// <summary>一次调节结束（抬手 / 收起）——此时才落盘，避免拖动过程中反复写 config。</summary>
    event EventHandler<double>? ValueCommitted;

    event EventHandler? CloseRequested;
}
