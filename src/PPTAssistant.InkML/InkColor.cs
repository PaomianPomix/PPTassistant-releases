namespace PPTAssistant.InkML;

public readonly record struct InkColor(byte R, byte G, byte B)
{

    public static readonly InkColor Default = new(0xD9, 0x1B, 0x1B);

    /// <summary>按 <c>#RRGGBB</c> / <c>RRGGBB</c> 解析；失败返回 <see cref="Default"/>。</summary>
    public static InkColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Default;
        }

        var span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length != 6 ||
            !byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(span[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Default;
        }

        return new InkColor(r, g, b);
    }

    /// <summary>转 <c>#RRGGBB</c>（InkML brushProperty / 配置持久化都用这个形式）。</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}
