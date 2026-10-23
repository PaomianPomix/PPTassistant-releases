using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Session.Sidecar;

/// <summary>
/// 旁路日志的表头（**第一行**）——崩溃恢复就靠它配对。
/// </summary>
/// <param name="Version">格式版本（将来加字段时靠它判断能否读懂）。</param>
/// <param name="Fingerprint">完整课件指纹（路径 + 尺寸 + 修改时间 的哈希）。</param>
/// <param name="DocumentPath">课件原始路径（给用户看的，也让恢复时不依赖目录名）。</param>
/// <param name="DocumentSizeBytes">开日志时课件的字节数（配对用）。</param>
/// <param name="DocumentLastWriteUtc">开日志时课件的最后写入时间（配对用）。</param>
/// <param name="SlideWidthPoints">页面宽度（pt）——写回时 InkML 的坐标基准。</param>
/// <param name="SlideHeightPoints">页面高度（pt）。</param>
/// <param name="StartedUtc">开场时间。</param>
/// <param name="ToolVersion">工具版本（诊断用）。</param>
public sealed record SidecarHeader(
    int Version,
    string Fingerprint,
    string DocumentPath,
    long DocumentSizeBytes,
    DateTime DocumentLastWriteUtc,
    double SlideWidthPoints,
    double SlideHeightPoints,
    DateTime StartedUtc,
    string ToolVersion)
{

    public const int CurrentVersion = 2;

    /// <summary>页面尺寸（InkML 坐标基准）。</summary>
    public SlideSize SlideSize => new(SlideWidthPoints, SlideHeightPoints);
}

/// <summary>一份可恢复的旁路日志（文件路径 + 表头 + 已解析内容）。</summary>
/// <param name="Path">日志文件路径。</param>
/// <param name="Header">表头。</param>
/// <param name="Strokes">按顺序读回的笔画。</param>
/// <param name="PagesSeen">日志里出现过的页码。</param>
/// <param name="DamagedLineCount">读取时跳过的坏行数（进程被杀时最后一行可能残缺——这是**正常**的，不是损坏）。</param>
public sealed record SidecarSession(
    string Path,
    SidecarHeader Header,
    IReadOnlyList<SidecarStroke> Strokes,
    IReadOnlyList<int> PagesSeen,
    int DamagedLineCount)
{
    /// <summary>有没有值得恢复的墨迹。</summary>
    public bool HasInk => Strokes.Count > 0;

    public IReadOnlyDictionary<int, InkDrawing> BuildDrawings()
    {
        var byPage = new Dictionary<int, InkDrawing>();
        foreach (var group in Strokes.GroupBy(static s => s.PageIndex))
        {
            var strokes = group
                .Select(static s => new InkStroke(
                    s.Points.Select(static p => new InkPoint(p.X, p.Y, p.Pressure, p.T)).ToList(),
                    s.WidthPoints,
                    InkColor.Parse(s.ColorHex)))
                .ToList();

            byPage[group.Key] = new InkDrawing(Header.SlideSize, strokes);
        }

        return byPage;
    }
}

/// <summary>日志里的一笔（幻灯片坐标，pt）。</summary>
/// <param name="PageIndex">页码（1 起）。</param>
/// <param name="WidthPoints">笔宽（pt）。</param>
/// <param name="ColorHex">颜色（#RRGGBB）。</param>
/// <param name="Device">设备（诊断用）。</param>
/// <param name="Points">采样点。</param>
/// <param name="Id">
/// 笔画编号（会话内唯一；旧日志没有这个字段时为 0）。
/// **擦除记录靠它定位笔画**——没有编号的旧日志不会出现 erase 记录，因此 0 不会误删。
/// </param>
public sealed record SidecarStroke(
    int PageIndex,
    double WidthPoints,
    string ColorHex,
    string Device,
    IReadOnlyList<SidecarPoint> Points,
    int Id = 0);

/// <summary>日志里的一个采样点。</summary>
public readonly record struct SidecarPoint(double X, double Y, float Pressure, int T);

/// <summary>恢复候选（给启动时的「恢复 / 丢弃」提示用）。</summary>
/// <param name="Session">读出来的会话。</param>
/// <param name="MatchKind">与当前课件文件的对齐程度。</param>
public sealed record SidecarCandidate(SidecarSession Session, SidecarMatchKind MatchKind);

/// <summary>旁路日志与当前课件文件的对齐程度。</summary>
public enum SidecarMatchKind
{
    /// <summary>路径、字节数、修改时间全部一致——可以放心恢复。</summary>
    Exact = 0,

    /// <summary>路径相同但文件已变（例如老师又保存过）——恢复前要提醒坐标可能偏移。</summary>
    PathOnly = 1,
}
