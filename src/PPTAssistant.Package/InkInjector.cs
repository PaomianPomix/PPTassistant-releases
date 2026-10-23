using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Package;

public sealed record InkInjectionResult(
    bool Success,
    string? Error,
    int SlideNumber,
    string? InkPartName = null,
    string? BackupPath = null,
    string? OriginalSha256 = null,
    int StrokeCount = 0)
{
    public static InkInjectionResult Failure(string error, int slideNumber, string? backupPath = null, string? sha256 = null)
        => new(false, error, slideNumber, BackupPath: backupPath, OriginalSha256: sha256);
}

public static class InkInjector
{
    /// <summary>墨迹部件名模板。</summary>
    public const string InkPartPattern = @"^ppt/ink/ink(\d+)\.xml$";

    private static readonly XNamespace MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace PresentationMl = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace DrawingMl = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PowerPoint2010 = "http://schemas.microsoft.com/office/powerpoint/2010/main";

    /// <summary>
    /// 把一页的墨迹注入到 pptx。
    /// </summary>
    /// <param name="pptxPath">目标文件（会被修改）。</param>
    /// <param name="slideNumber">页码（1 起）。</param>
    /// <param name="drawing">该页的墨迹（幻灯片坐标，pt）。</param>
    /// <param name="backupDirectory">备份目录；null = 默认 <c>%LOCALAPPDATA%\PPTAssistant\backups</c>。</param>
    /// <param name="timestampUtc">墨迹时间戳（写进 InkML 的 timeString）。</param>
    /// <param name="trace">日志回调。</param>
    public static InkInjectionResult Inject(
        string pptxPath,
        int slideNumber,
        InkDrawing drawing,
        string? backupDirectory = null,
        DateTime? timestampUtc = null,
        Action<string>? trace = null,
        bool createBackup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pptxPath);
        ArgumentNullException.ThrowIfNull(drawing);

        if (slideNumber < 1)
        {
            return InkInjectionResult.Failure($"页码非法：{slideNumber}", slideNumber);
        }

        if (!File.Exists(pptxPath))
        {
            return InkInjectionResult.Failure($"文件不存在：{pptxPath}", slideNumber);
        }

        if (drawing.Strokes.Count == 0)
        {

            return InkInjectionResult.Failure("这一页没有墨迹，未做任何改动", slideNumber);
        }

        var originalHash = ComputeSha256(pptxPath);
        string? backupPath = null;
        var temporaryPath = pptxPath + ".pptassistant-tmp";

        try
        {
            // ① 备份（写前必做）。
            if (createBackup)
            {
                backupPath = CreateBackup(pptxPath, backupDirectory, trace);
            }

            // ② 生成墨迹文本与落位矩形。
            var document = InkMlWriter.Write(drawing, timestampUtc ?? DateTime.UtcNow);
            var slidePart = $"ppt/slides/slide{slideNumber}.xml";
            var relsPart = $"ppt/slides/_rels/slide{slideNumber}.xml.rels";

            var slideXml = PptxPackage.ReadText(pptxPath, slidePart)
                ?? throw new InvalidDataException($"包里没有 {slidePart}");
            var relsXml = PptxPackage.ReadText(pptxPath, relsPart)
                ?? throw new InvalidDataException($"包里没有 {relsPart}");
            var contentTypesXml = PptxPackage.ReadText(pptxPath, PptxPackage.ContentTypesPart)
                ?? throw new InvalidDataException($"包里没有 {PptxPackage.ContentTypesPart}");

            var parts = PptxPackage.ListParts(pptxPath);
            var (inkPartName, inkNumber) = NextInkPart(parts);
            var newRelationshipId = NextRelationshipId(relsXml);
            var newShapeId = NextShapeId(slideXml);

            // ③ 三处精确插入（都保留原有文本的其余部分）。
            var newContentTypes = InsertContentTypeOverride(contentTypesXml, inkPartName);
            var newRels = InsertInkRelationship(relsXml, newRelationshipId, inkNumber);
            var newSlide = InsertContentPart(slideXml, document, newRelationshipId, newShapeId);

            // ④ 写新包 → 自检 → 才替换原文件。
            PptxPackage.Rewrite(
                pptxPath,
                temporaryPath,
                replacedParts: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PptxPackage.ContentTypesPart] = newContentTypes,
                    [relsPart] = newRels,
                    [slidePart] = newSlide,
                },
                addedParts: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [inkPartName] = document.Xml,
                });

            var check = Verify(temporaryPath, slideNumber, inkPartName, newRelationshipId, document);
            if (!check.Success)
            {
                // 自检没过：原文件还没动过，直接丢弃临时文件即可。
                TryDelete(temporaryPath);
                RestoreFromBackup(pptxPath, backupPath, trace);
                return InkInjectionResult.Failure($"注入后自检失败：{check.Error}", slideNumber, backupPath, originalHash);
            }

            File.Move(temporaryPath, pptxPath, overwrite: true);
            trace?.Invoke(
                $"[package] 已注入墨迹：{inkPartName} → {slidePart}（{drawing.Strokes.Count} 笔 / {drawing.PointCount} 点；"
                + $"原文件 SHA256={originalHash?[..12]}…；备份={backupPath}）");

            return new InkInjectionResult(
                true,
                null,
                slideNumber,
                inkPartName,
                backupPath,
                originalHash,
                drawing.Strokes.Count);
        }
        catch (Exception ex)
        {
            trace?.Invoke($"[package] 注入失败：{ex.GetType().Name} {ex.Message}");
            TryDelete(temporaryPath);
            if (backupPath is not null)
            {
                RestoreFromBackup(pptxPath, backupPath, trace);
            }

            return InkInjectionResult.Failure($"{ex.GetType().Name}: {ex.Message}", slideNumber, backupPath, originalHash);
        }
    }

    /// <summary>
    /// 注入后自检（**结构性**，不做渲染判断）：部件在、Content_Types 声明了、关系在、
    /// contentPart 引用的 rId 与关系一致、slide XML 与墨迹 XML 都能解析、墨迹里有实际采样点。
    /// </summary>
    public static (bool Success, string? Error) Verify(
        string pptxPath,
        int slideNumber,
        string inkPartName,
        string relationshipId,
        InkMlDocument document)
    {
        try
        {
            var parts = PptxPackage.ListParts(pptxPath);
            if (!parts.Contains(inkPartName, StringComparer.Ordinal))
            {
                return (false, $"缺少墨迹部件 {inkPartName}");
            }

            var contentTypes = PptxPackage.ReadText(pptxPath, PptxPackage.ContentTypesPart) ?? string.Empty;
            if (!contentTypes.Contains($"PartName=\"/{inkPartName}\"", StringComparison.Ordinal) ||
                !contentTypes.Contains("application/inkml+xml", StringComparison.Ordinal))
            {
                return (false, "Content_Types 里缺少墨迹部件的 Override");
            }

            var slidePart = $"ppt/slides/slide{slideNumber}.xml";
            var relsPart = $"ppt/slides/_rels/slide{slideNumber}.xml.rels";
            var slideXml = PptxPackage.ReadText(pptxPath, slidePart);
            var relsXml = PptxPackage.ReadText(pptxPath, relsPart);
            if (slideXml is null || relsXml is null)
            {
                return (false, "注入后找不到 slide / rels 部件");
            }

            PptxPackage.EnsureXmlParses(slideXml, slidePart);
            PptxPackage.EnsureXmlParses(relsXml, relsPart);

            if (!relsXml.Contains($"Id=\"{relationshipId}\"", StringComparison.Ordinal) ||
                !relsXml.Contains($"Target=\"../ink/ink{ExtractInkNumber(inkPartName)}.xml\"", StringComparison.Ordinal))
            {
                return (false, "rels 里缺少指向墨迹部件的 customXml 关系");
            }

            if (!slideXml.Contains($"r:id=\"{relationshipId}\"", StringComparison.Ordinal) ||
                !slideXml.Contains("contentPart", StringComparison.Ordinal))
            {
                return (false, "slide 里没有引用该关系的 contentPart");
            }

            var inkXml = PptxPackage.ReadText(pptxPath, inkPartName);
            if (string.IsNullOrEmpty(inkXml))
            {
                return (false, "墨迹部件为空");
            }

            PptxPackage.EnsureXmlParses(inkXml, inkPartName);
            var readBack = InkMlReader.Read(inkXml);
            if (readBack.Drawing.Strokes.Count == 0 || readBack.Drawing.PointCount == 0)
            {
                return (false, "墨迹部件里没有采样点");
            }

            if (document.Bounds.Width <= 0 || document.Bounds.Height <= 0)
            {
                return (false, $"墨迹包围盒退化：{document.Bounds}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>计算文件 SHA256（小写十六进制）。</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>默认备份目录。</summary>
    public static string DefaultBackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PPTAssistant",
        "backups");

    public static string Backup(string pptxPath, string? backupDirectory = null, Action<string>? trace = null)
        => CreateBackup(pptxPath, backupDirectory, trace);

    private static string CreateBackup(string pptxPath, string? backupDirectory, Action<string>? trace)
    {
        var directory = backupDirectory ?? DefaultBackupDirectory;
        Directory.CreateDirectory(directory);

        var name = Path.GetFileNameWithoutExtension(pptxPath);
        var path = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pptx");
        File.Copy(pptxPath, path, overwrite: false);
        trace?.Invoke($"[package] 已备份原文件 → {path}");
        return path;
    }

    private static void RestoreFromBackup(string pptxPath, string? backupPath, Action<string>? trace)
    {
        if (backupPath is null || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Copy(backupPath, pptxPath, overwrite: true);
            trace?.Invoke($"[package] 已从备份回滚：{backupPath}");
        }
        catch (Exception ex)
        {
            trace?.Invoke($"[package] 回滚失败（原文件可能已被改动）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static (string PartName, int Number) NextInkPart(IReadOnlyList<string> parts)
    {
        var used = new HashSet<int>();
        foreach (var part in parts)
        {
            var match = Regex.Match(part, InkPartPattern);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                used.Add(number);
            }
        }

        var next = 1;
        while (used.Contains(next))
        {
            next++;
        }

        return ($"ppt/ink/ink{next}.xml", next);
    }

    private static string NextRelationshipId(string relsXml)
    {
        var max = 0;
        foreach (Match match in Regex.Matches(relsXml, "Id=\"rId(\\d+)\""))
        {
            if (int.TryParse(match.Groups[1].Value, out var value) && value > max)
            {
                max = value;
            }
        }

        return $"rId{max + 1}";
    }

    private static int NextShapeId(string slideXml)
    {
        var max = 1;
        foreach (Match match in Regex.Matches(slideXml, "<p:cNvPr id=\"(\\d+)\""))
        {
            if (int.TryParse(match.Groups[1].Value, out var value) && value > max)
            {
                max = value;
            }
        }

        return max + 1;
    }

    private static int ExtractInkNumber(string inkPartName)
    {
        var match = Regex.Match(inkPartName, InkPartPattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 1;
    }

    private static string InsertContentTypeOverride(string contentTypesXml, string inkPartName)
    {
        var insertion = $"<Override PartName=\"/{inkPartName}\" ContentType=\"application/inkml+xml\"/>";
        return contentTypesXml.Contains("</Types>", StringComparison.Ordinal)
            ? contentTypesXml.Replace("</Types>", insertion + "</Types>", StringComparison.Ordinal)
            : throw new InvalidDataException("[Content_Types].xml 里没有 </Types>");
    }

    private static string InsertInkRelationship(string relsXml, string relationshipId, int inkNumber)
    {
        var insertion =
            $"<Relationship Id=\"{relationshipId}\" "
            + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml\" "
            + $"Target=\"../ink/ink{inkNumber}.xml\"/>";
        return relsXml.Contains("</Relationships>", StringComparison.Ordinal)
            ? relsXml.Replace("</Relationships>", insertion + "</Relationships>", StringComparison.Ordinal)
            : throw new InvalidDataException("slide rels 里没有 </Relationships>");
    }

    private static string InsertContentPart(string slideXml, InkMlDocument document, string relationshipId, int shapeId)
    {
        var prefixes = ResolvePrefixes(slideXml);
        var p = prefixes[PresentationMl];
        var a = prefixes[DrawingMl];
        var r = prefixes[Relationships];

        var bounds = document.Bounds.WithMinimumSize(1.0);
        var offsetX = InkMlDialect.ToEmu(bounds.X);
        var offsetY = InkMlDialect.ToEmu(bounds.Y);
        var extentX = InkMlDialect.ToEmu(bounds.Width);
        var extentY = InkMlDialect.ToEmu(bounds.Height);

        var fragment =
            $"<mc:AlternateContent xmlns:mc=\"{MarkupCompatibility}\">"
            + $"<mc:Choice xmlns:p14=\"{PowerPoint2010}\" Requires=\"p14\">"
            + $"<{p}:contentPart p14:bwMode=\"auto\" {r}:id=\"{relationshipId}\">"
            + $"<p14:nvContentPartPr><p14:cNvPr id=\"{shapeId}\" name=\"PPTAssistant ink\"/>"
            + "<p14:cNvContentPartPr/><p14:nvPr/></p14:nvContentPartPr>"
            + $"<p14:xfrm><{a}:off x=\"{offsetX}\" y=\"{offsetY}\"/>"
            + $"<{a}:ext cx=\"{extentX}\" cy=\"{extentY}\"/></p14:xfrm>"
            + $"</{p}:contentPart>"
            + "</mc:Choice>"
            + "<mc:Fallback>"
            + $"<{p}:sp><{p}:nvSpPr><{p}:cNvPr id=\"{shapeId}\" name=\"PPTAssistant ink\"/><{p}:cNvSpPr/><{p}:nvPr/></{p}:nvSpPr>"
            + $"<{p}:spPr><{a}:xfrm><{a}:off x=\"{offsetX}\" y=\"{offsetY}\"/>"
            + $"<{a}:ext cx=\"{extentX}\" cy=\"{extentY}\"/></{a}:xfrm>"
            + $"<{a}:prstGeom prst=\"rect\"><{a}:avLst/></{a}:prstGeom><{a}:noFill/><{a}:ln><{a}:noFill/></{a}:ln></{p}:spPr>"
            + $"<{p}:txBody><{a}:bodyPr/><{a}:lstStyle/><{a}:p/></{p}:txBody></{p}:sp>"
            + "</mc:Fallback>"
            + "</mc:AlternateContent>";

        var closing = $"</{p}:spTree>";
        if (!slideXml.Contains(closing, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"slide XML 里没有 {closing}");
        }

        return slideXml.Replace(closing, fragment + closing, StringComparison.Ordinal);
    }

    /// <summary>从根元素的 xmlns 声明里解析出各命名空间用的前缀。</summary>
    private static Dictionary<XNamespace, string> ResolvePrefixes(string slideXml)
    {
        var root = XDocument.Parse(slideXml).Root
                   ?? throw new InvalidDataException("slide XML 没有根元素");

        var map = new Dictionary<XNamespace, string>();
        foreach (var attribute in root.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                map[XNamespace.Get(attribute.Value)] = attribute.Name.LocalName == "xmlns"
                    ? "p" // 默认命名空间（无前缀）——实际不会出现，兜底用 p
                    : attribute.Name.LocalName;
            }
        }

        foreach (var required in new[] { PresentationMl, DrawingMl, Relationships })
        {
            if (!map.ContainsKey(required))
            {
                throw new InvalidDataException($"slide XML 根元素上没有声明命名空间 {required}");
            }
        }

        return map;
    }
}
