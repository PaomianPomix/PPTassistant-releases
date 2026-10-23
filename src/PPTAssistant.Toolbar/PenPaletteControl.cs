using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.Toolbar;

public enum PalettePickSource
{
    /// <summary>点预设 / 最近使用的色块（快选：选完即收起）。</summary>
    Swatch = 0,

    /// <summary>拖色相条或明度/饱和度方块（精调：保持打开，允许继续微调）。</summary>
    Picker = 1,

    /// <summary>在 RGB 输入框里输入并按回车 / 失焦提交（精调：保持打开）。</summary>
    Rgb = 2,
}

/// <summary>一次选色事件：颜色 + 来源。</summary>
/// <param name="Color">选中的颜色。</param>
/// <param name="Source">来源（见 <see cref="PalettePickSource"/>）。</param>
public sealed class PalettePickEventArgs(InkColor color, PalettePickSource source) : EventArgs
{
    /// <summary>选中的颜色。</summary>
    public InkColor Color { get; } = color;

    /// <summary>来源。</summary>
    public PalettePickSource Source { get; } = source;
}

public sealed class PenPaletteControl : ContentControl
{
    private const double SwatchSize = 30;
    private const double SquareWidth = 208;
    private const double SquareHeight = 132;
    private const double HueWidth = 208;
    private const double HueHeight = 16;
    private const double ThumbSize = 12;

    private readonly StackPanel _presetRow;
    private readonly StackPanel _recentRow;
    private readonly Dictionary<InkColor, Border> _swatchBorders = [];
    private readonly List<Border> _recentSlots = [];

    private readonly Canvas _square;
    private readonly Border _squareBase;
    private readonly Ellipse _squareThumb;
    private readonly Canvas _hue;
    private readonly Border _hueStrip;
    private readonly Border _hueThumb;
    private readonly Border _preview;
    private readonly TextBox _redBox;
    private readonly TextBox _greenBox;
    private readonly TextBox _blueBox;

    private UiTierMetrics? _appliedMetrics;
    private CornerRadius _swatchRadius = new(6);
    private HsvColor _hsv = new(0, 0, 0);
    private bool _suppress;
    private bool _draggingHue;
    private bool _draggingSquare;

    public PenPaletteControl()
    {
        _presetRow = new StackPanel { Orientation = Orientation.Horizontal };
        _recentRow = new StackPanel { Orientation = Orientation.Horizontal };

        _hue = new Canvas
        {
            Width = HueWidth,
            Height = HueHeight,
            Margin = new Thickness(0, 4, 0, 6),
            Background = Brushes.Transparent,
        };
        _hueStrip = new Border
        {
            Width = HueWidth,
            Height = HueHeight,
            CornerRadius = new CornerRadius(3),
            Background = BuildHueGradient(),
            IsHitTestVisible = false,
        };
        _hue.Children.Add(_hueStrip);
        _hueThumb = new Border
        {
            Width = 4,
            Height = HueHeight + 6,
            Background = UiTheme.Text,
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
        };
        _hue.Children.Add(_hueThumb);
        _hue.MouseLeftButtonDown += OnHueDown;
        _hue.MouseMove += OnHueMove;
        _hue.MouseLeftButtonUp += OnHueUp;

        // ——— 取色器：饱和度 / 明度方块 ———
        // 同色相条：Canvas 自带 Transparent 底色才可命中（见上方说明）。
        _square = new Canvas { Width = SquareWidth, Height = SquareHeight, Background = Brushes.Transparent };
        _squareBase = new Border { Width = SquareWidth, Height = SquareHeight, IsHitTestVisible = false };
        _square.Children.Add(_squareBase);
        _square.Children.Add(new Border
        {
            Width = SquareWidth,
            Height = SquareHeight,
            // 白/黑是取色器方块的**数学端点**（饱和度/明度的定义），不是主题色——主题只管界面，不管色域。
            Background = BuildHorizontalFade(Colors.White),
            IsHitTestVisible = false,
        });
        _square.Children.Add(new Border
        {
            Width = SquareWidth,
            Height = SquareHeight,
            Background = BuildVerticalFade(Colors.Black),
            IsHitTestVisible = false,
        });
        _squareThumb = new Ellipse
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Stroke = UiTheme.Text,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _square.Children.Add(_squareThumb);
        _square.MouseLeftButtonDown += OnSquareDown;
        _square.MouseMove += OnSquareMove;
        _square.MouseLeftButtonUp += OnSquareUp;

        // ——— 当前色预览 + RGB 数值 ———
        _preview = new Border
        {
            Width = 46,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            BorderBrush = UiTheme.Border,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _redBox = CreateNumberBox();
        _greenBox = CreateNumberBox();
        _blueBox = CreateNumberBox();
        _redBox.LostFocus += (_, _) => CommitRgb();
        _greenBox.LostFocus += (_, _) => CommitRgb();
        _blueBox.LostFocus += (_, _) => CommitRgb();
        _redBox.KeyDown += OnRgbKeyDown;
        _greenBox.KeyDown += OnRgbKeyDown;
        _blueBox.KeyDown += OnRgbKeyDown;

        var rgbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        rgbRow.Children.Add(_preview);
        rgbRow.Children.Add(new TextBlock
        {
            Text = "RGB",
            Foreground = UiTheme.TextMuted,
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        rgbRow.Children.Add(_redBox);
        rgbRow.Children.Add(_greenBox);
        rgbRow.Children.Add(_blueBox);

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(SectionLabel("预设"));
        root.Children.Add(_presetRow);
        root.Children.Add(SectionLabel("最近使用"));
        root.Children.Add(_recentRow);
        root.Children.Add(SectionLabel("取色"));
        root.Children.Add(_square);
        root.Children.Add(_hue);
        root.Children.Add(rgbRow);
        Content = root;

        BuildPresets();
        BuildRecentSlots();
        ApplyHsv(new HsvColor(0, 0.88, 1.0));
    }

    public event EventHandler<PalettePickEventArgs>? ColorPicked;

    /// <summary>当前颜色。</summary>
    public InkColor CurrentColor => InkPalette.HsvToRgb(_hsv);

    public void ApplyMetrics(UiTierMetrics metrics)
    {
        if (_appliedMetrics == metrics)
        {
            return;
        }

        _appliedMetrics = metrics;
        _swatchRadius = new CornerRadius(Math.Min(metrics.CornerRadius, SwatchSize / 4));

        foreach (var swatch in _swatchBorders.Values)
        {
            swatch.CornerRadius = _swatchRadius;
        }

        foreach (var slot in _recentSlots)
        {
            slot.CornerRadius = _swatchRadius;
        }

        _preview.CornerRadius = new CornerRadius(Math.Min(metrics.CornerRadius, 6));
        _hueStrip.CornerRadius = new CornerRadius(Math.Min(metrics.CornerRadius, HueHeight / 2));
    }

    /// <summary>同步当前色（只更新显示，不抛 <see cref="ColorPicked"/>）。</summary>
    public void SetCurrentColor(InkColor color)
    {
        _suppress = true;
        try
        {
            ApplyHsv(InkPalette.RgbToHsv(color));
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>同步最近使用色（新色顶置；不足 5 个时显示空槽，避免行宽跳动）。</summary>
    public void SetRecentColors(IReadOnlyList<InkColor> recent)
    {
        for (var i = 0; i < _recentSlots.Count; i++)
        {
            var slot = _recentSlots[i];
            if (i < recent.Count)
            {
                slot.Background = new SolidColorBrush(ToColor(recent[i]));
                slot.ToolTip = $"{recent[i].ToHex()}（最近使用）";
                var captured = recent[i];
                slot.Tag = captured;
                slot.Opacity = 1;
            }
            else
            {
                slot.Background = Brushes.Transparent;
                slot.ToolTip = "（还没有用过其它颜色）";
                slot.Tag = null;
                slot.Opacity = 0.25;
            }
        }

        UpdateSwatchSelection();
    }

    private void BuildPresets()
    {
        foreach (var color in InkPalette.Presets)
        {
            var swatch = CreateSwatch(color, "预设");
            _swatchBorders[color] = swatch;
            _presetRow.Children.Add(swatch);
        }
    }

    private void BuildRecentSlots()
    {
        for (var i = 0; i < InkPalette.MaxRecent; i++)
        {
            var slot = CreateSwatch(new InkColor(0, 0, 0), "最近使用");
            slot.Background = Brushes.Transparent;
            slot.Opacity = 0.25;
            slot.Tag = null;
            _recentSlots.Add(slot);
            _recentRow.Children.Add(slot);
        }
    }

    private Border CreateSwatch(InkColor color, string label)
    {
        var swatch = new Border
        {
            Width = SwatchSize,
            Height = SwatchSize,
            Margin = new Thickness(3, 3, 0, 3),
            CornerRadius = _swatchRadius,
            BorderThickness = new Thickness(1),
            BorderBrush = UiTheme.Border,
            Background = new SolidColorBrush(ToColor(color)),
            Cursor = Cursors.Hand,
            ToolTip = $"{color.ToHex()}（{label}）",
            Tag = color,
        };

        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var picked = swatch.Tag is InkColor value ? value : color;
            ApplyHsv(InkPalette.RgbToHsv(picked));
            RaisePicked(picked, PalettePickSource.Swatch);
        };

        return swatch;
    }

    private static TextBox CreateNumberBox() => new()
    {
        Width = 48,
        Margin = new Thickness(0, 0, 6, 0),
        TextAlignment = TextAlignment.Center,
        Background = UiTheme.White,
        Foreground = UiTheme.Text,
        BorderBrush = UiTheme.Border,
        BorderThickness = new Thickness(1),
        MaxLength = 3,
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = UiTheme.TextMuted,
        FontSize = 12,
        Margin = new Thickness(0, 8, 0, 2),
    };

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        _hue.CaptureMouse();
        UpdateHueFrom(e.GetPosition(_hue));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue)
        {
            UpdateHueFrom(e.GetPosition(_hue));
        }
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHue)
        {
            return;
        }

        _draggingHue = false;
        _hue.ReleaseMouseCapture();
        RaisePicked(CurrentColor, PalettePickSource.Picker);
    }

    private void UpdateHueFrom(Point position)
    {
        var hue = Math.Clamp(position.X / HueWidth, 0, 1) * 360;
        ApplyHsv(_hsv with { Hue = hue });
    }

    private void OnSquareDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSquare = true;
        _square.CaptureMouse();
        UpdateSquareFrom(e.GetPosition(_square));
    }

    private void OnSquareMove(object sender, MouseEventArgs e)
    {
        if (_draggingSquare)
        {
            UpdateSquareFrom(e.GetPosition(_square));
        }
    }

    private void OnSquareUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingSquare)
        {
            return;
        }

        _draggingSquare = false;
        _square.ReleaseMouseCapture();
        RaisePicked(CurrentColor, PalettePickSource.Picker);
    }

    private void UpdateSquareFrom(Point position)
    {
        var saturation = Math.Clamp(position.X / SquareWidth, 0, 1);
        var value = 1 - Math.Clamp(position.Y / SquareHeight, 0, 1);
        ApplyHsv(_hsv with { Saturation = saturation, Value = value });
    }

    private void OnRgbKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRgb();
        }
    }

    private void CommitRgb()
    {
        if (_suppress)
        {
            return;
        }

        var color = new InkColor(ParseByte(_redBox.Text), ParseByte(_greenBox.Text), ParseByte(_blueBox.Text));
        ApplyHsv(InkPalette.RgbToHsv(color));
        RaisePicked(color, PalettePickSource.Rgb);
    }

    private static byte ParseByte(string? text)
        => byte.TryParse(text, out var value) ? value : (byte)0;

    private void RaisePicked(InkColor color, PalettePickSource source)
    {
        if (_suppress)
        {
            return;
        }

        ColorPicked?.Invoke(this, new PalettePickEventArgs(color, source));
    }

    private void ApplyHsv(HsvColor hsv)
    {
        _hsv = hsv;
        var color = CurrentColor;

        _squareBase.Background = new SolidColorBrush(ToColor(InkPalette.HsvToRgb(new HsvColor(hsv.Hue, 1, 1))));
        Canvas.SetLeft(_squareThumb, (hsv.Saturation * SquareWidth) - (ThumbSize / 2));
        Canvas.SetTop(_squareThumb, ((1 - hsv.Value) * SquareHeight) - (ThumbSize / 2));
        Canvas.SetLeft(_hueThumb, ((hsv.Hue / 360) * HueWidth) - 2);
        Canvas.SetTop(_hueThumb, -3);

        _preview.Background = new SolidColorBrush(ToColor(color));
        if (!_redBox.IsKeyboardFocusWithin)
        {
            _redBox.Text = color.R.ToString();
        }

        if (!_greenBox.IsKeyboardFocusWithin)
        {
            _greenBox.Text = color.G.ToString();
        }

        if (!_blueBox.IsKeyboardFocusWithin)
        {
            _blueBox.Text = color.B.ToString();
        }

        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        var current = CurrentColor;
        foreach (var (color, border) in _swatchBorders)
        {
            ApplySwatchState(border, color == current);
        }

        foreach (var slot in _recentSlots)
        {
            ApplySwatchState(slot, slot.Tag is InkColor value && value == current);
        }
    }

    private static void ApplySwatchState(Border swatch, bool selected)
    {
        swatch.BorderBrush = selected ? UiTheme.Accent : UiTheme.Border;
        swatch.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    private static Color ToColor(InkColor color) => Color.FromRgb(color.R, color.G, color.B);

    private static Brush BuildHueGradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
        {
            brush.GradientStops.Add(new GradientStop(ToColor(InkPalette.HsvToRgb(new HsvColor(i * 60, 1, 1))), i / 6.0));
        }

        return brush;
    }

    private static Brush BuildHorizontalFade(Color color) => new LinearGradientBrush(
        color,
        Color.FromArgb(0, color.R, color.G, color.B),
        new Point(0, 0),
        new Point(1, 0));

    private static Brush BuildVerticalFade(Color color) => new LinearGradientBrush(
        Color.FromArgb(0, color.R, color.G, color.B),
        color,
        new Point(0, 0),
        new Point(0, 1));
}
