using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PPTAssistant.Coords;

namespace PPTAssistant.Toolbar;

internal enum ToolIconKind
{
    Pointer,
    Pen,
    Eraser,
    Magnifier,
    Palette,
    Brush,
    Exit,
    ChevronLeft,
    ChevronRight,
    Thumbnails,
    Close,
}

internal enum ToolIconState
{
    /// <summary>常态：图标主体 <c>#FF666666</c>。</summary>
    Normal = 0,

    /// <summary>选中/按压态：几何变化 + 高亮层 <c>#FFD8D8D8</c> 显现。</summary>
    Selected = 1,
}

internal static class ToolIcons
{
    /// <summary>图标方格边长（与 <see cref="Geometry"/> 的坐标约定一致；由 <see cref="UiMetrics.IconDesignBox"/> 单点定义）。</summary>
    public const double Box = UiMetrics.IconDesignBox;

    private const double StrokeThickness = 1.9;

    /// <summary>造一个图标元素（<paramref name="state"/> 决定它是常态还是选中态）。</summary>
    public static UIElement Create(ToolIconKind kind, ToolIconState state)
    {
        var grid = new Grid { Width = Box, Height = Box, IsHitTestVisible = false };

        foreach (var (geometry, paint) in Parts(kind, state))
        {
            grid.Children.Add(CreatePath(geometry, paint));
        }

        return grid;
    }

    private static Path CreatePath(Geometry geometry, IconPaint paint)
    {
        var path = new Path
        {
            Data = geometry,
            IsHitTestVisible = false,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        switch (paint)
        {
            case IconPaint.Fill:
                path.Fill = UiTheme.IconNormal;
                break;

            case IconPaint.Stroke:
                path.Stroke = UiTheme.IconNormal;
                path.StrokeThickness = StrokeThickness;
                break;

            case IconPaint.HighlightFill:
                path.Fill = UiTheme.IconSelected;
                break;

            case IconPaint.HighlightStroke:
                path.Stroke = UiTheme.IconSelected;
                path.StrokeThickness = StrokeThickness * 0.8;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(paint), paint, null);
        }

        return path;
    }

    /// <summary>一个图标由哪些子图形组成、各自填充还是描边。</summary>
    private static IEnumerable<(Geometry Geometry, IconPaint Paint)> Parts(ToolIconKind kind, ToolIconState state)
    {
        var selected = state == ToolIconState.Selected;

        switch (kind)
        {
            case ToolIconKind.Pointer:

                yield return (Poly("M5,3 L5,20.5 L9.4,16.1 L12.2,21.8 L14.9,20.5 L12.1,15 L18.6,15 Z"), IconPaint.Fill);
                if (selected)
                {
                    yield return (Poly("M6.7,5.4 L6.7,17.3 L10.3,13.7"), IconPaint.HighlightStroke);
                }

                break;

            case ToolIconKind.Pen:
                yield return (Poly("M3.6,20.4 L4.1,16.2 L14.6,5.7 L18.3,9.4 L7.8,19.9 Z"), IconPaint.Fill);
                yield return (Poly("M15.4,4.9 L17.6,2.7 L21.3,6.4 L19.1,8.6 Z"), IconPaint.Fill);
                yield return (Poly("M3.6,20.4 L6.6,19.7 L4.3,17.4 Z"), IconPaint.Fill);
                if (selected)
                {
                    yield return (Poly("M5.9,16.6 L15.1,7.4 L16.2,8.5 L7.0,17.7 Z"), IconPaint.HighlightFill);
                }

                break;

            case ToolIconKind.Eraser:
                yield return (Poly("M3,18.2 L10.4,10.8 L20,10.8 L12.6,18.2 Z"), IconPaint.Fill);
                yield return (Poly("M10.2,11 L13.6,7.6 L23.2,7.6 L19.8,11 Z"), IconPaint.Fill);
                if (selected)
                {
                    yield return (Poly("M4.6,16.9 L11.0,10.5"), IconPaint.HighlightStroke);
                }

                break;

            case ToolIconKind.Magnifier:
                if (selected)
                {
                    // 选中：镜片变实心（线型→实心，一眼可辨），里面一道浅色反光。
                    yield return (new EllipseGeometry(new Point(10.4, 10.4), 6.6, 6.6), IconPaint.Fill);
                    yield return (Poly("M7.3,11.2 L11.2,7.3"), IconPaint.HighlightStroke);
                }
                else
                {
                    yield return (new EllipseGeometry(new Point(10.4, 10.4), 6.6, 6.6), IconPaint.Stroke);
                }

                yield return (new LineGeometry(new Point(15.3, 15.3), new Point(21.2, 21.2)), IconPaint.Stroke);
                break;

            case ToolIconKind.Palette:

                if (selected)
                {
                    yield return (new EllipseGeometry(new Point(12, 12), 8.6, 8.6), IconPaint.Fill);
                    yield return (new EllipseGeometry(new Point(9.1, 9.1), 1.5, 1.5), IconPaint.HighlightFill);
                    yield return (new EllipseGeometry(new Point(14.9, 9.1), 1.5, 1.5), IconPaint.HighlightFill);
                    yield return (new EllipseGeometry(new Point(12, 15), 1.5, 1.5), IconPaint.HighlightFill);
                }
                else
                {
                    yield return (new EllipseGeometry(new Point(12, 12), 8.6, 8.6), IconPaint.Stroke);
                    yield return (new EllipseGeometry(new Point(9.1, 9.1), 1.4, 1.4), IconPaint.Fill);
                    yield return (new EllipseGeometry(new Point(14.9, 9.1), 1.4, 1.4), IconPaint.Fill);
                    yield return (new EllipseGeometry(new Point(12, 15), 1.4, 1.4), IconPaint.Fill);
                }

                break;

            case ToolIconKind.Brush:
                yield return (Poly("M3.4,20.6 L3.4,17 L12.8,7.6 L17,11.8 L7.6,21.2 Z"), IconPaint.Fill);
                yield return (Poly("M13.6,6.8 L17.4,3 L21.2,6.8 L17.4,10.6 Z"), IconPaint.Fill);
                if (selected)
                {
                    yield return (Poly("M5.2,17.6 L13.8,9.0 L14.7,9.9 L6.1,18.5 Z"), IconPaint.HighlightFill);
                }

                break;

            case ToolIconKind.Exit:
                yield return (Poly("M10.5,4 L5,4 L5,20 L10.5,20"), IconPaint.Stroke);
                yield return (Poly("M11,12 L20,12"), IconPaint.Stroke);
                if (selected)
                {
                    yield return (Poly("M16.2,8.2 L20.4,12 L16.2,15.8 Z"), IconPaint.Fill);
                }
                else
                {
                    yield return (Poly("M16.6,8.6 L20,12 L16.6,15.4"), IconPaint.Stroke);
                }

                break;

            case ToolIconKind.ChevronLeft:
                yield return selected
                    ? (Poly("M15,4.4 L7.2,12 L15,19.6 Z"), IconPaint.Fill)
                    : (Poly("M15,5 L8,12 L15,19"), IconPaint.Stroke);
                break;

            case ToolIconKind.ChevronRight:
                yield return selected
                    ? (Poly("M9,4.4 L16.8,12 L9,19.6 Z"), IconPaint.Fill)
                    : (Poly("M9,5 L16,12 L9,19"), IconPaint.Stroke);
                break;

            case ToolIconKind.Thumbnails:
                foreach (var cell in ThumbnailCells())
                {
                    yield return (cell, selected ? IconPaint.Fill : IconPaint.Stroke);
                }

                break;

            case ToolIconKind.Close:
                yield return (Poly("M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5"), IconPaint.Stroke);
                break;

            default:
                yield break;
        }
    }

    /// <summary>缩略图图标的 6 个格子（3×2）。</summary>
    private static IEnumerable<Geometry> ThumbnailCells()
    {
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                yield return new RectangleGeometry(new Rect(3 + (column * 6.3), 4.5 + (row * 8.0), 5.4, 6.0));
            }
        }
    }

    private static Geometry Poly(string points)
    {
        // 约定：路径串以 Z 结尾 = 闭合填充型；否则是折线（线型不能闭合，否则会多一条回边）。
        var closed = points.TrimEnd().EndsWith('Z');
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var figure in points.Split('M', StringSplitOptions.RemoveEmptyEntries))
            {
                var numbers = figure
                    .Replace("L", " ")
                    .Replace("Z", " ")
                    .Replace(",", " ")
                    .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(static token => double.Parse(token, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
                if (numbers.Length < 4)
                {
                    continue;
                }

                context.BeginFigure(new Point(numbers[0], numbers[1]), isFilled: closed, isClosed: closed);
                for (var i = 2; i + 1 < numbers.Length; i += 2)
                {
                    context.LineTo(new Point(numbers[i], numbers[i + 1]), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>子图形的画法。</summary>
    private enum IconPaint
    {
        /// <summary>实心，主体色。</summary>
        Fill,

        /// <summary>描边，主体色。</summary>
        Stroke,

        /// <summary>实心，高亮色（浅灰）——必须叠在实心主体之上才看得见。</summary>
        HighlightFill,

        /// <summary>描边，高亮色（浅灰）。</summary>
        HighlightStroke,
    }
}
