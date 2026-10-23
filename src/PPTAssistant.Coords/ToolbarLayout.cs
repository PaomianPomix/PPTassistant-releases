namespace PPTAssistant.Coords;

/// <summary>工具栏三块区域的布局结果（屏幕 DIP 坐标）。</summary>
/// <param name="CenterBar">底部中央工具条（指针/笔/橡皮/放大镜/色板/笔刷/退出）。</param>
/// <param name="LeftCorner">左下角翻页区（上一页/缩略图/下一页/页码）。</param>
/// <param name="RightCorner">右下角翻页区（与左角对称，服务讲台另一侧）。</param>
public readonly record struct ToolbarLayoutPlan(DipRect CenterBar, DipRect LeftCorner, DipRect RightCorner);

public static class ToolbarLayout
{
    /// <summary>贴底留边（DIP）——太贴边会被某些一体机的底边手势/任务栏吃掉点击。</summary>
    public const double BottomMarginDips = 14;

    /// <summary>角区与屏幕左/右边的留边（DIP）。</summary>
    public const double CornerMarginDips = 14;

    /// <summary>
    /// 规划三块区域。
    /// </summary>
    /// <param name="screen">目标屏的屏幕矩形（DIP）。</param>
    /// <param name="centerWidth">中央条宽（DIP）。</param>
    /// <param name="centerHeight">中央条高（DIP）。</param>
    /// <param name="cornerWidth">角区宽（DIP）。</param>
    /// <param name="cornerHeight">角区高（DIP）。</param>
    /// <param name="bottomMargin">贴底留边（默认 <see cref="BottomMarginDips"/>）。</param>
    /// <param name="cornerMargin">角区左右留边（默认 <see cref="CornerMarginDips"/>）。</param>
    public static ToolbarLayoutPlan Plan(
        DipRect screen,
        double centerWidth,
        double centerHeight,
        double cornerWidth,
        double cornerHeight,
        double bottomMargin = BottomMarginDips,
        double cornerMargin = CornerMarginDips)
    {
        var centerLeft = screen.X + ((screen.Width - centerWidth) / 2);
        var centerTop = screen.Bottom - bottomMargin - centerHeight;

        var cornerTop = screen.Bottom - bottomMargin - cornerHeight;
        var leftCornerLeft = screen.X + cornerMargin;
        var rightCornerLeft = screen.Right - cornerMargin - cornerWidth;

        centerLeft = Math.Clamp(centerLeft, screen.X, Math.Max(screen.X, screen.Right - centerWidth));
        centerTop = Math.Clamp(centerTop, screen.Y, Math.Max(screen.Y, screen.Bottom - centerHeight));
        leftCornerLeft = Math.Clamp(leftCornerLeft, screen.X, Math.Max(screen.X, screen.Right - cornerWidth));
        rightCornerLeft = Math.Clamp(rightCornerLeft, screen.X, Math.Max(screen.X, screen.Right - cornerWidth));
        cornerTop = Math.Clamp(cornerTop, screen.Y, Math.Max(screen.Y, screen.Bottom - cornerHeight));

        return new ToolbarLayoutPlan(
            new DipRect(centerLeft, centerTop, centerWidth, centerHeight),
            new DipRect(leftCornerLeft, cornerTop, cornerWidth, cornerHeight),
            new DipRect(rightCornerLeft, cornerTop, cornerWidth, cornerHeight));
    }

    public static bool AreDisjoint(ToolbarLayoutPlan plan)
        => plan.CenterBar.Intersect(plan.LeftCorner).Area <= 0
           && plan.CenterBar.Intersect(plan.RightCorner).Area <= 0
           && plan.LeftCorner.Intersect(plan.RightCorner).Area <= 0;
}

public static class PageLabel
{
    /// <summary>格式化页码。</summary>
    /// <param name="index">当前页（1 起；≤0 表示未知）。</param>
    /// <param name="count">总页数（≤0 表示未知）。</param>
    public static string Format(int index, int count)
    {
        var indexText = index > 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        var countText = count > 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        return $"{indexText} / {countText}";
    }
}
