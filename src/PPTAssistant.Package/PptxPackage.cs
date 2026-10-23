using System.IO.Compression;
using System.Text;

namespace PPTAssistant.Package;

public static class PptxPackage
{
    /// <summary>内容类型部件名。</summary>
    public const string ContentTypesPart = "[Content_Types].xml";

    /// <summary>读写用的编码（OPC 的 XML 部件都是 UTF-8；不写 BOM 以免个别解析器挑剔）。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>列出包内所有部件名。</summary>
    public static IReadOnlyList<string> ListParts(string pptxPath)
    {
        using var archive = ZipFile.OpenRead(pptxPath);
        return archive.Entries.Select(static e => e.FullName).ToList();
    }

    /// <summary>读取一个部件为文本；不存在返回 null。</summary>
    public static string? ReadText(string pptxPath, string partName)
    {
        using var archive = ZipFile.OpenRead(pptxPath);
        var entry = archive.GetEntry(partName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 按 <paramref name="transform"/> 的指示把原包复制到新包：可替换若干部件文本、追加若干新部件。
    /// </summary>
    /// <param name="sourcePath">原包。</param>
    /// <param name="destinationPath">新包（会被覆盖）。</param>
    /// <param name="replacedParts">要替换文本的部件（键 = 部件名）。</param>
    /// <param name="addedParts">要新增的部件（键 = 部件名，值 = 文本内容）。</param>
    public static void Rewrite(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, string>? replacedParts = null,
        IReadOnlyDictionary<string, string>? addedParts = null)
    {
        replacedParts ??= new Dictionary<string, string>();
        addedParts ??= new Dictionary<string, string>();

        using var source = ZipFile.OpenRead(sourcePath);
        using var destination = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        // 1) [Content_Types].xml 先写（OPC 约定）。
        WriteEntry(destination, source, ContentTypesPart, replacedParts);

        // 2) 其余原有部件原样复制（除已替换的）。
        foreach (var entry in source.Entries)
        {
            if (string.Equals(entry.FullName, ContentTypesPart, StringComparison.Ordinal) ||
                string.Equals(entry.FullName, destinationPath, StringComparison.Ordinal))
            {
                continue;
            }

            WriteEntry(destination, source, entry.FullName, replacedParts);
        }

        // 3) 追加新部件。
        foreach (var (name, content) in addedParts)
        {
            if (source.GetEntry(name) is not null)
            {
                throw new InvalidOperationException($"部件已存在，不能当作新部件追加：{name}");
            }

            var entry = destination.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(content);
        }
    }

    /// <summary>解析 XML 文本（校验用；失败即抛）。</summary>
    public static void EnsureXmlParses(string xml, string what)
    {
        try
        {
            _ = System.Xml.Linq.XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{what} 不是合法 XML：{ex.Message}", ex);
        }
    }

    private static void WriteEntry(
        ZipArchive destination,
        ZipArchive source,
        string entryName,
        IReadOnlyDictionary<string, string> replacedParts)
    {
        var sourceEntry = source.GetEntry(entryName);
        if (sourceEntry is null)
        {
            return;
        }

        var target = destination.CreateEntry(entryName, CompressionLevel.Optimal);
        using var targetStream = target.Open();

        if (replacedParts.TryGetValue(entryName, out var replacement))
        {
            using var writer = new StreamWriter(targetStream, Utf8NoBom);
            writer.Write(replacement);
            return;
        }

        using var sourceStream = sourceEntry.Open();
        sourceStream.CopyTo(targetStream);
    }
}
