using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Session;

public readonly record struct PenStyleSnapshot(double WidthDips, InkColor Color);

/// <summary>运行时笔样式的变化（供上层持久化）。</summary>
/// <param name="WidthDips">当前笔宽（DIP）。</param>
/// <param name="Color">当前笔色。</param>
/// <param name="Recent">最近使用色（新色顶置）。</param>
public sealed record PenAppearance(double WidthDips, InkColor Color, IReadOnlyList<InkColor> Recent);

public sealed class PenStyleState
{
    private List<InkColor> _recent;

    /// <param name="widthDips">初始笔宽（DIP）；越界会被夹进 <see cref="PenWidthRange"/>。</param>
    /// <param name="color">初始笔色。</param>
    /// <param name="recent">初始最近使用色（可为 null）。</param>
    public PenStyleState(double widthDips, InkColor color, IReadOnlyList<InkColor>? recent = null)
    {
        WidthDips = PenWidthRange.Clamp(widthDips);
        Color = color;
        _recent = Normalize(recent);
    }

    /// <summary>当前笔宽（DIP，恒在 <see cref="PenWidthRange"/> 内）。</summary>
    public double WidthDips { get; private set; }

    /// <summary>当前笔色。</summary>
    public InkColor Color { get; private set; }

    /// <summary>最近使用色（新色顶置，最多 <see cref="InkPalette.MaxRecent"/> 个）。</summary>
    public IReadOnlyList<InkColor> Recent => _recent;

    /// <summary>改笔宽（夹进合法区间）；返回是否真的变了。</summary>
    public bool SetWidth(double widthDips)
    {
        var clamped = PenWidthRange.Clamp(widthDips);
        if (Math.Abs(clamped - WidthDips) < 0.001)
        {
            return false;
        }

        WidthDips = clamped;
        return true;
    }

    /// <summary>改笔色并把新色并入「最近使用」；返回是否真的变了。</summary>
    public bool SetColor(InkColor color)
    {
        PushRecent(color);
        if (color == Color)
        {
            return false;
        }

        Color = color;
        return true;
    }

    /// <summary>把颜色并入最近使用（去重 + 顶置 + 截断）。</summary>
    public void PushRecent(InkColor color) => _recent = [.. InkPalette.AddRecent(_recent, color)];

    /// <summary>取起笔快照——落盘时用这个值，而不是当时的「当前值」。</summary>
    public PenStyleSnapshot Snapshot() => new(WidthDips, Color);

    /// <summary>导出为持久化用的外观记录。</summary>
    public PenAppearance ToAppearance() => new(WidthDips, Color, Recent);

    private static List<InkColor> Normalize(IReadOnlyList<InkColor>? recent)
    {
        var result = new List<InkColor>(InkPalette.MaxRecent);
        if (recent is null)
        {
            return result;
        }

        foreach (var color in recent)
        {
            if (!result.Contains(color) && result.Count < InkPalette.MaxRecent)
            {
                result.Add(color);
            }
        }

        return result;
    }
}
