namespace PPTAssistant.Coords;

/// <summary>
/// 从多屏里挑出放映窗口所在的那一块。
/// </summary>
/// <remarks>
/// <para><b>两套入口，用途不同，别混用坐标系</b>：
/// <list type="bullet">
///   <item><see cref="PickBestForComRect"/> —— 宿主只给得出「点/DIP」矩形时用它（DIP 空间比较）；</item>
///   <item><see cref="PickBest"/> —— 手里有<b>精确物理像素</b>矩形（<c>GetWindowRect</c> 之类）时用它。</item>
/// </list>
/// 把这两件事混在一起是坐标映射类 bug 的常见来源，所以这里干脆拆成两个方法名。</para>
/// <para>判别用「最大相交面积 / 归一化覆盖率」而不是「必须包含 / 完全相等」：Windows 的窗口矩形
/// 与实际显示器矩形常常差几个像素（边框、最大化偏移、DPI 取整），严格包含判断会挑不到屏。
/// 全部不相交时退化为「中心点最近」，保证任何情况下都有屏可用（绝不返回 null 让上层无处安放）。</para>
/// </remarks>
public static class ScreenSelector
{
    /// <summary>用「宿主报告的放映窗口矩形（DIP）」挑屏——挑不到时按 DIP 中心就近，最后退化为主屏。</summary>
    public static ScreenInfo? PickBestForComRect(IReadOnlyList<ScreenInfo> screens, DipRect comRectDips)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
        {
            return null;
        }

        if (screens.Count == 1)
        {
            // 单屏（教室最常见）：不存在歧义，也不必猜。
            return screens[0];
        }

        if (comRectDips.Area <= 0)
        {
            // 退化矩形（读不到窗口尺寸）→ 主屏优先，最后取第一块。
            return PickPrimary(screens) ?? screens[0];
        }

        ScreenInfo? best = null;
        var bestCoverage = 0.0;
        foreach (var screen in screens)
        {
            // 归一化覆盖率：不同屏的 DIP 面积可能相差很大（4K vs 1080p），
            // 用「相交面积 ÷ 窗口面积」衡量的是「这块屏装下了窗口的多大比例」。
            var coverage = screen.Bounds.Intersect(comRectDips).Area / comRectDips.Area;
            if (coverage > bestCoverage)
            {
                bestCoverage = coverage;
                best = screen;
            }
        }

        if (best is not null)
        {
            return best;
        }

        return PickNearestByDips(screens, comRectDips.CenterX, comRectDips.CenterY)
               ?? PickPrimary(screens)
               ?? screens[0];
    }

    /// <summary>按与 <paramref name="windowBoundsPixels"/>（**物理像素**）的相交面积挑屏。</summary>
    public static ScreenInfo? PickBest(IReadOnlyList<ScreenInfo> screens, DipRect windowBoundsPixels)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
        {
            return null;
        }

        ScreenInfo? best = null;
        var bestArea = -1.0;
        foreach (var screen in screens)
        {
            var area = screen.PhysicalBounds.Intersect(windowBoundsPixels).Area;
            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
        }

        if (bestArea > 0)
        {
            return best;
        }

        // 完全不相交（例如取不到窗口句柄、矩形退化为 0）→ 取中心最近的一块。
        return PickNearest(screens, windowBoundsPixels.CenterX, windowBoundsPixels.CenterY)
               ?? PickPrimary(screens)
               ?? screens[0];
    }

    /// <summary>挑中心距目标点最近的屏（输入为**物理像素**）。</summary>
    public static ScreenInfo? PickNearest(IReadOnlyList<ScreenInfo> screens, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return PickNearest(
            screens,
            x,
            y,
            static screen => screen.PhysicalBounds);
    }

    /// <summary>挑中心距目标点最近的屏（输入为 **DIP**）。</summary>
    public static ScreenInfo? PickNearestByDips(IReadOnlyList<ScreenInfo> screens, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return PickNearest(
            screens,
            x,
            y,
            static screen => screen.Bounds);
    }

    /// <summary>主屏（没有标记主屏时返回 null）。</summary>
    public static ScreenInfo? PickPrimary(IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        foreach (var screen in screens)
        {
            if (screen.IsPrimary)
            {
                return screen;
            }
        }

        return null;
    }

    /// <summary>按设备名精确匹配（拿得到权威设备名时用）。</summary>
    public static ScreenInfo? PickByDeviceName(IReadOnlyList<ScreenInfo> screens, string? deviceName)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (string.IsNullOrEmpty(deviceName))
        {
            return null;
        }

        foreach (var screen in screens)
        {
            if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }

    private static ScreenInfo? PickNearest(
        IReadOnlyList<ScreenInfo> screens,
        double x,
        double y,
        Func<ScreenInfo, DipRect> projection)
    {
        if (screens.Count == 0)
        {
            return null;
        }

        ScreenInfo? best = null;
        var bestDistance = double.MaxValue;
        foreach (var screen in screens)
        {
            var bounds = projection(screen);
            var dx = bounds.CenterX - x;
            var dy = bounds.CenterY - y;
            var distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = screen;
            }
        }

        return best;
    }
}
