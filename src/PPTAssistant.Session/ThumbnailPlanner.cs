namespace PPTAssistant.Session;

public static class ThumbnailPlanner
{
    /// <summary>规划要保证可用的页号（升序，1 起，已夹在 [1, slideCount] 内）。</summary>
    public static IReadOnlyList<int> Plan(int currentIndex, int slideCount, int radius)
    {
        if (slideCount <= 0)
        {
            return [];
        }

        var current = Math.Clamp(currentIndex <= 0 ? 1 : currentIndex, 1, slideCount);
        var r = Math.Max(0, radius);
        var from = Math.Max(1, current - r);
        var to = Math.Min(slideCount, current + r);

        var plan = new List<int>(to - from + 1);
        for (var i = from; i <= to; i++)
        {
            plan.Add(i);
        }

        return plan;
    }

    /// <summary>从计划里挑出「还没有缩略图」的页号（保持升序）。</summary>
    public static IReadOnlyList<int> MissingFrom(IReadOnlyList<int> plan, IReadOnlyCollection<int> alreadyExported)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(alreadyExported);

        var missing = new List<int>(plan.Count);
        foreach (var index in plan)
        {
            if (!alreadyExported.Contains(index))
            {
                missing.Add(index);
            }
        }

        return missing;
    }
}
