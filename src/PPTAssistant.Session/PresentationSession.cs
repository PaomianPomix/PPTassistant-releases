using PPTAssistant.Session.WriteBack;
using PPTAssistant.InkML;
using PPTAssistant.Session.Sidecar;
using PPTAssistant.Coords;
using PPTAssistant.Host;
using PPTAssistant.Overlay;
using PPTAssistant.Toolbar;

namespace PPTAssistant.Session;

public sealed class PresentationSession : IDisposable
{
    private readonly PptHostWatcher _watcher;
    private readonly IInkSurface _overlay;
    private readonly IToolbarView _toolbar;
    private readonly IUiDispatcher _ui;
    private readonly SessionOptions _options;
    private readonly Action<string>? _trace;
    private readonly LeadingEdgeDebouncer _pageTurnDebouncer;
    private readonly List<StrokePoint> _strokeBuffer = [];
    private InputDeviceKind _strokeBufferDevice = InputDeviceKind.Unknown;
    private readonly IWriteBackPrompt? _prompt;

    
    private readonly PenStyleState _pen;

    /// <summary>当前这一笔的样式快照（**起笔时**取，落盘用它——中途改宽不影响已写笔画）。</summary>
    private PenStyleSnapshot _strokeStyle;

    
    private WriteBackMode _writeBackMode;

    /// <summary>兜底对账周期（见 <see cref="Start"/> 里的说明）。</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    private Timer? _reconcileTimer;
    private SidecarLog? _sidecar;
    private SlideShowGeometry? _geometry;
    private int _currentPageIndex = 1;

    /// <summary>本页已被橡皮擦掉的笔画数（状态行 / 诊断用；翻页或清空后归零）。</summary>
    private int _erasedOnPage;
    private readonly IMagnifierView? _magnifier;
    private readonly IThumbnailPanelView? _thumbnails;
    private readonly IPenPaletteView? _penPalette;
    private readonly IPenBrushView? _penBrush;
    private readonly ThumbnailService _thumbnailService;
    private PptHost? _subscribedHost;
    private ScreenInfo? _showScreen;
    private bool _started;
    private bool _disposed;

    

    public PresentationSession(
        PptHostWatcher watcher,
        IInkSurface overlay,
        IToolbarView toolbar,
        IUiDispatcher ui,
        SessionOptions? options = null,
        Action<string>? trace = null,
        IMagnifierView? magnifier = null,
        IThumbnailPanelView? thumbnails = null,
        string? thumbnailRootDirectory = null,
        IWriteBackPrompt? prompt = null,
        IPenPaletteView? penPalette = null,
        IPenBrushView? penBrush = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _prompt = prompt;
        _options = options ?? SessionOptions.Default;
        _trace = trace;
        _magnifier = magnifier;
        _thumbnails = thumbnails;
        _penPalette = penPalette;
        _penBrush = penBrush;
        _pen = new PenStyleState(_options.PenWidthDips, _options.PenColor, _options.RecentPenColors);
        _strokeStyle = _pen.Snapshot();
        _writeBackMode = _options.WriteBackMode;
        _thumbnailService = new ThumbnailService(thumbnailRootDirectory, trace);
        _pageTurnDebouncer = new LeadingEdgeDebouncer(_options.PageTurnDebounce);
        _ = _pageTurnDebouncer.TryAccept(); // 消费掉「首次调用」的时间基准，避免开场第一下被误判为连点。
    }

    /// <summary>当前状态。</summary>
    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>状态迁移事件（托盘提示 / 日志用）。</summary>
    public event EventHandler<SessionState>? StateChanged;

    /// <summary>当前已挂接的宿主（可能为 null）。</summary>
    public PptHost? Host => _watcher.Host;

    
    public double PenWidthDips => _pen.WidthDips;

    
    public InkColor PenColor => _pen.Color;

    /// <summary>最近使用的笔色（新色顶置）。</summary>
    public IReadOnlyList<InkColor> RecentPenColors => _pen.Recent;

    /// <summary>笔样式变化（供上层持久化到 config）——**每次变化抛一次**，不节流（变化是低频的用户动作）。</summary>
    public event EventHandler<PenAppearance>? PenAppearanceChanged;

    /// <summary>当前写回模式。</summary>
    public WriteBackMode WriteBackMode => _writeBackMode;

    
    public void SetWriteBackMode(WriteBackMode mode)
    {
        if (_writeBackMode == mode)
        {
            return;
        }

        _writeBackMode = mode;
        _trace?.Invoke($"[session] 写回模式 → {mode}");
    }

    
    public void SetAlwaysRoutePenToInk(bool alwaysRoute)
    {
        _overlay.AlwaysRoutePenToInk = alwaysRoute;
    }

    

    public void SetPenWidth(double widthDips)
    {
        if (_pen.SetWidth(widthDips))
        {
            ApplyPenToOverlay();
        }

        _penBrush?.SetValue(_pen.WidthDips);
        RaisePenAppearance();
    }

    
    public void SetPenColor(InkColor color)
    {
        var changed = _pen.SetColor(color);
        _penPalette?.SetCurrentColor(color);
        _penPalette?.SetRecentColors(_pen.Recent);

        if (changed)
        {
            ApplyPenToOverlay();
        }

        RaisePenAppearance();
    }

    /// <summary>开始编排：订阅宿主与 UI 事件，并启动宿主监视器。</summary>
    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;

        _watcher.HostAttached += OnHostAttached;
        _watcher.HostLost += OnHostLost;

        
        
        
        
        
        
        _reconcileTimer = new Timer(_ => RunOnUi(ReconcilePeriodic), null, ReconcileInterval, ReconcileInterval);

        _overlay.StrokeBegan += OnStrokeBegan;
        _overlay.StrokeUpdated += OnStrokeUpdated;
        _overlay.StrokeEnded += OnStrokeEnded;
        _overlay.StrokesErased += OnStrokesErased;

        // ★ 结构性防线：覆盖层一旦把自己置顶（切笔态、翻页、显示后重申），工具栏就可能被压到下面——
        //   而工具栏是老师唯一的操作入口（也是唯一能切回鼠标态的地方），被压住就等于「工具卡死」。
        //   修复前它之所以没暴露：覆盖层全透明时对鼠标是完全透明的（分层窗口按 alpha 命中），
        //   点击会「穿过」覆盖层落到工具栏上。加了命中面之后这个隐患就变成真故障了。
        _overlay.ModeChanged += OnOverlayModeChanged;

        _toolbar.NextSlideRequested += OnNextSlideRequested;
        _toolbar.PreviousSlideRequested += OnPreviousSlideRequested;
        _toolbar.PenModeRequested += OnPenModeRequested;
        _toolbar.EraseModeRequested += OnEraseModeRequested;
        _toolbar.ClearPageRequested += OnClearPageRequested;
        _toolbar.MagnifierToggleRequested += OnMagnifierToggleRequested;
        _toolbar.ThumbnailPanelToggleRequested += OnThumbnailPanelToggleRequested;
        _toolbar.PaletteToggleRequested += OnPaletteToggleRequested;
        _toolbar.PenBrushToggleRequested += OnPenBrushToggleRequested;
        _toolbar.ExitShowRequested += OnExitShowRequested;

        if (_thumbnails is not null)
        {
            _thumbnails.SlideActivated += OnThumbnailSlideActivated;
            _thumbnails.CloseRequested += OnThumbnailPanelCloseRequested;
        }

        if (_penPalette is not null)
        {
            _penPalette.ColorPicked += OnPaletteColorPicked;
            _penPalette.CloseRequested += OnPaletteCloseRequested;
        }

        if (_penBrush is not null)
        {
            _penBrush.ValueChanged += OnBrushValueChanged;
            _penBrush.ValueCommitted += OnBrushValueCommitted;
            _penBrush.CloseRequested += OnPenBrushCloseRequested;
        }

        _watcher.Start();

        
        
        var pruned = SidecarStore.PruneOlderThan(_options.SidecarRetention, _options.SidecarRoot);
        if (pruned > 0)
        {
            _trace?.Invoke($"[sidecar] 已清理 {pruned} 份过期日志（保留 {_options.SidecarRetention.TotalDays:0.#} 天）");
        }

        _trace?.Invoke("[session] 已启动（等待宿主：POWERPNT / wpp）");
    }

    /// <summary>停止编排（不释放宿主监视器本身；App 负责它的生命周期）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            _watcher.HostAttached -= OnHostAttached;
            _watcher.HostLost -= OnHostLost;
            _overlay.StrokeBegan -= OnStrokeBegan;
            _overlay.StrokeEnded -= OnStrokeEnded;
            _overlay.StrokesErased -= OnStrokesErased;
            _overlay.ModeChanged -= OnOverlayModeChanged;
            _toolbar.NextSlideRequested -= OnNextSlideRequested;
            _toolbar.PreviousSlideRequested -= OnPreviousSlideRequested;
            _toolbar.PenModeRequested -= OnPenModeRequested;
            _toolbar.EraseModeRequested -= OnEraseModeRequested;
            _toolbar.ClearPageRequested -= OnClearPageRequested;
            _toolbar.MagnifierToggleRequested -= OnMagnifierToggleRequested;
            _toolbar.ThumbnailPanelToggleRequested -= OnThumbnailPanelToggleRequested;
            _toolbar.PaletteToggleRequested -= OnPaletteToggleRequested;
            _toolbar.PenBrushToggleRequested -= OnPenBrushToggleRequested;
            _toolbar.ExitShowRequested -= OnExitShowRequested;

            if (_thumbnails is not null)
            {
                _thumbnails.SlideActivated -= OnThumbnailSlideActivated;
                _thumbnails.CloseRequested -= OnThumbnailPanelCloseRequested;
            }

            if (_penPalette is not null)
            {
                _penPalette.ColorPicked -= OnPaletteColorPicked;
                _penPalette.CloseRequested -= OnPaletteCloseRequested;
            }

            if (_penBrush is not null)
            {
                _penBrush.ValueChanged -= OnBrushValueChanged;
                _penBrush.ValueCommitted -= OnBrushValueCommitted;
                _penBrush.CloseRequested -= OnPenBrushCloseRequested;
            }

            _watcher.Stop();
        }

        DetachHostHandlers();
        LeavePresentationStage("会话结束");
        _thumbnailService.Dispose();
    }

    private void OnHostAttached(object? sender, PptHost host)
    {
        
        DetachHostHandlers();
        _subscribedHost = host;
        host.SlideShowBegin += OnSlideShowBegin;
        host.SlideChanged += OnSlideChanged;
        host.SlideShowEnd += OnSlideShowEnd;
        host.PresentationCloseFinal += OnPresentationCloseFinal;

        
        _toolbar.SetNativeInkSupported(host.Info.IsNativeInkSupported);

        SetState(SessionState.Attached);
        _trace?.Invoke($"[session] 宿主就绪：{host.Info}");

        
        
        
        
        ReconcileShowState(host, "挂接对账");
    }

    private void OnHostLost(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            DetachHostHandlers();
            LeavePresentationStage("宿主已退出");
            _toolbar.SetNativeInkSupported(true);
            SetState(SessionState.Idle);
        });
    }

    private void OnSlideShowBegin(object? sender, SlideShowBeginEventArgs e) => RunOnUi(() => HandleShowBegin(e));

    private void OnSlideChanged(object? sender, SlideChangedEventArgs e) => RunOnUi(() => HandleSlideChanged(e));

    private void OnSlideShowEnd(object? sender, SlideShowEndEventArgs e) => RunOnUi(() => HandleShowEnd());

    private void OnPresentationCloseFinal(object? sender, SlideShowEndEventArgs e)
        => _trace?.Invoke("[session] 宿主演示文稿已关闭（PresentationCloseFinal）");

    private void HandleShowBegin(SlideShowBeginEventArgs e)
    {
        var host = _watcher.Host;
        if (host is null)
        {
            return;
        }

        
        var state = host.ReadShowState();
        var showType = state.IsShowActive ? state.ShowType : 0;
        if (!ShowGate.IsAccepted(showType))
        {
            _trace?.Invoke($"[session] 门控未通过（ShowType={showType}），本次放映不介入");
            return;
        }

        // 事件里的页码偶尔读不到（见 ComLateBinder.ReadSlidePosition），退回状态快照里的值。
        var slideIndex = e.SlideIndex > 0 ? e.SlideIndex : state.SlideIndex;
        var slideCount = e.SlideCount > 0 ? e.SlideCount : state.SlideCount;

        EnterPresentationStage(slideIndex, slideCount, state.PresentationPath);
    }

    

    private void ReconcilePeriodic()
    {
        if (_disposed)
        {
            return;
        }

        var host = _watcher.Host;
        if (host is null)
        {
            return;
        }

        try
        {
            ReconcileShowState(host, "周期对账");
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[session] 周期对账失败（下一轮再试）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 宿主事件是**一次性通知**，没有重放机制；而本工具的挂接时机受进程启动顺序影响
    /// （教室机器上是开机自启，但老师也可能中途手动启动，或本工具刚崩溃重启）。
    /// 因此每个可以便宜拿到状态快照的时机都做一次对账：挂接成功时、翻页事件到来时、以及**周期兜底**。
    /// </remarks>
    private void ReconcileShowState(PptHost host, string reason)
    {
        var state = host.ReadShowState();

        if (state.IsSpeakerShow && State != SessionState.Presenting)
        {
            _trace?.Invoke(
                $"[session] {reason}：发现已在放映（页={state.SlideIndex}/{state.SlideCount}），补开场");
            EnterPresentationStage(state.SlideIndex, state.SlideCount, state.PresentationPath);
            return;
        }

        if (!state.IsShowActive && State == SessionState.Presenting)
        {
            _trace?.Invoke($"[session] {reason}：放映已结束，补收尾");
            HandleShowEnd();
        }
    }

    /// <summary>进入「授课循环」阶段（开场）：门控已通过，把浮层就位。</summary>
    private void EnterPresentationStage(int slideIndex, int slideCount, string? presentationPath)
    {
        var host = _watcher.Host;
        if (host is null)
        {
            return;
        }

        if (State == SessionState.Presenting)
        {
            // 已在授课循环（例如 Begin 与对账撞在一起）：只刷新页码，不重建浮层。
            _toolbar.UpdateSlidePosition(ClampForDisplay(slideIndex, slideCount), slideCount);
            return;
        }

        var screen = ResolveShowScreen(host);
        if (screen is null)
        {
            _trace?.Invoke("[session] 取不到屏幕信息，覆盖层不予显示");
            return;
        }

        var targetScreen = screen.Value;
        _showScreen = targetScreen;
        _pageTurnDebouncer.Reset();

        // 开场一律回到默认鼠标态：「哪有上课一进 ppt 就要板书的」。
        _overlay.Mode = InputMode.Mouse;
        
        ApplyPenToOverlay();
        // 输入分流策略也要下发：配置里改了却没人读，是最难发现的那类 bug（日志显示已生效，行为却没变）。
        _overlay.IgnoreTouchWhenPenPresent = _options.IgnoreTouchWhenPenPresent;
        _overlay.AllowMouseDrawing = _options.AllowMouseDrawing;
        _overlay.ClearInk();
        _overlay.ShowOn(targetScreen);
        _overlay.EnsureOnTop();

        _toolbar.SetPenMode(false);
        _toolbar.SetEraserMode(false);
        _erasedOnPage = 0;
        _toolbar.UpdateSlidePosition(ClampForDisplay(slideIndex, slideCount), slideCount);
        _toolbar.SetStatus(null);
        _toolbar.ShowOn(targetScreen);
        _toolbar.EnsureOnTop();
        SyncInputExclusion();

        
        
        StartSidecarLog(host, presentationPath, slideSize: TryReadSlideSize(host));
        _currentPageIndex = ClampForDisplay(slideIndex, slideCount);
        _sidecar?.AppendPage(_currentPageIndex, slideCount);

        SetState(SessionState.Presenting);
        _trace?.Invoke(
            $"[session] 开场：页={slideIndex}/{slideCount} 屏={targetScreen.DeviceName} 课件={presentationPath}");
    }

    private void HandleSlideChanged(SlideChangedEventArgs e)
    {
        if (State != SessionState.Presenting)
        {
            // 翻页事件是「已经错过开场」的最好补救时机：顺手对一次账。
            var host = _watcher.Host;
            if (host is not null)
            {
                ReconcileShowState(host, "翻页对账");
            }

            if (State != SessionState.Presenting)
            {
                return;
            }
        }

        _toolbar.UpdateSlidePosition(ClampForDisplay(e.SlideIndex, e.SlideCount), e.SlideCount);

        
        _currentPageIndex = ClampForDisplay(e.SlideIndex, e.SlideCount);
        _sidecar?.AppendPage(_currentPageIndex, e.SlideCount);
        _erasedOnPage = 0;

        
        
        
        _overlay.ClearInk();

        // 宿主的放映窗口也是置顶窗口，翻页时会重新置顶，可能把我们的浮层压下去。
        _overlay.EnsureOnTop();
        _toolbar.EnsureOnTop();
        SyncInputExclusion();

        
        if (_thumbnails is not null && _thumbnails.IsVisible)
        {
            var current = ClampForDisplay(e.SlideIndex, e.SlideCount);
            _thumbnails.SetCurrentSlide(current);
            PopulateThumbnails(current, e.SlideCount);
        }

        _trace?.Invoke($"[session] 翻页 → {e.SlideIndex}/{e.SlideCount}（页码来源：{e.IndexSource}）");
    }

    private void HandleShowEnd()
    {
        // 先取快照：LeavePresentationStage 会清空墨迹（并归零计数）。
        var strokes = _overlay.StrokeCount;
        var points = _overlay.PointCount;

        LeavePresentationStage("散场");

        
        var sidecarPath = _sidecar?.Path;
        var sidecarStrokes = _sidecar?.StrokeCount ?? 0;
        CloseSidecarLog("showEnd");

        SetState(_watcher.IsAttached ? SessionState.Attached : SessionState.Idle);

        _trace?.Invoke(
            $"[session] 散场：已收回覆盖层与工具栏（本次板书 {strokes} 笔 / {points} 采样点）"
            + (sidecarStrokes > 0 ? $"；{sidecarStrokes} 笔已落盘 → {sidecarPath}" : "；本次没有板书需要落盘"));

        
        if (sidecarPath is not null && sidecarStrokes == 0)
        {
            SidecarStore.Delete(sidecarPath);
            return;
        }

        
        ResolvePendingInk(sidecarPath);
    }

    

    private void ResolvePendingInk(string? sidecarPath)
    {
        if (sidecarPath is null)
        {
            return;
        }

        var session = SidecarStore.TryRead(sidecarPath);
        if (session is null)
        {
            _trace?.Invoke($"[writeback] 日志读不回来（{sidecarPath}），跳过写回");
            return;
        }

        if (!session.HasInk)
        {
            
            SidecarStore.Delete(sidecarPath);
            _toolbar.SetStatus("本次板书已全部擦除，无需写回");
            _trace?.Invoke($"[writeback] 本次板书已全部擦除（erase 记录生效），删除空日志：{sidecarPath}");
            return;
        }

        // ★ 用**擦除后**的最终笔画数问用户 / 打日志（不是追加过的笔数）。
        var strokeCount = session.Strokes.Count;
        var choice = _writeBackMode switch
        {
            WriteBackMode.AutoRetain => WriteBackChoice.Retain,
            WriteBackMode.AutoDiscard => WriteBackChoice.Discard,
            _ => _prompt?.AskCommit(strokeCount, session.PagesSeen.Count, session.Header.DocumentPath)
                 ?? WriteBackChoice.Later,
        };

        _trace?.Invoke($"[writeback] 用户选择：{choice}（{strokeCount} 笔 / {session.PagesSeen.Count} 页）");

        switch (choice)
        {
            case WriteBackChoice.Discard:
                InkWriteBack.Discard(session, _trace);
                _toolbar.SetStatus($"已丢弃本次板书（{strokeCount} 笔）");
                break;

            case WriteBackChoice.Retain:
                var host = _watcher.Host is { } attached ? new PptHostWriteBack(attached) : null;
                var result = InkWriteBack.Commit(session, host, _options.BackupDirectory, _trace);

                if (result.Success)
                {
                    _toolbar.SetStatus($"板书已保存到课件（{result.PagesWritten} 页 / {result.StrokesWritten} 笔）");
                    if (!_options.ReopenAfterWriteBack && result.Reopened && host is not null)
                    {
                        // 配置要求不重开：那就关掉它，别让老师的界面上突然多出个窗口。
                        _ = host.SaveAndClose(session.Header.DocumentPath, out _, out _);
                    }
                }
                else
                {
                    _toolbar.SetStatus("板书写回失败，已回滚；板书仍在旁路日志里");
                    _trace?.Invoke($"[writeback] 失败：{result.Error}");
                }

                break;

            default:
                _toolbar.SetStatus($"本次板书暂未写回（{strokeCount} 笔），下次启动可恢复");
                break;
        }
    }

    private void OnNextSlideRequested(object? sender, EventArgs e)
    {
        if (!_pageTurnDebouncer.TryAccept())
        {
            _trace?.Invoke("[session] 翻页请求被 30ms 防抖丢弃");
            return;
        }

        ExecuteShowCommand("下一页", static host => host.NextSlide());
    }

    private void OnPreviousSlideRequested(object? sender, EventArgs e)
    {
        if (!_pageTurnDebouncer.TryAccept())
        {
            _trace?.Invoke("[session] 翻页请求被 30ms 防抖丢弃");
            return;
        }

        ExecuteShowCommand("上一页", static host => host.PreviousSlide());
    }

    private void OnExitShowRequested(object? sender, EventArgs e)
        => ExecuteShowCommand("退出放映", static host => host.EndShow());

    

    private void SyncInputExclusion()
    {
        var rects = new List<DipRect>(6);
        if (_toolbar.IsVisible)
        {
            rects.AddRange(_toolbar.InputExclusionRects);
        }

        if (_penPalette is { IsVisible: true } palette)
        {
            rects.Add(palette.Bounds);
        }

        if (_penBrush is { IsVisible: true } brush)
        {
            rects.Add(brush.Bounds);
        }

        if (_thumbnails is { IsVisible: true } thumbnails)
        {
            rects.Add(thumbnails.Bounds);
        }

        _overlay.SetInputExclusion(rects.Count > 0 ? rects : null);
    }

    private void OnPenModeRequested(object? sender, bool penMode)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        // 先清状态行再切模式：状态行高度会影响中央条位置，ApplyInputMode 里的挖洞同步
        // 必须读到最后的高度（反过来则会用旧矩形挖洞）。
        _toolbar.SetStatus(null);
        ApplyInputMode(penMode ? InputMode.Pen : InputMode.Mouse);
        _trace?.Invoke($"[session] 输入模式 → {(penMode ? "笔态" : "鼠标态")}");
    }

    

    private void OnEraseModeRequested(object? sender, EventArgs e)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        _erasedOnPage = 0;

        // 先设状态行再切模式：状态行会改变中央条高度，ApplyInputMode 里的挖洞同步
        // 必须读到最后的高度（否则多出/漏掉一条，老师点工具栏的那一块会被覆盖层吞掉）。
        _toolbar.SetStatus("橡皮：拖动擦除笔迹（长按橡皮清空本页）");
        ApplyInputMode(InputMode.Erase);
        _trace?.Invoke("[session] 输入模式 → 橡皮态（整笔局部擦除，长按清页）");
    }

    

    private void ApplyInputMode(InputMode mode)
    {
        _overlay.Mode = mode;
        _toolbar.SetPenMode(mode == InputMode.Pen);
        _toolbar.SetEraserMode(mode == InputMode.Erase);
        _toolbar.EnsureOnTop();
        SyncInputExclusion();
    }

    /// <summary>覆盖层把自己置顶之后，把工具栏抬回最上层（见 <see cref="ApplyInputMode"/> 的说明）。</summary>
    private void OnOverlayModeChanged(object? sender, InputMode mode)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        _toolbar.EnsureOnTop();
    }

    

    private void OnClearPageRequested(object? sender, EventArgs e)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        _sidecar?.AppendClearPage(_currentPageIndex);
        _overlay.ClearInk();
        _erasedOnPage = 0;
        _toolbar.SetStatus("已清空本页板书");
        SyncInputExclusion();

        _trace?.Invoke(
            $"[session] 橡皮长按 → 清空第 {_currentPageIndex} 页板书"
            + (_sidecar is not null ? "（已写入旁路日志 erase-all）" : "（⚠ 无旁路日志）"));
    }

    /// <summary>
    /// 橡皮擦掉了一批笔画：同步旁路日志（否则「屏上擦了、写回又出现」）。
    /// </summary>
    private void OnStrokesErased(object? sender, InkStrokesErasedEventArgs e)
    {
        if (State != SessionState.Presenting || e.StrokeIds.Count == 0)
        {
            return;
        }

        _sidecar?.AppendErase(_currentPageIndex, e.StrokeIds);
        _erasedOnPage += e.StrokeIds.Count;
        _toolbar.SetStatus($"橡皮：已擦除 {_erasedOnPage} 笔（拖动继续擦；长按橡皮清空整页）");
        SyncInputExclusion(); // 状态行高度变了（同 OnEraseModeRequested 的 ★）

        _trace?.Invoke(
            $"[session] 橡皮擦除第 {_currentPageIndex} 页 {e.StrokeIds.Count} 笔"
            + $"（编号 {string.Join(",", e.StrokeIds)}；本页累计 {_erasedOnPage}；"
            + (_sidecar is not null ? "旁路日志已记 erase）" : "⚠ 无旁路日志，写回时这些笔会重新出现）"));
    }

    
    private void OnMagnifierToggleRequested(object? sender, EventArgs e)
    {
        if (State != SessionState.Presenting || _showScreen is not { } screen)
        {
            return;
        }

        if (_magnifier is null)
        {
            _toolbar.SetStatus("本环境不支持放大镜");
            return;
        }

        if (_magnifier.IsActive)
        {
            _magnifier.Hide();
            _toolbar.SetMagnifierActive(false);
            _toolbar.SetStatus(null);
            _trace?.Invoke("[session] 放大镜关闭");
            return;
        }

        _magnifier.Zoom = _options.MagnifierZoom;
        _magnifier.ShowOn(screen);

        // ★ 浮窗也是置顶窗口，可被老师拖到工具栏上方——把「关掉放大镜」的按钮盖住就麻烦了。
        //   所以显示之后仍要把工具栏重新置顶一次（管的是视觉与命中遮挡；浮窗能收鼠标，工具栏必须压得住它）。
        _toolbar.EnsureOnTop();
        _toolbar.SetMagnifierActive(true);
        _toolbar.SetStatus($"放大镜 {_magnifier.Zoom:0.0}×（拖动 · 滚轮调倍率）");
        _trace?.Invoke($"[session] 放大镜开启：{_magnifier.Zoom:0.0}× 屏={screen.DeviceName}");
    }

    
    private void OnThumbnailPanelToggleRequested(object? sender, ThumbnailSide side)
    {
        if (State != SessionState.Presenting || _showScreen is not { } screen)
        {
            return;
        }

        if (_thumbnails is null)
        {
            _toolbar.SetStatus("本环境不支持缩略图导航");
            return;
        }

        
        if (_thumbnails.IsVisible && _thumbnails.Side == side)
        {
            _thumbnails.Hide();
            _toolbar.SetThumbnailPanelActive(false);
            _toolbar.SetStatus(null);
            SyncInputExclusion();
            _trace?.Invoke("[session] 缩略图侧栏关闭");
            return;
        }

        var state = _watcher.Host?.ReadShowState() ?? SlideShowState.Empty;
        _thumbnails.ShowOn(screen, side);
        _toolbar.EnsureOnTop();
        _toolbar.SetThumbnailPanelActive(true);
        PopulateThumbnails(ClampForDisplay(state.SlideIndex, state.SlideCount), state.SlideCount);
        SyncInputExclusion();
        _trace?.Invoke(
            $"[session] 缩略图侧栏开启：{state.SlideCount} 页，停在{(side == ThumbnailSide.Left ? "左" : "右")}侧，窗口半径 {_options.ThumbnailRadius}");
    }

    
    private void OnThumbnailSlideActivated(object? sender, int oneBasedIndex)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        if (!_pageTurnDebouncer.TryAccept())
        {
            _trace?.Invoke("[session] 跳页请求被 30ms 防抖丢弃");
            return;
        }

        ExecuteShowCommand($"跳到第 {oneBasedIndex} 页", host => host.GotoSlide(oneBasedIndex));
        _thumbnails?.SetCurrentSlide(oneBasedIndex);
    }

    
    private void OnThumbnailPanelCloseRequested(object? sender, EventArgs e)
    {
        _thumbnails?.Hide();
        _toolbar.SetThumbnailPanelActive(false);
        SyncInputExclusion();
    }

    

    
    private void OnPaletteToggleRequested(object? sender, EventArgs e)
    {
        if (State != SessionState.Presenting || _showScreen is not { } screen)
        {
            return;
        }

        if (_penPalette is null)
        {
            _toolbar.SetStatus("本环境不支持色板");
            return;
        }

        if (_penPalette.IsVisible)
        {
            HidePalette();
            return;
        }

        // 打开时把当前色与最近色灌进去：老师看到的必须是「现在用的是哪个色」。
        _penPalette.SetCurrentColor(_pen.Color);
        _penPalette.SetRecentColors(_pen.Recent);
        _penPalette.ShowOn(screen, _toolbar.Bounds);
        _toolbar.EnsureOnTop();
        _toolbar.SetPaletteActive(true);
        SyncInputExclusion();
        _trace?.Invoke($"[session] 色板开启：当前 {_pen.Color.ToHex()}，最近 {_pen.Recent.Count} 色");
    }

    
    private void OnPaletteColorPicked(object? sender, InkColor color)
    {
        SetPenColor(color);
        _trace?.Invoke($"[session] 笔色 → {color.ToHex()}（下一笔生效）");
    }

    
    private void OnPaletteCloseRequested(object? sender, EventArgs e) => HidePalette();

    private void HidePalette()
    {
        _penPalette?.Hide();
        _toolbar.SetPaletteActive(false);
        SyncInputExclusion();
    }

    

    
    private void OnPenBrushToggleRequested(object? sender, EventArgs e)
    {
        if (State != SessionState.Presenting || _showScreen is not { } screen)
        {
            return;
        }

        if (_penBrush is null)
        {
            _toolbar.SetStatus("本环境不支持笔刷调节");
            return;
        }

        if (_penBrush.IsVisible)
        {
            HidePenBrush();
            return;
        }

        _penBrush.SetValue(_pen.WidthDips);
        _penBrush.ShowOn(screen, _toolbar.Bounds);
        _toolbar.EnsureOnTop();
        _toolbar.SetPenBrushActive(true);
        SyncInputExclusion();
        _trace?.Invoke($"[session] 笔刷开启：当前 {PenWidthRange.Format(_pen.WidthDips)} DIP");
    }

    
    private void OnBrushValueChanged(object? sender, double value)
    {
        if (_pen.SetWidth(value))
        {
            ApplyPenToOverlay();
        }
    }

    
    private void OnBrushValueCommitted(object? sender, double value)
    {
        _pen.SetWidth(value);
        RaisePenAppearance();
        _trace?.Invoke($"[session] 笔宽 → {PenWidthRange.Format(_pen.WidthDips)} DIP（下一笔生效）");
    }

    
    private void OnPenBrushCloseRequested(object? sender, EventArgs e) => HidePenBrush();

    private void HidePenBrush()
    {
        _penBrush?.Hide();
        _toolbar.SetPenBrushActive(false);
        SyncInputExclusion();
    }

    /// <summary>把当前笔样式下发到覆盖层（只在授课循环里有意义）。</summary>
    private void ApplyPenToOverlay()
    {
        _overlay.PenWidthDips = _pen.WidthDips;
        _overlay.PenColor = _pen.Color;
    }

    /// <summary>把笔样式变化抛给上层（App 负责写 config）——**这是「下场放映默认值即所见」的链路**。</summary>
    private void RaisePenAppearance() => PenAppearanceChanged?.Invoke(this, _pen.ToAppearance());

    /// <summary>
    /// 按「当前页 ± 半径」补齐缩略图（**只补缺的，绝不整本导出**）。
    /// </summary>
    private void PopulateThumbnails(int currentSlideIndex, int slideCount)
    {
        var panel = _thumbnails;
        var host = _watcher.Host;
        if (panel is null || host is null || slideCount <= 0)
        {
            return;
        }

        var plan = ThumbnailPlanner.Plan(currentSlideIndex, slideCount, _options.ThumbnailRadius);
        var missing = ThumbnailPlanner.MissingFrom(plan, _thumbnailService.ExportedSlides);
        foreach (var index in missing)
        {
            _thumbnailService.EnsureThumbnail(host, index, _options.ThumbnailPixelWidth, _options.ThumbnailPixelHeight);
        }

        // 面板只呈现「窗口内」的页：让列表长度与导出代价都保持恒定。
        var items = new List<SlideThumbnail>(plan.Count);
        foreach (var index in plan)
        {
            var path = Path.Combine(_thumbnailService.SessionDirectory, $"slide-{index:D3}.png");
            items.Add(new SlideThumbnail(index, File.Exists(path) ? path : null));
        }

        panel.SetThumbnails(items, currentSlideIndex);
    }

    private void ExecuteShowCommand(string name, Func<PptHost, bool> command)
    {
        if (State != SessionState.Presenting)
        {
            return;
        }

        RunOnUi(() =>
        {
            var host = _watcher.Host;
            if (host is null)
            {
                return;
            }

            if (!command(host))
            {
                _trace?.Invoke($"[session] {name}失败（宿主可能已退出放映）");
            }
        });
    }

    /// <summary>挑出放映窗口所在的那块屏（挑不到就退化为主屏 / 第一块屏）。</summary>
    private ScreenInfo? ResolveShowScreen(PptHost host)
    {
        var screens = ScreenEnumerator.Enumerate();
        if (screens.Count == 0)
        {
            return null;
        }

        
        
        
        var comBounds = host.ReadShowWindowBoundsDips();
        ScreenInfo? picked = comBounds is null ? null : ScreenSelector.PickBestForComRect(screens, comBounds.Value);
        picked ??= ScreenSelector.PickPrimary(screens) ?? screens[0];

        _trace?.Invoke(
            $"[session] 挑屏：{picked.Value}（宿主报告的放映窗口={(comBounds is null ? "不可用" : comBounds.Value.ToString())}）");
        return picked;
    }

    private void LeavePresentationStage(string reason)
    {
        if (State == SessionState.Presenting)
        {
            _trace?.Invoke($"[session] 收回浮层（{reason}）");
        }

        
        _magnifier?.Hide();
        _toolbar.SetMagnifierActive(false);
        _thumbnails?.Hide();
        _toolbar.SetThumbnailPanelActive(false);

        
        _penPalette?.Hide();
        _toolbar.SetPaletteActive(false);
        _penBrush?.Hide();
        _toolbar.SetPenBrushActive(false);

        _overlay.Mode = InputMode.Mouse;
        _overlay.ClearInk();
        _overlay.Hide();
        _toolbar.SetPenMode(false);
        _toolbar.SetEraserMode(false);
        _erasedOnPage = 0;
        _toolbar.Hide();
        _showScreen = null;

        // 缩略图缓存按课件生命周期有效：散场就让它失效（换课件时页号含义会变）。
        _thumbnailService.ResetSlideCache();
    }

    private void DetachHostHandlers()
    {
        var host = _subscribedHost;
        _subscribedHost = null;
        if (host is null)
        {
            return;
        }

        host.SlideShowBegin -= OnSlideShowBegin;
        host.SlideChanged -= OnSlideChanged;
        host.SlideShowEnd -= OnSlideShowEnd;
        host.PresentationCloseFinal -= OnPresentationCloseFinal;
    }

    private void RunOnUi(Action action)
    {
        if (_ui.IsCurrent)
        {
            action();
            return;
        }

        _ui.Post(action);
    }

    

    private static int ClampForDisplay(int slideIndex, int slideCount)
    {
        if (slideIndex <= 0)
        {
            return 0;
        }

        return slideCount > 0 ? Math.Clamp(slideIndex, 1, slideCount) : slideIndex;
    }

    private void SetState(SessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        _trace?.Invoke($"[session] 状态 → {state}");
        StateChanged?.Invoke(this, state);
    }

    private void OnStrokeBegan(object? sender, InkStrokeEventArgs e)
    {
        
        
        
        
        _strokeBuffer.Clear();
        _strokeBuffer.AddRange(e.Points);
        _strokeBufferDevice = e.Device;

        
        
        _strokeStyle = _pen.Snapshot();

        _trace?.Invoke($"[ink] 起笔 #{e.StrokeId} 设备={e.Device} 压力={(e.Points.Count > 0 && e.Points[0].HasPressure ? e.Points[0].Pressure.ToString("0.00") : "无")}");
    }

    /// <summary>累积后续采样点（覆盖层的流式上报）。</summary>
    private void OnStrokeUpdated(object? sender, InkStrokeEventArgs e)
    {
        if (_strokeBufferDevice == InputDeviceKind.Unknown && e.Device != InputDeviceKind.Unknown)
        {
            _strokeBufferDevice = e.Device;
        }

        _strokeBuffer.AddRange(e.Points);
    }

    

    private void OnStrokeEnded(object? sender, InkStrokeEventArgs e)
    {
        // 用自己累积的点（StrokeEnded 里没有点，见 OnStrokeBegan 的说明）。
        var points = _strokeBuffer.Count > 0 ? _strokeBuffer.ToArray() : [];
        var device = _strokeBufferDevice == InputDeviceKind.Unknown ? e.Device : _strokeBufferDevice;

        var slideStroke = _sidecar is null ? null : ConvertToSlideStroke(points);
        if (_sidecar is not null && slideStroke is not null)
        {
            
            _sidecar.AppendStroke(_currentPageIndex, slideStroke, device.ToString(), e.StrokeId);
        }

        _trace?.Invoke(
            $"[ink] 收笔 #{e.StrokeId}（本次 {points.Length} 点；累计笔画={_overlay.StrokeCount} 采样点={_overlay.PointCount}"
            + (_sidecar is not null ? $"；已落盘 {_sidecar.StrokeCount} 笔" : "；⚠ 无旁路日志，未落盘")
            + "）");

        _strokeBuffer.Clear();
        _strokeBufferDevice = InputDeviceKind.Unknown;
    }

    /// <summary>把覆盖层窗口坐标（DIP）的一笔换算成幻灯片坐标（pt）的笔画。</summary>
    private InkStroke? ConvertToSlideStroke(IReadOnlyList<StrokePoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var geometry = _geometry;
        if (geometry is null)
        {
            _trace?.Invoke("[sidecar] 还没有几何信息（视口/页面尺寸未就绪），这一笔未落盘");
            return null;
        }

        
        
        
        return ScreenStrokeConverter.Convert(geometry, points, _strokeStyle.WidthDips, _strokeStyle.Color);
    }

    /// <summary>打开旁路日志，并把几何信息准备好（落盘要用幻灯片坐标）。</summary>
    private void StartSidecarLog(PptHost host, string? presentationPath, SlideSize? slideSize)
    {
        CloseSidecarLog("reopen");

        if (!_options.SidecarEnabled)
        {
            _trace?.Invoke("[sidecar] 旁路日志已按配置关闭（SidecarEnabled=false）");
            return;
        }

        if (string.IsNullOrWhiteSpace(presentationPath))
        {
            // 没有课件路径就无法与课件配对（也不能写回），此时不落盘，但要说清楚原因。
            _trace?.Invoke("[sidecar] 取不到课件路径，本次不落盘");
            return;
        }

        if (slideSize is null)
        {
            _trace?.Invoke("[sidecar] 取不到页面尺寸，本次不落盘（没有坐标基准就没有写回的意义）");
            return;
        }

        var viewport = _overlay.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            _trace?.Invoke($"[sidecar] 覆盖层视口异常（{viewport}），本次不落盘");
            return;
        }

        try
        {
            _geometry = new SlideShowGeometry(viewport, slideSize.Value);
            _sidecar = SidecarLog.Start(
                presentationPath,
                slideSize.Value,
                _options.SidecarRoot,
                toolVersion: typeof(PresentationSession).Assembly.GetName().Version?.ToString() ?? "dev",
                trace: _trace);
        }
        catch (Exception ex)
        {
            _sidecar = null;
            _geometry = null;
            _trace?.Invoke($"[sidecar] 开日志失败：{ex.GetType().Name} {ex.Message}（本次不落盘，但板书仍可正常书写）");
        }
    }

    
    private void CloseSidecarLog(string reason)
    {
        if (_sidecar is null)
        {
            return;
        }

        _sidecar.AppendEnd(reason);
        var path = _sidecar.Path;
        var strokes = _sidecar.StrokeCount;
        _sidecar.Dispose();
        _sidecar = null;

        _trace?.Invoke(
            $"[sidecar] 旁路日志已收尾（{reason}）：{strokes} 笔 → {path}"
            + (strokes > 0 ? "；**保留在磁盘上**，等 #26 落盘编排决定写回或丢弃" : string.Empty));
    }

    private static SlideSize? TryReadSlideSize(PptHost host)
    {
        try
        {
            return host.ReadSlideSize();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
