using System.Buffers;
using System.Text;
using System.Text.Json;
using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Session.Sidecar;

public sealed class SidecarLog : IDisposable
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    private readonly FileStream _stream;
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private bool _disposed;

    private SidecarLog(FileStream stream, string path, SidecarHeader header)
    {
        _stream = stream;
        Path = path;
        Header = header;
    }

    /// <summary>日志文件路径。</summary>
    public string Path { get; }

    /// <summary>表头（已写进文件）。</summary>
    public SidecarHeader Header { get; }

    /// <summary>已写入的笔画数（诊断用）。</summary>
    public int StrokeCount { get; private set; }

    /// <summary>
    /// 开一份新日志。目录按**路径哈希**分（指纹的一部分），文件名带时间与进程号，便于一次性配对。
    /// </summary>
    public static SidecarLog Start(
        string documentPath,
        SlideSize slideSize,
        string? rootDirectory = null,
        string toolVersion = "dev",
        Action<string>? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);

        var fullPath = System.IO.Path.GetFullPath(documentPath);
        var (fingerprint, bucket) = SidecarStore.ComputeFingerprint(fullPath);
        var directory = System.IO.Path.Combine(rootDirectory ?? SidecarStore.DefaultRoot, bucket);
        Directory.CreateDirectory(directory);

        var file = System.IO.Path.Combine(
            directory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");

        var header = new SidecarHeader(
            SidecarHeader.CurrentVersion,
            fingerprint,
            fullPath,
            TryFileSize(fullPath),
            TryLastWriteUtc(fullPath),
            slideSize.Width,
            slideSize.Height,
            DateTime.UtcNow,
            toolVersion);

        var stream = new FileStream(
            file,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.None);

        var log = new SidecarLog(stream, file, header);
        log.WriteHeader();
        trace?.Invoke($"[sidecar] 旁路日志已开：{file}");
        return log;
    }

    /// <summary>记一次翻页（页码是 1 起的放映页序号）。</summary>
    public void AppendPage(int pageIndex, int pageCount)
    {
        if (_disposed)
        {
            return;
        }

        var at = DateTime.UtcNow.ToString("O");
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "page");
            writer.WriteNumber("page", pageIndex);
            writer.WriteNumber("count", pageCount);
            writer.WriteString("at", at);
            writer.WriteEndObject();
        });
    }

    /// <summary>记一笔墨迹（**这是随写随落的关键路径**）。</summary>
    /// <param name="pageIndex">页码（1 起）。</param>
    /// <param name="stroke">笔画（幻灯片坐标）。</param>
    /// <param name="device">设备（诊断用）。</param>
    /// <param name="strokeId">
    /// 会话内唯一的笔画编号（与覆盖层的 <c>StrokeId</c> 一致）。
    /// 擦除记录靠它定位要删除的笔画；0 表示旧日志 / 无编号（不会被 erase 选中）。
    /// </param>
    public void AppendStroke(int pageIndex, InkStroke stroke, string device, int strokeId = 0)
    {
        if (_disposed || stroke.Points.Count == 0)
        {
            return;
        }

        var colorHex = stroke.Color.ToHex();
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "stroke");
            writer.WriteNumber("page", pageIndex);
            writer.WriteNumber("id", strokeId);
            writer.WriteNumber("width", stroke.WidthPoints);
            writer.WriteString("color", colorHex);
            writer.WriteString("device", device);
            writer.WriteNumber("n", stroke.Points.Count);

            // 点用紧凑数组：[x, y, pressure, t]，避免每点一个对象撑大日志。
            writer.WriteStartArray("pts");
            foreach (var point in stroke.Points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(point.X, 2));
                writer.WriteNumberValue(Math.Round(point.Y, 2));
                writer.WriteNumberValue(Math.Round(point.Pressure, 3));
                writer.WriteNumberValue(point.T);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        StrokeCount++;
    }

    public void AppendErase(int pageIndex, IReadOnlyList<int> strokeIds)
    {
        if (_disposed || strokeIds.Count == 0)
        {
            return;
        }

        var at = DateTime.UtcNow.ToString("O");
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "erase");
            writer.WriteNumber("page", pageIndex);
            writer.WriteString("at", at);
            writer.WriteStartArray("ids");
            foreach (var id in strokeIds)
            {
                writer.WriteNumberValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public void AppendClearPage(int pageIndex)
    {
        if (_disposed)
        {
            return;
        }

        var at = DateTime.UtcNow.ToString("O");
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "erase");
            writer.WriteNumber("page", pageIndex);
            writer.WriteBoolean("all", true);
            writer.WriteString("at", at);
            writer.WriteEndObject();
        });
    }

    /// <summary>记一次收场（正常散场 / 用户退出）。</summary>
    public void AppendEnd(string reason)
    {
        if (_disposed)
        {
            return;
        }

        var at = DateTime.UtcNow.ToString("O");
        var strokes = StrokeCount;
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "end");
            writer.WriteString("reason", reason);
            writer.WriteString("at", at);
            writer.WriteNumber("strokes", strokes);
            writer.WriteEndObject();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
        }

        _stream.Dispose();
    }

    private void WriteHeader()
    {
        WriteRecord(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "header");
            writer.WriteNumber("version", Header.Version);
            writer.WriteString("fingerprint", Header.Fingerprint);
            writer.WriteString("document", Header.DocumentPath);
            writer.WriteNumber("documentSize", Header.DocumentSizeBytes);
            writer.WriteString("documentLastWrite", Header.DocumentLastWriteUtc.ToString("O"));
            writer.WriteNumber("slideWidth", Header.SlideWidthPoints);
            writer.WriteNumber("slideHeight", Header.SlideHeightPoints);
            writer.WriteString("started", Header.StartedUtc.ToString("O"));
            writer.WriteString("tool", Header.ToolVersion);
            writer.WriteEndObject();
        });
    }

    private void WriteRecord(Action<Utf8JsonWriter> write)
    {
        _buffer.Clear();
        using (var writer = new Utf8JsonWriter(_buffer, WriterOptions))
        {
            write(writer);
        }

        _stream.Write(_buffer.WrittenSpan);
        _stream.WriteByte((byte)'\n');
        _stream.Flush(flushToDisk: true);
    }

    private static long TryFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static DateTime TryLastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
