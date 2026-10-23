using System.Diagnostics;
using System.IO;
using System.Text;

namespace PPTAssistant.App;

public static class AppLog
{
    private const long MaxBytes = 2L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _directory;
    private static string? _filePath;
    private static long _writtenBytes;

    /// <summary>日志目录（未初始化时为 null）。</summary>
    public static string? Directory => _directory;

    /// <summary>当前日志文件路径（未初始化时为 null）。</summary>
    public static string? FilePath => _filePath;

    /// <summary>初始化日志目录（默认 <c>%LOCALAPPDATA%\PPTAssistant\logs</c>）。</summary>
    /// <remarks>
    /// <b>本方法自身绝不抛异常</b>：它是启动路径上的第一件事，而全局异常钩子还没安装
    /// （见 <c>Program.Main</c> 的顺序）。若在这里逸出异常，进程会静默结束、不留任何记录——
    /// 对一个没有控制台的 WinExe 来说就是「双击图标什么也没发生」，恰好与「安静但可诊断」相反。
    /// 失败时降级为「无文件日志继续运行」（<see cref="Write"/> 已能容忍 <c>_filePath == null</c>）。
    /// </remarks>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            try
            {
                _directory = directory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PPTAssistant",
                    "logs");
                System.IO.Directory.CreateDirectory(_directory);
                _filePath = Path.Combine(_directory, "app.log");
                _writtenBytes = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
            }
            catch (Exception)
            {
                _directory = null;
                _filePath = null;
                _writtenBytes = 0;
            }
        }
    }

    /// <summary>写一条常规日志。</summary>
    public static void Info(string message) => Write("INFO ", message, null);

    /// <summary>写一条警告日志。</summary>
    public static void Warn(string message) => Write("WARN ", message, null);

    /// <summary>写一条错误日志（可带异常）。</summary>
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>把异常写全（含堆栈）——崩溃兜底用。</summary>
    public static void Fatal(string message, Exception exception) => Write("FATAL", message, exception, includeStack: true);

    private static void Write(string level, string message, Exception? exception, bool includeStack = false)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Environment.CurrentManagedThreadId.ToString("D2")).Append("] ")
            .Append(level).Append(' ')
            .Append(message);

        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            if (includeStack && exception.StackTrace is { Length: > 0 } stack)
            {
                line.AppendLine().Append(stack);
            }
        }

        var text = line.ToString();
        Debug.WriteLine(text);

        lock (Gate)
        {
            if (_filePath is null)
            {
                return;
            }

            try
            {
                RollIfNeeded();
                File.AppendAllText(_filePath, text + Environment.NewLine, Encoding.UTF8);
                _writtenBytes += text.Length + Environment.NewLine.Length;
            }
            catch (Exception)
            {
                // 日志自身绝不能成为故障源（磁盘满 / 权限异常时静默放弃）。
            }
        }
    }

    private static void RollIfNeeded()
    {
        if (_filePath is null || _writtenBytes < MaxBytes || !File.Exists(_filePath))
        {
            return;
        }

        var archive = Path.Combine(_directory!, "app.1.log");
        try
        {
            File.Delete(archive);
            File.Move(_filePath, archive);
        }
        catch (Exception)
        {
            // 滚转失败不致命：继续往原文件追加。
        }

        _writtenBytes = 0;
    }
}
