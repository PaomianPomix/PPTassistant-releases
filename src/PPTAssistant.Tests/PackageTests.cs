using System.IO.Compression;
using System.Xml.Linq;
using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Package;

namespace PPTAssistant.Tests;

/// <summary>
/// OOXML 包注入：结构正确性、备份、回滚与原始数据完整性。
/// </summary>
public class InkInjectorTests : IDisposable
{
    private readonly string _workDirectory;

    public InkInjectorTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "pptassistant-package-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDirectory))
            {
                Directory.Delete(_workDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static InkDrawing SampleDrawing() => new(new SlideSize(960, 540),
    [
        new InkStroke(
            [
                new InkPoint(100, 100, 0.2f, 0),
                new InkPoint(300, 200, 0.6f, 25),
                new InkPoint(500, 150, 1.0f, 50),
            ],
            4.0,
            new InkColor(0xFF, 0x00, 0x00)),
    ]);

    private string CreateMinimalPptx(string name = "minimal.pptx")
        => CreateMinimalPptxAt(Path.Combine(_workDirectory, name));

    /// <summary>创建用于包结构测试的最小 PPTX。</summary>
    internal static string CreateMinimalPptxAt(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
            </Types>
            """);

        WriteEntry(archive, "ppt/slides/_rels/slide1.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
            </Relationships>
            """);

        WriteEntry(archive, "ppt/slides/slide1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>
            </p:sld>
            """);

        return path;
    }

    internal static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    [Fact]
    public void InjectsAllFourPiecesOfTheRecipe()
    {
        var pptx = CreateMinimalPptx();
        var backupDirectory = Path.Combine(_workDirectory, "backups");

        var result = InkInjector.Inject(pptx, 1, SampleDrawing(), backupDirectory);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ppt/ink/ink1.xml", result.InkPartName);
        Assert.Equal(1, result.StrokeCount);
        Assert.NotNull(result.OriginalSha256);

        // ① 墨迹部件
        var inkXml = PptxPackage.ReadText(pptx, "ppt/ink/ink1.xml");
        Assert.NotNull(inkXml);
        Assert.Contains("http://www.w3.org/2003/InkML", inkXml);

        // ② Content_Types 的 Override
        var contentTypes = PptxPackage.ReadText(pptx, "[Content_Types].xml")!;
        Assert.Contains("PartName=\"/ppt/ink/ink1.xml\"", contentTypes);
        Assert.Contains("application/inkml+xml", contentTypes);

        // ③ rels 的 customXml 关系
        var rels = PptxPackage.ReadText(pptx, "ppt/slides/_rels/slide1.xml.rels")!;
        Assert.Contains("relationships/customXml", rels);
        Assert.Contains("Target=\"../ink/ink1.xml\"", rels);

        var slide = PptxPackage.ReadText(pptx, "ppt/slides/slide1.xml")!;
        Assert.Contains("mc:AlternateContent", slide);
        Assert.Contains("p:contentPart", slide);
        Assert.Contains("p14:xfrm", slide);
        var expected = InkMlWriter.Write(SampleDrawing(), DateTime.UtcNow);
        Assert.Contains($"<a:off x=\"{InkMlDialect.ToEmu(expected.Bounds.X)}\" y=\"{InkMlDialect.ToEmu(expected.Bounds.Y)}\"/>", slide);
        Assert.Contains($"<a:ext cx=\"{InkMlDialect.ToEmu(expected.Bounds.Width)}\" cy=\"{InkMlDialect.ToEmu(expected.Bounds.Height)}\"/>", slide);
        // Fallback 也要在（旧客户端的显示退化路径）
        Assert.Contains("mc:Fallback", slide);

        // 关系 id 与 contentPart 引用必须一致
        var relationshipId = System.Text.RegularExpressions.Regex
            .Match(rels, "Id=\"(rId\\d+)\"[^>]*customXml").Groups[1].Value;
        Assert.Contains($"r:id=\"{relationshipId}\"", slide);
    }

    [Fact]
    public void KeepsEveryOriginalPartIntact()
    {
        var pptx = CreateMinimalPptx();
        var before = PptxPackage.ListParts(pptx).OrderBy(static p => p).ToArray();
        var relsBefore = PptxPackage.ReadText(pptx, "ppt/slides/_rels/slide1.xml.rels")!;
        var slideBefore = PptxPackage.ReadText(pptx, "ppt/slides/slide1.xml")!;

        Assert.True(InkInjector.Inject(pptx, 1, SampleDrawing(), Path.Combine(_workDirectory, "b")).Success);

        var after = PptxPackage.ListParts(pptx).OrderBy(static p => p).ToArray();
        // 只多了一个墨迹部件，原有部件一个不少（这是「不做 XDocument 全量重写」换来的保证）。
        Assert.Equal(before.Length + 1, after.Length);
        Assert.All(before, part => Assert.Contains(part, after));

        // 原有内容仍被完整保留（插入是「加」不是「改」）。
        var relsAfter = PptxPackage.ReadText(pptx, "ppt/slides/_rels/slide1.xml.rels")!;
        Assert.Contains("slideLayout1.xml", relsAfter);
        Assert.Contains("relationships/slideLayout", relsAfter);

        var slideAfter = PptxPackage.ReadText(pptx, "ppt/slides/slide1.xml")!;
        Assert.Contains("<p:grpSpPr/>", slideAfter);
        Assert.True(slideAfter.Length > slideBefore.Length);
        Assert.StartsWith("<?xml", slideAfter);

        // [Content_Types].xml 必须仍在包内第一位（OPC 约定）。
        Assert.Equal("[Content_Types].xml", PptxPackage.ListParts(pptx)[0]);
    }

    [Fact]
    public void CreatesBackupAndRecordsOriginalHash()
    {
        var pptx = CreateMinimalPptx();
        var backupDirectory = Path.Combine(_workDirectory, "backups");
        var hashBefore = InkInjector.ComputeSha256(pptx);

        var result = InkInjector.Inject(pptx, 1, SampleDrawing(), backupDirectory);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        Assert.Equal(hashBefore, result.OriginalSha256);
        Assert.Equal(hashBefore, InkInjector.ComputeSha256(result.BackupPath!));
        // 注入后原文件确实变了。
        Assert.NotEqual(hashBefore, InkInjector.ComputeSha256(pptx));
    }

    [Fact]
    public void RefusesToTouchFileWhenSlideIsMissing()
    {
        var pptx = CreateMinimalPptx();
        var hashBefore = InkInjector.ComputeSha256(pptx);
        var partsBefore = PptxPackage.ListParts(pptx).Count;

        // 第 9 页不存在 → 必须**干净失败**，且原文件一个字节都不变（不留半成品）。
        var result = InkInjector.Inject(pptx, 9, SampleDrawing(), Path.Combine(_workDirectory, "b"));

        Assert.False(result.Success);
        Assert.Equal(hashBefore, InkInjector.ComputeSha256(pptx));
        Assert.Equal(partsBefore, PptxPackage.ListParts(pptx).Count);
        // 临时文件也必须被清掉。
        Assert.False(File.Exists(pptx + ".pptassistant-tmp"));
    }

    [Fact]
    public void EmptyDrawingIsANoOp()
    {
        var pptx = CreateMinimalPptx();
        var hashBefore = InkInjector.ComputeSha256(pptx);

        var result = InkInjector.Inject(pptx, 1, new InkDrawing(new SlideSize(960, 540), []));

        Assert.False(result.Success);
        Assert.Contains("没有墨迹", result.Error);
        Assert.Equal(hashBefore, InkInjector.ComputeSha256(pptx));
    }

    [Fact]
    public void SecondInjectionUsesTheNextInkPartNumber()
    {
        var pptx = CreateMinimalPptx();
        var backup = Path.Combine(_workDirectory, "b");

        var first = InkInjector.Inject(pptx, 1, SampleDrawing(), backup);
        var second = InkInjector.Inject(pptx, 1, SampleDrawing(), backup);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal("ppt/ink/ink1.xml", first.InkPartName);
        Assert.Equal("ppt/ink/ink2.xml", second.InkPartName);

        // 两个部件都在，且指向各自的部件。
        var rels = PptxPackage.ReadText(pptx, "ppt/slides/_rels/slide1.xml.rels")!;
        Assert.Contains("../ink/ink1.xml", rels);
        Assert.Contains("../ink/ink2.xml", rels);
    }

    [Fact]
    public void VerifyCatchesStructuralDamage()
    {
        var pptx = CreateMinimalPptx();
        var backup = Path.Combine(_workDirectory, "b");
        var result = InkInjector.Inject(pptx, 1, SampleDrawing(), backup);
        Assert.True(result.Success, result.Error);

        // 自检对「部件不存在」必须报错（而不是静默通过）。
        var missing = InkInjector.Verify(pptx, 1, "ppt/ink/ink9.xml", "rId2", new InkMlDocument("<x/>", new InkBounds(0, 0, 1, 1), 1, false));
        Assert.False(missing.Success);

        // 正确参数下应当通过。
        var document = InkMlWriter.Write(SampleDrawing(), DateTime.UtcNow);
        var ok = InkInjector.Verify(pptx, 1, "ppt/ink/ink1.xml", "rId2", document);
        Assert.True(ok.Success, ok.Error);
    }

    [Fact]
    public void RejectsNonPptxInput()
    {
        var path = Path.Combine(_workDirectory, "not-a-pptx.pptx");
        File.WriteAllText(path, "这不是压缩包");

        var result = InkInjector.Inject(path, 1, SampleDrawing(), Path.Combine(_workDirectory, "b"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}

/// <summary>在自造的已有墨迹包中注入新墨迹，校验原有数据仍然完整。</summary>
public class InkInjectorExistingInkTests : IDisposable
{
    private readonly string _workDirectory;

    public InkInjectorExistingInkTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "pptassistant-existing-ink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void InjectsWithoutOverwritingExistingInkOrUnrelatedParts()
    {
        var target = InkInjectorTests.CreateMinimalPptxAt(Path.Combine(_workDirectory, "existing-ink.pptx"));
        AddExistingParts(target);
        var partsBefore = ReadPartBytes(target);
        var backupDirectory = Path.Combine(_workDirectory, "backups");

        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke(
                [
                    new InkPoint(120, 120, 0.3f, 0),
                    new InkPoint(480, 360, 0.9f, 40),
                ],
                6.0,
                new InkColor(0x00, 0x80, 0xFF)),
        ]);

        var result = InkInjector.Inject(target, 1, drawing, backupDirectory);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("ppt/ink/ink2.xml", result.InkPartName);

        var partsAfter = ReadPartBytes(target);
        Assert.Equal(partsBefore.Count + 1, partsAfter.Count);
        Assert.All(partsBefore.Keys, part => Assert.Contains(part, partsAfter.Keys));
        Assert.Contains(result.InkPartName!, partsAfter.Keys);
        Assert.DoesNotContain(result.InkPartName!, partsBefore.Keys);

        var changedParts = new HashSet<string>
        {
            "[Content_Types].xml", "ppt/slides/slide1.xml", "ppt/slides/_rels/slide1.xml.rels",
        };
        foreach (var (name, bytes) in partsBefore.Where(part => !changedParts.Contains(part.Key)))
        {
            Assert.Equal(bytes, partsAfter[name]);
        }

        var slide = XDocument.Parse(PptxPackage.ReadText(target, "ppt/slides/slide1.xml")!);
        XNamespace presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Assert.Equal(["rId2", "rId3"], slide.Descendants(presentation + "contentPart")
            .Select(part => (string?)part.Attribute(officeRelationships + "id")).ToArray());
        var relationships = XDocument.Parse(PptxPackage.ReadText(target, "ppt/slides/_rels/slide1.xml.rels")!);
        Assert.Contains(relationships.Root!.Elements(), relationship =>
            (string?)relationship.Attribute("Id") == "rId2"
            && (string?)relationship.Attribute("Target") == "../ink/ink1.xml");
        PptxPackage.EnsureXmlParses(PptxPackage.ReadText(target, result.InkPartName!)!, result.InkPartName!);

        var parsed = InkMlReader.Read(PptxPackage.ReadText(target, result.InkPartName!)!);
        Assert.Single(parsed.Drawing.Strokes);
        Assert.Equal(2, parsed.Drawing.Strokes[0].Points.Count);
        Assert.Equal(new InkColor(0x00, 0x80, 0xFF), parsed.Drawing.Strokes[0].Color);
    }

    private static void AddExistingParts(string path)
    {
        XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var types = XDocument.Parse(PptxPackage.ReadText(path, "[Content_Types].xml")!);
        types.Root!.Add(new XElement(contentTypes + "Override",
            new XAttribute("PartName", "/ppt/ink/ink1.xml"), new XAttribute("ContentType", "application/inkml+xml")));
        types.Root.Add(new XElement(contentTypes + "Default",
            new XAttribute("Extension", "bin"), new XAttribute("ContentType", "application/octet-stream")));
        var rels = XDocument.Parse(PptxPackage.ReadText(path, "ppt/slides/_rels/slide1.xml.rels")!);
        rels.Root!.Add(new XElement(relationships + "Relationship", new XAttribute("Id", "rId2"),
            new XAttribute("Type", officeRelationships.NamespaceName + "/customXml"), new XAttribute("Target", "../ink/ink1.xml")));
        var slide = XDocument.Parse(PptxPackage.ReadText(path, "ppt/slides/slide1.xml")!);
        slide.Descendants(presentation + "spTree").Single().Add(
            new XElement(presentation + "contentPart", new XAttribute(officeRelationships + "id", "rId2")));

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var (name, document) in new[]
        {
            ("[Content_Types].xml", types), ("ppt/slides/_rels/slide1.xml.rels", rels), ("ppt/slides/slide1.xml", slide),
        })
        {
            archive.GetEntry(name)!.Delete();
            InkInjectorTests.WriteEntry(archive, name, document.ToString(SaveOptions.DisableFormatting));
        }

        InkInjectorTests.WriteEntry(archive, "ppt/ink/ink1.xml", """
            <ink xmlns="http://www.w3.org/2003/InkML">
              <definitions><context xml:id="ctx0"/><brush xml:id="brush0">
                <brushProperty name="color" value="#008000"/>
              </brush></definitions>
              <trace contextRef="#ctx0" brushRef="#brush0">0 0 0,20 30 10</trace>
            </ink>
            """);
        InkInjectorTests.WriteEntry(archive, "customXml/settings.xml", "<settings owner=\"synthetic fixture\">保留原有文本</settings>");
        using var binary = archive.CreateEntry("ppt/media/payload.bin").Open();
        binary.Write([0, 1, 2, 128, 254, 255]);
    }

    private static Dictionary<string, byte[]> ReadPartBytes(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var source = entry.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            return memory.ToArray();
        });
    }
}
