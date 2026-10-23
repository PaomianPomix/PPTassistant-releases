using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PPTAssistant.Coords;
using PPTAssistant.InkML;
using PPTAssistant.Session;

namespace PPTAssistant.App;

/// <summary>本机偏好：分类导航、可搜索的设置和即时更新。持久化由调用方负责。</summary>
internal sealed class SettingsWindow : Window
{
    private readonly List<SettingsPage> _pages = [];
    private readonly TextBox _search = new();
    private readonly StackPanel _pagesHost = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _breadcrumb = SettingsUi.Label("设置 / 放映与界面", 12);
    private readonly TextBlock _status = SettingsUi.Description("更改自动保存");
    private readonly TextBlock _searchSummary = SettingsUi.Description(string.Empty);
    private int _selectedPage;
    private bool _loading = true;

    public SettingsWindow(
        double penWidthDips, InkColor penColor, IReadOnlyList<InkColor> recentColors,
        WriteBackMode writeBackMode, bool alwaysRoutePenToInk,
        Action<double> onPenWidth, Action<InkColor> onPenColor,
        Action<WriteBackMode> onWriteBackMode, Action<bool> onAlwaysRoutePenToInk,
        double uiScale, Action<double> onUiScale, AutoStartResult autoStartState,
        Func<bool, AutoStartResult> onAutoStart, string currentVersion,
        Func<CancellationToken, Task<UpdateCheckResult>> onCheckUpdate, Action<Uri> onOpenRelease)
    {
        Title = "PPTAssistant 设置";
        Width = Math.Min(940, SystemParameters.WorkArea.Width);
        Height = Math.Min(740, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(780, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(560, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Background = Brushes.White;
        Foreground = SettingsUi.Text;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Resources = SettingsUi.CreateResources();

        AddPage("Interface", "放映与界面", "调整工具栏和浮窗大小，让常用操作触手可及。",
            "界面 显示 大小 缩放 比例 工具栏 浮窗 屏幕 75% 100% 200% 重置 恢复",
            "M2,2 L18,2 18,13 2,13 Z M6,17 L14,17 M10,13 L10,17", BuildInterface(uiScale, onUiScale));

        var pen = new StackPanel();
        pen.Children.Add(new SettingsPenPanel(penWidthDips, penColor, recentColors, onPenWidth, onPenColor, NotifyChanged));
        pen.Children.Add(SettingsUi.Divider());
        var penRouting = new CheckBox
        {
            Content = SettingsUi.Label("压感笔始终用于书写", 14, true),
            IsChecked = alwaysRoutePenToInk, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        SettingsUi.Identify(penRouting, "AlwaysRoutePenToInk", "压感笔始终用于书写");
        var routeDescription = SettingsUi.Description(string.Empty);
        void RefreshRouting() => routeDescription.Text = penRouting.IsChecked == true
            ? "已开启。无论工具栏处于哪种模式，压感笔都使用 PPTAssistant 书写；鼠标和手指仍跟随工具栏模式。"
            : "已关闭。压感笔跟随工具栏模式，可用于点击课件；PowerPoint 可能生成独立的批注。";
        RefreshRouting();
        penRouting.Checked += (_, _) => ChangeRouting(true);
        penRouting.Unchecked += (_, _) => ChangeRouting(false);
        void ChangeRouting(bool enabled)
        {
            RefreshRouting();
            if (_loading) return;
            onAlwaysRoutePenToInk(enabled);
            NotifyChanged("已更新 · 压感笔书写方式");
        }
        pen.Children.Add(penRouting);
        pen.Children.Add(routeDescription);
        AddPage("Pen", "画笔与书写", "设置习惯的笔触和颜色，下次书写时直接使用。",
            "画笔 笔宽 粗细 颜色 笔色 预设 最近 自定义 HEX RGB HSV 压感 书写 触控 笔穿透",
            "M3,13 L13,3 17,7 7,17 2,18 Z M11,5 L15,9 M3,13 L7,17", pen);

        AddPage("Saving", "板书保存", "决定结束放映后，如何处理本次板书。",
            "板书 保存 保留 丢弃 询问 自动 写回 备份 课件 Ask AutoRetain AutoDiscard",
            "M4,2 L13,2 17,6 17,18 4,18 Z M12,2 L12,7 17,7 M7,11 L14,11 M7,14 L12,14",
            BuildSaving(writeBackMode, onWriteBackMode));

        var maintenance = new MaintenanceSettingsPanel(autoStartState, onAutoStart, currentVersion,
            onCheckUpdate, onOpenRelease, NotifyChanged);
        AddPage("Maintenance", "启动与更新", "管理启动方式，按需检查新版本。",
            "启动 开机 自启 Windows 登录 托盘 更新 升级 版本 下载 GitHub 网络 重试",
            "M16,7 A7,7 0 1 0 17,13 M16,2 L16,7 11,7", maintenance);
        Closed += (_, _) => maintenance.Dispose();

        var root = new Grid { Background = Brushes.White };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildSidebar(currentVersion));
        var main = new Grid();
        Grid.SetColumn(main, 1);
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _breadcrumb.Foreground = SettingsUi.Muted;
        main.Children.Add(new Border
        {
            BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(32, 0, 32, 0), Child = _breadcrumb,
        });
        _searchSummary.Visibility = Visibility.Collapsed;
        _searchSummary.Margin = new Thickness(0, 0, 0, 24);
        _pagesHost.Children.Add(_searchSummary);
        foreach (var page in _pages) _pagesHost.Children.Add(page.Content);
        var emptyPanel = new StackPanel { Margin = new Thickness(0, 40, 0, 40), Visibility = Visibility.Collapsed };
        var empty = SettingsUi.Label("没有找到相关设置", 20, true);
        SettingsUi.Identify(empty, "SettingsSearchEmpty", "没有找到相关设置");
        emptyPanel.Children.Add(empty);
        emptyPanel.Children.Add(SettingsUi.Description("试试“颜色”“缩放”或“更新”，也可以直接选择左侧分类。"));
        var clearEmpty = new Button { Content = "清空搜索", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
        clearEmpty.Click += (_, _) => { _search.Clear(); _search.Focus(); };
        emptyPanel.Children.Add(clearEmpty);
        _pagesHost.Children.Add(emptyPanel);
        _pagesHost.Margin = new Thickness(32, 28, 32, 32);
        _scroll.Content = _pagesHost;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.PanningMode = PanningMode.VerticalOnly;
        SettingsUi.Identify(_scroll, "SettingsContentScroll", "设置内容");
        Grid.SetRow(_scroll, 1);
        main.Children.Add(_scroll);
        main.Children.Add(BuildFooter());
        root.Children.Add(main);
        Content = root;

        _search.TextChanged += (_, _) =>
        {
            var terms = _search.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var searching = terms.Length > 0;
            var matches = 0;
            for (var i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                var visible = searching ? terms.All(term => page.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)) : i == _selectedPage;
                page.Content.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) matches++;
            }
            _searchSummary.Text = $"找到 {matches} 个相关分类";
            _searchSummary.Visibility = searching && matches > 0 ? Visibility.Visible : Visibility.Collapsed;
            emptyPanel.Visibility = searching && matches == 0 ? Visibility.Visible : Visibility.Collapsed;
            _breadcrumb.Text = searching ? "设置 / 搜索结果" : $"设置 / {_pages[_selectedPage].Title}";
            _scroll.ScrollToTop();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _search.Focus(); _search.SelectAll(); e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (_search.Text.Length > 0) _search.Clear(); else Close();
            e.Handled = true;
        };
        _pages[0].Navigation.IsChecked = true;
        _loading = false;
    }

    private void NotifyChanged(string message) => _status.Text = message;

    private Border BuildSidebar(string version)
    {
        var sidebar = new Grid { Margin = new Thickness(14, 22, 14, 18) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new DockPanel { Margin = new Thickness(8, 0, 0, 24) };
        brand.Children.Add(new Border
        {
            Background = SettingsUi.Text, CornerRadius = new CornerRadius(5), Width = 27, Height = 29, Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock { Text = "P", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        brand.Children.Add(SettingsUi.Label("PPTAssistant", 14, true));
        sidebar.Children.Add(brand);
        var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        _search.Padding = new Thickness(30, 7, 26, 7);
        _search.FontSize = 12;
        _search.Background = Brushes.White;
        SettingsUi.Identify(_search, "SettingsSearch", "搜索设置");
        _search.ToolTip = "搜索设置（Ctrl+K）";
        searchGrid.Children.Add(_search);
        var searchIcon = CreateIcon("M13,13 L18,18 M15,9 A6,6 0 1 1 3,9 A6,6 0 1 1 15,9", 14);
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.Margin = new Thickness(10, 0, 0, 0);
        searchIcon.IsHitTestVisible = false;
        searchGrid.Children.Add(searchIcon);
        var placeholder = SettingsUi.Description("搜索设置");
        placeholder.Margin = new Thickness(31, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        searchGrid.Children.Add(placeholder);
        var clear = new Button { Content = "×", Width = 26, MinHeight = 28, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 3, 2), Visibility = Visibility.Collapsed, ToolTip = "清空搜索" };
        SettingsUi.Identify(clear, "ClearSettingsSearch", "清空搜索");
        clear.Click += (_, _) => { _search.Clear(); _search.Focus(); };
        searchGrid.Children.Add(clear);
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        Grid.SetRow(searchGrid, 1);
        sidebar.Children.Add(searchGrid);
        var nav = new StackPanel();
        var navLabel = SettingsUi.Description("偏好设置");
        navLabel.Margin = new Thickness(12, 0, 0, 10);
        nav.Children.Add(navLabel);
        foreach (var page in _pages) nav.Children.Add(page.Navigation);
        Grid.SetRow(nav, 2);
        sidebar.Children.Add(nav);
        var foot = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        foot.Children.Add(SettingsUi.Description("为每一次讲解做好准备"));
        foot.Children.Add(SettingsUi.Description($"版本 {version}"));
        Grid.SetRow(foot, 3);
        sidebar.Children.Add(foot);
        return new Border { Background = SettingsUi.Subtle, BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
    }

    private Border BuildFooter()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Margin = new Thickness(0, 0, 14, 0);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        SettingsUi.Identify(_status, "SettingsStatus", "设置状态");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        grid.Children.Add(_status);
        var done = new Button { Content = "完成", MinWidth = 76 };
        SettingsUi.Identify(done, "CloseSettings", "完成并关闭设置");
        done.Click += (_, _) => Close();
        Grid.SetColumn(done, 1);
        grid.Children.Add(done);
        var footer = new Border { BorderBrush = SettingsUi.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(32, 12, 32, 12), Child = grid };
        Grid.SetRow(footer, 2);
        return footer;
    }

    private void AddPage(string id, string title, string description, string keywords, string icon, UIElement body)
    {
        var index = _pages.Count;
        var content = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 20) };
        SettingsUi.Identify(content, $"Page{id}", title);
        content.Children.Add(SettingsUi.Label(title, 27, true));
        var subtitle = SettingsUi.Description(description);
        subtitle.Margin = new Thickness(0, 8, 0, 28);
        content.Children.Add(subtitle);
        content.Children.Add(body);
        var navContent = new DockPanel();
        var glyph = CreateIcon(icon, 17);
        glyph.Margin = new Thickness(0, 0, 11, 0);
        navContent.Children.Add(glyph);
        navContent.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var nav = new RadioButton
        {
            Content = navContent, GroupName = "SettingsNavigation", Style = (Style)Resources["SettingsNavigation"],
            Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        SettingsUi.Identify(nav, $"Nav{id}", title);
        nav.Checked += (_, _) =>
        {
            _selectedPage = index;
            if (_search.Text.Length > 0) _search.Clear();
            foreach (var page in _pages) page.Content.Visibility = page.Navigation == nav ? Visibility.Visible : Visibility.Collapsed;
            _breadcrumb.Text = $"设置 / {title}";
            _scroll.ScrollToTop();
        };
        nav.Click += (_, _) => { if (_search.Text.Length > 0) _search.Clear(); };
        _pages.Add(new SettingsPage(title, $"{title} {description} {keywords} {CollectSearchText(body)}", content, nav));
    }

    // 搜索包含界面上的完整文案，避免“自动保留”等组合词因关键词被拆开而无法命中。
    private static string CollectSearchText(DependencyObject element)
    {
        if (element is TextBlock text) return text.Text;
        return string.Join(" ", LogicalTreeHelper.GetChildren(element).Cast<object>()
            .Select(child => child is DependencyObject dependency ? CollectSearchText(dependency) : child as string ?? string.Empty));
    }

    private StackPanel BuildInterface(double uiScale, Action<double> onUiScale)
    {
        var panel = new StackPanel();
        var scale = UiScalePolicy.Normalize(uiScale);
        panel.Children.Add(BuildInterfacePreview(scale, out var previewToolbar, out var previewCaption));
        panel.Children.Add(SettingsUi.Label("放映界面缩放", 15, true));
        panel.Children.Add(SettingsUi.Description("同时调整工具栏、色板、笔刷面板和放大镜等浮窗的大小。"));
        var controls = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
        var slider = new Slider
        {
            Minimum = UiScalePolicy.Minimum, Maximum = UiScalePolicy.Maximum, Value = scale,
            TickFrequency = UiScalePolicy.Step, SmallChange = UiScalePolicy.Step, LargeChange = UiScalePolicy.Step,
            IsSnapToTickEnabled = true,
        };
        SettingsUi.Identify(slider, "UiScale", "放映界面缩放");
        var value = SettingsUi.Label($"{scale:0%}", 16, true);
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 1);
        controls.Children.Add(slider);
        controls.Children.Add(value);
        panel.Children.Add(controls);
        var endpoints = new Grid { Margin = new Thickness(8, 0, 65, 14) };
        endpoints.Children.Add(SettingsUi.Description("75% · 更紧凑"));
        var maximum = SettingsUi.Description("200% · 更醒目");
        maximum.HorizontalAlignment = HorizontalAlignment.Right;
        endpoints.Children.Add(maximum);
        panel.Children.Add(endpoints);
        var reset = new Button { Content = "恢复 100%", HorizontalAlignment = HorizontalAlignment.Left };
        SettingsUi.Identify(reset, "ResetUiScale", "恢复 100% 的界面缩放");
        reset.Click += (_, _) => slider.Value = UiScalePolicy.Default;
        panel.Children.Add(reset);
        slider.ValueChanged += (_, e) =>
        {
            var normalized = UiScalePolicy.Normalize(e.NewValue);
            value.Text = $"{normalized:0%}";
            previewCaption.Text = $"{normalized:0%} · 比例示意";
            previewToolbar.LayoutTransform = new ScaleTransform(normalized, normalized);
            if (_loading) return;
            onUiScale(normalized);
            NotifyChanged("已更新 · 界面缩放，下次放映生效");
        };
        panel.Children.Add(SettingsUi.Divider());
        panel.Children.Add(SettingsUi.Note("100% 会根据当前屏幕自动选择合适尺寸。每次调整 25%，下次放映生效；屏幕空间不足时，界面会自动缩小以保持完整显示。"));
        return panel;
    }

    private static Border BuildInterfacePreview(double scale, out FrameworkElement toolbar, out TextBlock caption)
    {
        var scene = new Grid { Height = 144, ClipToBounds = true };
        var slide = new Border
        {
            Width = 214, Height = 94, Background = Brushes.White, BorderBrush = SettingsUi.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var lines = new StackPanel { Margin = new Thickness(18, 16, 18, 0) };
        lines.Children.Add(new Border { Height = 5, Width = 80, Background = SettingsUi.Selected, HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Height = 3, Width = 156, Background = SettingsUi.Hover, Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Height = 3, Width = 122, Background = SettingsUi.Hover, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        slide.Child = lines;
        scene.Children.Add(slide);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var geometry in new[] { "M12,4 L6,10 12,16", "M4,16 L6,11 14,3 18,7 10,15 Z", "M4,13 L12,3 18,9 10,17 7,17 Z", "M4,10 L16,10 M10,4 L10,16", "M8,4 L14,10 8,16" })
        {
            var icon = CreateIcon(geometry, 12);
            icon.Margin = new Thickness(8, 7, 8, 7);
            tools.Children.Add(icon);
        }
        toolbar = new Border
        {
            Background = Brushes.White, BorderBrush = SettingsUi.Muted, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Child = tools, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 54, 0, 0), LayoutTransform = new ScaleTransform(scale, scale),
        };
        scene.Children.Add(toolbar);
        caption = SettingsUi.Description($"{scale:0%} · 比例示意");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.VerticalAlignment = VerticalAlignment.Bottom;
        caption.Margin = new Thickness(0, 0, 0, 12);
        scene.Children.Add(caption);
        return new Border { Background = SettingsUi.Subtle, CornerRadius = new CornerRadius(6), Child = scene, Margin = new Thickness(0, 0, 0, 24), IsHitTestVisible = false };
    }

    private StackPanel BuildSaving(WriteBackMode selected, Action<WriteBackMode> onChanged)
    {
        var panel = new StackPanel();
        panel.Children.Add(SettingsUi.Label("结束放映后", 15, true));
        panel.Children.Add(SettingsUi.Description("选择一种处理方式，之后的放映都会遵循这个设置。"));
        var choices = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var noteText = SettingsUi.Description(string.Empty);
        var note = new Border { Background = SettingsUi.Subtle, CornerRadius = new CornerRadius(5), Padding = new Thickness(14, 10, 14, 12), Child = noteText };
        void UpdateNote(WriteBackMode mode)
        {
            noteText.Foreground = mode == WriteBackMode.AutoDiscard ? SettingsUi.Error : SettingsUi.Muted;
            noteText.Text = mode switch
            {
                WriteBackMode.AutoRetain => "结束放映后，会先备份课件，再写入板书并重新打开课件。请确保课件可以保存。",
                WriteBackMode.AutoDiscard => "注意：结束放映后，本次板书会被丢弃，课件保持原样。需要留下板书时，请选择其他方式。",
                _ => "结束放映时，你可以再决定保留或丢弃。选择保留后，会先备份课件，再写入板书。",
            };
        }
        foreach (var option in new[]
        {
            (WriteBackMode.Ask, "Ask", "每次询问", "结束放映时再决定是否保留板书。", true),
            (WriteBackMode.AutoRetain, "Retain", "自动保留", "将板书保存到课件中，无需再次确认。", false),
            (WriteBackMode.AutoDiscard, "Discard", "自动丢弃", "结束放映后丢弃本次板书，不修改课件。", false),
        })
        {
            var content = new StackPanel();
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(SettingsUi.Label(option.Item3, 14, true));
            if (option.Item5)
            {
                var recommended = SettingsUi.Label("推荐", 11);
                recommended.Foreground = SettingsUi.Success;
                heading.Children.Add(new Border { Background = SettingsUi.Subtle, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(10, 0, 0, 0), Child = recommended });
            }
            content.Children.Add(heading);
            content.Children.Add(SettingsUi.Description(option.Item4));
            var choice = new RadioButton { Content = content, GroupName = "WriteBackMode", IsChecked = selected == option.Item1, Margin = new Thickness(0, 0, 0, 5) };
            SettingsUi.Identify(choice, $"WriteBack{option.Item2}", option.Item3);
            choice.Checked += (_, _) =>
            {
                UpdateNote(option.Item1);
                if (_loading) return;
                onChanged(option.Item1);
                NotifyChanged($"已更新 · 板书{option.Item3}");
            };
            choices.Children.Add(choice);
        }
        UpdateNote(selected);
        panel.Children.Add(choices);
        panel.Children.Add(SettingsUi.Divider());
        panel.Children.Add(note);
        return panel;
    }

    private static FrameworkElement CreateIcon(string geometry, double size) => new Viewbox
    {
        Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
        Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry), Stroke = SettingsUi.Muted, StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = 20, Height = 20,
        },
    };

    private sealed record SettingsPage(string Title, string SearchText, StackPanel Content, RadioButton Navigation);
}
