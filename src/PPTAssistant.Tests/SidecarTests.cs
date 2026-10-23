using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Session.Sidecar;

namespace PPTAssistant.Tests;

public class SidecarLogTests : IDisposable
{
    private readonly string _root;

    public SidecarLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pptassistant-sidecar-test", Guid.NewGuid().ToString("N"));
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

    private string CreateDocument(string name = "lesson.pptx", string content = "fake pptx")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static InkStroke Stroke(double x, double y, int t = 0, string color = "#D91B1B", double width = 8.0)
        => new(
            [new InkPoint(x, y, 0.5f, t), new InkPoint(x + 10, y + 5, 0.9f, t + 20)],
            width,
            InkColor.Parse(color));

    [Fact]
    public void StrokeIsOnDiskBeforeTheProcessEnds()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);

        log.AppendPage(1, 3);
        log.AppendStroke(1, Stroke(100, 200, 0), "Pen");

        // ★ 刻意不 Dispose（模拟被强杀）：内容必须已经在磁盘上。
        var readBack = SidecarStore.Read(log.Path);

        Assert.True(readBack.HasInk);
        Assert.Single(readBack.Strokes);
        Assert.Equal(1, readBack.Strokes[0].PageIndex);
        Assert.Equal(2, readBack.Strokes[0].Points.Count);
        Assert.Equal(100, readBack.Strokes[0].Points[0].X, 2);
        Assert.Equal(0.5f, readBack.Strokes[0].Points[0].Pressure, 3);

        log.Dispose();
    }

    [Fact]
    public void HeaderCarriesFingerprintAndSlideSize()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, new SlideSize(960, 540), _root);
        log.AppendStroke(1, Stroke(1, 1), "Mouse");

        var readBack = SidecarStore.Read(log.Path);

        Assert.Equal(SidecarHeader.CurrentVersion, readBack.Header.Version);
        Assert.Equal(Path.GetFullPath(document), readBack.Header.DocumentPath);
        Assert.Equal(960, readBack.Header.SlideWidthPoints, 3);
        Assert.Equal(540, readBack.Header.SlideHeightPoints, 3);
        Assert.False(string.IsNullOrWhiteSpace(readBack.Header.Fingerprint));
        Assert.NotEqual(default, readBack.Header.StartedUtc);

        log.Dispose();
    }

    [Fact]
    public void TruncatedLastLineIsToleratedNotFatal()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(10, 10), "Pen");
        var path = log.Path;
        log.Dispose();

        // 模拟「写到一半被拔电源」：追加半行 JSON。
        File.AppendAllText(path, "{\"kind\":\"stroke\",\"page\":1,\"pts\":[[12,3");

        var readBack = SidecarStore.Read(path);

        // 已经写完的那一笔必须完好；残缺的那行只计入 damaged，不影响其余内容。
        Assert.Single(readBack.Strokes);
        Assert.Equal(1, readBack.DamagedLineCount);
    }

    [Fact]
    public void MultiplePagesKeepTheirOwnStrokes()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);

        log.AppendPage(1, 2);
        log.AppendStroke(1, Stroke(10, 10), "Pen");
        log.AppendPage(2, 2);
        log.AppendStroke(2, Stroke(20, 20), "Pen");
        log.AppendStroke(2, Stroke(30, 30), "Pen");

        var readBack = SidecarStore.Read(log.Path);
        var drawings = readBack.BuildDrawings();

        Assert.Equal([1, 2], readBack.PagesSeen);
        Assert.Equal(2, drawings.Count);
        Assert.Single(drawings[1].Strokes);
        Assert.Equal(2, drawings[2].Strokes.Count);
        // 画面尺寸要跟着表头走，否则写回时 InkML 的坐标基准就错了。
        Assert.Equal(960, drawings[1].SlideSize.Width, 3);

        log.Dispose();
    }

    [Fact]
    public void BuildDrawingsProducesInkReadyForInjection()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(3, Stroke(120, 240, 0, "#00FF00", 14.0), "Mouse");

        var drawings = SidecarStore.Read(log.Path).BuildDrawings();
        var drawing = drawings[3];

        Assert.True(drawing.HasPressure);
        Assert.Equal(14.0, drawing.Strokes[0].WidthPoints, 3);
        Assert.Equal(new InkColor(0x00, 0xFF, 0x00), drawing.Strokes[0].Color);

        var ink = InkMlWriter.Write(drawing, DateTime.UtcNow);
        Assert.Contains("InkML", ink.Xml);

        log.Dispose();
    }

    [Fact]
    public void FindRecoverablePairsByPathAndDetectsChange()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(5, 5), "Pen");
        log.Dispose();

        // ① 文件没动过 → 完全匹配。
        var exact = SidecarStore.FindRecoverable(document, _root);
        Assert.Single(exact);
        Assert.Equal(SidecarMatchKind.Exact, exact[0].MatchKind);

        // ② 文件被改过（老师中途保存了课件）→ 仍然能靠路径找到，但要标记为「仅路径匹配」，
        //    因为坐标基准可能已经变了，恢复前必须提醒用户。
        File.AppendAllText(document, "  // changed");
        var pathOnly = SidecarStore.FindRecoverable(document, _root);
        Assert.Single(pathOnly);
        Assert.Equal(SidecarMatchKind.PathOnly, pathOnly[0].MatchKind);
    }

    [Fact]
    public void FindRecoverableIgnoresLogsWithoutInk()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendPage(1, 5);
        log.AppendEnd("showEnd");
        log.Dispose();

        // 一笔没写的日志没有恢复价值（否则老师每次开一次放映都会被问一次「要不要恢复」）。
        Assert.Empty(SidecarStore.FindRecoverable(document, _root));
    }

    [Fact]
    public void FindRecoverableDoesNotMixUpDifferentDocuments()
    {
        var first = CreateDocument("a.pptx", "A");
        var second = CreateDocument("b.pptx", "B");

        var logA = SidecarLog.Start(first, SlideSize.Widescreen, _root);
        logA.AppendStroke(1, Stroke(1, 1), "Pen");
        logA.Dispose();

        var logB = SidecarLog.Start(second, SlideSize.Widescreen, _root);
        logB.AppendStroke(1, Stroke(2, 2), "Pen");
        logB.Dispose();

        var foundForA = SidecarStore.FindRecoverable(first, _root);
        Assert.Single(foundForA);
        Assert.Equal(Path.GetFullPath(first), foundForA[0].Session.Header.DocumentPath);
    }

    [Fact]
    public void DeleteRemovesTheLogAndItsData()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(1, 1), "Pen");
        var path = log.Path;
        log.Dispose();

        SidecarStore.Delete(path);

        Assert.False(File.Exists(path));
        Assert.Empty(SidecarStore.FindRecoverable(document, _root));
    }

    [Fact]
    public void PruneRemovesOnlyOldLogs()
    {
        var document = CreateDocument();
        var fresh = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        fresh.AppendStroke(1, Stroke(1, 1), "Pen");
        fresh.Dispose();

        var oldDocument = CreateDocument("old.pptx", "old");
        var old = SidecarLog.Start(oldDocument, SlideSize.Widescreen, _root);
        old.AppendStroke(1, Stroke(1, 1), "Pen");
        var oldPath = old.Path;
        old.Dispose();
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-30));

        var removed = SidecarStore.PruneOlderThan(TimeSpan.FromDays(7), _root);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(fresh.Path));
    }

    [Fact]
    public void MissingHeaderIsReportedLoudly()
    {
        var path = Path.Combine(_root, "broken.jsonl");
        File.WriteAllText(path, "{\"kind\":\"stroke\",\"page\":1,\"pts\":[[1,2,3,4]]}\n");

        // 没有表头就没有坐标基准与配对信息 —— 这种日志必须**明确报错**，不能当空日志静默放过。
        Assert.Throws<InvalidDataException>(() => SidecarStore.Read(path));
        Assert.Null(SidecarStore.TryRead(path));
    }

    [Fact]
    public void StrokeWithoutWidthOrColorFieldsFallsBackInsteadOfFailing()
    {
        var path = Path.Combine(_root, "legacy.jsonl");
        File.WriteAllText(
            path,
            "{\"kind\":\"header\",\"version\":1,\"fingerprint\":\"x\",\"document\":\"d\",\"documentSize\":1,"
            + "\"documentLastWrite\":\"2026-01-01T00:00:00Z\",\"slideWidth\":960,\"slideHeight\":540,"
            + "\"started\":\"2026-01-01T00:00:00Z\",\"tool\":\"legacy\"}\n"
            + "{\"kind\":\"stroke\",\"page\":1,\"device\":\"Pen\",\"pts\":[[10,20,0.5,0],[30,40,0.9,20]]}\n");

        var session = SidecarStore.Read(path);

        Assert.True(session.HasInk);
        var stroke = session.Strokes[0];
        Assert.True(stroke.WidthPoints > 0, "缺 width 时必须回落一个正的默认笔宽");
        Assert.False(string.IsNullOrWhiteSpace(stroke.ColorHex), "缺 color 时必须回落默认色");
        Assert.Equal(2, stroke.Points.Count);
    }

    /// <summary>逐笔宽度/颜色在旁路日志里各自保持（改宽前后两笔落盘不同的值）。</summary>
    [Fact]
    public void PerStrokeWidthsAndColorsSurviveTheRoundTrip()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(0, 0, 0, "#FF0000", 3.0), "Pen");
        log.AppendStroke(1, Stroke(50, 0, 0, "#0085CD", 12.0), "Pen");
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Equal(2, session.Strokes.Count);
        Assert.Equal(3.0, session.Strokes[0].WidthPoints, 3);
        Assert.Equal(12.0, session.Strokes[1].WidthPoints, 3);
        Assert.Equal("#FF0000", session.Strokes[0].ColorHex);
        Assert.Equal("#0085CD", session.Strokes[1].ColorHex);
    }

    [Fact]
    public void StrokeIdsSurviveTheRoundTrip()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(10, 10), "Pen", strokeId: 7);
        log.AppendStroke(1, Stroke(20, 20), "Pen", strokeId: 8);
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Equal([7, 8], session.Strokes.Select(static stroke => stroke.Id));
    }

    /// <summary>
    /// 橡皮擦除必须写进旁路日志，读回后**被删的笔画不再进入写回模型**。
    /// 这是「屏上擦了、保存后又出现」的直接防线。
    /// </summary>
    [Fact]
    public void EraseRecordRemovesExactlyTheNamedStroke()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(10, 10), "Pen", strokeId: 1);
        log.AppendStroke(1, Stroke(20, 20), "Pen", strokeId: 2);
        log.AppendStroke(1, Stroke(30, 30), "Pen", strokeId: 3);
        log.AppendErase(1, [2]);
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Equal([1, 3], session.Strokes.Select(static stroke => stroke.Id));
        var drawings = session.BuildDrawings();
        Assert.Single(drawings);                 // 只有第 1 页
        Assert.Equal(2, drawings[1].Strokes.Count); // 第 1 与第 3 笔仍在
    }

    /// <summary>
    /// 老师可能翻回某一页继续写：erase 只删「记录之前」的笔画，之后的必须保留。
    /// </summary>
    [Fact]
    public void EraseOnlyAffectsStrokesWrittenBeforeIt()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(2, Stroke(1, 1), "Pen", strokeId: 10);
        log.AppendErase(2, [10]);
        log.AppendStroke(2, Stroke(2, 2), "Pen", strokeId: 11);
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Single(session.Strokes);
        Assert.Equal(11, session.Strokes[0].Id);
    }

    /// <summary>清空整页（长按橡皮）只清当前页，且不影响之后新写的笔画。</summary>
    [Fact]
    public void ClearPageRemovesEarlierStrokesOnThatPageOnly()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(1, 1), "Pen", strokeId: 1);
        log.AppendStroke(1, Stroke(2, 2), "Pen", strokeId: 2);
        log.AppendStroke(2, Stroke(3, 3), "Pen", strokeId: 3);
        log.AppendClearPage(1);
        log.AppendStroke(1, Stroke(4, 4), "Pen", strokeId: 4);
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Equal([3, 4], session.Strokes.Select(static stroke => stroke.Id));
    }

    /// <summary>擦除记录只作用于它写明的页（防御性：编号排布出问题时也不能误删别页）。</summary>
    [Fact]
    public void EraseForAnotherPageDoesNotTouchThisPage()
    {
        var document = CreateDocument();
        var log = SidecarLog.Start(document, SlideSize.Widescreen, _root);
        log.AppendStroke(1, Stroke(1, 1), "Pen", strokeId: 5);
        log.AppendErase(2, [5]);
        log.Dispose();

        var session = SidecarStore.Read(log.Path);

        Assert.Single(session.Strokes);
    }

    /// <summary>旧日志没有 id 字段：必须照常读回（Id=0），不能因缺字段整份日志不可用。</summary>
    [Fact]
    public void LegacyStrokeWithoutIdReadsAsZeroAndIsNeverErasedById()
    {
        var path = Path.Combine(_root, "legacy-no-id.jsonl");
        File.WriteAllText(
            path,
            "{\"kind\":\"header\",\"version\":1,\"fingerprint\":\"x\",\"document\":\"d\",\"documentSize\":1,"
            + "\"documentLastWrite\":\"2026-01-01T00:00:00Z\",\"slideWidth\":960,\"slideHeight\":540,"
            + "\"started\":\"2026-01-01T00:00:00Z\",\"tool\":\"legacy\"}\n"
            + "{\"kind\":\"stroke\",\"page\":1,\"device\":\"Pen\",\"pts\":[[10,20,0.5,0],[30,40,0.9,20]]}\n");

        var session = SidecarStore.Read(path);

        Assert.Single(session.Strokes);
        Assert.Equal(0, session.Strokes[0].Id);
    }
}
