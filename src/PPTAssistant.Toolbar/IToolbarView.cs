using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

public interface IToolbarView
{
    /// <summary>工具栏当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>
    /// 底部中央条的屏幕矩形（DIP）——也用作「色板/笔刷悬浮窗就近摆放」的锚点。
    /// </summary>
    DipRect Bounds { get; }

    IReadOnlyList<DipRect> InputExclusionRects { get; }

    /// <summary>当前是否处于笔态（决定「指针 / 笔」两个按钮的互斥高亮）。</summary>
    bool IsPenMode { get; }

    bool IsEraserMode { get; }

    /// <summary>把整套工具窗口摆到指定屏幕并显示（幂等）。</summary>
    void ShowOn(ScreenInfo screen);

    /// <summary>隐藏整套工具窗口。</summary>
    void Hide();

    void SetPenMode(bool penMode);

    void SetEraserMode(bool eraserMode);

    void SetMagnifierActive(bool active);

    void SetThumbnailPanelActive(bool active);

    void SetPaletteActive(bool active);

    void SetPenBrushActive(bool active);

    void UpdateSlidePosition(int slideIndex, int slideCount);

    void SetNativeInkSupported(bool supported);

    /// <summary>显示一条状态文本（null / 空 = 清除）。</summary>
    void SetStatus(string? status);

    /// <summary>置顶且不激活（宿主的放映窗口重新置顶后要重申）。</summary>
    void EnsureOnTop();

    event EventHandler? NextSlideRequested;

    event EventHandler? PreviousSlideRequested;

    /// <summary>请求切换笔态（true = 进笔态，false = 回鼠标态）。</summary>
    event EventHandler<bool>? PenModeRequested;

    event EventHandler? EraseModeRequested;

    event EventHandler? ClearPageRequested;

    event EventHandler? MagnifierToggleRequested;

    event EventHandler<ThumbnailSide>? ThumbnailPanelToggleRequested;

    event EventHandler? PaletteToggleRequested;

    event EventHandler? PenBrushToggleRequested;

    event EventHandler? ExitShowRequested;
}
