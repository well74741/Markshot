namespace Markshot.Annotations;

/// <summary>
/// Whole-list snapshot undo (see the SketchPlayer annotation notes, §6): every
/// edit session — drawing, moving, resizing, restyling, typing, deleting —
/// takes exactly one step. Annotations are cloned; frozen bitmaps are shared.
/// </summary>
public sealed class AnnotationHistory
{
    private const int MaxDepth = 50;
    private const long MaxExtraBytes = 500L * 1024 * 1024;

    /// <summary>A snapshot of the elements plus whatever else the owner wants undone with them.</summary>
    private sealed class Entry : List<Annotation>
    {
        public object? Extra;
        // A restored archive step stays on disk until it is actually undone/redone.
        public Func<ArchiveStep>? Load;
    }

    public sealed record ArchiveStep(List<Annotation> Items, object? Extra);

    /// <summary>Optional disk-backed snapshots for a restored pin; no full-size undo images are cached.</summary>
    public Func<ArchiveStep, Func<ArchiveStep>?>? StoreSnapshot { get; set; }
    public sealed record ArchiveBatch(IReadOnlyList<Func<ArchiveStep>> Steps,
        Func<ArchiveStep, Func<ArchiveStep>?> StoreSnapshot,
        Func<IEnumerable<ArchiveStep>, Action<List<Annotation>>, ArchiveBatch?> StoreTransform);
    /// <summary>Batch transforms without keeping the owner's bitmap-bearing Action closure.</summary>
    public Func<IEnumerable<ArchiveStep>, Action<List<Annotation>>, ArchiveBatch?>? StoreTransformedSteps { get; set; }

    public IEnumerable<ArchiveStep> UndoSteps => _undo.Select(Export);
    // Oldest redo first, i.e. stack bottom first.
    public IEnumerable<ArchiveStep> RedoSteps => _redo.Reverse().Select(Export);

    public void LoadArchive(IEnumerable<Func<ArchiveStep>> undo, IEnumerable<Func<ArchiveStep>> redo, long changes)
    {
        Clear();
        _undo.AddRange(undo.Select(load => new Entry { Load = load }));
        foreach (var load in redo) _redo.Push(new Entry { Load = load });
        ChangeCount = changes;
    }

    private static List<Annotation> Materialize(List<Annotation> step)
    {
        if (step is not Entry { Load: { } load }) return step;
        var state = load();
        var entry = new Entry { Extra = state.Extra };
        entry.AddRange(state.Items);
        return entry;
    }

    private static ArchiveStep Export(List<Annotation> step)
    {
        var loaded = Materialize(step);
        return new ArchiveStep(loaded, (loaded as Entry)?.Extra);
    }

    private static List<Annotation> Map(List<Annotation> step, Func<List<Annotation>, List<Annotation>> change)
    {
        if (step is Entry { Load: { } load })
        {
            return new Entry { Load = () => { var state = load(); return new ArchiveStep(change(state.Items), state.Extra); } };
        }
        return change(step);
    }

    /// <summary>State stored with every step (the image editor's bitmaps; frozen, so shared between steps).</summary>
    public Func<object?>? CaptureExtra { get; set; }

    public Action<object?>? RestoreExtra { get; set; }

    /// <summary>The objects an extra keeps alive and their sizes: old steps are dropped above a memory limit.</summary>
    public Func<object?, IEnumerable<(object Key, long Bytes)>>? ExtraSize { get; set; }

    /// <summary>Counts every change, undo and redo (the editor compares it to know about unsaved changes).</summary>
    public long ChangeCount { get; private set; }

    private readonly List<List<Annotation>> _undo = new();
    private readonly Stack<List<Annotation>> _redo = new();
    private List<Annotation>? _armed;
    private object? _coalesceKey;
    private long _coalesceAt;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>A copy of every step with its elements converted (a capture's steps in the pin's space).</summary>
    public AnnotationHistory Convert(Func<IReadOnlyList<Annotation>, List<Annotation>> convert)
    {
        var copy = new AnnotationHistory();
        copy._undo.AddRange(_undo.Select(step => Map(step, items => convert(items))));
        foreach (var step in _redo.Reverse())
        {
            copy._redo.Push(Map(step, items => convert(items)));
        }
        copy.ChangeCount = ChangeCount;

        return copy;
    }

    /// <summary>Continues another history: its steps become this one's (the pin goes on with the capture's undo).</summary>
    public void Adopt(AnnotationHistory other)
    {
        _undo.Clear();
        _undo.AddRange(other._undo);
        _redo.Clear();
        foreach (var step in other._redo.Reverse())
        {
            _redo.Push(step);
        }

        _armed = null;
        _armedCommitted = false;
        _coalesceKey = null;
        ChangeCount = other.ChangeCount;
        StoreSnapshot = other.StoreSnapshot;
        StoreTransformedSteps = other.StoreTransformedSteps;
    }

    /// <summary>Changes every stored element the same way as the live ones (a pin flipped / turned under them).</summary>
    public void ForEachStored(Action<Annotation> change)
    {
        ForEachStep(items => { foreach (var annotation in items) change(annotation); });
        if (_armed != null)
            _armed = Map(_armed, items => { foreach (var annotation in items) change(annotation); return items; });
    }

    /// <summary>How many steps can be undone (to drop the steps of something cancelled later).</summary>
    public int Depth => _undo.Count;

    /// <summary>Forgets the steps after <paramref name="depth"/> (a cancelled paste leaves no step behind).</summary>
    public void TrimTo(int depth)
    {
        if (depth >= 0 && depth < _undo.Count)
        {
            _undo.RemoveRange(depth, _undo.Count - depth);
            _redo.Clear();
            _armed = null;
        }
    }

    /// <summary>Changes every stored step (a capture's steps get the editor's picture element).</summary>
    public void ForEachStep(Action<List<Annotation>> change)
    {
        if (StoreTransformedSteps is { } store && _undo.Count + _redo.Count > 0)
        {
            var undoCount = _undo.Count;
            if (store(UndoSteps.Concat(RedoSteps), change) is { } batch)
            {
                var armed = _armed; var committed = _armedCommitted;
                LoadArchive(batch.Steps.Take(undoCount), batch.Steps.Skip(undoCount), ChangeCount);
                _armed = armed; _armedCommitted = committed;
                StoreSnapshot = batch.StoreSnapshot;
                StoreTransformedSteps = batch.StoreTransform;
                return;
            }
            // Disk unavailable: materialize once rather than retaining a closure over the
            // owner's image/pattern cache; subsequent snapshots use the usual memory budget.
            StoreSnapshot = null;
            StoreTransformedSteps = null;
            for (var i = 0; i < _undo.Count; i++) _undo[i] = Materialize(_undo[i]);
            var materializedRedo = _redo.Reverse().Select(Materialize).ToArray();
            _redo.Clear();
            foreach (var step in materializedRedo) _redo.Push(step);
        }
        for (var i = 0; i < _undo.Count; i++)
            _undo[i] = Map(_undo[i], items => { change(items); return items; });
        var redo = _redo.Reverse().Select(step => Map(step, items => { change(items); return items; })).ToArray();
        _redo.Clear();
        foreach (var step in redo) _redo.Push(step);
    }

    /// <summary>Records the current state as one undo step.</summary>
    public void Checkpoint(IReadOnlyList<Annotation> items)
    {
        Push(Snapshot(items));
    }

    /// <summary>
    /// Like <see cref="Checkpoint"/>, but repeated calls with the same key in
    /// quick succession (a slider drag, a burst of wheel notches) share one step.
    /// </summary>
    public void CheckpointCoalesced(IReadOnlyList<Annotation> items, object key)
    {
        var now = Environment.TickCount64;
        if (Equals(key, _coalesceKey) && now - _coalesceAt < 800 && _undo.Count > 0)
        {
            _coalesceAt = now;
            return;
        }

        Checkpoint(items);
        _coalesceKey = key;
        _coalesceAt = now;
    }

    /// <summary>
    /// Captures the state at the start of a gesture without recording it yet.
    /// <see cref="CommitArmed"/> turns it into a step once something really
    /// changes, so a click that moves nothing does not cost an undo step.
    /// </summary>
    public void Arm(IReadOnlyList<Annotation> items)
    {
        _armed = Snapshot(items);
        _armedCommitted = false;
    }

    // True while the last undo entry is the armed snapshot of the gesture still in progress.
    private bool _armedCommitted;

    /// <summary>
    /// Right-click during a drag: puts the elements back as they were when the gesture was
    /// armed. Returns true when something had changed (the items were replaced by copies).
    /// </summary>
    public bool CancelGesture(List<Annotation> items)
    {
        if (_armed != null)
        {
            _armed = null; // nothing changed yet
            return false;
        }

        if (!_armedCommitted || _undo.Count == 0)
        {
            return false;
        }

        _armedCommitted = false;
        var snapshot = Materialize(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        items.Clear();
        items.AddRange(snapshot.Select(item => item.Clone()));
        return true;
    }

    public void CommitArmed()
    {
        if (_armed == null)
        {
            return;
        }

        Push(_armed);
        _armed = null;
        _armedCommitted = true;
    }

    public void Disarm() => _armed = null;

    public bool Undo(List<Annotation> items)
    {
        _armed = null;
        if (_undo.Count == 0)
        {
            return false;
        }

        var target = Materialize(_undo[^1]); // a damaged lazy step must not alter either stack
        var current = Snapshot(items);
        ChangeCount++;
        _redo.Push(current);
        Restore(items, target);
        _undo.RemoveAt(_undo.Count - 1);
        TrimExtras();
        return true;
    }

    public bool Redo(List<Annotation> items)
    {
        _armed = null;
        if (_redo.Count == 0)
        {
            return false;
        }

        var target = Materialize(_redo.Peek());
        var current = Snapshot(items);
        ChangeCount++;
        _undo.Add(current);
        Restore(items, target);
        _redo.Pop();
        TrimExtras();
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _armed = null;
    }

    private void Push(List<Annotation> snapshot)
    {
        ChangeCount++;
        _coalesceKey = null;
        _undo.Add(snapshot);
        if (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        TrimExtras();
    }

    private static IEnumerable<BitmapSource> Pictures(Annotation item) => item switch
    {
        ImageAnnotation image => new[] { image.Image, image.Origin }.OfType<BitmapSource>(),
        MosaicAnnotation mosaic => new[] { mosaic.Pattern }.OfType<BitmapSource>(),
        MosaicBrushAnnotation mosaic => new[] { mosaic.Pattern }.OfType<BitmapSource>(),
        _ => Enumerable.Empty<BitmapSource>()
    };

    private long StoredBytes() => _undo.Concat(_redo).SelectMany(step =>
        step.SelectMany(Pictures).Select(bitmap => ((object Key, long Bytes))(bitmap, bitmap.PixelWidth * 4L * bitmap.PixelHeight))
            .Concat(step is Entry entry && ExtraSize != null ? ExtraSize(entry.Extra) : Enumerable.Empty<(object Key, long Bytes)>()))
        .DistinctBy(item => item.Key).Sum(item => item.Bytes);

    private void TrimExtras()
    {
        // Lazy entries are empty lists: never decode an archive merely to measure it. Include
        // image-layer resources as well as owner extras in the fallback resident-memory budget.
        while (_undo.Count > 1 && StoredBytes() > MaxExtraBytes) _undo.RemoveAt(0);
        while (_redo.Count > 1 && StoredBytes() > MaxExtraBytes)
        {
            var keep = _redo.Reverse().Skip(1).ToArray();
            _redo.Clear();
            foreach (var step in keep) _redo.Push(step);
        }
    }

    private List<Annotation> Snapshot(IReadOnlyList<Annotation> items)
    {
        var entry = new Entry { Extra = CaptureExtra?.Invoke() };
        entry.AddRange(items.Select(item => item.Clone()));
        if (StoreSnapshot?.Invoke(new ArchiveStep(entry, entry.Extra)) is { } load)
            return new Entry { Load = load };
        return entry;
    }

    private void Restore(List<Annotation> items, List<Annotation> snapshot)
    {
        snapshot = Materialize(snapshot);
        items.Clear();
        // Clone again so the stored snapshot stays immutable if it is redone later.
        items.AddRange(snapshot.Select(item => item.Clone()));
        if (snapshot is Entry entry)
        {
            RestoreExtra?.Invoke(entry.Extra);
        }
    }
}
