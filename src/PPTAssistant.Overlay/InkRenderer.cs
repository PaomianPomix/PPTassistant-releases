using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PPTAssistant.Coords;

namespace PPTAssistant.Overlay;

internal sealed class InkRenderer : IDisposable
{
    /// <summary>平滑容差（DIP）：亚像素。</summary>
    internal const double SmoothingToleranceDips = StrokeSmoothing.DefaultToleranceDips;

    /// <summary>
    /// 已平滑前缀的保留上限（采样点数）。超过就整条重建一次——防止「前缀 + 尾巴」在长笔画上无界增长，
    /// 也避免每点都去改一个越来越大的几何。
    /// </summary>
    private const int MaxSmoothedPrefix = 2048;

    /// <summary>尾巴重算的最大采样点数（超过就把前缀向前推进）。</summary>
    private const int MaxTailPoints = 24;

    private readonly Canvas _canvas;
    private readonly Image _cacheImage;

    /// <summary>已定格笔画的像素缓存（预分配，一次分配、反复增量更新）。</summary>
    private WriteableBitmap? _cacheBitmap;

    /// <summary>缓存位图里「已经画过内容」的瓦片集合——用于清空时只清脏的瓦片。</summary>
    private readonly HashSet<int> _dirtyTiles = [];

    

    private readonly List<CommittedStroke> _committed = [];

    /// <summary>当前笔的图元（起笔建、收笔丢）。</summary>
    private Path? _live;

    /// <summary>当前笔的编号（由覆盖层在起笔时给出；探针直接驱动时可省略）。</summary>
    private int _pendingStrokeId;

    /// <summary>一笔已定格、可参与擦除命中与瓦片重画的几何记录。</summary>
    private sealed class CommittedStroke
    {
        public required int Id { get; init; }

        /// <summary>该笔的脏矩形（DIP，已按笔宽外扩、对齐物理像素）。</summary>
        public required DipRect Dirty { get; init; }

        /// <summary>密集中心线（DIP）——命中判定与重画都用它。</summary>
        public required DipPoint[] Centerline { get; init; }

        /// <summary>逐点笔宽（DIP，与 <see cref="Centerline"/> 等长）。</summary>
        public required double[] Widths { get; init; }

        /// <summary>可重画的图元（几何 + 填充/描边设置，已冻结）。</summary>
        public required Path Visual { get; init; }

        /// <summary>是否仍以独立图元形式挂在画布上（只在应急开关 <see cref="UseTileCache"/>=false 时）。</summary>
        public bool OnCanvas { get; set; }
    }

    /// <summary>当前笔的原始采样点（未平滑）。</summary>
    private readonly List<StrokePoint> _rawPoints = new(256);

    /// <summary>已平滑前缀的中心线（累计，避免每点重算整条）。</summary>
    private readonly List<DipPoint> _smoothedPrefix = new(256);

    /// <summary>已平滑前缀对应的逐点宽度（变宽笔画用）。</summary>
    private readonly List<double> _widthPrefix = new(256);

    /// <summary>已消费的原始点数（对应 <see cref="_smoothedPrefix"/> 的末尾）。</summary>
    private int _consumed;

    private int _strokeCount;
    private bool _disposed;

    /// <summary>是否启用增量缓存（关掉时退化为「逐笔临时图元」——真机上出问题时的应急开关）。</summary>
    internal bool UseTileCache { get; set; } = true;

    

    internal bool UseInputFilter { get; set; } = true;

    /// <summary>已发生多少次位图瓦片写入（诊断 / 性能计量用）。</summary>
    internal int TileWriteCount { get; private set; }

    
    internal int FullRebakeCount { get; private set; }

    internal InkRenderer(Canvas canvas)
    {
        _canvas = canvas;
        _cacheImage = new Image
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,   // 1:1 物理像素映射，绝不做插值缩放（否则笔迹会糊）。
            SnapsToDevicePixels = false,
        };
        _canvas.Children.Add(_cacheImage);
    }

    
    internal double PenWidthDips { get; set; } = 6.0;

    
    internal Color PenColor { get; set; } = Color.FromRgb(0xD9, 0x1B, 0x1B);

    /// <summary>当前视口（DIP）——决定缓存位图的物理像素尺寸。</summary>
    internal DipRect Viewport { get; private set; }

    /// <summary>DPI 缩放（物理像素 / DIP）。</summary>
    internal double Scale { get; private set; } = 1.0;

    /// <summary>画布上的图元个数（诊断用：缓存位图 + 当前笔 + 命中面）。</summary>
    internal int StrokeVisualCount => _canvas.Children.Count;

    /// <summary>已完成笔画数（诊断 / 旁路日志用）。</summary>
    internal int StrokeCount => _strokeCount;

    
    internal int CommittedStrokeCount => _committed.Count;

    
    internal IReadOnlyList<int> CommittedStrokeIds => _committed.Select(static stroke => stroke.Id).ToArray();

    /// <summary>缓存位图的瓦片规划（诊断 / 验证用）。</summary>
    internal StrokeCachePlan CachePlan { get; private set; } = StrokeCachePlan.ForViewport(new DipRect(0, 0, 0, 0));

    

    internal WriteableBitmap? CacheBitmapForDiagnostics => _cacheBitmap;

    

    internal bool IsVariableWidthForDiagnostics => IsVariableWidth();

    /// <summary>
    /// 视口变化（窗口尺寸 / 位置）：重建缓存位图并重绘画布上已有的笔迹。
    /// </summary>
    /// <remarks>覆盖层是全屏固定尺寸，正常流程里只会发生一次（ShowOn）。提供重建而不是断言，
    /// 是因为多屏切换 / 分辨率变化在真机上确实可能出现。</remarks>
    internal void SetViewport(DipRect viewportDips, double scale)
    {
        var newScale = scale > 0 ? scale : 1.0;
        if (_cacheBitmap is not null && Viewport == viewportDips && Math.Abs(Scale - newScale) < double.Epsilon)
        {
            return;
        }

        Viewport = viewportDips;
        Scale = newScale;
        CachePlan = StrokeCachePlan.ForViewport(viewportDips, newScale);

        var pixelWidth = Math.Max(1, CachePlan.PixelWidth);
        var pixelHeight = Math.Max(1, CachePlan.PixelHeight);
        _cacheBitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96 * newScale, 96 * newScale, PixelFormats.Pbgra32, null);
        _cacheImage.Source = _cacheBitmap;
        _cacheImage.Width = pixelWidth / newScale;
        _cacheImage.Height = pixelHeight / newScale;
        _cacheImage.InvalidateVisual();
        _dirtyTiles.Clear();
        _committed.Clear();
        _live = null;
        _pendingStrokeId = 0;

        // 视口变了：旧瓦片与旧图元都不再对得上新位图，全部丢弃
        // （正常情况下此时还没有任何笔画；多屏切换时墨迹随翻页一起清空）。
        _canvas.Children.Clear();
        _canvas.Children.Add(_cacheImage);
        _strokeCount = 0;
    }

    /// <summary>开始新的一笔（探针 / 兼容入口：不带笔画编号）。</summary>
    internal void BeginStroke() => BeginStroke(0);

    /// <summary>
    /// 开始新的一笔。
    /// </summary>
    /// <param name="strokeId">本次会话内唯一的笔画编号（覆盖层给出；擦除事件要靠它对应旁路日志）。</param>
    internal void BeginStroke(int strokeId)
    {
        _pendingStrokeId = strokeId;
        _rawPoints.Clear();
        _smoothedPrefix.Clear();
        _widthPrefix.Clear();
        _consumed = 0;

        _live = new Path
        {
            // 覆盖层靠窗口背景做命中测试，图元本身不参与，免得笔迹挡住输入。
            IsHitTestVisible = false,
        };

        _canvas.Children.Add(_live);
    }

    /// <summary>追加采样点（高频：每次输入事件一次）。只有当前笔存在时才有效。</summary>
    internal void AddPoints(IReadOnlyList<StrokePoint> points)
    {
        if (_live is null || points.Count == 0)
        {
            return;
        }

        _rawPoints.AddRange(points);
        RebuildLive();
    }

    /// <summary>
    /// 结束当前笔：把它「定格」——增量并入缓存位图，然后**丢掉临时图元**。
    /// </summary>
    /// <remarks>
    /// 这里是「图元数恒定」的落点：定格之后屏幕上不再多一个图元，
    /// 只在缓存位图里多了一小块像素。所以书写时长不会让重绘成本增长。
    /// <para>收笔前先 <b>冲刷尾巴</b>（见 <see cref="ExtendSmoothedPrefix"/>）：
    /// 否则最后 4 个原始点会随本笔一起被丢掉，短笔会「后半截不见了」。</para>
    /// </remarks>
    internal void EndStroke()
    {
        if (_live is null)
        {
            return;
        }

        // ★ 收笔：把为「等下一个点」而保留的尾巴也并进几何，然后按最终的完整几何定格。
        //   顺序很重要——必须在提交之前，否则最后几个点不会出现在缓存位图里。
        RebuildLive(flush: true);

        var live = _live;
        _live = null;

        var committed = BuildCommittedStroke(live);
        if (committed is null || !CommitStroke(committed))
        {
            // 没有可见几何 / 完全在视口外：图元不留。
            _canvas.Children.Remove(live);
        }
        else
        {
            _committed.Add(committed);
            _strokeCount++;
        }

        _pendingStrokeId = 0;
        _rawPoints.Clear();
        _smoothedPrefix.Clear();
        _widthPrefix.Clear();
        _consumed = 0;
    }

    /// <summary>清空全部墨迹（翻页 / 散场）。</summary>
    internal void Clear()
    {
        _live = null;
        _pendingStrokeId = 0;
        _committed.Clear();
        _canvas.Children.Clear();
        _canvas.Children.Add(_cacheImage);
        _rawPoints.Clear();
        _smoothedPrefix.Clear();
        _widthPrefix.Clear();
        _consumed = 0;
        _strokeCount = 0;

        if (_cacheBitmap is not null)
        {
            // 只清「画过东西」的瓦片：整张 4K 位图清零是 33MB 的写入，翻页时会卡一下。
            foreach (var tile in _dirtyTiles)
            {
                ClearTile(tile);
            }
        }

        _dirtyTiles.Clear();
    }

    

    internal IReadOnlyList<int> Erase(IReadOnlyList<DipPoint> path, double radiusDips)
    {
        if (_disposed || path.Count == 0 || _committed.Count == 0)
        {
            return [];
        }

        List<CommittedStroke>? hit = null;
        foreach (var stroke in _committed)
        {
            if (!StrokeEraser.Intersects(stroke.Centerline, stroke.Widths, path, radiusDips))
            {
                continue;
            }

            (hit ??= []).Add(stroke);
        }

        if (hit is null)
        {
            return [];
        }

        var ids = new int[hit.Count];
        var tiles = new HashSet<int>();

        for (var i = 0; i < hit.Count; i++)
        {
            var stroke = hit[i];
            ids[i] = stroke.Id;

            if (stroke.OnCanvas)
            {
                _canvas.Children.Remove(stroke.Visual);
            }

            if (UseTileCache && _cacheBitmap is not null)
            {
                foreach (var tile in CachePlan.TilesFor(stroke.Dirty))
                {
                    tiles.Add(tile);
                }
            }
        }

        _committed.RemoveAll(stroke => hit.Contains(stroke));
        _strokeCount = Math.Max(0, _strokeCount - hit.Count);

        // 瓦片按编号升序重建；块内仍按 _committed 的顺序画，保证「先画的在下、后画的在上」不变。
        foreach (var tile in tiles.Order())
        {
            RebuildTile(tile);
        }

        return ids;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _live = null;
        _cacheBitmap = null;
        _cacheImage.Source = null;
    }

    /// <summary>按当前采样点重建「活笔」图元（平滑 → 逐点宽度 → 几何）。</summary>
    /// <param name="flush">true = 收笔冲刷（不再为等下一个点保留尾巴，见 <see cref="ExtendSmoothedPrefix"/>）。</param>
    private void RebuildLive(bool flush = false)
    {
        if (_live is null || _rawPoints.Count == 0)
        {
            return;
        }

        // ① 把「还没平滑的尾巴」补进前缀（只重算尾巴，不重算整条笔）。
        ExtendSmoothedPrefix(flush);

        
        var variableWidth = IsVariableWidth();
        var widths = variableWidth ? _widthPrefix : [];
        var dense = _smoothedPrefix;

        if (dense.Count == 0)
        {
            return;
        }

        // ③ 单点笔（「点一下」）：描边路径画不出任何东西，必须走圆点填充。
        if (dense.Count == 1)
        {
            _live.Stroke = null;
            _live.Fill = FrozenBrush();
            _live.Data = BuildPolygons([StrokeOutline.Circle(dense[0], Math.Max(_widthPrefix[0], 1.0) / 2)]);
            return;
        }

        if (!variableWidth)
        {
            // 等宽快路径：一条密集折线 + 圆头圆角描边（触摸屏/鼠标的主力路径）。
            _live.Stroke = FrozenBrush();
            _live.StrokeThickness = _widthPrefix.Count > 0 ? _widthPrefix[0] : PenWidthDips;
            _live.StrokeLineJoin = PenLineJoin.Round;
            _live.StrokeStartLineCap = PenLineCap.Round;
            _live.StrokeEndLineCap = PenLineCap.Round;
            _live.Fill = null;
            _live.Data = BuildPolyline(dense);
            return;
        }

        // 变宽路径：中心线 + 逐点宽度 → 填充轮廓（压感笔）。
        var polygons = StrokeOutline.Build(dense, widths);

        _live.Stroke = null;
        _live.Fill = FrozenBrush();
        _live.Data = BuildPolygons(polygons);
    }

    

    private void ExtendSmoothedPrefix(bool flush = false)
    {
        var tail = flush ? StrokePrefixConsumption.FlushTailPoints : StrokePrefixConsumption.LiveTailPoints;
        var (start, end, count) = StrokePrefixConsumption.Plan(_rawPoints.Count, _consumed, tail);
        if (count == 0)
        {
            return;
        }

        // 前缀过长的保护：整条重建（把当前前缀当作原始点重新平滑），避免无界增长。
        if (_smoothedPrefix.Count > MaxSmoothedPrefix)
        {
            RebuildFromRaw();
            return;
        }

        
        
        if (_rawPoints.Count <= tail + 1)
        {
            for (var i = start; i <= end; i++)
            {
                _smoothedPrefix.Add(_rawPoints[i].Position);
                _widthPrefix.Add(WidthOf(_rawPoints[i]));
            }

            _consumed = end + 1;
            return;
        }

        var positions = new List<DipPoint>(count);
        var widths = new List<double>(count);
        for (var i = start; i <= end; i++)
        {
            positions.Add(_rawPoints[i].Position);
            widths.Add(WidthOf(_rawPoints[i]));
        }

        // 平滑窗口：从 start 前一个点开始，好让段 start-1 → start 的切线也有左邻居。
        var windowStart = Math.Max(0, start - 1);
        var window = new List<DipPoint>(positions.Count + 2);
        var windowWidths = new List<double>(positions.Count + 2);

        if (UseInputFilter)
        {
            
            
            
            
            
            
            
            var ctxStart = Math.Max(0, windowStart - 2);
            var ctxEnd = Math.Min(_rawPoints.Count - 1, end + 2);
            var ctx = new List<DipPoint>(ctxEnd - ctxStart + 1);
            for (var i = ctxStart; i <= ctxEnd; i++)
            {
                ctx.Add(_rawPoints[i].Position);
            }

            var filtered = StrokeInputFilter.Binomial3(ctx);
            for (var i = windowStart; i <= end; i++)
            {
                window.Add(filtered[i - ctxStart]);
                windowWidths.Add(WidthOf(_rawPoints[i]));
            }
        }
        else
        {
            for (var i = windowStart; i <= end; i++)
            {
                window.Add(_rawPoints[i].Position);
                windowWidths.Add(WidthOf(_rawPoints[i]));
            }
        }

        var dense = StrokeSmoothing.Flatten(window, SmoothingToleranceDips);
        var denseWidths = StrokeSmoothing.SampleWidths(window, windowWidths, dense);

        // 丢掉已经在画上的那一段（避免与前缀重复），其余追加。
        var skip = 0;
        if (_smoothedPrefix.Count > 0 && dense.Count > 0)
        {
            // 前缀的末点与窗口首点重合的情形：跳过窗口里等于前缀末点的那些密集点。
            var last = _smoothedPrefix[^1];
            while (skip < dense.Count && NearlyEqual(dense[skip], last))
            {
                skip++;
            }
        }

        for (var i = skip; i < dense.Count; i++)
        {
            _smoothedPrefix.Add(dense[i]);
            _widthPrefix.Add(i < denseWidths.Count ? denseWidths[i] : WidthOf(_rawPoints[end]));
        }

        // 下次从 end 的下一个点开始（区间是闭区间，已含 end）。
        _consumed = end + 1;
    }

    /// <summary>整条笔重建（前缀保护触发时的退路）。</summary>
    /// <remarks>消费区间同样走 <see cref="StrokePrefixConsumption.Plan"/>，
    /// 保证「重建」与「增量」两条路对「哪些点已经进了前缀」的判断完全一致——
    /// 两条路算不一样是这类缺陷最容易复发的地方。</remarks>
    private void RebuildFromRaw()
    {
        _smoothedPrefix.Clear();
        _widthPrefix.Clear();

        if (_rawPoints.Count == 0)
        {
            _consumed = 0;
            return;
        }

        var (start, end, count) = StrokePrefixConsumption.Plan(
            _rawPoints.Count, 0, StrokePrefixConsumption.LiveTailPoints);
        if (count == 0)
        {
            _consumed = 0;
            return;
        }

        var positions = new List<DipPoint>(count);
        var widths = new List<double>(count);
        for (var i = start; i <= end; i++)
        {
            positions.Add(_rawPoints[i].Position);
            widths.Add(WidthOf(_rawPoints[i]));
        }

        // 整条重建时滤波窗口的起点就是 0（与增量首轮的窗口语义一致：
        // 下标 0 无双侧邻居 → 滤波保留原值），右侧同样多取 2 点上下文供居中核取邻居。
        IReadOnlyList<DipPoint> smoothInput = positions;
        if (UseInputFilter)
        {
            var ctxEnd = Math.Min(_rawPoints.Count - 1, end + 2);
            var ctx = new List<DipPoint>(ctxEnd + 1);
            for (var i = 0; i <= ctxEnd; i++)
            {
                ctx.Add(_rawPoints[i].Position);
            }

            var filtered = StrokeInputFilter.Binomial3(ctx);
            smoothInput = filtered.Take(end + 1).ToList();
        }

        var dense = StrokeSmoothing.Flatten(smoothInput, SmoothingToleranceDips);
        var denseWidths = StrokeSmoothing.SampleWidths(positions, widths, dense);

        for (var i = 0; i < dense.Count; i++)
        {
            _smoothedPrefix.Add(dense[i]);
            _widthPrefix.Add(i < denseWidths.Count ? denseWidths[i] : widths[^1]);
        }

        _consumed = end + 1;
    }

    private static bool NearlyEqual(DipPoint a, DipPoint b)
        => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;

    /// <summary>整条笔的宽度是否一致（一致就走描边快路径）。</summary>
    private bool IsVariableWidth()
    {
        if (_widthPrefix.Count < 2)
        {
            return false;
        }

        var first = _widthPrefix[0];
        for (var i = 1; i < _widthPrefix.Count; i++)
        {
            if (Math.Abs(_widthPrefix[i] - first) > 0.01)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单个采样点的笔宽（基准宽 + 压感调制）。</summary>
    private double WidthOf(StrokePoint point)
    {
        var hasPressure = point.HasPressure && point.Device == InputDeviceKind.Pen;
        return PenWidthModel.WidthAt(PenWidthDips, point.Pressure, hasPressure);
    }

    /// <summary>由当前活笔构造可长期保留的几何记录（没有可见几何时返回 null）。</summary>
    private CommittedStroke? BuildCommittedStroke(Path live)
    {
        if (_smoothedPrefix.Count == 0)
        {
            return null;
        }

        var maxWidth = 0.0;
        foreach (var width in _widthPrefix)
        {
            maxWidth = Math.Max(maxWidth, width);
        }

        return new CommittedStroke
        {
            Id = _pendingStrokeId,
            Dirty = DirtyRect.FromPoints(_smoothedPrefix, maxWidth, Scale),
            Centerline = _smoothedPrefix.ToArray(),
            Widths = _widthPrefix.ToArray(),
            Visual = live,
        };
    }

    

    private bool CommitStroke(CommittedStroke stroke)
    {
        if (!UseTileCache || _cacheBitmap is null)
        {
            // 应急开关：不做缓存，把笔迹原样留在画布上（图元数会随书写增长，但一定画得出来）。
            stroke.Visual.IsHitTestVisible = false;
            stroke.OnCanvas = true;
            return true;
        }

        var tiles = CachePlan.TilesFor(stroke.Dirty);
        if (tiles.Count == 0)
        {
            return false;
        }

        // 注意：这里**刻意不加** `_cacheBitmap.Lock()`。
        // 整块重画需要先把「本瓦片已定格的旧像素」读回来当底图，而 `WriteableBitmap`
        // 在 `Lock()` 期间**读自己会死锁**（真机表现：整条 UI 线程卡住）。
        // `CopyPixels` / `WritePixels` 本身已经各自做了必要的加锁，外层再包一层是多余的
        // 而且正好会把这个「读—改—写」序列变成死锁。
        foreach (var tile in tiles)
        {
            var (column, row) = CachePlan.TileCoordinates(tile);
            var tilePixel = CachePlan.TilePixelRect(column, row);
            if (tilePixel.Width <= 0 || tilePixel.Height <= 0)
            {
                continue;
            }

            // 整块瓦片重画（铺回旧像素 + 叠当前笔）——
            // 不再按「dirty ∩ tile」裁剪，否则会擦掉先前笔画（见 CompositeStrokeIntoTile 的 ★）。
            CompositeStrokeIntoTile(tile, column, row, stroke.Visual);
            TileWriteCount++;
        }

        return true;
    }

    

    private void CompositeStrokeIntoTile(int tile, int column, int row, Path stroke)
    {
        if (_cacheBitmap is null)
        {
            return;
        }

        var tilePixel = CachePlan.TilePixelRect(column, row);
        var tileWidth = (int)Math.Round(tilePixel.Width);
        var tileHeight = (int)Math.Round(tilePixel.Height);
        if (tileWidth <= 0 || tileHeight <= 0)
        {
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var existing = new byte[tileWidth * tileHeight * 4];
        _cacheBitmap.CopyPixels(
            new Int32Rect((int)tilePixel.X, (int)tilePixel.Y, tileWidth, tileHeight),
            existing,
            tileWidth * 4,
            0);

        var backdrop = BitmapSource.Create(
            tileWidth, tileHeight, 96 * Scale, 96 * Scale,
            PixelFormats.Pbgra32, null, existing, tileWidth * 4);
        backdrop.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // ① 铺回本瓦片已定格的旧像素（底图，1:1 物理像素）。
            context.DrawImage(backdrop, new Rect(0, 0, tileWidth / Scale, tileHeight / Scale));

            // ② 再把当前笔叠上去（平移到瓦片局部坐标系；瓦片外的部分被 RTB 裁掉，符合预期）。
            context.PushTransform(TileTransform(tilePixel));
            DrawStroke(context, stroke);
            context.Pop();
        }

        var stride = tileWidth * 4;
        var buffer = new byte[stride * tileHeight];

        var render = new RenderTargetBitmap(tileWidth, tileHeight, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        render.Render(visual);
        render.CopyPixels(new Int32Rect(0, 0, tileWidth, tileHeight), buffer, stride, 0);

        // ③ 整块写回（与「整块重画」一致——这是「不擦除」的保证）。
        _cacheBitmap.WritePixels(
            new Int32Rect((int)tilePixel.X, (int)tilePixel.Y, tileWidth, tileHeight),
            buffer,
            stride,
            0);

        _dirtyTiles.Add(tile);
    }

    /// <summary>
    /// 擦除后重建一块瓦片：清空 → 把**仍然存在的**笔画按原顺序重画一遍。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CompositeStrokeIntoTile"/> 的区别：这里没有「旧像素底图」——
    /// 被删掉的笔画残留必须消失，所以整块从空开始，只画 <see cref="_committed"/> 里幸存的笔画。
    /// 块内没有幸存笔画时直接清成透明，并把它从 <see cref="_dirtyTiles"/> 里移除
    /// （翻页清空时就不必再写这一块）。
    /// </remarks>
    private void RebuildTile(int tile)
    {
        if (_cacheBitmap is null)
        {
            return;
        }

        var (column, row) = CachePlan.TileCoordinates(tile);
        if (column < 0 || row < 0)
        {
            return;
        }

        var tilePixel = CachePlan.TilePixelRect(column, row);
        var tileWidth = (int)Math.Round(tilePixel.Width);
        var tileHeight = (int)Math.Round(tilePixel.Height);
        if (tileWidth <= 0 || tileHeight <= 0)
        {
            return;
        }

        TileWriteCount++;

        var tileDips = new DipRect(
            tilePixel.X / Scale,
            tilePixel.Y / Scale,
            tilePixel.Width / Scale,
            tilePixel.Height / Scale);

        var survivors = new List<CommittedStroke>();
        foreach (var stroke in _committed)
        {
            if (DirtyRect.Intersects(stroke.Dirty, tileDips))
            {
                survivors.Add(stroke);
            }
        }

        if (survivors.Count == 0)
        {
            ClearTile(tile);
            _dirtyTiles.Remove(tile);
            return;
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(TileTransform(tilePixel));
            foreach (var stroke in survivors)
            {
                DrawStroke(context, stroke.Visual);
            }

            context.Pop();
        }

        var stride = tileWidth * 4;
        var buffer = new byte[stride * tileHeight];
        var render = new RenderTargetBitmap(tileWidth, tileHeight, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        render.Render(visual);
        render.CopyPixels(new Int32Rect(0, 0, tileWidth, tileHeight), buffer, stride, 0);

        _cacheBitmap.WritePixels(
            new Int32Rect((int)tilePixel.X, (int)tilePixel.Y, tileWidth, tileHeight),
            buffer,
            stride,
            0);

        _dirtyTiles.Add(tile);
    }

    

    private Transform TileTransform(DipRect tilePixel)
    {
        var translate = new TranslateTransform(-tilePixel.X / Scale, -tilePixel.Y / Scale);
        translate.Freeze();
        return translate;
    }

    /// <summary>把某个瓦片整块清成透明。</summary>
    private void ClearTile(int tile)
    {
        if (_cacheBitmap is null)
        {
            return;
        }

        var (column, row) = CachePlan.TileCoordinates(tile);
        if (column < 0 || row < 0)
        {
            return;
        }

        var tilePixel = CachePlan.TilePixelRect(column, row);
        var tileWidth = (int)Math.Round(tilePixel.Width);
        var tileHeight = (int)Math.Round(tilePixel.Height);
        if (tileWidth <= 0 || tileHeight <= 0)
        {
            return;
        }

        var zero = new byte[tileWidth * tileHeight * 4];
        _cacheBitmap.WritePixels(
            new Int32Rect((int)tilePixel.X, (int)tilePixel.Y, tileWidth, tileHeight),
            zero,
            tileWidth * 4,
            0);
    }

    /// <summary>把一个 <see cref="Path"/> 的笔迹画进 <see cref="DrawingContext"/>。</summary>
    private static void DrawStroke(DrawingContext context, Path path)
    {
        if (path.Data is null)
        {
            return;
        }

        if (path.Fill is not null && path.Stroke is null)
        {
            context.DrawGeometry(path.Fill, null, path.Data);
            return;
        }

        if (path.Stroke is not null)
        {
            var pen = new Pen(path.Stroke, path.StrokeThickness)
            {
                StartLineCap = path.StrokeStartLineCap,
                EndLineCap = path.StrokeEndLineCap,
                LineJoin = path.StrokeLineJoin,
            };
            pen.Freeze();
            context.DrawGeometry(null, pen, path.Data);
        }
    }

    private SolidColorBrush FrozenBrush()
    {
        var brush = new SolidColorBrush(PenColor);
        brush.Freeze();
        return brush;
    }

    /// <summary>把密集折线做成一条**不填充**的开放路径（描边用）。</summary>
    private static StreamGeometry BuildPolyline(IReadOnlyList<DipPoint> path)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(path[0].X, path[0].Y), isFilled: false, isClosed: false);
            if (path.Count > 1)
            {
                var rest = new Point[path.Count - 1];
                for (var i = 1; i < path.Count; i++)
                {
                    rest[i - 1] = new Point(path[i].X, path[i].Y);
                }

                context.PolyLineTo(rest, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    

    private static StreamGeometry BuildPolygons(IReadOnlyList<DipPoint[]> polygons)
    {
        var geometry = new StreamGeometry();

        
        geometry.FillRule = FillRule.Nonzero;
        using (var context = geometry.Open())
        {
            foreach (var polygon in polygons)
            {
                if (polygon.Length < 3)
                {
                    continue;
                }

                context.BeginFigure(new Point(polygon[0].X, polygon[0].Y), isFilled: true, isClosed: true);
                var rest = new Point[polygon.Length - 1];
                for (var i = 1; i < polygon.Length; i++)
                {
                    rest[i - 1] = new Point(polygon[i].X, polygon[i].Y);
                }

                context.PolyLineTo(rest, isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
