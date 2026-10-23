using PPTAssistant.InkML;
using System.Diagnostics;
using PPTAssistant.Coords;
using PPTAssistant.Host;
using PPTAssistant.Overlay;

namespace PPTAssistant.Session;

/// <summary>
/// 会话编排的纯逻辑部件（**不碰 COM、不碰 UI**，因此可以单元测试）。
/// </summary>
/// <remarks>
/// 这里刻意把「规则」从「执行」里剥出来：状态机本身只做编排，
/// 而防抖窗口、门控判据这类容易写错又有明确规格的东西放在本文件里，配单元测试。
/// </remarks>
public static class ShowGate
{

    public static bool IsAccepted(int showType) => showType == SlideShowState.ShowTypeSpeaker;
}

public sealed class LeadingEdgeDebouncer
{
    private readonly long _windowTicks;
    private readonly Func<long> _timestamp;
    private long _lastAccepted;

    /// <param name="window">防抖窗口。</param>
    /// <param name="timestamp">时间戳源；默认 <see cref="Stopwatch.GetTimestamp"/>（测试可注入）。</param>
    public LeadingEdgeDebouncer(TimeSpan window, Func<long>? timestamp = null)
    {
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "防抖窗口不能为负。");
        }

        Window = window;
        _windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _lastAccepted = long.MinValue / 2;
    }

    /// <summary>防抖窗口。</summary>
    public TimeSpan Window { get; }

    /// <summary>尝试受理一次请求；窗口期内返回 false。</summary>
    public bool TryAccept()
    {
        var now = _timestamp();
        if (_windowTicks > 0 && now - _lastAccepted < _windowTicks)
        {
            return false;
        }

        _lastAccepted = now;
        return true;
    }

    public void Reset() => _lastAccepted = long.MinValue / 2;
}

public sealed record SessionOptions
{

    public TimeSpan PageTurnDebounce { get; init; } = TimeSpan.FromMilliseconds(30);

    public double PenWidthDips { get; init; } = 6.0;

    public InkColor PenColor { get; init; } = InkColor.Default;

    public IReadOnlyList<InkColor> RecentPenColors { get; init; } = [];

    public bool IgnoreTouchWhenPenPresent { get; init; } = true;

    /// <summary>是否允许鼠标绘制（开发期验证用；正式场景由触摸/笔主导）。</summary>
    public bool AllowMouseDrawing { get; init; } = true;

    public bool SidecarEnabled { get; init; } = true;

    /// <summary>
    /// 旁路日志根目录；null = 默认 <c>%LOCALAPPDATA%\PPTAssistant\sessions</c>（测试会指定临时目录）。
    /// </summary>
    public string? SidecarRoot { get; init; }

    public WriteBackMode WriteBackMode { get; init; } = WriteBackMode.Ask;

    /// <summary>写回时的备份目录；null = 默认 %LOCALAPPDATA%\PPTAssistant\backups。</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>写回时是否重新打开课件还给老师（默认是）。</summary>
    public bool ReopenAfterWriteBack { get; init; } = true;

    /// <summary>启动时清理超过这个时长的残留日志（避免被强杀过的会话永久占盘）。</summary>
    public TimeSpan SidecarRetention { get; init; } = TimeSpan.FromDays(7);

    public double MagnifierZoom { get; init; } = 2.0;

    public int ThumbnailRadius { get; init; } = 6;

    /// <summary>缩略图导出像素宽（16:9）。</summary>
    public int ThumbnailPixelWidth { get; init; } = 320;

    /// <summary>缩略图导出像素高。</summary>
    public int ThumbnailPixelHeight { get; init; } = 180;

    /// <summary>默认值。</summary>
    public static SessionOptions Default { get; } = new();
}

public enum WriteBackMode
{
    /// <summary>弹窗询问（产品默认）——改用户文件必须得到明确同意。</summary>
    Ask = 0,

    /// <summary>不询问，直接保留（自动化验证用）。</summary>
    AutoRetain = 1,

    /// <summary>不询问，直接丢弃。</summary>
    AutoDiscard = 2,
}

/// <summary>
/// 写回模式的字符串 ↔ 枚举转换（配置里存的是名字，改配置的人看得到、改得动）。
/// </summary>
/// <remarks>
/// 放在 Session 层是为了**可单测**：配置解析是「非法值回落」最容易出错的地方
/// （写错一个字符串就变成静默不写回，而老师不会去看 JSON）。App 层的 <c>AppConfig</c> 直接用它。
/// </remarks>
public static class WriteBackModes
{
    /// <summary>解析；非法 / 空值一律回落 <see cref="WriteBackMode.Ask"/>（最安全的那个：至少会问）。</summary>
    public static WriteBackMode Parse(string? text)
        => Enum.TryParse<WriteBackMode>(text, ignoreCase: true, out var mode) ? mode : WriteBackMode.Ask;

    /// <summary>转成配置里存的字符串。</summary>
    public static string Format(WriteBackMode mode) => mode.ToString();
}
