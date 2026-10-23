namespace PPTAssistant.Host;

/// <summary>宿主类型。</summary>
public enum HostKind
{
    /// <summary>未识别（既不是 POWERPNT 也不是 wpp）。</summary>
    Unknown = 0,

    /// <summary>Microsoft PowerPoint（POWERPNT.EXE）。</summary>
    PowerPoint = 1,

    /// <summary>WPS 演示（wpp.exe）。</summary>
    WpsPresentation = 2,
}

public sealed record PptHostInfo(
    HostKind Kind,
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    string Version,
    IntPtr MainWindowHandle)
{

    public bool IsNativeInkSupported => Kind switch
    {
        HostKind.WpsPresentation => true,
        HostKind.PowerPoint => VersionMajor >= 16,
        _ => false,
    };

    /// <summary>
    /// 身份是否已解析（进程与宿主类型都能确定）。
    /// </summary>
    /// <remarks>未解析通常意味着「挂接得太早」：宿主进程刚起来、窗口还没建，
    /// 此时 <see cref="ProcessId"/> 为 0、<see cref="Kind"/> 为 Unknown。
    /// 监视器会在未解析期间反复调用 <c>PptHost.RefreshIdentity()</c> 补全。</remarks>
    public bool IsResolved => ProcessId > 0 && Kind != HostKind.Unknown;

    /// <summary>版本主号；解析失败为 0。</summary>
    public int VersionMajor    {
        get
        {
            var span = Version.AsSpan().Trim();
            var dot = span.IndexOf('.');
            var head = dot > 0 ? span[..dot] : span;
            return int.TryParse(head, out var major) ? major : 0;
        }
    }

    public override string ToString()
        => $"{Kind} v{Version} pid={ProcessId} ({ProcessName}) ink={(IsNativeInkSupported ? "ok" : "降级")}";
}

/// <summary>放映状态快照（门控与坐标映射的输入）。</summary>
/// <param name="IsShowActive">是否正在放映（<c>SlideShowWindows.Count &gt; 0</c>）。</param>
/// <param name="ShowType"><c>SlideShowSettings.ShowType</c> 原始值；1 = <c>ppShowTypeSpeaker</c>。</param>
/// <param name="SlideIndex">当前页（1 起，未知为 0）。</param>
/// <param name="SlideCount">总页数（未知为 0）。</param>
/// <param name="PresentationPath">演示文稿完整路径（可能为空）。</param>
public sealed record SlideShowState(
    bool IsShowActive,
    int ShowType,
    int SlideIndex,
    int SlideCount,
    string? PresentationPath)
{

    public const int ShowTypeSpeaker = 1;

    public bool IsSpeakerShow => IsShowActive && ShowType == ShowTypeSpeaker;

    public static readonly SlideShowState Empty = new(false, 0, 0, 0, null);
}
