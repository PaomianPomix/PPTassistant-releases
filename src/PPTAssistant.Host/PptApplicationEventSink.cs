using System.Runtime.InteropServices;

namespace PPTAssistant.Host;

[ComVisible(true)]
[Guid("3F1C9A24-8E52-4F0B-9C77-2A6D5E1B8C40")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IPptApplicationEvents
{
    /// <summary>放映开始（含窗口对象）。</summary>
    [DispId(2011)]
    void SlideShowBegin([MarshalAs(UnmanagedType.IDispatch)] object window);

    /// <summary>切换到下一页（或跳页）。</summary>
    [DispId(2013)]
    void SlideShowNextSlide([MarshalAs(UnmanagedType.IDispatch)] object window);

    /// <summary>放映结束（含演示文稿对象）。</summary>
    [DispId(2014)]
    void SlideShowEnd([MarshalAs(UnmanagedType.IDispatch)] object presentation);

    /// <summary>演示文稿关闭流程最后一步——此时释放 COM 引用最安全。</summary>
    [DispId(2031)]
    void PresentationCloseFinal([MarshalAs(UnmanagedType.IDispatch)] object presentation);
}

/// <summary>事件载荷：放映开始。</summary>
public sealed class SlideShowBeginEventArgs : EventArgs
{
    public SlideShowBeginEventArgs(object window, object application, int slideIndex, int slideCount, string? indexSource = null)
    {
        Window = window;
        Application = application;
        SlideIndex = slideIndex;
        SlideCount = slideCount;
        IndexSource = indexSource;
    }

    /// <summary>SlideShowWindow 的 COM 对象（后续查询 View/Slide 用）。</summary>
    public object Window { get; }

    /// <summary>Application 的 COM 对象。</summary>
    public object Application { get; }

    /// <summary>当前页（1 起）。</summary>
    public int SlideIndex { get; }

    /// <summary>总页数。</summary>
    public int SlideCount { get; }

    /// <summary>页码读到的来源（诊断用，见 <c>ComLateBinder.ReadSlidePosition</c>）。</summary>
    public string? IndexSource { get; }
}

/// <summary>事件载荷：翻页 / 跳页。</summary>
public sealed class SlideChangedEventArgs : EventArgs
{
    public SlideChangedEventArgs(object window, int slideIndex, int slideCount, string? indexSource = null)
    {
        Window = window;
        SlideIndex = slideIndex;
        SlideCount = slideCount;
        IndexSource = indexSource;
    }

    public object Window { get; }

    public int SlideIndex { get; }

    public int SlideCount { get; }

    /// <summary>页码读到的来源（诊断用，见 <c>ComLateBinder.ReadSlidePosition</c>）。</summary>
    public string? IndexSource { get; }
}

/// <summary>事件载荷：放映结束 / 演示文稿关闭。</summary>
public sealed class SlideShowEndEventArgs : EventArgs
{
    public SlideShowEndEventArgs(object presentation)
    {
        Presentation = presentation;
    }

    public object Presentation { get; }
}

/// <summary>
/// 是 COM 事件的接收器（sink），把宿主回调翻译成托管事件。
/// </summary>
/// <remarks>
/// <b>线程</b>：回调发生在 COM 传入线程（非 UI 线程）。本类只做「转发 + 抛到调用方」，
/// 不碰任何 UI；由上层（Session）负责 marshal 到 WPF Dispatcher（AGENTS 约定：不散在各窗口类里）。
/// <b>异常</b>：绝不让异常穿过 COM 边界——否则宿主可能把我们的进程视为故障接收器并断开连接。
/// </remarks>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("6B2D5C31-4A87-4E6D-8F19-7C0E3A9D2B55")]
public sealed class PptApplicationEventSink : IPptApplicationEvents, IDisposable
{
    private readonly Action<string>? _trace;
    private bool _disposed;

    public PptApplicationEventSink(Action<string>? trace = null) => _trace = trace;

    public event EventHandler<SlideShowBeginEventArgs>? SlideShowBegin;

    public event EventHandler<SlideChangedEventArgs>? SlideChanged;

    public event EventHandler<SlideShowEndEventArgs>? SlideShowEnd;

    public event EventHandler<SlideShowEndEventArgs>? PresentationCloseFinal;

    // 显式实现接口方法：事件名与接口方法名同名（都是宿主事件的自然命名），
    // 用显式实现避免冲突，同时对上层只暴露 NET 风格的事件。
    void IPptApplicationEvents.SlideShowBegin(object window)
    {
        Guard(() =>
        {
            var app = ComLateBinder.GetApplication(window) ?? this;
            var (index, count) = ComLateBinder.ReadSlidePosition(window);
            SlideShowBegin?.Invoke(this, new SlideShowBeginEventArgs(window, app!, index, count, ComLateBinder.LastSlideIndexSource));
        }, nameof(SlideShowBegin));
    }

    void IPptApplicationEvents.SlideShowNextSlide(object window)
    {
        Guard(() =>
        {
            var (index, count) = ComLateBinder.ReadSlidePosition(window);
            SlideChanged?.Invoke(this, new SlideChangedEventArgs(window, index, count, ComLateBinder.LastSlideIndexSource));
        }, nameof(IPptApplicationEvents.SlideShowNextSlide));
    }

    void IPptApplicationEvents.SlideShowEnd(object presentation)
    {
        Guard(() => SlideShowEnd?.Invoke(this, new SlideShowEndEventArgs(presentation)), nameof(SlideShowEnd));
    }

    void IPptApplicationEvents.PresentationCloseFinal(object presentation)
    {
        Guard(
            () => PresentationCloseFinal?.Invoke(this, new SlideShowEndEventArgs(presentation)),
            nameof(PresentationCloseFinal));
    }

    public void Dispose() => _disposed = true;

    private void Guard(Action action, string eventName)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            // 吞掉一切异常：穿越 COM 边界的异常会污染宿主的调用栈。
            _trace?.Invoke($"[sink] {eventName} 处理失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}
