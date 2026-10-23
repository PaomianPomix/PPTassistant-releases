using PPTAssistant.Host;
using PPTAssistant.InkML;
using PPTAssistant.Package;

namespace PPTAssistant.Session.WriteBack;

public enum WriteBackChoice
{

    Retain = 0,

    /// <summary>丢弃（删日志，完全不触碰用户文件）。</summary>
    Discard = 1,

    /// <summary>先不决定（保留日志，下次启动还能恢复）。</summary>
    Later = 2,
}

public interface IWriteBackPrompt
{
    /// <summary>散场时问「本次板书要保留吗」。</summary>
    /// <param name="strokeCount">本次板书的笔画数。</param>
    /// <param name="pageCount">涉及的页数。</param>
    /// <param name="documentPath">课件路径（给用户看清楚是写进哪份文件）。</param>
    WriteBackChoice AskCommit(int strokeCount, int pageCount, string documentPath);

    /// <summary>启动/恢复时问「上次残留的板书要写进课件吗」。</summary>
    /// <param name="strokeCount">残留板书的笔画数。</param>
    /// <param name="documentPath">课件路径。</param>
    /// <param name="exactMatch">课件文件与开日志时是否完全一致（不一致要提醒坐标可能偏移）。</param>
    /// <param name="startedUtc">那次放映的时间（给用户认领用）。</param>
    WriteBackChoice AskRecover(int strokeCount, string documentPath, bool exactMatch, DateTime startedUtc);
}

/// <summary>不做任何询问的实现（自动化/无界面场景）：[Commit] 一律保留、[Recover] 一律先不决定。</summary>
public sealed class SilentWriteBackPrompt : IWriteBackPrompt
{
    /// <summary>散场时的固定答案。</summary>
    public WriteBackChoice CommitChoice { get; init; } = WriteBackChoice.Retain;

    /// <inheritdoc />
    public WriteBackChoice AskCommit(int strokeCount, int pageCount, string documentPath) => CommitChoice;

    /// <inheritdoc />
    public WriteBackChoice AskRecover(int strokeCount, string documentPath, bool exactMatch, DateTime startedUtc)
        => WriteBackChoice.Later;
}

public sealed record WriteBackResult(
    bool Success,
    string? Error = null,
    int PagesWritten = 0,
    int StrokesWritten = 0,
    string? BackupPath = null,
    bool Reopened = false,
    bool LogDeleted = false);

public static class InkWriteBack
{
    /// <summary>
    /// 把一份旁路日志的板书写进它记录的课件。
    /// </summary>
    /// <param name="session">读出来的旁路日志。</param>
    /// <param name="host">宿主操作（COM）；崩溃恢复路径下 PowerPoint 没开着时传 null。</param>
    /// <param name="backupDirectory">备份目录；null = 默认位置。</param>
    /// <param name="trace">日志回调。</param>
    /// <param name="deleteLogOnSuccess">成功后是否删日志（默认是）。</param>
    public static WriteBackResult Commit(
        Sidecar.SidecarSession session,
        IWriteBackHost? host,
        string? backupDirectory = null,
        Action<string>? trace = null,
        bool deleteLogOnSuccess = true)
    {
        ArgumentNullException.ThrowIfNull(session);

        var documentPath = session.Header.DocumentPath;
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return new WriteBackResult(false, "旁路日志里没有课件路径");
        }

        if (!File.Exists(documentPath))
        {
            return new WriteBackResult(false, $"课件已不在原位置：{documentPath}");
        }

        var drawings = session.BuildDrawings();
        if (drawings.Count == 0)
        {

            var deleted = false;
            if (deleteLogOnSuccess)
            {
                Sidecar.SidecarStore.Delete(session.Path);
                deleted = !File.Exists(session.Path);
            }

            trace?.Invoke("[writeback] 本次板书已被全部擦除，没有需要写回的墨迹（未触碰课件）");
            return new WriteBackResult(true, null, LogDeleted: deleted);
        }

        var strokeTotal = drawings.Values.Sum(static d => d.Strokes.Count);

        var wasOpen = false;
        if (host is not null)
        {
            if (!host.SaveAndClose(documentPath, out wasOpen, out var closeError))
            {
                return new WriteBackResult(false, $"保存并关闭课件失败（未改动任何文件）：{closeError}");
            }
        }
        else if (PptHost.IsFileLockedByAnotherProcess(documentPath))
        {
            return new WriteBackResult(
                false,
                "课件正被 PowerPoint 打开，请先关闭它再恢复（本次未改动任何文件）");
        }

        string? backupPath = null;
        var originalHash = InkInjector.ComputeSha256(documentPath);
        try
        {
            backupPath = InkInjector.Backup(documentPath, backupDirectory);
            trace?.Invoke($"[writeback] 已备份课件：{backupPath}（原文件 SHA256={originalHash[..12]}…）");
        }
        catch (Exception ex)
        {
            ReopenIfNeeded(host, documentPath, wasOpen, trace);
            return new WriteBackResult(false, $"备份失败，未改动课件：{ex.GetType().Name} {ex.Message}");
        }

        // ③ 逐页注入。
        var pagesWritten = 0;
        var strokesWritten = 0;
        try
        {
            foreach (var (pageIndex, drawing) in drawings.OrderBy(static pair => pair.Key))
            {
                var result = InkInjector.Inject(
                    documentPath,
                    pageIndex,
                    drawing,
                    backupDirectory,
                    createBackup: false,
                    trace: trace);

                if (!result.Success)
                {
                    throw new InvalidOperationException($"第 {pageIndex} 页注入失败：{result.Error}");
                }

                pagesWritten++;
                strokesWritten += result.StrokeCount;
            }
        }
        catch (Exception ex)
        {

            var rolledBack = pagesWritten > 0 && TryRollback(documentPath, backupPath, trace);
            trace?.Invoke($"[writeback] 注入失败：{ex.Message}");
            ReopenIfNeeded(host, documentPath, wasOpen, trace);

            return new WriteBackResult(
                false,
                pagesWritten > 0
                    ? $"{ex.Message}（{(rolledBack ? "已从备份回滚" : "**回滚失败，请用备份手动恢复**")}）"
                    : $"{ex.Message}（未写入任何一页，课件未被改动）",
                pagesWritten,
                strokesWritten,
                backupPath,
                Reopened: wasOpen,
                LogDeleted: false);
        }

        // ⑤ 重新打开，把界面还给老师。
        var reopened = ReopenIfNeeded(host, documentPath, wasOpen, trace);

        // ⑥ 成功才删日志。
        var logDeleted = false;
        if (deleteLogOnSuccess)
        {
            Sidecar.SidecarStore.Delete(session.Path);
            logDeleted = !File.Exists(session.Path);
        }

        trace?.Invoke(
            $"[writeback] 完成：{pagesWritten} 页 / {strokesWritten} 笔已写入课件「{Path.GetFileName(documentPath)}」"
            + (logDeleted ? "；旁路日志已删除" : "；日志保留"));

        return new WriteBackResult(true, null, pagesWritten, strokesWritten, backupPath, reopened, logDeleted);
    }

    /// <summary>把备份拷回原位（回滚用），返回是否成功。</summary>
    public static bool TryRollback(string documentPath, string? backupPath, Action<string>? trace = null)
    {
        if (backupPath is null || !File.Exists(backupPath))
        {
            return false;
        }

        try
        {
            File.Copy(backupPath, documentPath, overwrite: true);
            trace?.Invoke($"[writeback] 已回滚：{backupPath} → {documentPath}");
            return true;
        }
        catch (Exception ex)
        {
            trace?.Invoke($"[writeback] 回滚失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    public static bool Discard(Sidecar.SidecarSession session, Action<string>? trace = null)
    {
        Sidecar.SidecarStore.Delete(session.Path);
        var deleted = !File.Exists(session.Path);
        trace?.Invoke($"[writeback] 已丢弃本次板书（未触碰课件）：{session.Path}");
        return deleted;
    }

    private static bool ReopenIfNeeded(IWriteBackHost? host, string documentPath, bool wasOpen, Action<string>? trace)
    {
        if (host is null || !wasOpen)
        {
            return false;
        }

        if (host.Reopen(documentPath, out var error))
        {
            return true;
        }

        trace?.Invoke($"[writeback] 重新打开课件失败（文件已写好，老师可手动打开）：{error}");
        return false;
    }
}
