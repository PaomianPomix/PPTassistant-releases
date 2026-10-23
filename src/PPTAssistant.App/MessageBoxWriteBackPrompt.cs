using System.Windows;
using PPTAssistant.Session.WriteBack;

namespace PPTAssistant.App;

public sealed class MessageBoxWriteBackPrompt : IWriteBackPrompt
{
    /// <inheritdoc />
    public WriteBackChoice AskCommit(int strokeCount, int pageCount, string documentPath)
    {
        var message =
            $"""
             本次板书 {strokeCount} 笔，分布在 {pageCount} 页。

             要写进这份课件吗？

             {documentPath}

             「是」= 保存（写前会自动备份原文件；若失败会回滚）
             「否」= 丢弃本次板书，完全不改动课件
             """;

        return Ask("板书要保留吗？", message);
    }

    /// <inheritdoc />
    public WriteBackChoice AskRecover(int strokeCount, string documentPath, bool exactMatch, DateTime startedUtc)
    {
        var warning = exactMatch
            ? string.Empty
            : "\n⚠ 课件在那之后被改动过，墨迹位置可能有偏移。\n";

        var message =
            $"""
             发现一次未写回的板书（{startedUtc.ToLocalTime():yyyy-MM-dd HH:mm}，{strokeCount} 笔）。

             要写进这份课件吗？
             {warning}
             {documentPath}

             「是」= 现在写入（写前会自动备份；课件若正被打开，请先关闭）
             「否」= 丢弃这次残留板书
             """;

        return Ask("发现未写回的板书", message);
    }

    private static WriteBackChoice Ask(string caption, string message)
    {
        // 默认按钮 = 是（保留）；同时提供「取消」让老师先不决定（日志留着，下次还能恢复）。
        var answer = MessageBox.Show(
            message,
            "PPT小工具 — " + caption,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        return answer switch
        {
            MessageBoxResult.Yes => WriteBackChoice.Retain,
            MessageBoxResult.No => WriteBackChoice.Discard,
            _ => WriteBackChoice.Later,
        };
    }
}
