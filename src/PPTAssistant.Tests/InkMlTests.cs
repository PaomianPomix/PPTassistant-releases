using System.Xml.Linq;
using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Tests;

public class InkMlWriterTests
{
    private static InkDrawing SampleDrawing(bool withPressure = false)
    {
        var strokes = new List<InkStroke>
        {
            new(
                [
                    new InkPoint(150, 150, withPressure ? 0.1f : 0f, 0),
                    new InkPoint(250, 200, withPressure ? 0.5f : 0f, 20),
                    new InkPoint(350, 150, withPressure ? 1.0f : 0f, 40),
                ],
                WidthPoints: 4.0,
                Color: new InkColor(0xFF, 0x00, 0x00)),
        };

        return new InkDrawing(new SlideSize(960, 540), strokes);
    }

    [Fact]
    public void DeclaresUtf8EncodingInTheXmlDeclaration()
    {

        var document = InkMlWriter.Write(SampleDrawing(), DateTime.UtcNow);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", document.Xml);
        Assert.DoesNotContain("utf-16", document.Xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmitsOfficeDialectEssentials()
    {
        var document = InkMlWriter.Write(SampleDrawing(), new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));
        var root = XDocument.Parse(document.Xml).Root!;

        Assert.Equal("ink", root.Name.LocalName);
        Assert.Equal("http://www.w3.org/2003/InkML", root.Name.NamespaceName);

        // 坑①：timeString 必须严格 YYYY-MM-DDTHH:MM:SS.mmm（无 Z、毫秒三位）。
        var timeString = (string?)root
            .Descendants().First(e => e.Name.LocalName == "timestamp").Attribute("timeString");
        Assert.Equal("2026-09-29T12:00:00.000", timeString);

        // 坑②：brushProperty 的值在 value 属性上，不是元素文本。
        var colorProperty = root.Descendants().First(e =>
            e.Name.LocalName == "brushProperty" && (string?)e.Attribute("name") == "color");
        Assert.Equal("#FF0000", (string?)colorProperty.Attribute("value"));
        Assert.Equal(string.Empty, colorProperty.Value);

        // 坑③：contextRef 指向 context，不是 inkSource。
        var contextId = (string?)root.Descendants().First(e => e.Name.LocalName == "context")
            .Attribute(XName.Get("id", "http://www.w3.org/XML/1998/namespace"));
        var trace = root.Elements().First(e => e.Name.LocalName == "trace");
        Assert.Equal("#ctx0", (string?)trace.Attribute("contextRef"));
        Assert.Equal("#ctx0", "#" + contextId);

        // 通道：X/Y 是 cm、T 是 dev；等宽笔迹不该出现 F 通道。
        var channels = root.Descendants()
            .Where(e => e.Name.LocalName == "channel")
            .Select(e => (string?)e.Attribute("name"))
            .ToArray();
        Assert.Equal(["X", "Y", "T"], channels);

        // brushProperty width 必须换算成厘米（4 pt = 0.14111 cm）。
        var widthProperty = root.Descendants().First(e =>
            e.Name.LocalName == "brushProperty" && (string?)e.Attribute("name") == "width");
        Assert.Equal("0.14111", (string?)widthProperty.Attribute("value"));
        Assert.Equal("cm", (string?)widthProperty.Attribute("units"));
    }

    [Fact]
    public void PressureEnablesFChannelWithDeclaredMax()
    {
        var document = InkMlWriter.Write(SampleDrawing(withPressure: true), DateTime.UtcNow);

        Assert.True(document.HasPressure);
        Assert.Contains("name=\"F\"", document.Xml);

        Assert.Contains("max=\"1023\"", document.Xml);
        // 有 F 通道时绝不能写 ignorePressure（写了就等于告诉渲染端忽略压力）。
        Assert.DoesNotContain("ignorePressure", document.Xml);

        var traceText = XDocument.Parse(document.Xml).Root!
            .Elements().First(e => e.Name.LocalName == "trace").Value;
        // 压力 0.1 / 0.5 / 1.0 → F 102 / 512 / 1023
        Assert.Contains(" 102", traceText);
        Assert.Contains(" 512", traceText);
        Assert.Contains(" 1023", traceText);
    }

    [Fact]
    public void FutureTimestampIsClampedToNow()
    {
        var future = DateTime.UtcNow.AddYears(1);
        var document = InkMlWriter.Write(SampleDrawing(), future);
        var timeString = (string?)XDocument.Parse(document.Xml).Root!
            .Descendants().First(e => e.Name.LocalName == "timestamp").Attribute("timeString");

        Assert.NotNull(timeString);
        Assert.True(
            DateTime.Parse(timeString!, System.Globalization.CultureInfo.InvariantCulture) <= DateTime.UtcNow.AddSeconds(2),
            $"时间戳不应是未来：{timeString}");
    }

    [Fact]
    public void BoundsCoverAllStrokesAndAreUsedAsTraceOrigin()
    {
        var document = InkMlWriter.Write(SampleDrawing(), DateTime.UtcNow);

        // 包围盒覆盖所有笔画（原点与尺寸都是；尺寸会按**整数单位跨度**微微调整，见下一条不变量）。
        Assert.Equal(150, document.Bounds.X, 3);
        Assert.Equal(150, document.Bounds.Y, 3);
        Assert.Equal(200, document.Bounds.Width, 0);
        Assert.Equal(50, document.Bounds.Height, 0);

        // 首点是相对包围盒原点的局部坐标 → 应当从 0 开始。
        var traceText = XDocument.Parse(document.Xml).Root!
            .Elements().First(e => e.Name.LocalName == "trace").Value;
        Assert.StartsWith("0 0 0", traceText.Trim());
    }

    [Theory]
    [InlineData(200, 50)]
    [InlineData(400, 0)]      // 完全水平（退化轴）
    [InlineData(0, 300)]      // 完全竖直（退化轴）
    [InlineData(0.05, 0.02)]  // 极小笔画
    public void BoundsMatchTheIntegerUnitSpanExactly(double width, double height)
    {
        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke(
                [
                    new InkPoint(100, 100, 0, 0),
                    new InkPoint(100 + width, 100 + height, 0, 10),
                ],
                4.0,
                InkColor.Default),
        ]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);
        var trace = XDocument.Parse(document.Xml).Root!
            .Elements().First(e => e.Name.LocalName == "trace").Value;

        // trace 的整数单位跨度
        var points = trace.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => (X: int.Parse(p[0]), Y: int.Parse(p[1])))
            .ToList();
        var spanX = points.Max(p => p.X) - points.Min(p => p.X);
        var spanY = points.Max(p => p.Y) - points.Min(p => p.Y);

        // ① 两轴都不能退化成 0（否则渲染端映射失定义）
        Assert.True(spanX >= 1, $"x 单位跨度应 ≥ 1，实际 {spanX}");
        Assert.True(spanY >= 1, $"y 单位跨度应 ≥ 1，实际 {spanY}");

        // ② 框尺寸 = 单位跨度 ÷ 换算率（严格对应，无拉伸）
        Assert.Equal(spanX / document.UnitsPerPoint, document.Bounds.Width, 6);
        Assert.Equal(spanY / document.UnitsPerPoint, document.Bounds.Height, 6);
    }

    /// <summary>一笔完全水平的线：几何不能被"撑开 1 个单位"这件事挪走（误差远小于 1 pt）。</summary>
    [Fact]
    public void DegenerateAxisSpreadDoesNotMoveTheInk()
    {
        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke([new InkPoint(200, 300, 0, 0), new InkPoint(800, 300, 0, 20)], 4.0, InkColor.Default),
        ]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);
        var readBack = InkMlReader.Read(document.Xml, document.Bounds);

        // 退化轴被撑开 1 个单位（≈0.5 pt）——这是"渲染端映射不失定义"的代价，
        // 只落在**最后一个点**上，量级远小于 1 pt（肉眼不可见）。
        // 另：坐标以整数单位编码，整幅跨度会有 ≤1 单位（≈0.52 pt）的量化误差——这是方言的固有精度上限。
        const double Quantum = 0.6;
        Assert.Equal(300.0, readBack.Drawing.Strokes[0].Points[0].Y, 3);
        Assert.True(
            Math.Abs(readBack.Drawing.Strokes[0].Points[1].Y - 300.0) <= 1.0 / document.UnitsPerPoint,
            $"末点被撑开的量应 ≤ 1 个单位（{1.0 / document.UnitsPerPoint:0.###} pt）");
        Assert.Equal(200.0, readBack.Drawing.Strokes[0].Points[0].X, 3);
        Assert.True(
            Math.Abs(readBack.Drawing.Strokes[0].Points[1].X - 800.0) <= Quantum,
            $"末点 x 的量化误差应 ≤ {Quantum} pt，实际 {Math.Abs(readBack.Drawing.Strokes[0].Points[1].X - 800.0):0.###}");
    }

    [Fact]
    public void DistinctStylesProduceDistinctBrushes()
    {
        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke([new InkPoint(10, 10, 0, 0), new InkPoint(20, 20, 0, 5)], 3.0, new InkColor(0xFF, 0, 0)),
            new InkStroke([new InkPoint(30, 30, 0, 0), new InkPoint(40, 40, 0, 5)], 3.0, new InkColor(0xFF, 0, 0)),
            new InkStroke([new InkPoint(50, 50, 0, 0), new InkPoint(60, 60, 0, 5)], 8.0, new InkColor(0, 0, 0xFF)),
        ]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);
        var root = XDocument.Parse(document.Xml).Root!;

        Assert.Equal(2, root.Descendants().Count(e => e.Name.LocalName == "brush"));
        Assert.Equal(3, root.Elements().Count(e => e.Name.LocalName == "trace"));
    }
}

/// <summary>生成 → 解析的往返一致性。</summary>
public class InkMlRoundTripTests
{
    [Fact]
    public void GeneratedInkReadsBackWithSameGeometry()
    {
        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke(
                [
                    new InkPoint(200.5, 300.25, 0.25f, 0),
                    new InkPoint(260.75, 340.5, 0.5f, 16),
                    new InkPoint(320.125, 300.75, 0.75f, 32),
                ],
                6.0,
                new InkColor(0x00, 0x85, 0xCD)),
        ]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);
        var parsed = InkMlReader.Read(document.Xml, document.Bounds);

        var original = drawing.Strokes[0];
        var readBack = parsed.Drawing.Strokes[0];

        Assert.Equal(original.Points.Count, readBack.Points.Count);
        Assert.Equal(original.Color, readBack.Color);
        // 笔宽经 cm 往返：4 位有效数字的精度足够（0.01 pt 级）。
        Assert.Equal(original.WidthPoints, readBack.WidthPoints, 2);

        for (var i = 0; i < original.Points.Count; i++)
        {
            // 本地坐标是整数单位，量化台阶 = 1 单位 ≈ 0.52 pt（约 0.18 mm）——这是方言本身的精度上限。
            Assert.True(
                Math.Abs(original.Points[i].X - readBack.Points[i].X) < 0.6,
                $"X 往返误差过大：{original.Points[i].X} → {readBack.Points[i].X}");
            Assert.True(
                Math.Abs(original.Points[i].Y - readBack.Points[i].Y) < 0.6,
                $"Y 往返误差过大：{original.Points[i].Y} → {readBack.Points[i].Y}");
            Assert.Equal(original.Points[i].T, readBack.Points[i].T);
            Assert.Equal(original.Points[i].Pressure, readBack.Points[i].Pressure, 2);
        }
    }

    [Fact]
    public void MultipleStrokesKeepTheirOwnStylesThroughRoundTrip()
    {
        var drawing = new InkDrawing(new SlideSize(960, 540),
        [
            new InkStroke([new InkPoint(10, 10, 0, 0), new InkPoint(100, 100, 0, 10)], 2.0, new InkColor(0xFF, 0, 0)),
            new InkStroke([new InkPoint(400, 200, 0, 0), new InkPoint(500, 260, 0, 10)], 12.0, new InkColor(0x00, 0xFF, 0x00)),
        ]);

        var document = InkMlWriter.Write(drawing, DateTime.UtcNow);
        var parsed = InkMlReader.Read(document.Xml, document.Bounds);

        Assert.Equal(2, parsed.Drawing.Strokes.Count);
        Assert.Equal(new InkColor(0xFF, 0, 0), parsed.Drawing.Strokes[0].Color);
        Assert.Equal(new InkColor(0x00, 0xFF, 0x00), parsed.Drawing.Strokes[1].Color);
        Assert.Equal(2.0, parsed.Drawing.Strokes[0].WidthPoints, 1);
        Assert.Equal(12.0, parsed.Drawing.Strokes[1].WidthPoints, 1);
    }

    /// <summary>差分前缀与负号可以直接分隔通道值，无需额外空白。</summary>
    [Fact]
    public void ParsesSyntheticInkWithCompactDifferentialEncoding()
    {
        const string xml = """
            <ink xmlns="http://www.w3.org/2003/InkML">
              <definitions>
                <context xml:id="ctx0">
                  <inkSource xml:id="source0"><traceFormat>
                    <channel name="X" type="integer" units="cm"/>
                    <channel name="Y" type="integer" units="cm"/>
                    <channel name="T" type="integer" units="dev"/>
                  </traceFormat></inkSource>
                  <timestamp xml:id="time0" timeString="2026-01-02T03:04:05.006"/>
                </context>
                <brush xml:id="blue"><brushProperty name="color" value="#0000FF"/></brush>
              </definitions>
              <trace contextRef="#ctx0" brushRef="#blue">100 200 0,'30'40'10,'-5'15'5,90-20 25</trace>
            </ink>
            """;
        var parsed = InkMlReader.Read(xml);

        Assert.Single(parsed.Drawing.Strokes);
        var stroke = parsed.Drawing.Strokes[0];
        Assert.Equal(new InkColor(0x00, 0x00, 0xFF), stroke.Color);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc), parsed.TimestampUtc);
        // 未指定外部包围盒时，以轨迹最小坐标 (90, -20) 为原点。
        Assert.Equal(
            [new InkPoint(10, 220, 0, 0), new InkPoint(40, 260, 0, 10),
             new InkPoint(35, 275, 0, 15), new InkPoint(0, 0, 0, 25)],
            stroke.Points);
    }
}

public class InkMlNormalizerTests
{
    private const string WpsDamaged =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<inkml:ink xmlns:inkml=\"http://www.w3.org/2003/InkML\"><inkml:definitions>" +
        "<inkml:context xml:id=\"ctx0\"><inkml:inkSource xml:id=\"inkSrc0\"><inkml:traceFormat>" +
        "<inkml:channel name=\"X\" type=\"integer\" max=\"2944\" units=\"cm\"/></inkml:traceFormat>" +
        "<inkml:channelProperties><inkml:channelProperty channel=\"X\" name=\"resolution\" " +
        "value=\"54.82309\" units=\"1/cm\"/></inkml:channelProperties></inkml:inkSource>" +
        "<inkml:timestamp xml:id=\"ts0\" timeString=\"2026-09-29T03:30:37\"/></inkml:context>" +
        "</inkml:definitions><inkml:trace contextRef=\"#ctx0\">150.000 200.000 0.000, 160.000 210.000 20.000" +
        "</inkml:trace></inkml:ink>";

    [Fact]
    public void RestoresMillisecondsInTimeString()
    {
        var fixedXml = InkMlNormalizer.Normalize(WpsDamaged);

        Assert.Contains("timeString=\"2026-09-29T03:30:37.000\"", fixedXml);
    }

    [Fact]
    public void StripsTrailingZerosInsideTraceOnly()
    {
        var fixedXml = InkMlNormalizer.Normalize(WpsDamaged);

        Assert.Contains(">150 200 0, 160 210 20</", fixedXml);
        // 绝不能碰属性里的高精度数值（那是方言要求的一部分）。
        Assert.Contains("value=\"54.82309\"", fixedXml);
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = InkMlNormalizer.Normalize(WpsDamaged);
        var twice = InkMlNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void LeavesAlreadyCorrectXmlUntouched()
    {
        var document = InkMlWriter.Write(
            new InkDrawing(new SlideSize(960, 540),
                [new InkStroke([new InkPoint(10, 10, 0, 0), new InkPoint(50, 50, 0, 10)], 4.0, InkColor.Default)]),
            DateTime.UtcNow);

        Assert.False(InkMlNormalizer.NeedsNormalization(document.Xml));
        Assert.Equal(document.Xml, InkMlNormalizer.Normalize(document.Xml));
    }

    [Fact]
    public void RepairedXmlIsParseableAndKeepsGeometry()
    {
        var before = InkMlReader.Read(WpsDamaged);
        var after = InkMlReader.Read(InkMlNormalizer.Normalize(WpsDamaged));

        Assert.Equal(before.Drawing.Strokes[0].Points.Count, after.Drawing.Strokes[0].Points.Count);
        Assert.Equal(before.Drawing.Strokes[0].Points[1].X, after.Drawing.Strokes[0].Points[1].X, 3);
    }
}
