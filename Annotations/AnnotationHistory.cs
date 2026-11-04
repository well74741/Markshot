namespace Markshot.Annotations;

/// <summary>
/// Whole-list snapshot undo (see the SketchPlayer annotation notes, §6): every
/// edit session — drawing, moving, resizing, restyling, typing, deleting —
/// takes exactly one step. Annotations are cloned; frozen bitmaps are shared.
/// </summary>
public sealed class AnnotationHistory
{
    private const int MaxDepth = 50;
    private const long MaxExtraBytes = 1500L * 1024 * 1024;

    /// <summary>A snapshot of the elements plus whatever else the owner wants undone with them.</summary>
    private sealed class Entry : List<Annotation>
    {
        public object? Extra;
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
        copy._undo.AddRange(_undo.Select(step => convert(step)));
        foreach (var step in _redo.Reverse())
        {
            copy._redo.Push(convert(step));
        }

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
        _coalesceKey = null;
    }

    /// <summary>Changes every stored element the same way as the live ones (a pin flipped / turned under them).</summary>
    public void ForEachStored(Action<Annotation> change)
    {
        foreach (var annotation in _undo.Concat(_redo).SelectMany(step => step).Concat(_armed ?? Enumerable.Empty<Annotation>()))
        {
            change(annotation);
        }
    }

    /// <summary>Changes every stored step (a capture's steps get the editor's picture element).</summary>
    public void ForEachStep(Action<List<Annotation>> change)
    {
        foreach (var step in _undo.Concat(_redo))
        {
            change(step);
        }
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
        var snapshot = _undo[^1];
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

        ChangeCount++;
        _redo.Push(Snapshot(items));
        Restore(items, _undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo(List<Annotation> items)
    {
        _armed = null;
        if (_redo.Count == 0)
        {
            return false;
        }

        ChangeCount++;
        _undo.Add(Snapshot(items));
        Restore(items, _redo.Pop());
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

    private void TrimExtras()
    {
        if (ExtraSize == null)
        {
            return;
        }

        while (_undo.Count > 1)
        {
            var total = _undo.OfType<Entry>().SelectMany(entry => ExtraSize(entry.Extra)).DistinctBy(item => item.Key).Sum(item => item.Bytes);
            if (total <= MaxExtraBytes)
            {
                return;
            }

            _undo.RemoveAt(0);
        }
    }

    private List<Annotation> Snapshot(IReadOnlyList<Annotation> items)
    {
        var entry = new Entry { Extra = CaptureExtra?.Invoke() };
        entry.AddRange(items.Select(item => item.Clone()));
        return entry;
    }

    private void Restore(List<Annotation> items, List<Annotation> snapshot)
    {
        items.Clear();
        // Clone again so the stored snapshot stays immutable if it is redone later.
        items.AddRange(snapshot.Select(item => item.Clone()));
        if (snapshot is Entry entry)
        {
            RestoreExtra?.Invoke(entry.Extra);
        }
    }
}
