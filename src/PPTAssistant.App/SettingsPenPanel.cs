using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PPTAssistant.Coords;
using PPTAssistant.InkML;

namespace PPTAssistant.App;

/// <summary>Settings-only pen editor. Construction and visual synchronization never persist values.</summary>
internal sealed class SettingsPenPanel : StackPanel
{
    private readonly Action<double> _onPenWidth;
    private readonly Action<InkColor> _onPenColor;
    private readonly Action<string> _onSaved;
    private readonly TextBlock _widthValue = SettingsUi.Label("", 14, true);
    private readonly TextBlock _previewCaption = SettingsUi.Description("笔画预览");
    private readonly Border _preview = new();
    private readonly Border _previewSurface = new();
    private readonly System.Windows.Shapes.Path _stroke = new();
    private readonly Border _currentSwatch = new();
    private readonly TextBox _hex = new();
    private readonly StackPanel _hexError = new();
    private readonly StackPanel _rgbError = new();
    private readonly WrapPanel _recentRow = new();
    private readonly List<(Button Button, InkColor Color, TextBlock Mark)> _swatches = [];
    private readonly List<(Button Button, InkColor Color, TextBlock Mark)> _recentSwatches = [];
    private readonly TextBox[] _rgb = new TextBox[3];
    private readonly Canvas _square = new();
    private readonly Border _squareHue = new();
    private readonly Ellipse _squareThumb = new();
    private readonly Canvas _hueStrip = new();
    private readonly Border _hueThumb = new();
    private IReadOnlyList<InkColor> _recentColors;
    private InkColor _color;
    private HsvColor _hsv;
    private double _width;
    private bool _syncing;
    private bool _darkPreview;
    private Canvas? _activePickerDrag;
    private const double PickerWidth = 240;
    private const double PickerHeight = 132;
    private const double HueHeight = 16;

    public SettingsPenPanel(
        double penWidthDips,
        InkColor penColor,
        IReadOnlyList<InkColor> recentColors,
        Action<double> onPenWidth,
        Action<InkColor> onPenColor,
        Action<string> onSaved)
    {
        _onPenWidth = onPenWidth;
        _onPenColor = onPenColor;
        _onSaved = onSaved;
        _width = PenWidthRange.Clamp(penWidthDips);
        _color = penColor;
        _hsv = InkPalette.RgbToHsv(penColor);
        _recentColors = recentColors.Distinct().Take(InkPalette.MaxRecent).ToArray();

        Children.Add(SettingsUi.Label("默认笔宽", 15, true));
        var widthDescription = SettingsUi.Description("范围 2–50，数值越大，笔画越粗。下方可直接预览效果。");
        widthDescription.Margin = new Thickness(0, 5, 0, 5);
        Children.Add(widthDescription);
        var widthRow = new Grid();
        widthRow.ColumnDefinitions.Add(new ColumnDefinition());
        widthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        var widthSlider = new Slider
        {
            Minimum = PenWidthRange.MinDips,
            Maximum = PenWidthRange.MaxDips,
            Value = _width,
            TickFrequency = PenWidthRange.StepDips,
            SmallChange = PenWidthRange.StepDips,
            LargeChange = 2,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        SettingsUi.Identify(widthSlider, "PenWidth", "默认笔宽，2 至 50");
        widthSlider.ValueChanged += (_, e) =>
        {
            _width = PenWidthRange.Clamp(e.NewValue);
            UpdatePreview();
            _onPenWidth(_width);
            _onSaved("已更新 · 默认笔宽");
        };
        widthRow.Children.Add(widthSlider);
        _widthValue.HorizontalAlignment = HorizontalAlignment.Right;
        _widthValue.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_widthValue, 1);
        widthRow.Children.Add(_widthValue);
        Children.Add(widthRow);
        Children.Add(BuildPreview());
        var divider = SettingsUi.Divider();
        divider.Margin = new Thickness(0, 12, 0, 12);
        Children.Add(divider);

        Children.Add(SettingsUi.Label("默认笔色", 15, true));
        var colorDescription = SettingsUi.Description("从常用颜色中选择，或输入颜色值精确设置。");
        colorDescription.Margin = new Thickness(0, 5, 0, 10);
        Children.Add(colorDescription);
        Children.Add(BuildHexRow());
        BuildError(_hexError, "请输入 6 位十六进制颜色，例如 #D91B1B。", RestoreHex, "PenColorHexRestore");
        SettingsUi.Identify(_hexError, "PenColorHexError", "HEX 颜色格式错误");
        Children.Add(_hexError);

        Children.Add(SectionLabel("常用颜色"));
        var presets = new WrapPanel();
        string[] names = ["红色", "蓝色", "黑色", "绿色", "橙色", "深灰色", "青色", "紫色"];
        for (var i = 0; i < InkPalette.Presets.Count; i++)
        {
            presets.Children.Add(CreateSwatch(InkPalette.Presets[i], names[i], $"PenColorPreset{i}", _swatches));
        }
        Children.Add(presets);
        Children.Add(SectionLabel("最近使用"));
        Children.Add(_recentRow);
        Children.Add(BuildCustomColor());
        RefreshRecent();
        SynchronizeColor();
    }

    private FrameworkElement BuildPreview()
    {
        var content = new Grid { Margin = new Thickness(16, 3, 16, 3) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        var heading = new DockPanel();
        var dark = new CheckBox
        {
            Content = "深色背景",
            FontSize = 12,
            Foreground = SettingsUi.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        SettingsUi.Identify(dark, "PenPreviewDarkBackground", "使用深色背景预览笔画");
        dark.Checked += (_, _) => { _darkPreview = true; UpdatePreview(); };
        dark.Unchecked += (_, _) => { _darkPreview = false; UpdatePreview(); };
        DockPanel.SetDock(dark, Dock.Right);
        heading.Children.Add(dark);
        heading.Children.Add(_previewCaption);
        content.Children.Add(heading);

        var canvas = new Canvas { Height = 72, ClipToBounds = true };
        _stroke.StrokeStartLineCap = PenLineCap.Round;
        _stroke.StrokeEndLineCap = PenLineCap.Round;
        _stroke.StrokeLineJoin = PenLineJoin.Round;
        canvas.Children.Add(_stroke);
        canvas.SizeChanged += (_, _) =>
        {
            var available = Math.Max(60, canvas.ActualWidth);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(28, 43), false, false);
                context.BezierTo(new Point(available * 0.27, 5), new Point(available * 0.35, 67), new Point(available * 0.51, 35), true, false);
                context.BezierTo(new Point(available * 0.65, 8), new Point(available * 0.72, 52), new Point(available - 28, 29), true, false);
            }
            geometry.Freeze();
            _stroke.Data = geometry;
        };
        _previewSurface.Child = canvas;
        _previewSurface.CornerRadius = new CornerRadius(4);
        Grid.SetRow(_previewSurface, 1);
        content.Children.Add(_previewSurface);
        _preview.Child = content;
        _preview.Height = 120;
        _preview.Margin = new Thickness(0, 10, 0, 0);
        _preview.CornerRadius = new CornerRadius(6);
        _preview.BorderBrush = SettingsUi.Border;
        _preview.BorderThickness = new Thickness(1);
        return _preview;
    }

    private FrameworkElement BuildHexRow()
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _currentSwatch.Width = 36;
        _currentSwatch.Height = 36;
        _currentSwatch.CornerRadius = new CornerRadius(5);
        _currentSwatch.BorderBrush = SettingsUi.Border;
        _currentSwatch.BorderThickness = new Thickness(1);
        _currentSwatch.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(_currentSwatch);
        var label = SettingsUi.Label("HEX", 12, true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(label);
        _hex.Width = 116;
        _hex.MaxLength = 32;
        _hex.Margin = new Thickness(0, 0, 8, 0);
        _hex.VerticalContentAlignment = VerticalAlignment.Center;
        SettingsUi.Identify(_hex, "PenColorHex", "默认笔色 HEX，6 位十六进制颜色");
        AutomationProperties.SetHelpText(_hex, "支持 #RRGGBB 或 RRGGBB。按 Enter 或离开输入框应用；按 Escape 撤销输入。");
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
            else if (e.Key == Key.Escape && _hex.Text != _color.ToHex()) { RestoreHex(); e.Handled = true; }
        };
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        row.Children.Add(_hex);
        var apply = new Button { Content = "应用", MinWidth = 58 };
        SettingsUi.Identify(apply, "PenColorHexApply", "应用 HEX 颜色");
        apply.Click += (_, _) => CommitHex();
        row.Children.Add(apply);
        return row;
    }

    private FrameworkElement BuildCustomColor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var pickerRow = new WrapPanel();
        var visualPicker = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        _square.Width = PickerWidth;
        _square.Height = PickerHeight;
        _square.Background = Brushes.Transparent;
        _square.Focusable = true;
        _square.SetResourceReference(FocusVisualStyleProperty, "SettingsFocus");
        _square.Cursor = Cursors.Cross;
        _squareHue.Width = PickerWidth;
        _squareHue.Height = PickerHeight;
        _squareHue.IsHitTestVisible = false;
        _square.Children.Add(_squareHue);
        _square.Children.Add(new Border
        {
            Width = PickerWidth,
            Height = PickerHeight,
            Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
            IsHitTestVisible = false,
        });
        _square.Children.Add(new Border
        {
            Width = PickerWidth,
            Height = PickerHeight,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
            IsHitTestVisible = false,
        });
        _squareThumb.Width = _squareThumb.Height = 12;
        _squareThumb.StrokeThickness = 2;
        _squareThumb.Stroke = Brushes.White;
        _squareThumb.Fill = Brushes.Transparent;
        _squareThumb.IsHitTestVisible = false;
        _square.Children.Add(_squareThumb);
        SettingsUi.Identify(_square, "PenColorSaturationValue", "颜色饱和度和明度，左右键调整饱和度，上下键调整明度");
        BindPickerMouse(_square, point => ChangeHsv(_hsv with
        {
            Saturation = Math.Clamp(point.X / PickerWidth, 0, 1),
            Value = 1 - Math.Clamp(point.Y / PickerHeight, 0, 1),
        }));
        _square.KeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
            var hsv = e.Key switch
            {
                Key.Left => _hsv with { Saturation = Math.Clamp(_hsv.Saturation - step, 0, 1) },
                Key.Right => _hsv with { Saturation = Math.Clamp(_hsv.Saturation + step, 0, 1) },
                Key.Up => _hsv with { Value = Math.Clamp(_hsv.Value + step, 0, 1) },
                Key.Down => _hsv with { Value = Math.Clamp(_hsv.Value - step, 0, 1) },
                _ => _hsv,
            };
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                ChangeHsv(hsv);
                e.Handled = true;
            }
        };
        visualPicker.Children.Add(new Border { BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(1), Child = _square });

        _hueStrip.Width = PickerWidth;
        _hueStrip.Height = HueHeight;
        _hueStrip.Margin = new Thickness(0, 12, 0, 3);
        _hueStrip.Focusable = true;
        _hueStrip.SetResourceReference(FocusVisualStyleProperty, "SettingsFocus");
        _hueStrip.Cursor = Cursors.Hand;
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
            gradient.GradientStops.Add(new GradientStop(ToColor(InkPalette.HsvToRgb(new HsvColor(i * 60, 1, 1))), i / 6.0));
        _hueStrip.Background = gradient;
        _hueThumb.Width = 6;
        _hueThumb.Height = HueHeight + 4;
        _hueThumb.CornerRadius = new CornerRadius(2);
        _hueThumb.BorderBrush = SettingsUi.Text;
        _hueThumb.BorderThickness = new Thickness(1);
        _hueThumb.Background = Brushes.White;
        _hueThumb.IsHitTestVisible = false;
        _hueStrip.Children.Add(_hueThumb);
        Canvas.SetTop(_hueThumb, -2);
        SettingsUi.Identify(_hueStrip, "PenColorHue", "色相，左右方向键调整");
        BindPickerMouse(_hueStrip, point => ChangeHsv(_hsv with { Hue = Math.Clamp(point.X / PickerWidth, 0, 1) * 360 }));
        _hueStrip.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            ChangeHsv(_hsv with { Hue = (_hsv.Hue + (e.Key == Key.Right ? step : -step) + 360) % 360 });
            e.Handled = true;
        };
        visualPicker.Children.Add(_hueStrip);
        pickerRow.Children.Add(visualPicker);

        var rgbPanel = new StackPanel { MinWidth = 140 };
        var rgbDescription = SettingsUi.Description("RGB · 0–255");
        rgbDescription.Margin = new Thickness(0, 0, 0, 8);
        rgbPanel.Children.Add(rgbDescription);
        string[] channels = ["红 R", "绿 G", "蓝 B"];
        string[] ids = ["Red", "Green", "Blue"];
        for (var i = 0; i < _rgb.Length; i++)
        {
            var index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var label = SettingsUi.Label(channels[i], 12);
            label.Width = 44;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var input = new TextBox { Width = 80, MaxLength = 12, VerticalContentAlignment = VerticalAlignment.Center };
            SettingsUi.Identify(input, $"PenColor{ids[i]}", $"{channels[i]}，0 至 255");
            AutomationProperties.SetHelpText(input, "按 Enter 或离开输入框应用；按 Escape 撤销输入。");
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitRgb(index); e.Handled = true; }
                else if (e.Key == Key.Escape && input.Text != ChannelValue(index).ToString(CultureInfo.InvariantCulture))
                {
                    RestoreRgb();
                    e.Handled = true;
                }
            };
            input.LostKeyboardFocus += (_, _) => CommitRgb(index);
            _rgb[i] = input;
            row.Children.Add(input);
            rgbPanel.Children.Add(row);
        }
        pickerRow.Children.Add(rgbPanel);
        panel.Children.Add(pickerRow);
        BuildError(_rgbError, "RGB 数值应为 0–255 的整数，当前颜色未改变。", RestoreRgb, "PenColorRgbRestore");
        SettingsUi.Identify(_rgbError, "PenColorRgbError", "RGB 颜色格式错误");
        panel.Children.Add(_rgbError);
        var help = SettingsUi.Description("先选色相，再调整明暗与浓淡。也可使用方向键或输入 RGB 数值。");
        help.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(help);
        var advanced = new Expander
        {
            Header = "自定义颜色",
            Content = panel,
            Margin = new Thickness(0, 18, 0, 0),
            Foreground = SettingsUi.Text,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        SettingsUi.Identify(advanced, "PenColorCustom", "展开自定义颜色取色器");
        return advanced;
    }

    private void BindPickerMouse(Canvas canvas, Action<Point> update)
    {
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            canvas.Focus();
            if (!canvas.CaptureMouse()) return;
            _activePickerDrag = canvas;
            update(e.GetPosition(canvas));
            e.Handled = true;
        };
        canvas.MouseMove += (_, e) =>
        {
            if (_activePickerDrag == canvas && canvas.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
                update(e.GetPosition(canvas));
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (_activePickerDrag != canvas) return;
            update(e.GetPosition(canvas));
            FinishPickerDrag(canvas);
            canvas.ReleaseMouseCapture();
            e.Handled = true;
        };
        canvas.LostMouseCapture += (_, _) => FinishPickerDrag(canvas);
    }

    private void FinishPickerDrag(Canvas canvas)
    {
        if (_activePickerDrag != canvas) return;
        // Clear before persisting or releasing capture: LostMouseCapture must not commit again.
        _activePickerDrag = null;
        SelectColor(InkPalette.HsvToRgb(_hsv), preserveHsv: true);
    }

    private Button CreateSwatch(InkColor color, string name, string id, List<(Button Button, InkColor Color, TextBlock Mark)> collection)
    {
        var mark = new TextBlock
        {
            Text = "✓",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (color.R * 0.299 + color.G * 0.587 + color.B * 0.114) > 155 ? Brushes.Black : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 8, 4),
            ToolTip = $"{name} · {color.ToHex()}",
            Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(4), Background = ColorBrush(color), Child = mark },
            Tag = $"{name} {color.ToHex()}",
        };
        SettingsUi.Identify(button, id, $"{name} {color.ToHex()}");
        button.Click += (_, _) => SelectColor(color);
        collection.Add((button, color, mark));
        return button;
    }

    private void RefreshRecent()
    {
        _recentRow.Children.Clear();
        _recentSwatches.Clear();
        if (_recentColors.Count == 0)
        {
            var empty = SettingsUi.Description("还没有最近使用的颜色，选择后会显示在这里。");
            empty.Margin = new Thickness(0, 4, 0, 4);
            _recentRow.Children.Add(empty);
            return;
        }
        for (var i = 0; i < _recentColors.Count; i++)
            _recentRow.Children.Add(CreateSwatch(_recentColors[i], "最近使用", $"PenColorRecent{i}", _recentSwatches));
    }

    private void ChangeHsv(HsvColor hsv)
    {
        _hsv = hsv;
        if (_activePickerDrag is not null)
        {
            // Keep committed inputs and recent colors unchanged while only the picker and stroke preview move.
            UpdatePickerVisuals(InkPalette.HsvToRgb(hsv));
            UpdatePreview();
            return;
        }
        SelectColor(InkPalette.HsvToRgb(hsv), preserveHsv: true);
    }

    private void SelectColor(InkColor color, bool preserveHsv = false)
    {
        if (_syncing) return;
        var nextRecent = InkPalette.AddRecent(_recentColors, color);
        var changed = _color != color || !_recentColors.SequenceEqual(nextRecent);
        var recentFocus = _recentSwatches.Any(swatch => swatch.Button.IsKeyboardFocused);
        _color = color;
        if (!preserveHsv) _hsv = InkPalette.RgbToHsv(color);
        _recentColors = nextRecent;
        RefreshRecent();
        SynchronizeColor();
        if (recentFocus && _recentSwatches.Count > 0) _recentSwatches[0].Button.Focus();
        if (!changed) return;
        _onPenColor(color);
        _onSaved("已更新 · 默认笔色");
    }

    private void SynchronizeColor()
    {
        _syncing = true;
        try
        {
            RestoreHex();
            RestoreRgb();
            UpdatePickerVisuals(_color);
            foreach (var (button, color, mark) in _swatches.Concat(_recentSwatches))
            {
                var selected = color == _color;
                button.Background = selected ? SettingsUi.Selected : Brushes.Transparent;
                button.BorderBrush = selected ? SettingsUi.Text : SettingsUi.Border;
                mark.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
                AutomationProperties.SetName(button, $"{button.Tag}{(selected ? "，当前选中" : "")}");
            }
            UpdatePreview();
        }
        finally { _syncing = false; }
    }

    private void UpdatePreview()
    {
        _widthValue.Text = PenWidthRange.Format(_width);
        _stroke.Stroke = ColorBrush(_activePickerDrag is null ? _color : InkPalette.HsvToRgb(_hsv));
        _stroke.StrokeThickness = _width;
        // Only the drawing surface changes; header text and the toggle retain the same high-contrast light background.
        _preview.Background = SettingsUi.Subtle;
        _previewSurface.Background = _darkPreview ? new SolidColorBrush(Color.FromRgb(0x2F, 0x34, 0x39)) : Brushes.White;
    }

    private void UpdatePickerVisuals(InkColor previewColor)
    {
        _currentSwatch.Background = ColorBrush(previewColor);
        _currentSwatch.ToolTip = $"{(_activePickerDrag is null ? "当前颜色" : "预览颜色")} {previewColor.ToHex()}";
        _squareHue.Background = ColorBrush(InkPalette.HsvToRgb(new HsvColor(_hsv.Hue, 1, 1)));
        Canvas.SetLeft(_squareThumb, Math.Clamp(_hsv.Saturation * PickerWidth - 6, 0, PickerWidth - 12));
        Canvas.SetTop(_squareThumb, Math.Clamp((1 - _hsv.Value) * PickerHeight - 6, 0, PickerHeight - 12));
        Canvas.SetLeft(_hueThumb, Math.Clamp(_hsv.Hue / 360 * PickerWidth - 3, 0, PickerWidth - 6));
    }

    private void CommitHex()
    {
        if (_syncing || _activePickerDrag is not null) return;
        var text = _hex.Text.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit))
        {
            _hex.BorderBrush = SettingsUi.Error;
            _hexError.Visibility = Visibility.Visible;
            return;
        }
        var color = InkColor.Parse(text);
        if (color == _color)
        {
            RestoreHex();
            return;
        }
        SelectColor(color);
    }

    private void RestoreHex()
    {
        _hex.Text = _color.ToHex();
        _hex.BorderBrush = SettingsUi.Border;
        _hexError.Visibility = Visibility.Collapsed;
    }

    private byte ChannelValue(int index) => index switch { 0 => _color.R, 1 => _color.G, _ => _color.B };

    private void CommitRgb(int index)
    {
        if (_syncing || _activePickerDrag is not null) return;
        if (!byte.TryParse(_rgb[index].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            _rgb[index].BorderBrush = SettingsUi.Error;
            _rgbError.Visibility = Visibility.Visible;
            return;
        }
        var color = index switch
        {
            0 => _color with { R = value },
            1 => _color with { G = value },
            _ => _color with { B = value },
        };
        if (color == _color)
        {
            _rgb[index].Text = value.ToString(CultureInfo.InvariantCulture);
            _rgb[index].BorderBrush = SettingsUi.Border;
            if (_rgb.All(input => input.BorderBrush != SettingsUi.Error)) _rgbError.Visibility = Visibility.Collapsed;
            return;
        }
        SelectColor(color);
    }

    private void RestoreRgb()
    {
        for (var i = 0; i < _rgb.Length; i++)
        {
            if (_rgb[i] is not { } input) continue;
            input.Text = ChannelValue(i).ToString(CultureInfo.InvariantCulture);
            input.BorderBrush = SettingsUi.Border;
        }
        _rgbError.Visibility = Visibility.Collapsed;
    }

    private static void BuildError(StackPanel panel, string text, Action restore, string id)
    {
        panel.Visibility = Visibility.Collapsed;
        panel.Margin = new Thickness(0, 8, 0, 0);
        var message = SettingsUi.Description(text);
        message.Foreground = SettingsUi.Error;
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
        panel.Children.Add(message);
        var reset = new Button { Content = "恢复当前颜色", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        SettingsUi.Identify(reset, id, "撤销无效输入，恢复当前颜色");
        reset.Click += (_, _) => restore();
        panel.Children.Add(reset);
    }

    private static TextBlock SectionLabel(string text)
    {
        var label = SettingsUi.Label(text, 12, true);
        label.Foreground = SettingsUi.Muted;
        label.Margin = new Thickness(0, 16, 0, 7);
        return label;
    }

    private static Color ToColor(InkColor color) => Color.FromRgb(color.R, color.G, color.B);
    private static SolidColorBrush ColorBrush(InkColor color) => new(ToColor(color));
}
