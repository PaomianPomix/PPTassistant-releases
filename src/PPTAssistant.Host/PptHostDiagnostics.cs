using PPTAssistant.Host.Native;

namespace PPTAssistant.Host;

/// <summary>
/// 挂接诊断（给开发探针与日志用）。
/// </summary>
/// <remarks>
/// 挂接失败时需要能区分三种原因：宿主没运行 / 宿主运行了但没登记 ROT / 登记了但我们匹配不到。
/// <see cref="EnumerateRunningObjectNames"/> 提供第三种情况的直接证据。
/// </remarks>
public static class PptHostDiagnostics
{
    /// <summary>枚举运行对象表里所有登记名。</summary>
    public static IReadOnlyList<string> EnumerateRunningObjectNames()
    {
        try
        {
            return RotInterop.EnumerateRunningObjectNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>带步骤日志的挂接尝试（挂接失败时用来看清卡在哪一步）。</summary>
    public static (bool Success, IReadOnlyList<string> Steps) TryAttachWithLog(string progId)
    {
        var log = new List<string>();
        try
        {
            var obj = RotInterop.TryGetActiveObject(progId, log);
            return (obj is not null, log);
        }
        catch (Exception ex)
        {
            log.Add($"异常：{ex.GetType().Name} {ex.Message}");
            return (false, log);
        }
    }
}
