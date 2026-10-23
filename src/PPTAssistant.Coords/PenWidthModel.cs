namespace PPTAssistant.Coords;

public static class PenWidthModel
{
    /// <summary>中位压力：该压力下宽度恰好等于基准宽度（与 InkML 语义对齐）。</summary>
    public const double NeutralPressure = 0.5;

    /// <summary>调制上限：压力从 0 → 1 时，宽度在基准的 [1-gain, 1+gain] 区间内变化。</summary>
    public const double PressureGain = 0.5;

    /// <summary>允许的最小笔宽（DIP）：压力极小时也不能细到看不见（否则像断笔）。</summary>
    public const double MinWidthDips = 1.0;

    public static double WidthAt(double baseWidthDips, float pressure, bool hasPressure)
    {
        var baseWidth = Math.Max(MinWidthDips, baseWidthDips);
        if (!hasPressure)
        {
            return baseWidth;
        }

        var normalized = Math.Clamp(pressure, 0f, 1f);
        // 中位压力 = 1.0 倍；两端 = 1 ∓ gain。乘以 2 是把 [0,1] 映射到 [-1,1]。
        var factor = 1.0 + (PressureGain * ((2.0 * normalized) - 1.0));
        return Math.Max(MinWidthDips, baseWidth * factor);
    }
}
