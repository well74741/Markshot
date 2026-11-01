namespace Markshot.Annotations;

/// <summary>
/// Whole-list snapshot undo (see the SketchPlayer annotation notes, §6): every
/// edit session — drawing, moving, resizing, restyling, typing, deleting —
/// takes exactly one step. Annotations are cloned; frozen bitmaps are shared.
/// </summary>
public sealed class AnnotationHistory
{
    private const int MaxDepth = 50;

    private readonly List<List<Annotation>> _undo = new();
    private readonly Stack<List<Annotation>> _redo = new();
    private List<Annotation>? _armed;
    private object? _coalesceKey;
    private long _coalesceAt;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

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
    }

    public void CommitArmed()
    {
        if (_armed == null)
        {
            return;
        }

        Push(_armed);
        _armed = null;
    }

    public void Disarm() => _armed = null;

    public bool Undo(List<Annotation> items)
    {
        _armed = null;
        if (_undo.Count == 0)
        {
            return false;
        }

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
        _coalesceKey = null;
        _undo.Add(snapshot);
        if (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }

    private static List<Annotation> Snapshot(IReadOnlyList<Annotation> items) =>
        items.Select(item => item.Clone()).ToList();

    private static void Restore(List<Annotation> items, List<Annotation> snapshot)
    {
        items.Clear();
        // Clone again so the stored snapshot stays immutable if it is redone later.
        items.AddRange(snapshot.Select(item => item.Clone()));
    }
}
