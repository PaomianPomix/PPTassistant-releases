using System.Globalization;

namespace PPTAssistant.InkML;

public static class InkMlDialect
{
    /// <summary>InkML 命名空间（[MS-ODRAWXML] 用的就是 W3C 的）。</summary>
    public const string Namespace = "http://www.w3.org/2003/InkML";

    /// <summary>约定前缀。</summary>
    public const string Prefix = "inkml";

    public const double Resolution = 54.82309;

    /// <summary>X 通道声明的最大值（照抄 Office 的取值，覆盖整页板书绰绰有余）。</summary>
    public const int ChannelMaxX = 2944;

    /// <summary>Y 通道声明的最大值。</summary>
    public const int ChannelMaxY = 1840;

    /// <summary>T 通道声明的最大值（照抄 Office 的写法与量级）。</summary>
    public const string ChannelMaxT = "2.14748E9";

    public const int ChannelMaxF = 1023;

    /// <summary>1 厘米 = 360000 EMU（OOXML 的通用长度单位）。</summary>
    public const int EmuPerCm = 360000;

    /// <summary>1 点（pt）= 12700 EMU。</summary>
    public const int EmuPerPoint = 12700;

    /// <summary>每 1 pt 对应多少本地单位（按统一分辨率，保证 X/Y 比例不失真）。</summary>
    public const double UnitsPerPoint = Resolution * 2.54 / 72.0;

    /// <summary>
    /// 按包围盒挑一个安全的「每 pt 单位数」：既要保持 X/Y 等比（否则渲染端把 trace 拉伸到 xfrm 框时会变形），
    /// 又不能让本地坐标超出通道声明的 max（超了会被裁剪或拒收）。
    /// </summary>
    public static double UnitsPerPointFor(InkBounds bounds)
    {
        var unitsPerPoint = UnitsPerPoint;

        // 两个方向各自算一个上限，取更严的那个，保证两个通道都不超 max。
        if (bounds.Width > 0)
        {
            unitsPerPoint = Math.Min(unitsPerPoint, (ChannelMaxX - 1) / bounds.Width);
        }

        if (bounds.Height > 0)
        {
            unitsPerPoint = Math.Min(unitsPerPoint, (ChannelMaxY - 1) / bounds.Height);
        }

        // 缩得太狠说明课件尺寸异常（比如自定义超大页面）；给一个下限，避免所有点塌到同一个整数上。
        return Math.Max(unitsPerPoint, 0.01);
    }

    /// <summary>pt → 本地单位。</summary>
    public static int ToUnits(double points, double unitsPerPoint)
        => (int)Math.Round(points * unitsPerPoint, MidpointRounding.AwayFromZero);

    /// <summary>本地单位 → pt。</summary>
    public static double FromUnits(int units, double unitsPerPoint)
        => units / unitsPerPoint;

    public static long ToEmu(double points) => (long)Math.Round(points * EmuPerPoint, MidpointRounding.AwayFromZero);

    /// <summary>pt → 厘米（写 brushProperty width 用）。</summary>
    public static double ToCentimeters(double points) => points * 2.54 / 72.0;

    public static string FormatTimestamp(DateTime timestampUtc)
    {
        var value = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        var now = DateTime.UtcNow;
        if (value > now)
        {
            value = now;
        }

        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>压力（0–1）→ F 通道整数（0–<see cref="ChannelMaxF"/>）。</summary>
    public static int ToPressureChannel(float pressure)
        => (int)Math.Round(Math.Clamp(pressure, 0f, 1f) * ChannelMaxF, MidpointRounding.AwayFromZero);

    /// <summary>F 通道整数 → 压力（0–1）。</summary>
    public static float FromPressureChannel(int value)
        => (float)Math.Clamp(value / (double)ChannelMaxF, 0.0, 1.0);

    /// <summary>数值 → 不变文化的字符串（必须用 <c>.</c> 作小数点，否则非英文区域会写出 <c>0,15</c>）。</summary>
    public static string Invariant(double value, string format = "0.#####")
        => value.ToString(format, CultureInfo.InvariantCulture);
}
