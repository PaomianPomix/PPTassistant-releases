namespace PPTAssistant.InkML;

/// <summary>HSV 颜色（H 0–360°，S / V 0–1）。取色器的中间表示。</summary>
public readonly record struct HsvColor(double Hue, double Saturation, double Value);

public static class InkPalette
{
    /// <summary>最近使用色的容量（去重、新色顶置）。</summary>
    public const int MaxRecent = 5;

    public static readonly IReadOnlyList<InkColor> Presets =
    [
        new(0xFF, 0x00, 0x00), 
        new(0x00, 0x85, 0xCD), 
        new(0x00, 0x00, 0x00), 
        new(0x00, 0xB0, 0x50), // 绿（浅底课件醒目）
        new(0xFF, 0x8C, 0x00), // 橙
        new(0x59, 0x59, 0x59), // 深灰（浅底批注）
        new(0x00, 0xB8, 0xD4), // 青（深底醒目）
        new(0x70, 0x30, 0xA0), // 紫
    ];

    public static readonly IReadOnlyList<InkColor> LegacyThreeColors =
    [
        new(0xFF, 0x00, 0x00),
        new(0x00, 0x85, 0xCD),
        new(0x00, 0x00, 0x00),
    ];

    /// <summary>
    /// 把一个新选中的颜色并入「最近使用」：**去重 + 新色顶置 + 截断到 <see cref="MaxRecent"/>**。
    /// </summary>
    /// <remarks>返回新列表（不修改入参）——最近使用色是配置的一部分，改它要能让调用方显式写盘。</remarks>
    public static IReadOnlyList<InkColor> AddRecent(IReadOnlyList<InkColor>? recent, InkColor color)
    {
        var result = new List<InkColor>(MaxRecent) { color };
        if (recent is not null)
        {
            foreach (var existing in recent)
            {
                if (existing == color || result.Count >= MaxRecent)
                {
                    continue;
                }

                result.Add(existing);
            }
        }

        return result;
    }

    /// <summary>RGB → HSV（saturation / value 为 0 时色相定义为 0，避免除零）。</summary>
    public static HsvColor RgbToHsv(InkColor color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta <= double.Epsilon)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        var saturation = max <= double.Epsilon ? 0 : delta / max;
        return new HsvColor(hue, saturation, max);
    }

    /// <summary>HSV → RGB（色相取模到 [0,360)，S/V 夹到 [0,1]）。</summary>
    public static InkColor HsvToRgb(HsvColor hsv)
    {
        var hue = hsv.Hue % 360;
        if (hue < 0)
        {
            hue += 360;
        }

        var saturation = Math.Clamp(hsv.Saturation, 0, 1);
        var value = Math.Clamp(hsv.Value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - c;

        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new InkColor(ToByte(r1 + m), ToByte(g1 + m), ToByte(b1 + m));
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
