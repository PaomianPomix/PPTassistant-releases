using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace PPTAssistant.Toolbar;

public static class UiSliderStyle
{
    /// <summary>把滑杆换成主题样式（幂等）。</summary>
    public static void Apply(Slider slider) => slider.Template = SliderTemplate;

    /// <summary>滑杆的轨道高度（DIP）。</summary>
    private const double TrackHeight = 4;

    /// <summary>拇指直径（DIP）。</summary>
    private const double ThumbSize = 16;

    /// <summary>轨道两端的留边（让拇指在两端时不出界）。</summary>
    private const double TrackInset = ThumbSize / 2;

    private static readonly string TemplateXaml = $$"""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type Slider}">
          <Grid Background="Transparent">
            <Border Height="{{TrackHeight}}" CornerRadius="2" Background="{{Hex(UiTheme.SliderTrack)}}"
                    VerticalAlignment="Center" Margin="{{TrackInset}},0,{{TrackInset}},0" />
            <Track x:Name="PART_Track" Margin="{{TrackInset}},0,{{TrackInset}},0"
                   Orientation="{TemplateBinding Orientation}"
                   IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton>
                <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False">
                  <RepeatButton.Template>
                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                      <Border Height="{{TrackHeight}}" CornerRadius="2" Background="{{Hex(UiTheme.SliderFill)}}"
                              VerticalAlignment="Center" />
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width="{{ThumbSize}}" Height="{{ThumbSize}}" Focusable="False" Cursor="Hand">
                  <Thumb.Template>
                    <ControlTemplate TargetType="{x:Type Thumb}">
                      <Ellipse Fill="{{Hex(UiTheme.SliderThumb)}}" Stroke="{{Hex(UiTheme.SliderThumbBorder)}}"
                               StrokeThickness="1" />
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False">
                  <RepeatButton.Template>
                    <ControlTemplate TargetType="{x:Type RepeatButton}">
                      <Border Background="Transparent" />
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
        """;

    private static readonly ControlTemplate SliderTemplate = Parse();

    private static ControlTemplate Parse()
    {
        EnsureCommandsExist();
        var template = (ControlTemplate)XamlReader.Parse(TemplateXaml);

        // 模板里的颜色来自主题层（这里把 Brush 转成 #AARRGGBB 写进 XAML，避免资源字典的间接层）。
        template.Seal();
        return template;
    }

    /// <summary>
    /// 编译期校验：XAML 里 <c>{x:Static Slider.DecreaseLarge}</c> / <c>IncreaseLarge</c> 引用的命令确实存在。
    /// </summary>
    /// <remarks>XAML 字符串是<b>运行时</b>解析的——名字写错不会编译失败，只会在真机上抛
    /// <see cref="XamlParseException"/>（表现是「滑块打不开」）。这两行把风险提前到编译期。</remarks>
    private static void EnsureCommandsExist()
    {
        _ = Slider.DecreaseLarge;
        _ = Slider.IncreaseLarge;
    }

    /// <summary>把主题画刷转成 <c>#AARRGGBB</c>（XAML 里只认字面量）。</summary>
    private static string Hex(Brush brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            throw new InvalidOperationException("滑杆换肤只支持纯色画刷。");
        }

        var color = solid.Color;
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
