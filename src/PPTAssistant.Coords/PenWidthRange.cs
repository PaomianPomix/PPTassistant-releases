namespace PPTAssistant.Coords;

public static class PenWidthRange
{
    /// <summary>最小笔宽（DIP）。</summary>
    public const double MinDips = 2.0;

    /// <summary>最大笔宽（DIP）。</summary>
    public const double MaxDips = 50.0;

    /// <summary>默认笔宽（DIP）——与原 <c>AppConfig.PenWidthDips</c> 的默认值一致。</summary>
    public const double DefaultDips = 6.0;

    /// <summary>滑杆的最小步进（DIP）——0.5 DIP 在 4K 屏上肉眼可辨。</summary>
    public const double StepDips = 0.5;

    /// <summary>值是否落在合法区间（含端点）。</summary>
    public static bool IsValid(double value)
        => double.IsFinite(value) && value >= MinDips && value <= MaxDips;

    /// <summary>把任意输入收进合法区间；非有限数（NaN/∞）回落默认值。</summary>
    /// <remarks>用于滑杆这类「用户只能往里拖」的入口——夹紧比丢弃更符合直觉（拖到底就停在端点）。</remarks>
    public static double Clamp(double value)
        => double.IsFinite(value) ? Math.Clamp(value, MinDips, MaxDips) : DefaultDips;

    /// <summary>把输入吸附到 <see cref="StepDips"/> 的整数倍并夹进区间。</summary>
    public static double Snap(double value)
    {
        var clamped = Clamp(value);
        var snapped = Math.Round(clamped / StepDips, MidpointRounding.AwayFromZero) * StepDips;
        return Clamp(snapped);
    }

    /// <summary>显示用的一位小数格式（滑杆数值框与状态行共用，避免两处格式不一致）。</summary>
    public static string Format(double value)
        => Clamp(value).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
