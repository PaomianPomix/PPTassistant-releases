using PPTAssistant.Coords;

namespace PPTAssistant.Host;

public interface IWriteBackHost
{
    /// <summary>经 COM 找到并关闭指定课件（关闭前先保存）。</summary>
    /// <param name="documentPath">课件路径（与旁路日志表头里记录的一致）。</param>
    /// <param name="wasOpen">它当时是否开着（没开着就不需要重新打开）。</param>
    /// <param name="error">失败原因。</param>
    bool SaveAndClose(string documentPath, out bool wasOpen, out string? error);

    /// <summary>重新打开课件（把界面还给老师）。</summary>
    bool Reopen(string documentPath, out string? error);

    /// <summary>
    /// 课件当前是否被宿主占用（无法独占写入）。
    /// </summary>
    /// <remarks>
    /// 崩溃恢复路径用得上：那时 PowerPoint 可能正开着那份课件，
    /// 那就**不能**改文件——要明确告诉用户「先关掉再恢复」，而不是冒险写坏它。
    /// </remarks>
    bool IsFileLocked(string documentPath);
}

/// <summary>用 COM 实现 <see cref="IWriteBackHost"/>（产品里的唯一实现）。</summary>
public sealed class PptHostWriteBack : IWriteBackHost
{
    private readonly PptHost _host;

    public PptHostWriteBack(PptHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc />
    public bool SaveAndClose(string documentPath, out bool wasOpen, out string? error)
        => _host.SaveAndClosePresentation(documentPath, out wasOpen, out error);

    /// <inheritdoc />
    public bool Reopen(string documentPath, out string? error)
        => _host.OpenPresentation(documentPath, out error);

    /// <inheritdoc />
    public bool IsFileLocked(string documentPath) => PptHost.IsFileLockedByAnotherProcess(documentPath);
}
