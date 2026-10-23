using PPTAssistant.InkML;

using PPTAssistant.Coords;

namespace PPTAssistant.Overlay;

public enum InputMode
{
    /// <summary>鼠标态（默认）：覆盖层 hit-test 穿透，触摸/鼠标直接操作 PowerPoint。</summary>
    Mouse = 0,

    /// <summary>笔态：覆盖层捕获触摸/笔并绘制，输入**不**穿透到宿主。</summary>
    Pen = 1,

    Erase = 2,
}

/// <summary>产生笔迹点的输入设备种类。</summary>
public enum InputDeviceKind
{
    Unknown = 0,

    /// <summary>真实笔（主动式电容笔 / 电磁笔）。</summary>
    Pen = 1,

    /// <summary>手指 / 触摸。</summary>
    Touch = 2,

    /// <summary>鼠标（仅开发调试用：无触摸的机器上验证墨迹链路）。</summary>
    Mouse = 3,
}

/// <summary>一个采样点：位置（覆盖层 DIP）+ 压力 + 设备。</summary>
/// <param name="Position">覆盖层窗口坐标系下的位置（DIP）。</param>
/// <param name="Pressure">归一化压力 0–1；无压感硬件恒为 <see cref="NoPressure"/>。</param>
/// <param name="Device">产生该点的设备。</param>
/// <param name="TimeMilliseconds">相对本笔起笔的毫秒数（InkML 的 T 通道；落盘要用真实采样时间，不能编）。</param>
public readonly record struct StrokePoint(
    DipPoint Position,
    float Pressure,
    InputDeviceKind Device,
    int TimeMilliseconds = 0)
{
    /// <summary>无压感硬件的压力值（WPF 在无压力数据时报 0.5，这里统一压到 0）。</summary>
    public const float NoPressure = 0f;

    /// <summary>有真实压力数据吗（红外/电容屏的「伪压力」不算）。</summary>
    public bool HasPressure => Pressure > 0f;
}

public sealed class InkStrokesErasedEventArgs : EventArgs
{
    public InkStrokesErasedEventArgs(IReadOnlyList<int> strokeIds, int remainingStrokeCount)
    {
        StrokeIds = strokeIds;
        RemainingStrokeCount = remainingStrokeCount;
    }

    /// <summary>本次删除的笔画编号（覆盖层自增编号，与旁路日志的 <c>id</c> 字段一致）。</summary>
    public IReadOnlyList<int> StrokeIds { get; }

    /// <summary>删除后当前页仍保留的笔画数（状态行 / 诊断用）。</summary>
    public int RemainingStrokeCount { get; }
}

/// <summary>笔迹流事件载荷（开始 / 更新 / 结束共用一个类型，减少分配）。</summary>
public sealed class InkStrokeEventArgs : EventArgs
{
    public InkStrokeEventArgs(int strokeId, InputDeviceKind device, IReadOnlyList<StrokePoint> points)
    {
        StrokeId = strokeId;
        Device = device;
        Points = points;
    }

    /// <summary>会话内自增的笔画编号（从 1 开始，便于日志与旁路日志对齐）。</summary>
    public int StrokeId { get; }

    /// <summary>产生本笔的设备。</summary>
    public InputDeviceKind Device { get; }

    /// <summary>本次事件新增的点（<c>StrokeEnded</c> 时为空）。</summary>
    public IReadOnlyList<StrokePoint> Points { get; }
}
