namespace PPTAssistant.Coords;

public readonly record struct ScreenInfo(
    string DeviceName,
    DipRect Bounds,
    DipRect PhysicalBounds,
    bool IsPrimary,
    double DisplayScale)
{
    /// <summary>按 DIP 矩形与缩放推出物理矩形的便捷构造（单元测试用）。</summary>
    public ScreenInfo(string deviceName, DipRect bounds, bool isPrimary, double displayScale)
        : this(
            deviceName,
            bounds,
            new DipRect(
                bounds.X * displayScale,
                bounds.Y * displayScale,
                bounds.Width * displayScale,
                bounds.Height * displayScale),
            isPrimary,
            displayScale)
    {
    }

    public double NormalizedScale => UnitConverter.NormalizeDisplayScale(DisplayScale);

    public override string ToString()
        => $"{DeviceName} {Bounds} px={PhysicalBounds} scale={NormalizedScale:0.##}{(IsPrimary ? " [primary]" : string.Empty)}";
}
