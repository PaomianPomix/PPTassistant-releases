using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PPTAssistant.Session.Sidecar;

public static class SidecarStore
{
    /// <summary>日志根目录。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PPTAssistant",
        "sessions");

    /// <summary>
    /// 计算课件指纹：返回（完整指纹, 目录桶名）。
    /// </summary>
    /// <remarks>目录桶名只用**路径**哈希（稳定）；完整指纹再加上尺寸与修改时间（精确）。</remarks>
    public static (string Fingerprint, string Bucket) ComputeFingerprint(string documentPath)
    {
        var normalized = Path.GetFullPath(documentPath).ToLowerInvariant();
        var bucket = ShortHash(normalized);

        string detail;
        try
        {
            var info = new FileInfo(normalized);
            detail = $"{normalized}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            detail = normalized;
        }

        return (ShortHash(detail), bucket);
    }

    /// <summary>找出与指定课件相关的旁路日志（按修改时间从新到旧）。</summary>
    /// <param name="documentPath">课件路径。</param>
    /// <param name="root">日志根目录（默认 <see cref="DefaultRoot"/>）。</param>
    public static IReadOnlyList<SidecarCandidate> FindRecoverable(string documentPath, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);

        var directory = Path.Combine(root ?? DefaultRoot, ComputeFingerprint(documentPath).Bucket);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var currentSize = TrySize(documentPath);
        var currentWriteUtc = TryLastWriteUtc(documentPath);
        var candidates = new List<SidecarCandidate>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var session = TryRead(file);
            if (session is null || !session.HasInk)
            {
                // 没有墨迹的日志没有恢复价值（例如开了日志但一笔没写）。
                continue;
            }

            var exact = session.Header.DocumentSizeBytes == currentSize &&
                        session.Header.DocumentLastWriteUtc == currentWriteUtc;
            candidates.Add(new SidecarCandidate(session, exact ? SidecarMatchKind.Exact : SidecarMatchKind.PathOnly));
        }

        return candidates;
    }

    /// <summary>读取一份日志；文件不可读时返回 null。坏行会被跳过并计数（进程被杀时最后一行可能残缺）。</summary>
    public static SidecarSession? TryRead(string logPath)
    {
        try
        {
            return Read(logPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读取一份日志（坏行跳过；表头缺失则抛）。</summary>
    /// <remarks>
    /// 用 <c>FileShare.ReadWrite</c> 打开：日志很可能**正在被写入**（同一进程的当前会话、
    /// 或另一个实例），读的人不该因为共享模式不对而被拒之门外——那会让"崩溃恢复"在最需要它的时候失败。
    /// 代价是可能读到写了一半的最后一行，而这正是 <see cref="SidecarSession.DamagedLineCount"/> 要处理的情况。
    /// </remarks>
    public static SidecarSession Read(string logPath)
    {
        SidecarHeader? header = null;
        var strokes = new List<SidecarStroke>();
        var pages = new List<int>();
        var damaged = 0;

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("kind", out var kindProperty))
                {
                    damaged++;
                    continue;
                }

                switch (kindProperty.GetString())
                {
                    case "header":
                        header = ParseHeader(root);
                        break;
                    case "page":
                        if (root.TryGetProperty("page", out var pageNumber))
                        {
                            pages.Add(pageNumber.GetInt32());
                        }

                        break;
                    case "stroke":
                        var stroke = ParseStroke(root);
                        if (stroke is not null)
                        {
                            strokes.Add(stroke);
                        }
                        else
                        {
                            damaged++;
                        }

                        break;
                    case "erase":
                        ApplyErase(root, strokes);
                        break;
                    case "end":
                        break;
                    default:
                        damaged++;
                        break;
                }
            }
            catch (JsonException)
            {
                // ★ 进程被强杀时最后一行常常写了一半——这是设计预期内的，不是文件损坏。
                damaged++;
            }
        }

        if (header is null)
        {
            throw new InvalidDataException($"旁路日志缺少表头：{logPath}");
        }

        return new SidecarSession(logPath, header, strokes, pages.Distinct().Order().ToList(), damaged);
    }

    /// <summary>
    /// 扫描**所有**残留日志，按时间从新到旧返回（启动时的崩溃恢复用）。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="FindRecoverable"/> 的区别：那个是"已知课件、找它的日志"，
    /// 这个是"开机时不知道上次讲的是哪份课件"。所以要把所有桶扫一遍，读表头拿到课件路径，
    /// 再判断课件文件是否还在、是否与开日志时一致。</para>
    /// <para>只返回**有墨迹**的日志；没有板书的空会话不该打扰老师。</para>
    /// </remarks>
    public static IReadOnlyList<SidecarCandidate> FindAllRecoverable(string? root = null)
    {
        var directory = root ?? DefaultRoot;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var candidates = new List<SidecarCandidate>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            var session = TryRead(file);
            if (session is null || !session.HasInk)
            {
                continue;
            }

            var documentPath = session.Header.DocumentPath;
            var exact = false;
            if (!string.IsNullOrWhiteSpace(documentPath) && File.Exists(documentPath))
            {
                exact = session.Header.DocumentSizeBytes == TrySize(documentPath) &&
                        session.Header.DocumentLastWriteUtc == TryLastWriteUtc(documentPath);
            }

            candidates.Add(new SidecarCandidate(session, exact ? SidecarMatchKind.Exact : SidecarMatchKind.PathOnly));
        }

        return candidates
            .OrderByDescending(static c => c.Session.Header.StartedUtc)
            .ToList();
    }

    public static void Delete(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>清理过期日志（启动时调用），返回删掉的份数。</summary>
    /// <remarks>不做这件事的话，被强杀过的会话会永久占着磁盘。</remarks>
    public static int PruneOlderThan(TimeSpan age, string? root = null)
    {
        var directory = root ?? DefaultRoot;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - age;
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < threshold)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
        }

        return removed;
    }

    private static SidecarHeader ParseHeader(JsonElement root) => new(
        root.TryGetProperty("version", out var version) ? version.GetInt32() : SidecarHeader.CurrentVersion,
        root.TryGetProperty("fingerprint", out var fingerprint) ? fingerprint.GetString() ?? string.Empty : string.Empty,
        root.TryGetProperty("document", out var document) ? document.GetString() ?? string.Empty : string.Empty,
        root.TryGetProperty("documentSize", out var size) ? size.GetInt64() : 0,
        root.TryGetProperty("documentLastWrite", out var write) &&
        DateTime.TryParse(write.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedWrite)
            ? parsedWrite
            : DateTime.MinValue,
        root.TryGetProperty("slideWidth", out var width) ? width.GetDouble() : 960,
        root.TryGetProperty("slideHeight", out var height) ? height.GetDouble() : 540,
        root.TryGetProperty("started", out var started) &&
        DateTime.TryParse(started.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedStarted)
            ? parsedStarted
            : DateTime.MinValue,
        root.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "unknown" : "unknown");

    private static void ApplyErase(JsonElement root, List<SidecarStroke> strokes)
    {
        var page = root.TryGetProperty("page", out var pageProperty) ? pageProperty.GetInt32() : 1;

        var clearAll = root.TryGetProperty("all", out var allProperty) &&
                       allProperty.ValueKind == JsonValueKind.True;
        if (clearAll)
        {
            strokes.RemoveAll(stroke => stroke.PageIndex == page);
            return;
        }

        if (!root.TryGetProperty("ids", out var idsProperty) || idsProperty.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var ids = new HashSet<int>();
        foreach (var id in idsProperty.EnumerateArray())
        {
            // 0 = 旧日志 / 无编号的笔画：永远不参与按 id 删除（否则可能误删 v1 日志的笔画）。
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value > 0)
            {
                ids.Add(value);
            }
        }

        if (ids.Count == 0)
        {
            return;
        }

        strokes.RemoveAll(stroke => stroke.PageIndex == page && ids.Contains(stroke.Id));
    }

    private static SidecarStroke? ParseStroke(JsonElement root)
    {
        if (!root.TryGetProperty("pts", out var points) || points.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<SidecarPoint>(points.GetArrayLength());
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
            {
                continue;
            }

            var values = point.EnumerateArray().ToArray();
            list.Add(new SidecarPoint(
                values[0].GetDouble(),
                values[1].GetDouble(),
                values.Length > 2 ? (float)values[2].GetDouble() : 0f,
                values.Length > 3 ? values[3].GetInt32() : 0));
        }

        if (list.Count == 0)
        {
            return null;
        }

        return new SidecarStroke(
            root.TryGetProperty("page", out var page) ? page.GetInt32() : 1,
            root.TryGetProperty("width", out var width) ? width.GetDouble() : 3.0,
            root.TryGetProperty("color", out var color) ? color.GetString() ?? "#000000" : "#000000",
            root.TryGetProperty("device", out var device) ? device.GetString() ?? "Unknown" : "Unknown",
            list,
            root.TryGetProperty("id", out var id) ? id.GetInt32() : 0);
    }

    private static string ShortHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static long TrySize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
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
