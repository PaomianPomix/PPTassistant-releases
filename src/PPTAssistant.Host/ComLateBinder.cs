namespace PPTAssistant.Host;

internal static class ComLateBinder
{
    /// <summary>最近一次读取失败的原因（诊断用，允许为 null）。</summary>
    [ThreadStatic]
    internal static string? LastError;

    [ThreadStatic]
    internal static string? LastSlideIndexSource;

    /// <summary>读取 COM 属性；失败或为 null 时返回 default。</summary>
    internal static T? TryRead<T>(object? target, Func<dynamic, object?> accessor)
    {
        if (target is null)
        {
            return default;
        }

        try
        {
            var raw = accessor((dynamic)target);
            if (raw is null)
            {
                return default;
            }

            if (raw is T typed)
            {
                return typed;
            }

            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return default;
        }
    }

    /// <summary>调用 COM 方法；失败返回 false（不抛）。</summary>
    internal static bool TryInvoke(object? target, Action<dynamic> action)
    {
        if (target is null)
        {
            return false;
        }

        try
        {
            action((dynamic)target);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>从 SlideShowWindow 反查 Application 对象。</summary>
    internal static object? GetApplication(object? window) => TryRead<object>(window, w => w.Application);

    /// <summary>读取当前页码与总页数（<c>View.Slide.SlideIndex</c> / <c>Presentation.Slides.Count</c>）。</summary>
    /// <remarks>
    /// <b>页码要多源尝试</b>：放映切换的瞬间，<c>View.Slide</c> 可能尚未指向新页
    /// （甚至返回 0 或抛异常）。按可靠性依次尝试四路，命中哪一路记进
    /// <see cref="LastSlideIndexSource"/>，供探针与日志核对。
    /// </remarks>
    internal static (int SlideIndex, int SlideCount) ReadSlidePosition(object? window)
    {
        var view = TryRead<object>(window, w => w.View);
        var slide = TryRead<object>(view, v => v.Slide);
        var index = TryRead<int>(slide, s => s.SlideIndex);
        LastSlideIndexSource = index > 0 ? "View.Slide.SlideIndex" : null;

        if (index <= 0)
        {

            index = TryRead<int>(window, w => w.CurrentShowPosition);
            if (index > 0)
            {
                LastSlideIndexSource = "SlideShowWindow.CurrentShowPosition";
            }
        }

        if (index <= 0)
        {
            // ② View 上的放映位置（部分实现只在 View 上暴露）。
            index = TryRead<int>(view, v => v.CurrentShowPosition);
            if (index > 0)
            {
                LastSlideIndexSource = "SlideShowView.CurrentShowPosition";
            }
        }

        if (index <= 0)
        {
            // ③ 反向路径：演示文稿的放映窗口 → View → Slide。
            var reversePresentation = TryRead<object>(window, w => w.Presentation);
            var showWindow = TryRead<object>(reversePresentation, p => p.SlideShowWindow);
            var reverseView = TryRead<object>(showWindow, s => s.View);
            var reverseSlide = TryRead<object>(reverseView, v => v.Slide);
            index = TryRead<int>(reverseSlide, s => s.SlideIndex);
            if (index > 0)
            {
                LastSlideIndexSource = "Presentation.SlideShowWindow.View.Slide.SlideIndex";
            }
        }

        if (index <= 0)
        {
            LastSlideIndexSource = $"未命中（LastError={LastError ?? "无"}）";
        }

        var presentation = TryRead<object>(window, w => w.Presentation);
        var slides = TryRead<object>(presentation, p => p.Slides);
        var count = TryRead<int>(slides, s => s.Count);

        return (index, count);
    }

    /// <summary>读取集合元素（1 起索引）。</summary>
    internal static object? Item(object? collection, int oneBasedIndex)
        => collection is null || oneBasedIndex < 1
            ? null
            : TryRead<object>(collection, c => c.Item(oneBasedIndex));
}
