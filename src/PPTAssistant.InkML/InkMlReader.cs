using System.Globalization;
using System.Xml.Linq;
using PPTAssistant.Coords;

namespace PPTAssistant.InkML;

public static class InkMlReader
{
    /// <summary>解析结果。</summary>
    /// <param name="Drawing">墨迹（坐标按 <c>bounds</c> 参数换算；未给定时为相对坐标）。</param>
    /// <param name="UnitsPerPoint">文件声明的 pt→本地单位换算率（由 resolution 推出）。</param>
    /// <param name="TimestampUtc">文件里的 timeString；解析失败为 null。</param>
    public sealed record Result(InkDrawing Drawing, double UnitsPerPoint, DateTime? TimestampUtc);

    public static Result Read(string xml, InkBounds? bounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("ink part 没有根元素");

        // 用 LocalName 匹配：命名空间前缀可能被 WPS 等实现改写。
        var definitions = Child(root, "definitions") ?? throw new InvalidDataException("缺少 definitions");
        var context = Child(definitions, "context");
        var inkSource = context is null ? null : Child(context, "inkSource");
        var traceFormat = inkSource is null ? null : Child(inkSource, "traceFormat");

        var channels = new List<string>();
        if (traceFormat is not null)
        {
            foreach (var channel in Children(traceFormat, "channel"))
            {
                var name = (string?)channel.Attribute("name");
                if (!string.IsNullOrEmpty(name))
                {
                    channels.Add(name!);
                }
            }
        }

        var resolution = ReadResolution(inkSource);
        var unitsPerPoint = resolution * 2.54 / 72.0;
        var timestamp = ReadTimestamp(context);

        // brush：色 + 宽度（同一 brush 的 width/height 取其一）
        var brushes = new Dictionary<string, (InkColor Color, double WidthPoints)>(StringComparer.Ordinal);
        foreach (var brush in Children(definitions, "brush"))
        {
            var id = (string?)brush.Attribute(XName.Get("id", "http://www.w3.org/XML/1998/namespace"))
                     ?? (string?)brush.Attribute("id")
                     ?? string.Empty;
            var color = InkColor.Default;
            var widthPoints = 3.0;
            foreach (var property in Children(brush, "brushProperty"))
            {
                var name = (string?)property.Attribute("name");
                var value = (string?)property.Attribute("value") ?? property.Value;
                switch (name)
                {
                    case "color":
                        color = InkColor.Parse(value);
                        break;
                    case "width":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) && cm > 0)
                        {
                            widthPoints = cm * 72.0 / 2.54;
                        }

                        break;
                }
            }

            brushes[id] = (color, widthPoints);
        }

        var strokes = new List<InkStroke>();
        var hasPressureChannel = channels.Contains("F");

        var rawTraces = new List<(string BrushRef, List<double[]> Points)>();
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var trace in Children(root, "trace"))
        {
            var brushRef = ((string?)trace.Attribute("brushRef") ?? string.Empty).TrimStart('#');
            var points = ParseTraceUnits(trace.Value, hasPressureChannel ? 4 : 3);
            if (points.Count == 0)
            {
                continue;
            }

            foreach (var point in points)
            {
                minX = Math.Min(minX, point[0]);
                maxX = Math.Max(maxX, point[0]);
                minY = Math.Min(minY, point[1]);
                maxY = Math.Max(maxY, point[1]);
            }

            rawTraces.Add((brushRef, points));
        }

        // 第二遍：把单位包围盒映射到 bounds 矩形（未给 bounds 时退化为「原点 0,0、1 单位 = 1 pt」）。
        var box = bounds ?? new InkBounds(0, 0, 0, 0);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        var scaleX = spanX > 0 && box.Width > 0 ? box.Width / spanX : 1.0;
        var scaleY = spanY > 0 && box.Height > 0 ? box.Height / spanY : 1.0;

        foreach (var (brushRef, points) in rawTraces)
        {
            var (color, widthPoints) = brushes.TryGetValue(brushRef, out var value)
                ? value
                : (InkColor.Default, 3.0);

            var converted = new List<InkPoint>(points.Count);
            foreach (var point in points)
            {
                converted.Add(new InkPoint(
                    box.X + ((point[0] - minX) * scaleX),
                    box.Y + ((point[1] - minY) * scaleY),
                    hasPressureChannel ? InkMlDialect.FromPressureChannel((int)Math.Round(point[3])) : 0f,
                    (int)Math.Round(point[2])));
            }

            strokes.Add(new InkStroke(converted, widthPoints, color));
        }

        var slideSize = bounds is { } provided && provided.Width > 0 && provided.Height > 0
            ? new SlideSize(provided.Right, provided.Bottom)
            : SlideSize.Widescreen;

        return new Result(new InkDrawing(slideSize, strokes), unitsPerPoint, timestamp);
    }

    private static List<double[]> ParseTraceUnits(string text, int channelCount)
    {
        var points = new List<double[]>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return points;
        }

        var previous = new double[4];
        var delta = new double[4];

        foreach (var rawPoint in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var values = ReadPointValues(rawPoint, channelCount);
            if (values.Count < 2)
            {
                continue;
            }

            var channels = new double[4];
            for (var channel = 0; channel < values.Count && channel < 4; channel++)
            {
                var (order, stored) = values[channel];
                channels[channel] = order switch
                {
                    1 => previous[channel] + stored,
                    2 => previous[channel] + delta[channel] + stored,
                    _ => stored,
                };
                delta[channel] = channels[channel] - previous[channel];
                previous[channel] = channels[channel];
            }

            points.Add(channels);
        }

        return points;
    }

    /// <summary>从一个「点」的文本里扫描出各通道值（含各自的差分阶数）。</summary>
    private static List<(int Order, double Value)> ReadPointValues(string text, int maxValues)
    {
        var result = new List<(int, double)>(maxValues);
        var index = 0;

        while (index < text.Length && result.Count < maxValues)
        {
            var order = 0;
            while (index < text.Length && (text[index] == '\'' || text[index] == '"'))
            {
                order++;
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+'))
            {
                index++;
            }

            while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.'))
            {
                index++;
            }

            if (index == start)
            {
                // 无法识别的字符（例如重复的分隔符）：跳过，避免死循环。
                index++;
                continue;
            }

            if (double.TryParse(
                    text.AsSpan(start, index - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                result.Add((order, value));
            }
        }

        return result;
    }

    private static double ReadResolution(XElement? inkSource)
    {
        var properties = inkSource is null ? null : Child(inkSource, "channelProperties");
        if (properties is null)
        {
            return InkMlDialect.Resolution;
        }

        foreach (var property in Children(properties, "channelProperty"))
        {
            if ((string?)property.Attribute("channel") == "X" &&
                (string?)property.Attribute("name") == "resolution" &&
                double.TryParse(
                    (string?)property.Attribute("value"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) &&
                value > 0)
            {
                return value;
            }
        }

        return InkMlDialect.Resolution;
    }

    private static DateTime? ReadTimestamp(XElement? context)
    {
        var timestamp = context is null ? null : Child(context, "timestamp");
        var text = (string?)timestamp?.Attribute("timeString");
        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static XElement? Child(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));
}
