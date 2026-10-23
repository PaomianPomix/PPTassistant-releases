using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Ink;

namespace PPTAssistant.App;

/// <summary>白板专用存档；原生 ISF 保留压力、画笔样式，临时文件完成后才替换原件。</summary>
internal static class WhiteboardStore
{
    private const int FormatVersion = 2;
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxPageBytes = 16 * 1024 * 1024;
    private const int MaxStrokesPerPage = 100_000;
    private const int MaxPointsPerDocument = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static WhiteboardDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream openedInput;
        try { openedInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new WhiteboardDocument(); }
        catch (DirectoryNotFoundException) { return new WhiteboardDocument(); }
        using var input = openedInput;
        if (input.Length > MaxArchiveBytes) throw Invalid("存档大小超过限制。");
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count is < 2 or > WhiteboardDocument.MaxPages + 1)
                throw Invalid("存档条目数量无效。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName)) throw Invalid("存档包含重名条目。");
                if (entry.Length < 0 || entry.Length > MaxPageBytes || totalBytes > MaxArchiveBytes - entry.Length)
                    throw Invalid("存档解压大小超过限制。");
                totalBytes += entry.Length;
            }

            var manifestEntry = zip.GetEntry("manifest.json") ?? throw Invalid("存档缺少清单。");
            var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(manifestEntry, MaxManifestBytes), JsonOptions)
                ?? throw Invalid("存档清单为空。");
            ValidateManifest(manifest);
            if (zip.Entries.Count != manifest.Pages!.Length + 1) throw Invalid("存档页数与清单不一致。");
            var pages = new List<WhiteboardPage>();
            var totalPoints = 0;
            for (var index = 0; index < manifest.Pages.Length; index++)
            {
                var page = manifest.Pages[index];
                var expectedName = PageEntryName(index);
                if (page is null || page.Entry != expectedName) throw Invalid("存档页面路径无效。");
                var entry = zip.GetEntry(expectedName) ?? throw Invalid("存档缺少页面笔迹。");
                using var ink = new MemoryStream(ReadEntry(entry, MaxPageBytes), writable: false);
                var strokes = new StrokeCollection(ink);
                ValidateStrokes(strokes, ref totalPoints);
                var width = manifest.Version == 1 ? manifest.Width : page.Width!.Value;
                var height = manifest.Version == 1 ? manifest.Height : page.Height!.Value;
                var restored = new WhiteboardPage(strokes, page.IsDark, width, height, isViewportInitialized: true);
                // 旧文件或四舍五入后的 ISF 可能稍超参考边界；扩大画布保留全部墨迹。
                ValidatePageDimensions(restored.Width, restored.Height);
                pages.Add(restored);
            }
            return WhiteboardDocument.Restore(pages, manifest.CurrentPageIndex);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("白板存档格式无效，原文件未修改。", ex);
        }
    }

    public static void Save(string path, WhiteboardDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        foreach (var page in document.Pages) page.IncludeInkBounds();
        var manifest = new Manifest
        {
            Version = FormatVersion,
            Width = document.Width,
            Height = document.Height,
            CurrentPageIndex = document.CurrentPageIndex,
            Pages = document.Pages.Select((page, index) => new PageManifest
            {
                Entry = PageEntryName(index), IsDark = page.IsDark, Width = page.Width, Height = page.Height,
            }).ToArray(),
        };
        ValidateManifest(manifest);
        var totalPoints = 0;
        foreach (var page in document.Pages) ValidateStrokes(page.Strokes, ref totalPoints);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                    WriteEntry(zip, "manifest.json", manifestBytes);
                    long totalBytes = manifestBytes.Length;
                    for (var index = 0; index < document.Pages.Count; index++)
                    {
                        using var ink = new MemoryStream();
                        document.Pages[index].Strokes.Save(ink);
                        if (ink.Length > MaxPageBytes || totalBytes > MaxArchiveBytes - ink.Length)
                            throw Invalid("白板笔迹大小超过存档限制。");
                        totalBytes += ink.Length;
                        WriteEntry(zip, PageEntryName(index), ink.GetBuffer().AsSpan(0, checked((int)ink.Length)));
                    }
                }
                if (output.Length > MaxArchiveBytes) throw Invalid("白板存档大小超过限制。");
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            // 清理失败不能掩盖原来的存档异常。
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.Version is not (1 or FormatVersion)) throw Invalid("不支持此白板存档版本。");
        if (manifest.Version == 1)
        {
            if (manifest.Width != 1600 || manifest.Height != 900) throw Invalid("旧版白板画布尺寸无效。");
        }
        else ValidatePageDimensions(manifest.Width, manifest.Height);
        if (manifest.Pages is null || manifest.Pages.Length is < 1 or > WhiteboardDocument.MaxPages)
            throw Invalid("白板页数无效。");
        if (manifest.CurrentPageIndex < 0 || manifest.CurrentPageIndex >= manifest.Pages.Length)
            throw Invalid("白板当前页索引无效。");
        foreach (var page in manifest.Pages)
        {
            if (page is null) throw Invalid("白板页面清单无效。");
            if (manifest.Version == FormatVersion)
            {
                if (!page.Width.HasValue || !page.Height.HasValue) throw Invalid("白板页面缺少参考尺寸。");
                ValidatePageDimensions(page.Width.Value, page.Height.Value);
            }
        }
    }

    private static void ValidatePageDimensions(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || width > WhiteboardPage.MaxViewportDimension || height > WhiteboardPage.MaxViewportDimension)
            throw Invalid("白板页面参考尺寸无效。");
    }

    private static void ValidateStrokes(StrokeCollection strokes, ref int totalPoints)
    {
        if (strokes.Count > MaxStrokesPerPage) throw Invalid("单页笔画数量超过限制。");
        foreach (var stroke in strokes)
        {
            if (stroke.StylusPoints.Count > MaxPointsPerDocument - totalPoints)
                throw Invalid("白板采样点数量超过限制。");
            totalPoints += stroke.StylusPoints.Count;
            foreach (var point in stroke.StylusPoints)
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !float.IsFinite(point.PressureFactor))
                    throw Invalid("白板包含无效笔迹坐标。");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length < 0 || entry.Length > limit) throw Invalid("存档条目大小超过限制。");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length > limit - read) throw Invalid("存档条目解压大小超过限制。");
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length) throw Invalid("存档条目已损坏。");
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, ReadOnlySpan<byte> bytes)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(bytes);
    }

    private static string PageEntryName(int index) => $"pages/{index + 1:D4}.isf";
    private static InvalidDataException Invalid(string reason) => new($"白板存档无效：{reason}");

    private sealed class Manifest
    {
        public required int Version { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required int CurrentPageIndex { get; init; }
        public required PageManifest[]? Pages { get; init; }
    }

    private sealed class PageManifest
    {
        public required string Entry { get; init; }
        public required bool IsDark { get; init; }
        public double? Width { get; init; }
        public double? Height { get; init; }
    }
}
