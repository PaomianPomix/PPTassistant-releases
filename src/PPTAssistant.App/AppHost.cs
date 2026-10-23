using PPTAssistant.Session.Sidecar;
using PPTAssistant.Session.WriteBack;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PPTAssistant.Host;
using PPTAssistant.InkML;
using PPTAssistant.Overlay;
using PPTAssistant.Session;
using PPTAssistant.Toolbar;

namespace PPTAssistant.App;

internal sealed class AppHost : IDisposable
{
    private readonly Application _application;
    private readonly AppConfig _config;
    private readonly bool _isAutoStart;
    private readonly AutoStartService _autoStart = new();
    private readonly UpdateChecker _updateChecker = new();
    private Window? _trayHostWindow;
    private TrayIcon? _tray;
    private IWriteBackPrompt? _prompt;
    private OverlayWindow? _overlay;
    private ToolbarWindow? _toolbar;
    private MagnifierWindow? _magnifier;
    private ThumbnailPanelWindow? _thumbnailPanel;
    private PenPaletteWindow? _penPalette;
    private PenBrushWindow? _penBrush;
    private SettingsWindow? _settings;
    private WhiteboardController? _whiteboard;
    private PptHostWatcher? _watcher;
    private PresentationSession? _session;
    private bool _disposed;

    internal AppHost(Application application, AppConfig config, bool isAutoStart = false)
    {
        _application = application;
        _config = config;
        _isAutoStart = isAutoStart;
    }

    /// <summary>启动全部部件。</summary>
    internal void Start()
    {
        var ui = new DispatcherUiDispatcher(_application.Dispatcher, AppLog.Info);

        _overlay = new OverlayWindow
        {
            Trace = AppLog.Info,

            RequestedSurfaceMode = _config.ParsedSurfaceMode,

            AlwaysRoutePenToInk = _config.AlwaysRoutePenToInk,
        };
        _toolbar = new ToolbarWindow { Trace = AppLog.Info, UiScale = _config.SanitizedUiScale };
        _overlay.PenWidthDips = _config.SanitizedPenWidth;

        _magnifier = new MagnifierWindow { Trace = AppLog.Info, UiScale = _config.SanitizedUiScale };
        _thumbnailPanel = new ThumbnailPanelWindow { UiScale = _config.SanitizedUiScale };

        _penPalette = new PenPaletteWindow { UiScale = _config.SanitizedUiScale };
        _penBrush = new PenBrushWindow { UiScale = _config.SanitizedUiScale };

        // 监视器：1 秒轮询（进程存在性），真正的挂接动作被投递回 UI 线程执行。
        _watcher = new PptHostWatcher(TimeSpan.FromSeconds(1), AppLog.Info, ui);

        _prompt = new MessageBoxWriteBackPrompt();

        _session = new PresentationSession(
            _watcher,
            _overlay,
            _toolbar,
            ui,
            _config.ToSessionOptions(),
            AppLog.Info,
            _magnifier,
            _thumbnailPanel,
            prompt: _prompt,
            penPalette: _penPalette,
            penBrush: _penBrush);
        _whiteboard = new WhiteboardController(_session, () => _config.SanitizedUiScale,
            message => _tray?.ShowBalloon("白板", message));
        _whiteboard.VisibilityChanged += (_, _) => _tray?.RefreshTooltip();
        _toolbar.WhiteboardRequested += (_, _) => OpenWhiteboard();
        _session.StateChanged += (_, _) =>
        {
            _whiteboard.OnSessionStateChanged();
            _tray?.RefreshTooltip();
            // 设置逐项即时保存；开场收起已打开的设置页，避免放映中改尺寸与抢焦点。
            if (_session.State == SessionState.Presenting)
            {
                _settings?.Close();
            }
        };
        _session.PenAppearanceChanged += (_, appearance) => PersistPenAppearance(appearance);
        _session.Start();

        CreateTray();

        if (!_isAutoStart && _session.State != SessionState.Presenting)
        {
            Safe(() => RecoverPendingInk(), "崩溃恢复");
        }
        else
        {
            AppLog.Info("静默启动/放映中：残留板书保留，等待托盘“恢复上次板书”命令");
        }

        AppLog.Info($"启动完成：界面缩放={_config.SanitizedUiScale:P0} 笔宽={_config.SanitizedPenWidth} 笔色={_config.PenColorHex} 忽略触摸={_config.IgnoreTouchWhenPenPresent} 笔永远走引擎={_config.AlwaysRoutePenToInk}");
    }

    private void RecoverPendingInk(bool userRequested = false)
    {
        var candidates = SidecarStore.FindAllRecoverable(_config.SidecarRootPath);
        if (candidates.Count == 0)
        {
            if (userRequested) _tray?.ShowBalloon("板书恢复", "没有可恢复的残留板书");
            return;
        }

        var newest = candidates[0];
        AppLog.Info(
            $"[writeback] 发现残留板书：{newest.Session.Strokes.Count} 笔 / {newest.Session.PagesSeen.Count} 页，"
            + $"课件={newest.Session.Header.DocumentPath} 匹配={newest.MatchKind}（另有 {candidates.Count - 1} 份更早的）");

        // 主动选择“恢复”不应继承自动丢弃，始终让用户决定这份残留板书。
        var recoveryMode = userRequested ? WriteBackMode.Ask : _config.ParsedWriteBackMode;
        var choice = recoveryMode switch
        {
            WriteBackMode.AutoRetain => WriteBackChoice.Retain,
            WriteBackMode.AutoDiscard => WriteBackChoice.Discard,
            _ => _prompt?.AskRecover(
                     newest.Session.Strokes.Count,
                     newest.Session.Header.DocumentPath,
                     newest.MatchKind == SidecarMatchKind.Exact,
                     newest.Session.Header.StartedUtc)
                 ?? WriteBackChoice.Later,
        };

        if (choice == WriteBackChoice.Later)
        {
            AppLog.Info("[writeback] 残留板书暂不处理（日志保留，下次启动仍可恢复）");
            return;
        }

        if (choice == WriteBackChoice.Discard)
        {
            InkWriteBack.Discard(newest.Session, AppLog.Info);
            return;
        }

        // 恢复路径下宿主可能没在跑：传 null，让编排层用「文件是否被独占」来判断能不能写。
        var result = InkWriteBack.Commit(newest.Session, host: null, _config.BackupDirectoryPath, AppLog.Info);
        if (!result.Success)
        {
            AppLog.Info($"[writeback] 恢复失败（板书仍在日志里）：{result.Error}");
            _tray?.ShowBalloon("板书还没写回", result.Error ?? "写回失败");
        }
        else
        {
            _tray?.ShowBalloon("板书已恢复", $"已写入 {result.PagesWritten} 页 / {result.StrokesWritten} 笔");
        }

    }

    /// <summary>关闭全部部件（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppLog.Info("开始关闭");

        Safe(() => _whiteboard?.Dispose(), "白板");
        Safe(() => _session?.Dispose(), "会话");
        Safe(() => _watcher?.Dispose(), "宿主监视器");
        Safe(() => _settings?.Close(), "设置页");
        Safe(() => _thumbnailPanel?.Dispose(), "缩略图侧栏");
        Safe(() => _penPalette?.Dispose(), "色板悬浮窗");
        Safe(() => _penBrush?.Dispose(), "笔刷悬浮窗");
        Safe(() => _magnifier?.Dispose(), "放大镜");
        Safe(() => _toolbar?.Dispose(), "工具栏");
        Safe(() => _overlay?.Dispose(), "覆盖层");
        Safe(() => _tray?.Dispose(), "托盘图标");

        if (_trayHostWindow is not null)
        {
            Safe(() => _trayHostWindow.Close(), "托盘宿主窗口");
            _trayHostWindow = null;
        }

        AppLog.Info("已关闭");
    }

    private void CreateTray()
    {
        try
        {
            // 托盘的隐藏宿主窗口：0×0、屏幕外、永不 Show()。
            // Shell_NotifyIcon 需要一个真实顶层窗口来接收回调消息，且必须能收到
            // WM_TASKBARCREATED 广播（消息专用窗口收不到广播），所以不能用 HWND_MESSAGE。
            _trayHostWindow = new Window
            {
                Title = "PPTAssistant.TrayHost",
                Width = 0,
                Height = 0,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Visibility = Visibility.Hidden,
            };

            var hwnd = new WindowInteropHelper(_trayHostWindow).EnsureHandle();
            _tray = new TrayIcon(hwnd, BuildStatus, OnTrayCommand);
        }
        catch (Exception ex)
        {
            // 托盘失败不致命：产品主体（挂接 + 覆盖层）不依赖它。
            AppLog.Error("托盘初始化失败，程序继续运行（将没有托盘入口）", ex);
        }
    }

    private string BuildStatus()
    {
        if (_whiteboard?.IsVisible == true) return "PPTAssistant 白板使用中";
        var session = _session;
        if (session is null)
        {
            return "PPTAssistant 正在启动…";
        }

        return session.State switch
        {
            SessionState.Idle => "PPTAssistant 待机（未发现 PowerPoint / WPS）",
            SessionState.Attached => $"PPTAssistant 已挂接：{session.Host?.Info.ProcessName ?? "宿主"}（未放映）",
            SessionState.Presenting => "PPTAssistant 放映中（板书工具已就位）",
            _ => "PPTAssistant",
        };
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenWhiteboard:
                OpenWhiteboard();
                break;
            case TrayCommand.OpenSettings:
                OpenSettings();
                break;
            case TrayCommand.RecoverPendingInk:
                if (_session?.State != SessionState.Presenting)
                    Safe(() => RecoverPendingInk(userRequested: true), "板书恢复");
                break;
            case TrayCommand.OpenLogDirectory:
                OpenDirectory(AppLog.Directory, "日志");
                break;
            case TrayCommand.OpenConfigDirectory:
                OpenDirectory(ConfigStore.Directory, "配置");
                break;
            case TrayCommand.Exit:
                AppLog.Info("用户从托盘菜单退出");
                _application.Shutdown();
                break;
            default:
                break;
        }
    }

    private void OpenWhiteboard()
    {
        _settings?.Close();
        _whiteboard?.Open();
        _tray?.RefreshTooltip();
    }

    private void OpenSettings()
    {
        if (_session is { State: SessionState.Presenting })
        {
            AppLog.Info("放映中：设置页不打开");
            return;
        }

        if (_settings is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        try
        {
            var window = new SettingsWindow(
                _config.SanitizedPenWidth,
                InkColor.Parse(_config.PenColorHex),
                _config.ParsedRecentPenColors,
                _config.ParsedWriteBackMode,
                _config.AlwaysRoutePenToInk,
                onPenWidth: width =>
                {
                    _config.PenWidthDips = width;
                    SaveConfig();
                    _session?.SetPenWidth(width);
                },
                onPenColor: color =>
                {
                    _config.PenColorHex = color.ToHex();
                    SaveConfig();
                    _session?.SetPenColor(color);
                },
                onWriteBackMode: mode =>
                {
                    _config.WriteBackMode = WriteBackModes.Format(mode);
                    SaveConfig();
                    _session?.SetWriteBackMode(mode);
                },
                onAlwaysRoutePenToInk: enabled =>
                {
                    _config.AlwaysRoutePenToInk = enabled;
                    SaveConfig();
                    _session?.SetAlwaysRoutePenToInk(enabled);
                },
                uiScale: _config.SanitizedUiScale,
                onUiScale: scale =>
                {
                    _config.UiScale = scale;
                    SaveConfig();
                    if (_toolbar is not null) _toolbar.UiScale = scale;
                    if (_magnifier is not null) _magnifier.UiScale = scale;
                    if (_thumbnailPanel is not null) _thumbnailPanel.UiScale = scale;
                    if (_penPalette is not null) _penPalette.UiScale = scale;
                    if (_penBrush is not null) _penBrush.UiScale = scale;
                },
                autoStartState: _autoStart.Read(),
                onAutoStart: SetAutoStart,
                currentVersion: _updateChecker.CurrentVersion,
                onCheckUpdate: cancellation => _updateChecker.CheckAsync(AppReleaseInfo.Repository, cancellation),
                onOpenRelease: uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));

            window.Closed += (_, _) => _settings = null;
            _settings = window;
            window.Show();
            AppLog.Info("设置页已打开");
        }
        catch (Exception ex)
        {
            AppLog.Error("打开设置页失败", ex);
        }
    }

    private AutoStartResult SetAutoStart(bool enabled)
    {
        var result = _autoStart.SetEnabled(enabled);
        if (!result.Success)
        {
            AppLog.Warn($"修改自启失败：{result.Error}");
            return result;
        }

        _config.AutoStart = result.Enabled;
        return ConfigStore.Save(_config)
            ? result
            : result with { Error = "Windows 启动项已生效，但配置文件保存失败；请检查配置目录权限。" };
    }

    private void PersistPenAppearance(PenAppearance appearance)
    {
        _config.PenWidthDips = appearance.WidthDips;
        _config.PenColorHex = appearance.Color.ToHex();
        _config.RecentPenColors = appearance.Recent.Select(static c => c.ToHex()).ToList();
        SaveConfig();
    }

    private static void SaveConfig(AppConfig config)
    {
        if (!ConfigStore.Save(config))
        {
            AppLog.Warn("配置保存失败（内存里的值仍然生效，重启后会回到上一次成功保存的值）");
        }
    }

    private void SaveConfig() => SaveConfig(_config);

    private static void OpenDirectory(string? directory, string label)
    {
        if (string.IsNullOrEmpty(directory))
        {
            AppLog.Warn($"要打开的{label}目录为空");
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"打开{label}目录失败", ex);
        }
    }

    private static void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error($"释放{what}失败", ex);
        }
    }
}
