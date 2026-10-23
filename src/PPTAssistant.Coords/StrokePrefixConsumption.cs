namespace PPTAssistant.Coords;

public static class StrokePrefixConsumption
{
    /// <summary>
    /// 书写过程中要为「等下一个点」保留的原始点数。
    /// Catmull-Rom 段 <c>P[i] → P[i+1]</c> 需要 <c>P[i-1]</c> 与 <c>P[i+2]</c> 两个邻居，
    /// 所以最后两段在拿到新点前画不准。4 个点足以覆盖这两个邻居。
    /// </summary>
    public const int LiveTailPoints = 4;

    /// <summary>
    /// 收笔时不再保留尾巴——剩余原始点必须全部并入前缀，否则最后 4 个点永远画不出来。
    /// </summary>
    public const int FlushTailPoints = 0;

    /// <summary>
    /// 计算本次应当消费的原始点区间（闭区间，下标口径）。
    /// </summary>
    /// <param name="rawCount">当前累计的原始采样点数。</param>
    /// <param name="consumedCount">已消费到的位置（下次从这里开始）。</param>
    /// <param name="tailPoints">要保留的尾巴点数（书写中 = <see cref="LiveTailPoints"/>，收笔 = <see cref="FlushTailPoints"/>）。</param>
    /// <returns>
    /// 非空表示可消费：<c>From</c>/<c>To</c> 是闭区间下标，且保证 <c>0 ≤ From ≤ To ≤ rawCount − 1</c>；
    /// 空区间（<c>Count == 0</c>）表示本次没有可消费的点。
    /// </returns>
    public static (int From, int To, int Count) Plan(int rawCount, int consumedCount, int tailPoints)
    {
        if (rawCount <= 0)
        {
            return (0, 0, 0);
        }

        var tail = Math.Max(0, tailPoints);
        var start = Math.Max(0, Math.Min(consumedCount, rawCount));

        // 可消费点数的**开区间上界**：总数减去要留给下一轮的尾巴。
        // 于是可消费的下标范围是 [start, exclusiveEnd − 1]。
        var exclusiveEnd = rawCount - tail;

        if (exclusiveEnd <= start)
        {
            if (rawCount <= tail + 1)
            {
                // 整条笔都短于平滑所需 —— 全部原样消费。
                return start >= rawCount ? (0, 0, 0) : (start, rawCount - 1, rawCount - start);
            }

            return (0, 0, 0);
        }

        var end = exclusiveEnd - 1;
        if (end < start)
        {
            return (0, 0, 0);
        }

        return (start, end, end - start + 1);
    }
}
