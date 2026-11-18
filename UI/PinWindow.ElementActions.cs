using Markshot.Annotations;

namespace Markshot.UI;

public partial class PinWindow
{
    private Annotation? _duplicateSource;
    private protected bool DrawingGestureActive => _gesture != null;

    private protected bool BeginDuplicateDrag(Point point, bool captureMouse = true)
    {
        var item = HitAnnotation(point);
        if (item == null || !CanModifyElement(item) || !AnnotationClipboard.CanCopy(item)) return false;
        BeforeElementMutation();
        SelectAnnotation(item);
        _duplicateSource = item;
        _movingSelected = true;
        _dragMoved = false;
        _pendingTextClick = false;
        _pressPoint = _lastPoint = point;
        _history.Arm(Editor.Items);
        if (captureMouse) CaptureMouse(); // tests validate the same data path without real capture/input
        return true;
    }

    private void CommitDuplicateDrag()
    {
        if (_duplicateSource is not { } original) return;
        _duplicateSource = null;
        _history.CommitArmed();
        var copy = AnnotationClipboard.CloneForDuplication(original);
        Editor.Items.Add(copy);
        SelectAnnotation(copy);
        _movingSelected = true;
        ElementsChanged();
    }

    private protected virtual void BeforeElementMutation() { }

    private protected virtual IReadOnlyList<Annotation> ElementSelection =>
        _selected == null ? Array.Empty<Annotation>() : new[] { _selected };

    private protected void FlipSelectedElements(bool horizontal)
    {
        var items = ElementSelection.Where(CanModifyElement).ToList();
        if (items.Count == 0) return;
        _textEditor.Commit();
        _history.Checkpoint(Editor.Items);
        AnnotationOps.FlipElements(items, horizontal);
        ElementsChanged();
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }
}
