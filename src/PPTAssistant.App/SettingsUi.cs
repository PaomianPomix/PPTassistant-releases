using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace PPTAssistant.App;

/// <summary>设置窗口的局部设计语言；不改变放映工具栏的主题与尺寸。</summary>
internal static class SettingsUi
{
    public static Brush Text { get; } = Brush("#37352F");
    public static Brush Muted { get; } = Brush("#6B6963");
    public static Brush Subtle { get; } = Brush("#F7F6F3");
    public static Brush Border { get; } = Brush("#E9E7E2");
    public static Brush Hover { get; } = Brush("#EFEDEA");
    public static Brush Selected { get; } = Brush("#E3E1DB");
    public static Brush Accent { get; } = Brush("#287DA0");
    public static Brush Success { get; } = Brush("#0F7B6C");
    public static Brush Error { get; } = Brush("#B4473D");

    public static TextBlock Label(string text, double size = 14, bool semibold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Text,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Description(string text)
    {
        var label = Label(text, 12);
        label.Foreground = Muted;
        label.LineHeight = 20;
        label.Margin = new Thickness(0, 5, 0, 0);
        return label;
    }

    public static Border Divider() => new()
    {
        Height = 1, Background = Border, Margin = new Thickness(0, 24, 0, 24),
    };

    public static Border Note(string text) => new()
    {
        Background = Subtle,
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(14, 10, 14, 12),
        Child = Description(text),
    };

    public static void Identify(DependencyObject element, string id, string name)
    {
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, name);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static ResourceDictionary CreateResources() => (ResourceDictionary)XamlReader.Parse(ResourcesXaml);

    // 所有控件保留 WPF 的键盘、自动化与取值语义，只替换视觉模板。
    private const string ResourcesXaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="SettingsFocus" TargetType="Control">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate>
                  <Border Margin="-3" CornerRadius="5" BorderBrush="#287DA0" BorderThickness="2"/>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Button">
            <Setter Property="Foreground" Value="#37352F"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="#DEDBD5"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="12,7"/>
            <Setter Property="MinHeight" Value="36"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource SettingsFocus}"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="5" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#EFEDEA"/></Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="#E3E1DB"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="Foreground" Value="#37352F"/>
            <Setter Property="Background" Value="White"/>
            <Setter Property="BorderBrush" Value="#DEDBD5"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,7"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="MinHeight" Value="36"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="SelectionBrush" Value="#B5D9E7"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#287DA0"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="Foreground" Value="#37352F"/>
            <Setter Property="MinHeight" Value="40"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource SettingsFocus}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                  <Grid Background="Transparent">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="44"/></Grid.ColumnDefinitions>
                    <ContentPresenter VerticalAlignment="Center" Margin="0,0,12,0"/>
                    <Border x:Name="Track" Grid.Column="1" Width="36" Height="20" CornerRadius="10" Background="#D5D2CB" VerticalAlignment="Center">
                      <Ellipse x:Name="Thumb" Fill="White" Width="14" Height="14" HorizontalAlignment="Left" Margin="3,0"/>
                    </Border>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Track" Property="Background" Value="#37352F"/>
                      <Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right"/>
                    </Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Track" Property="Opacity" Value="0.8"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="RadioButton">
            <Setter Property="Foreground" Value="#37352F"/>
            <Setter Property="Padding" Value="14"/>
            <Setter Property="MinHeight" Value="44"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource SettingsFocus}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Surface" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}">
                    <Grid>
                      <Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Grid Width="16" Height="16" VerticalAlignment="Top" Margin="0,3,0,0">
                        <Ellipse Stroke="#9A968D" StrokeThickness="1"/>
                        <Ellipse x:Name="SelectedDot" Width="8" Height="8" Fill="#37352F" Visibility="Collapsed"/>
                      </Grid>
                      <ContentPresenter Grid.Column="1"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#EFEDEA"/></Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Surface" Property="Background" Value="#F7F6F3"/>
                      <Setter TargetName="SelectedDot" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="#E3E1DB"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="SettingsNavigation" TargetType="RadioButton" BasedOn="{StaticResource {x:Type RadioButton}}">
            <Setter Property="Padding" Value="12,10"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Surface" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="5">
                    <ContentPresenter VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#EFEDEA"/></Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Surface" Property="Background" Value="#E3E1DB"/>
                      <Setter Property="FontWeight" Value="SemiBold"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="#DDDAD2"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Slider">
            <Setter Property="MinHeight" Value="36"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource SettingsFocus}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Slider">
                  <Grid Background="Transparent">
                    <Border Height="3" CornerRadius="1.5" Background="#E3E1DB" VerticalAlignment="Center" Margin="8,0"/>
                    <Track x:Name="PART_Track" Margin="8,0" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                      <Track.DecreaseRepeatButton>
                        <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False">
                          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Grid Background="Transparent"><Border Height="3" Background="#6B6963" VerticalAlignment="Center"/></Grid></ControlTemplate></RepeatButton.Template>
                        </RepeatButton>
                      </Track.DecreaseRepeatButton>
                      <Track.Thumb>
                        <Thumb Width="16" Height="16" Cursor="Hand" Focusable="False">
                          <Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse x:Name="Knob" Fill="White" Stroke="#6B6963" StrokeThickness="1.5"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Knob" Property="Fill" Value="#EFEDEA"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Knob" Property="Fill" Value="#E3E1DB"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template>
                        </Thumb>
                      </Track.Thumb>
                      <Track.IncreaseRepeatButton>
                        <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False">
                          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                        </RepeatButton>
                      </Track.IncreaseRepeatButton>
                    </Track>
                  </Grid>
                  <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Expander">
            <Setter Property="Foreground" Value="#37352F"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Expander">
                  <StackPanel>
                    <ToggleButton x:Name="Header" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}"
                                  Content="{TemplateBinding Header}" FocusVisualStyle="{StaticResource SettingsFocus}" Cursor="Hand">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="ToggleButton">
                          <Border x:Name="Surface" Background="Transparent" Padding="0,10" CornerRadius="4">
                            <DockPanel><TextBlock x:Name="Arrow" Text="›" Width="20" FontSize="18" VerticalAlignment="Center"/><ContentPresenter VerticalAlignment="Center"/></DockPanel>
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Text" Value="⌄"/></Trigger>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#EFEDEA"/></Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter x:Name="Body" Visibility="Collapsed" Margin="0,8,0,0"/>
                  </StackPanel>
                  <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="10"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Grid Background="Transparent">
                    <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="Vertical">
                      <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                      <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="Grip" Background="#CCC9C1" CornerRadius="3" Margin="2"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#A6A299"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                      <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                    </Track>
                  </Grid>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
