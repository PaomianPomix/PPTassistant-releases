using PPTAssistant.Overlay.Native;

namespace PPTAssistant.Overlay;

public enum OverlaySurfaceMode
{
    /// <summary>分层透明窗口（<c>AllowsTransparency = true</c>）——**唯一能让覆盖层真的透明的路径**。</summary>
    Layered = 0,

    BlurBehind = 1,
}

public readonly record struct SurfaceModeDecision(
    OverlaySurfaceMode Requested,
    OverlaySurfaceMode Effective,
    string Reason)
{
    /// <summary>是否发生了回落（请求的模式没有生效）。</summary>
    public bool FellBack => Requested != Effective;

    public override string ToString()
        => $"请求={Requested} 生效={Effective} 回落={FellBack}（{Reason}）";
}

public static class OverlaySurfaceSelector
{

    public static SurfaceModeDecision DecideForConstruction(
        OverlaySurfaceMode requested,
        bool compositionEnabled)
    {
        var composition = compositionEnabled ? "DWM 合成开" : "DWM 合成关";

        return requested switch
        {
            OverlaySurfaceMode.Layered =>
                new SurfaceModeDecision(requested, OverlaySurfaceMode.Layered, $"分层窗口（{composition}）"),

            _ => new SurfaceModeDecision(
                requested,
                OverlaySurfaceMode.Layered,
                $"非分层窗口无法提供所需的透明效果，回落分层（{composition}）"),
        };
    }
}
