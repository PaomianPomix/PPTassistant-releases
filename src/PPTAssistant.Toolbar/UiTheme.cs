using System.Windows.Media;

namespace PPTAssistant.Toolbar;

public static class UiTheme
{

    public static readonly Color PanelColor = Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF);

    public static readonly Brush Panel = Frozen(PanelColor);

    public static readonly Color BorderColor = Color.FromArgb(0x80, 0x66, 0x66, 0x66);

    public static readonly Brush Border = Frozen(BorderColor);

    public static readonly Brush HoverTint = Frozen(Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

    /// <summary>按压底（比悬停略深一档；按压的主要反馈是图标双态交叉淡变，见 <see cref="IconPressed"/>）。</summary>
    public static readonly Brush PressedTint = Frozen(Color.FromArgb(0x17, 0x00, 0x00, 0x00));

    public static readonly Color AccentColor = Color.FromRgb(0x6F, 0xB5, 0x43);

    public static readonly Brush Accent = Frozen(AccentColor);

    /// <summary>互斥高亮的淡底：强调绿 20% 不透明度（在 63% 白面板上仍是「磨砂」而不是「贴纸」）。</summary>
    public static readonly Brush ActiveTint = Frozen(Color.FromArgb(0x33, 0x6F, 0xB5, 0x43));

    public static readonly Color AccentCoralColor = Color.FromRgb(0xFF, 0x7F, 0x50);

    public static readonly Brush AccentCoral = Frozen(AccentCoralColor);

    public static readonly Color TextColor = Color.FromRgb(0x66, 0x66, 0x66);

    public static readonly Brush Text = Frozen(TextColor);

    public static readonly Brush TextMuted = Frozen(Color.FromRgb(0x97, 0x97, 0x97));

    public static readonly Brush IconNormal = Frozen(Color.FromRgb(0x66, 0x66, 0x66));

    public static readonly Brush IconSelected = Frozen(Color.FromRgb(0xD8, 0xD8, 0xD8));

    public static readonly FontFamily PageFont = new("Microsoft YaHei");

    public static readonly Brush MessageBackground = Frozen(Color.FromArgb(0x19, 0x00, 0x00, 0x00));

    public static readonly Color ScrimColor = Color.FromArgb(0x66, 0x00, 0x00, 0x00);

    public static readonly Brush Scrim = Frozen(ScrimColor);

    /// <summary>纯粹的白（缩略图项底、关闭按钮圆盘）。</summary>
    public static readonly Brush White = Frozen(Colors.White);

    public static readonly Brush LensBackground = Frozen(Colors.Black);

    public static readonly Brush ThumbnailItemBackground = Frozen(Colors.White);

    public static readonly Brush SidebarBackground = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    public static readonly Brush BadgeBorder = Frozen(Color.FromArgb(0xA0, 0x66, 0x66, 0x66));

    public static readonly Brush PlaceholderLine = Frozen(Color.FromRgb(0xDD, 0xDD, 0xDD));

    public static readonly Brush DialogBackground = Frozen(Colors.White);

    public static readonly Brush DialogDivider = Frozen(Color.FromRgb(0xCC, 0xCC, 0xCC));

    public const double DialogCornerRadius = 4;

    public const double DialogTitleFontSize = 15;

    public const double DialogBodyFontSize = 15;

    // ――――――――― 关闭按钮（圆形 ✕；证据：PNG 逐像素采样） ―――――――――

    /// <summary>关闭按钮的圆盘（常态）：白 70%（<c>closebuttonnormal.png</c> 主色 <c>(255,255,255,179)</c>）。</summary>
    public static readonly Brush CloseDisc = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    public static readonly Brush CloseGlyph = Frozen(Color.FromArgb(0xD9, 0x69, 0x69, 0x69));

    /// <summary>关闭按钮的圆盘（按下）：白 90%（<c>closebuttonpressed.png</c> 主色 <c>(255,255,255,230)</c>）。</summary>
    public static readonly Brush CloseDiscPressed = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    public static readonly Brush CloseDiscHover = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    /// <summary>关闭按钮的 ✕（按下）：<c>(120,120,120,242)</c>。</summary>
    public static readonly Brush CloseGlyphPressed = Frozen(Color.FromArgb(0xF2, 0x78, 0x78, 0x78));

    public static readonly Brush SliderTrack = Frozen(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));

    /// <summary>已选区间：强调绿（与色盘选中环同一语言）。</summary>
    public static readonly Brush SliderFill = Accent;

    /// <summary>滑块拇指底：白 + <see cref="SliderThumbBorder"/> 描边。</summary>
    public static readonly Brush SliderThumb = Frozen(Colors.White);

    public static readonly Brush SliderThumbBorder = Frozen(Color.FromArgb(0xCC, 0x66, 0x66, 0x66));

    public static readonly Brush ThumbDotBorder = Frozen(Color.FromRgb(0xAA, 0xAA, 0xAA));

    public static readonly Brush StatusText = Frozen(Color.FromRgb(0xB4, 0x53, 0x09));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
