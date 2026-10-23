using PPTAssistant.InkML;
using PPTAssistant.Coords;

namespace PPTAssistant.Overlay;

/// <summary>
/// 覆盖层对上层暴露的能力（**Session 只依赖这个接口**，不依赖具体窗口类型）。
/// </summary>
/// <remarks>
/// <para>刻意只暴露「会话编排需要的语义」：显示/隐藏、笔鼠标态、清空墨迹、位置变化通知、笔迹流事件。
/// 不暴露 WPF 类型（本接口的签名里没有 <c>System.Windows.*</c>），这样：
/// ① Session 层不需要引用 WPF 的具体实现细节；② 单元测试可以用假实现替代整个覆盖层。</para>
/// <para><b>线程</b>：实现是 UI 对象，所有成员都必须在 UI 线程访问。</para>
/// </remarks>
public interface IInkSurface
{

    InputMode Mode { get; set; }

    double PenWidthDips { get; set; }

    InkColor PenColor { get; set; }

    bool IgnoreTouchWhenPenPresent { get; set; }

    /// <summary>是否允许鼠标绘制（开发期验证用；教室里由触摸/笔主导）。</summary>
    bool AllowMouseDrawing { get; set; }

    bool AlwaysRoutePenToInk { get; set; }

    /// <summary>覆盖层当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>覆盖层窗口矩形（DIP，多屏时可为负坐标）——即坐标映射的视口。</summary>
    DipRect Viewport { get; }

    /// <summary>当前页**仍保留在屏上**的笔画数（擦除后会减少；诊断 / 旁路日志用）。</summary>
    int StrokeCount { get; }

    int PointCount { get; }

    /// <summary>把覆盖层铺满指定屏幕并显示（幂等：已显示时只重新定位）。</summary>
    void ShowOn(ScreenInfo screen);

    /// <summary>隐藏覆盖层（不清空墨迹模型；需要清空请显式调用 <see cref="ClearInk"/>）。</summary>
    void Hide();

    /// <summary>
    /// 确保覆盖层仍在最上层（且不改变焦点）。
    /// </summary>
    /// <remarks>宿主的放映窗口本身也是置顶窗口，翻页/切页时它会重新置顶，可能把覆盖层压到下面
    /// ——那会让老师的板书瞬间「消失」。因此每次页码变化都顺手重申一次置顶。</remarks>
    void EnsureOnTop();

    /// <summary>清空全部墨迹（翻页 / 散场时调用——墨迹是「按页」的）。</summary>
    void ClearInk();

    void SetInputExclusion(IReadOnlyList<DipRect>? excludedAreasDips);

    /// <summary>一笔开始（已捕获输入）。</summary>
    event EventHandler<InkStrokeEventArgs>? StrokeBegan;

    /// <summary>一笔的增量采样点（高频：每次输入事件一次）。</summary>
    event EventHandler<InkStrokeEventArgs>? StrokeUpdated;

    /// <summary>一笔结束。</summary>
    event EventHandler<InkStrokeEventArgs>? StrokeEnded;

    event EventHandler<InkStrokesErasedEventArgs>? StrokesErased;

    /// <summary>窗口矩形变化（上层需据此重建 <see cref="SlideShowGeometry"/>）。</summary>
    event EventHandler? ViewportChanged;

    /// <summary>输入模式变化。</summary>
    event EventHandler<InputMode>? ModeChanged;
}
