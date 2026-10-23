using System.Globalization;
using System.Text;
using System.Xml;
using PPTAssistant.Coords;

namespace PPTAssistant.InkML;

public sealed record InkMlDocument(string Xml, InkBounds Bounds, double UnitsPerPoint, bool HasPressure);

public static class InkMlWriter
{
    private const double MinimumBoundsSize = 1.0;

    /// <summary>生成 ink part。</summary>
    /// <param name="drawing">墨迹（幻灯片坐标，pt）。</param>
    /// <param name="timestampUtc">书写时间（UTC；未来时间会被回落到当前时间——见方言说明）。</param>
    public static InkMlDocument Write(InkDrawing drawing, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(drawing);

        var physical = drawing.ComputeBounds() ?? new InkBounds(0, 0, 0, 0);
        var unitsPerPoint = InkMlDialect.UnitsPerPointFor(physical.WithMinimumSize(MinimumBoundsSize));

        var unitSpans = MeasureUnitSpans(drawing, physical, unitsPerPoint);
        var bounds = new InkBounds(
            physical.X,
            physical.Y,
            unitSpans.SpanX / unitsPerPoint,
            unitSpans.SpanY / unitsPerPoint);
        var degenerateX = unitSpans.SpanX <= 1 && physical.Width * unitsPerPoint < 1.5;
        var degenerateY = unitSpans.SpanY <= 1 && physical.Height * unitsPerPoint < 1.5;
        bounds = new InkBounds(
            bounds.X,
            bounds.Y,
            degenerateX ? 1.0 / unitsPerPoint : bounds.Width,
            degenerateY ? 1.0 / unitsPerPoint : bounds.Height);

        var spread = new UnitSpread(degenerateX, degenerateY);
        var hasPressure = drawing.HasPressure;

        // 每种 (颜色, 笔宽) 一个 brush；笔画按首次出现顺序分配 brushRef。
        var brushes = new List<(InkColor Color, double WidthPoints)>();
        var brushIndexByStroke = new List<int>(drawing.Strokes.Count);
        foreach (var stroke in drawing.Strokes)
        {
            var index = brushes.FindIndex(b =>
                b.Color == stroke.Color && Math.Abs(b.WidthPoints - stroke.WidthPoints) < 0.0001);
            if (index < 0)
            {
                brushes.Add((stroke.Color, stroke.WidthPoints));
                index = brushes.Count - 1;
            }

            brushIndexByStroke.Add(index);
        }

        var builder = new StringBuilder(4096);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        byte[] bytes;
        using (var stream = new MemoryStream(4096))
        {
            using (var writer = XmlWriter.Create(stream, settings))
            {
                WriteDocument(writer, drawing, timestampUtc, brushes, brushIndexByStroke, bounds, unitsPerPoint, hasPressure, spread);
            }

            bytes = stream.ToArray();
        }

        return new InkMlDocument(Encoding.UTF8.GetString(bytes), bounds, unitsPerPoint, hasPressure);
    }

    /// <summary>把整份文档写进给定的 writer（用 <see cref="XmlWriter"/> 以保证转义与命名空间正确）。</summary>
    private static void WriteDocument(
        XmlWriter writer,
        InkDrawing drawing,
        DateTime timestampUtc,
        List<(InkColor Color, double WidthPoints)> brushes,
        List<int> brushIndexByStroke,
        InkBounds bounds,
        double unitsPerPoint,
        bool hasPressure,
        UnitSpread spread)
    {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement(InkMlDialect.Prefix, "ink", InkMlDialect.Namespace);

            writer.WriteStartElement(InkMlDialect.Prefix, "definitions", InkMlDialect.Namespace);

            // context：traceFormat + channelProperties + timestamp
            writer.WriteStartElement(InkMlDialect.Prefix, "context", InkMlDialect.Namespace);
            writer.WriteAttributeString("xml", "id", null, "ctx0");
            writer.WriteStartElement(InkMlDialect.Prefix, "inkSource", InkMlDialect.Namespace);
            writer.WriteAttributeString("xml", "id", null, "inkSrc0");

            writer.WriteStartElement(InkMlDialect.Prefix, "traceFormat", InkMlDialect.Namespace);
            WriteChannel(writer, "X", InkMlDialect.ChannelMaxX.ToString(CultureInfo.InvariantCulture), "cm");
            WriteChannel(writer, "Y", InkMlDialect.ChannelMaxY.ToString(CultureInfo.InvariantCulture), "cm");
            WriteChannel(writer, "T", InkMlDialect.ChannelMaxT, "dev");
            if (hasPressure)
            {
                WriteChannel(writer, "F", InkMlDialect.ChannelMaxF.ToString(CultureInfo.InvariantCulture), "dev");
            }

            writer.WriteEndElement(); // traceFormat

            writer.WriteStartElement(InkMlDialect.Prefix, "channelProperties", InkMlDialect.Namespace);
            WriteChannelProperty(writer, "X", "resolution", InkMlDialect.Invariant(InkMlDialect.Resolution, "0.#####"), "1/cm");
            WriteChannelProperty(writer, "Y", "resolution", InkMlDialect.Invariant(InkMlDialect.Resolution, "0.#####"), "1/cm");
            WriteChannelProperty(writer, "T", "resolution", "1", "1/dev");
            writer.WriteEndElement(); // channelProperties

            writer.WriteEndElement(); // inkSource

            writer.WriteStartElement(InkMlDialect.Prefix, "timestamp", InkMlDialect.Namespace);
            writer.WriteAttributeString("xml", "id", null, "ts0");
            writer.WriteAttributeString("timeString", InkMlDialect.FormatTimestamp(timestampUtc));
            writer.WriteEndElement(); // timestamp

            writer.WriteEndElement(); // context

            for (var i = 0; i < brushes.Count; i++)
            {
                var (color, widthPoints) = brushes[i];
                var widthCm = InkMlDialect.ToCentimeters(widthPoints);
                writer.WriteStartElement(InkMlDialect.Prefix, "brush", InkMlDialect.Namespace);
                writer.WriteAttributeString("xml", "id", null, $"br{i}");
                WriteBrushProperty(writer, "width", InkMlDialect.Invariant(widthCm, "0.#####"), "cm");
                WriteBrushProperty(writer, "height", InkMlDialect.Invariant(widthCm, "0.#####"), "cm");
                WriteBrushProperty(writer, "color", color.ToHex(), null);
                writer.WriteEndElement(); // brush
            }

            writer.WriteEndElement(); // definitions

            for (var i = 0; i < drawing.Strokes.Count; i++)
            {
                var stroke = drawing.Strokes[i];
                if (stroke.Points.Count == 0)
                {
                    continue;
                }

                writer.WriteStartElement(InkMlDialect.Prefix, "trace", InkMlDialect.Namespace);
                writer.WriteAttributeString("contextRef", "#ctx0");
                writer.WriteAttributeString("brushRef", $"#br{brushIndexByStroke[i]}");
                writer.WriteString(FormatTrace(stroke, bounds, unitsPerPoint, hasPressure, spread));
                writer.WriteEndElement(); // trace
            }

            writer.WriteEndElement(); // ink
            writer.WriteEndDocument();
    }

    /// <summary>整数单位跨度的最小安全值：太小的跨度会让「单位 ↔ pt」的映射失去意义。</summary>
    private const int MinimumUnitSpan = 1;

    /// <summary>某一轴是否需要人为撑出 1 个单位（退化轴）。</summary>
    private readonly record struct UnitSpread(bool X, bool Y);

    /// <summary>按 <paramref name="unitsPerPoint"/> 把整幅墨迹量成整数单位跨度（每轴至少 1）。</summary>
    private static (int SpanX, int SpanY) MeasureUnitSpans(InkDrawing drawing, InkBounds physical, double unitsPerPoint)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        foreach (var stroke in drawing.Strokes)
        {
            foreach (var point in stroke.Points)
            {
                var x = InkMlDialect.ToUnits(point.X - physical.X, unitsPerPoint);
                var y = InkMlDialect.ToUnits(point.Y - physical.Y, unitsPerPoint);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (minX == int.MaxValue)
        {
            return (MinimumUnitSpan, MinimumUnitSpan);
        }

        return (
            Math.Max(MinimumUnitSpan, maxX - minX),
            Math.Max(MinimumUnitSpan, maxY - minY));
    }

    /// <summary>把一个 trace 的所有点编成 "x y t [f], x y t [f], …"（绝对坐标）。</summary>
    private static string FormatTrace(InkStroke stroke, InkBounds bounds, double unitsPerPoint, bool includePressure, UnitSpread spread)
    {
        var builder = new StringBuilder(stroke.Points.Count * 18);
        for (var i = 0; i < stroke.Points.Count; i++)
        {
            var point = stroke.Points[i];
            if (i > 0)
            {
                builder.Append(", ");
            }

            var isLast = i == stroke.Points.Count - 1;
            var x = InkMlDialect.ToUnits(point.X - bounds.X, unitsPerPoint) + (isLast && spread.X ? 1 : 0);
            var y = InkMlDialect.ToUnits(point.Y - bounds.Y, unitsPerPoint) + (isLast && spread.Y ? 1 : 0);
            builder.Append(x);
            builder.Append(' ');
            builder.Append(y);
            builder.Append(' ');
            builder.Append(point.T.ToString(CultureInfo.InvariantCulture));

            if (includePressure)
            {
                // 没有压感数据的点写 0（渲染端会画成最细），有数据的按 max=1023 映射。
                builder.Append(' ');
                builder.Append(InkMlDialect.ToPressureChannel(point.Pressure));
            }
        }

        return builder.ToString();
    }

    private static void WriteChannel(XmlWriter writer, string name, string max, string units)
    {
        writer.WriteStartElement(InkMlDialect.Prefix, "channel", InkMlDialect.Namespace);
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("type", "integer");
        writer.WriteAttributeString("max", max);
        writer.WriteAttributeString("units", units);
        writer.WriteEndElement();
    }

    private static void WriteChannelProperty(XmlWriter writer, string channel, string name, string value, string units)
    {
        writer.WriteStartElement(InkMlDialect.Prefix, "channelProperty", InkMlDialect.Namespace);
        writer.WriteAttributeString("channel", channel);
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("value", value);
        writer.WriteAttributeString("units", units);
        writer.WriteEndElement();
    }

    private static void WriteBrushProperty(XmlWriter writer, string name, string value, string? units)
    {
        writer.WriteStartElement(InkMlDialect.Prefix, "brushProperty", InkMlDialect.Namespace);
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("value", value);
        if (units is not null)
        {
            writer.WriteAttributeString("units", units);
        }

        writer.WriteEndElement();
    }
}
