using PPTAssistant.Session;
using System.IO.Compression;
using PPTAssistant.Coords;
using PPTAssistant.Host;
using PPTAssistant.InkML;
using PPTAssistant.Package;
using PPTAssistant.Session.Sidecar;
using PPTAssistant.Session.WriteBack;

namespace PPTAssistant.Tests;

public class InkWriteBackTests : IDisposable
{
    private readonly string _root;

    public InkWriteBackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pptassistant-writeback-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>造一个最小合法 pptx。</summary>
    private string CreatePptx(string name = "lesson.pptx")
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>" +
            "<Override PartName=\"/ppt/slides/slide2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>" +
            "</Types>");

        for (var i = 1; i <= 2; i++)
        {
            WriteEntry(archive, $"ppt/slides/_rels/slide{i}.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                "</Relationships>");

            WriteEntry(archive, $"ppt/slides/slide{i}.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr/></p:spTree></p:cSld></p:sld>");
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>造一份"有板书的旁路日志"：第 1 页一笔、第 2 页两笔。</summary>
    private SidecarSession CreateSessionWithInk(string documentPath, int pages = 2)
    {
        var log = SidecarLog.Start(documentPath, new SlideSize(960, 540), _root);
        for (var page = 1; page <= pages; page++)
        {
            log.AppendPage(page, pages);
            var strokes = page == 1 ? 1 : 2;
            for (var i = 0; i < strokes; i++)
            {
                log.AppendStroke(
                    page,
                    new InkStroke(
                        [new InkPoint(100 + (i * 20), 100, 0.3f, 0), new InkPoint(200 + (i * 20), 220, 0.8f, 30)],
                        6.0,
                        InkColor.Default),
                    "Pen");
            }
        }

        log.AppendEnd("showEnd");
        var path = log.Path;
        log.Dispose();
        return SidecarStore.Read(path);
    }

    private sealed class FakeHost : IWriteBackHost
    {
        public List<string> Calls { get; } = [];

        public bool FailSaveAndClose { get; init; }

        public bool FailReopen { get; init; }

        public bool SaveAndClose(string documentPath, out bool wasOpen, out string? error)
        {
            Calls.Add("SaveAndClose");
            wasOpen = true;
            error = FailSaveAndClose ? "假宿主：关闭失败" : null;
            return !FailSaveAndClose;
        }

        public bool Reopen(string documentPath, out string? error)
        {
            Calls.Add("Reopen");
            error = FailReopen ? "假宿主：重开失败" : null;
            return !FailReopen;
        }

        public bool IsFileLocked(string documentPath)
        {
            Calls.Add("IsFileLocked");
            return false;
        }
    }

    [Fact]
    public void CommitInjectsEveryPageAndDeletesTheLog()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);
        var host = new FakeHost();
        var backups = Path.Combine(_root, "backups");

        var result = InkWriteBack.Commit(session, host, backups);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.PagesWritten);
        Assert.Equal(3, result.StrokesWritten);
        Assert.True(result.Reopened);
        Assert.True(result.LogDeleted);

        Assert.Equal(["SaveAndClose", "Reopen"], host.Calls);

        // 两页都真的注入了墨迹（各自一个 ink 部件），且引用关系正确。
        var parts = PptxPackage.ListParts(pptx);
        Assert.Contains("ppt/ink/ink1.xml", parts);
        Assert.Contains("ppt/ink/ink2.xml", parts);

        var slide1 = PptxPackage.ReadText(pptx, "ppt/slides/slide1.xml")!;
        var slide2 = PptxPackage.ReadText(pptx, "ppt/slides/slide2.xml")!;
        Assert.Contains("p:contentPart", slide1);
        Assert.Contains("p:contentPart", slide2);

        // 墨迹内容能读回来，页数正确。
        var ink1 = InkMlReader.Read(PptxPackage.ReadText(pptx, "ppt/ink/ink1.xml")!);
        var ink2 = InkMlReader.Read(PptxPackage.ReadText(pptx, "ppt/ink/ink2.xml")!);
        Assert.Single(ink1.Drawing.Strokes);
        Assert.Equal(2, ink2.Drawing.Strokes.Count);

        // 日志删掉了（板书已经进课件，日志的使命完成）。
        Assert.False(File.Exists(session.Path));
        // 备份留着（老师后悔时的唯一退路）。
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public void BackupHappensOnceForTheWholeCommit()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx, pages: 2);
        var backups = Path.Combine(_root, "backups");

        var result = InkWriteBack.Commit(session, new FakeHost(), backups);

        Assert.True(result.Success, result.Error);
        // 逐页备份会在一次写回里堆出 N 份副本；整份只该有一份。
        var files = Directory.GetFiles(backups, "*.pptx");
        Assert.Single(files);
    }

    [Fact]
    public void SaveAndCloseFailureAbortsWithoutTouchingTheFile()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);
        var hashBefore = InkInjector.ComputeSha256(pptx);
        var host = new FakeHost { FailSaveAndClose = true };

        var result = InkWriteBack.Commit(session, host, Path.Combine(_root, "backups"));

        Assert.False(result.Success);
        Assert.Contains("关闭", result.Error);

        Assert.Equal(hashBefore, InkInjector.ComputeSha256(pptx));
        // 日志必须保留：那是老师唯一的板书副本。
        Assert.True(File.Exists(session.Path));
        // 也不该重开（它根本没被关掉）。
        Assert.DoesNotContain("Reopen", host.Calls);
    }

    [Fact]
    public void MissingDocumentFailsWithoutDeletingTheLog()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);
        File.Delete(pptx);

        var result = InkWriteBack.Commit(session, new FakeHost(), Path.Combine(_root, "backups"));

        Assert.False(result.Success);
        Assert.Contains("不在原位置", result.Error);
        Assert.True(File.Exists(session.Path));
    }

    [Fact]
    public void ReopenFailureStillCountsAsSuccess()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);

        // 重开失败不影响"板书已经写进文件"这个事实——老师的文件是好的，
        // 只是要自己再打开一次。不能因此回滚已经成功的写入。
        var result = InkWriteBack.Commit(session, new FakeHost { FailReopen = true }, Path.Combine(_root, "backups"));

        Assert.True(result.Success, result.Error);
        Assert.False(result.Reopened);
        Assert.True(result.LogDeleted);
        Assert.Contains("ppt/ink/ink1.xml", PptxPackage.ListParts(pptx));
    }

    [Fact]
    public void NullHostSkipsComButRefusesWhenFileIsLocked()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);

        // 崩溃恢复路径：PowerPoint 没在跑 → 不需要 COM，直接改文件。
        var result = InkWriteBack.Commit(session, host: null, Path.Combine(_root, "backups"));
        Assert.True(result.Success, result.Error);

        // 文件被独占（PowerPoint 正开着）→ 必须拒绝，而不是冒险写坏它。
        var second = CreateSessionWithInk(pptx);
        using (var hold = new FileStream(pptx, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = InkWriteBack.Commit(second, host: null, Path.Combine(_root, "backups"));
            Assert.False(locked.Success);
            Assert.Contains("正被 PowerPoint 打开", locked.Error);
        }

        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public void CommitWithEverythingErasedDeletesTheLogWithoutTouchingTheDocument()
    {
        var pptx = CreatePptx();
        var hashBefore = InkInjector.ComputeSha256(pptx);
        var log = SidecarLog.Start(pptx, new SlideSize(960, 540), _root);
        log.AppendStroke(
            1,
            new InkStroke([new InkPoint(100, 100, 0.5f, 0), new InkPoint(200, 200, 0.5f, 20)], 6.0, InkColor.Default),
            "Pen",
            strokeId: 1);
        log.AppendErase(1, [1]);
        log.AppendEnd("showEnd");
        var path = log.Path;
        log.Dispose();

        var session = SidecarStore.Read(path);
        Assert.False(session.HasInk);

        var host = new FakeHost();
        var result = InkWriteBack.Commit(session, host, Path.Combine(_root, "backups"));

        Assert.True(result.Success, result.Error);
        Assert.True(result.LogDeleted);
        Assert.False(File.Exists(path));
        Assert.Empty(host.Calls); // 没有墨迹 ⇒ 连 COM 都不该碰
        Assert.Equal(hashBefore, InkInjector.ComputeSha256(pptx));
        Assert.DoesNotContain("ppt/ink/ink1.xml", PptxPackage.ListParts(pptx));
    }

    [Fact]
    public void DiscardDeletesTheLogAndNeverTouchesTheDocument()
    {
        var pptx = CreatePptx();
        var session = CreateSessionWithInk(pptx);
        var hashBefore = InkInjector.ComputeSha256(pptx);

        var deleted = InkWriteBack.Discard(session);

        Assert.True(deleted);
        Assert.False(File.Exists(session.Path));
        Assert.Equal(hashBefore, InkInjector.ComputeSha256(pptx));
        Assert.DoesNotContain("ppt/ink/ink1.xml", PptxPackage.ListParts(pptx));
    }
}

public class WriteBackPromptTests
{
    [Fact]
    public void SilentPromptObeysConfiguredChoice()
    {
        var retain = new SilentWriteBackPrompt { CommitChoice = WriteBackChoice.Retain };
        Assert.Equal(WriteBackChoice.Retain, retain.AskCommit(10, 2, "x.pptx"));
        // 恢复路径的默认是"先不决定"——没人看着的时候不该擅自改用户文件。
        Assert.Equal(WriteBackChoice.Later, retain.AskRecover(10, "x.pptx", true, DateTime.UtcNow));

        var discard = new SilentWriteBackPrompt { CommitChoice = WriteBackChoice.Discard };
        Assert.Equal(WriteBackChoice.Discard, discard.AskCommit(1, 1, "y.pptx"));
    }

    [Fact]
    public void SessionOptionsDefaultsToAskingTheUser()
    {
        var options = new SessionOptions();

        Assert.Equal(WriteBackMode.Ask, options.WriteBackMode);
        Assert.True(options.ReopenAfterWriteBack);
        Assert.True(options.SidecarEnabled);
    }
}
