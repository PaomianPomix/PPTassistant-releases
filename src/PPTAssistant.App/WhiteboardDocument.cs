using System.Collections.ObjectModel;
using System.Windows.Ink;

namespace PPTAssistant.App;

/// <summary>独立于演示文稿的多页白板；每页保留自己的参考尺寸与原始矢量坐标。</summary>
internal sealed class WhiteboardDocument
{
    public const int MaxPages = 100;
    private readonly List<WhiteboardPage> _pages;

    public WhiteboardDocument() : this([new WhiteboardPage()], 0) { }

    private WhiteboardDocument(List<WhiteboardPage> pages, int currentPageIndex)
    {
        _pages = pages;
        Pages = new ReadOnlyCollection<WhiteboardPage>(_pages);
        CurrentPageIndex = currentPageIndex;
        foreach (var page in _pages) page.Changed += OnPageChanged;
    }

    public double Width => CurrentPage.Width;
    public double Height => CurrentPage.Height;
    public IReadOnlyList<WhiteboardPage> Pages { get; }
    public int CurrentPageIndex { get; private set; }
    public WhiteboardPage CurrentPage => _pages[CurrentPageIndex];
    public bool CanUndo => CurrentPage.CanUndo;
    public bool CanRedo => CurrentPage.CanRedo;
    public event EventHandler? Changed;

    public bool AddPage()
    {
        if (_pages.Count >= MaxPages) return false;
        var page = new WhiteboardPage(isDark: CurrentPage.IsDark);
        page.Changed += OnPageChanged;
        _pages.Insert(CurrentPageIndex + 1, page);
        CurrentPageIndex++;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool DeleteCurrentPage()
    {
        if (_pages.Count == 1) return false;
        CurrentPage.Changed -= OnPageChanged;
        _pages.RemoveAt(CurrentPageIndex);
        CurrentPageIndex = Math.Min(CurrentPageIndex, _pages.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool GoToPage(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0 || zeroBasedIndex >= _pages.Count) return false;
        if (CurrentPageIndex == zeroBasedIndex) return true;
        CurrentPageIndex = zeroBasedIndex;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetDarkBackground(bool isDark) => CurrentPage.SetDarkBackground(isDark);
    public void ClearCurrentPage() => CurrentPage.Strokes.Clear();
    public void Undo() => CurrentPage.Undo();
    public void Redo() => CurrentPage.Redo();

    internal static WhiteboardDocument Restore(IEnumerable<WhiteboardPage> pages, int currentPageIndex)
    {
        var restoredPages = pages.ToList();
        if (restoredPages.Count is < 1 or > MaxPages || currentPageIndex < 0 || currentPageIndex >= restoredPages.Count)
            throw new ArgumentException("白板页数或当前页索引无效。");
        return new WhiteboardDocument(restoredPages, currentPageIndex);
    }

    private void OnPageChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
}

internal sealed class WhiteboardPage
{
    internal const double MaxViewportDimension = 32768;

    private const int MaxHistoryEntries = 100;
    private readonly List<PageEdit> _undo = [];
    private readonly List<PageEdit> _redo = [];
    private Stroke[] _knownOrder;
    private bool _applyingHistory;

    internal WhiteboardPage(StrokeCollection? strokes = null, bool isDark = false,
        double width = 1600, double height = 900, bool isViewportInitialized = false)
    {
        ValidateViewport(width, height);
        Strokes = strokes ?? new StrokeCollection();
        IsDark = isDark;
        Width = width;
        Height = height;
        IsViewportInitialized = isViewportInitialized;
        IncludeInkBounds();
        _knownOrder = Strokes.ToArray();
        Strokes.StrokesChanged += OnStrokesChanged;
    }

    public StrokeCollection Strokes { get; }
    public bool IsDark { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }
    internal bool IsViewportInitialized { get; private set; }
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal event EventHandler? Changed;

    internal void InitializeViewport(double width, double height)
    {
        if (IsViewportInitialized) return;
        ValidateViewport(width, height);
        Width = width;
        Height = height;
        IsViewportInitialized = true;
        IncludeInkBounds();
    }

    /// <summary>只向右和下扩展参考范围，不因换屏、擦除或撤销缩小或缩放已有墨迹。</summary>
    internal void IncludeInkBounds()
    {
        var bounds = Strokes.GetBounds();
        if (bounds.IsEmpty) return;
        Width = Math.Max(Width, bounds.Right);
        Height = Math.Max(Height, bounds.Bottom);
    }

    internal static void ValidateViewport(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || width > MaxViewportDimension || height > MaxViewportDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "白板参考尺寸必须为有限正数，且不大于 32768。");
    }

    internal void SetDarkBackground(bool isDark)
    {
        if (IsDark == isDark) return;
        var oldIsDark = IsDark;
        IsDark = isDark;
        Record(new PageEdit([], [], oldIsDark, isDark));
    }

    internal void Undo()
    {
        if (!CanUndo) return;
        var edit = _undo[^1];
        Apply(edit, undo: true);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Redo()
    {
        if (!CanRedo) return;
        var edit = _redo[^1];
        Apply(edit, undo: false);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_applyingHistory) return;
        var removed = e.Removed.Select(stroke => new IndexedStroke(Array.IndexOf(_knownOrder, stroke), stroke))
            .OrderBy(item => item.Index).ToArray();
        var added = e.Added.Select(stroke => new IndexedStroke(Strokes.IndexOf(stroke), stroke))
            .OrderBy(item => item.Index).ToArray();
        _knownOrder = Strokes.ToArray();
        if (added.Length > 0)
        {
            // 即使由非 UI 调用方直接加笔，也不能在之后重设其原始坐标系。
            IsViewportInitialized = true;
            IncludeInkBounds();
        }
        if (removed.Length != 0 || added.Length != 0) Record(new PageEdit(removed, added, null, null));
    }

    private void Record(PageEdit edit)
    {
        _undo.Add(edit);
        if (_undo.Count > MaxHistoryEntries) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(PageEdit edit, bool undo)
    {
        _applyingHistory = true;
        try
        {
            var remove = undo ? edit.Added : edit.Removed;
            var insert = undo ? edit.Removed : edit.Added;
            if (remove.Length > 0) Strokes.Remove(new StrokeCollection(remove.Select(item => item.Stroke)));
            foreach (var item in insert) Strokes.Insert(item.Index, item.Stroke);
            if (insert.Length > 0) IncludeInkBounds();
            var isDark = undo ? edit.OldIsDark : edit.NewIsDark;
            if (isDark.HasValue) IsDark = isDark.Value;
        }
        finally
        {
            _knownOrder = Strokes.ToArray();
            _applyingHistory = false;
        }
    }

    private sealed record IndexedStroke(int Index, Stroke Stroke);
    private sealed record PageEdit(IndexedStroke[] Removed, IndexedStroke[] Added, bool? OldIsDark, bool? NewIsDark);
}
