using Markshot.Annotations;

namespace Markshot.UI;

public partial class OverlayWindow
{
    private Annotation? _duplicateDragSource;

    private bool BeginDuplicateElementDrag(Point point, bool captureMouse = true)
    {
        var item = HitAnnotation(point);
        if (item == null || !AnnotationClipboard.CanCopy(item)) return false;
        SelectAnnotation(item);
        _duplicateDragSource = item;
        _draggingAnnotation = true;
        _dragMoved = false;
        _pendingTextClick = false;
        _dragStart = _lastAnnotationPoint = point;
        _annotationStartBounds = item.Bounds;
        _history.Arm(Editor.Items);
        if (captureMouse) InputLayer.CaptureMouse();
        return true;
    }

    private bool PrepareElementDrag(Point point)
    {
        if (!_draggingAnnotation || _dragMoved || _selectedAnnotation == null) return true;
        if ((point - _dragStart).Length < 3 * _scale) return false;
        CommitDuplicateElementDrag(); // all subsequent E/R and translation branches work on the copy
        _dragMoved = true;
        return true;
    }

    private void CommitDuplicateElementDrag()
    {
        if (_duplicateDragSource is not { } original) return;
        _duplicateDragSource = null;
        _history.CommitArmed();
        var copy = AnnotationClipboard.CloneForDuplication(original);
        Editor.Items.Add(copy);
        SelectAnnotation(copy);
        _draggingAnnotation = true;
        _pendingTextClick = false;
        UpdateUndoButtons();
    }
    private void ShowSelectedElementMenu()
    {
        if (_selectedAnnotation is not { } item) return;
        var menu = new ContextMenu();
        foreach (var horizontal in new[] { true, false })
        {
            var option = new MenuItem { Header = horizontal ? "左右翻转" : "上下翻转" };
            var direction = horizontal;
            option.Click += (_, _) =>
            {
                _history.Checkpoint(Editor.Items);
                AnnotationOps.FlipElements(new[] { item }, direction);
                UpdateUndoButtons();
                Editor.InvalidateVisual();
            };
            menu.Items.Add(option);
        }
        menu.PlacementTarget = InputLayer;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

}
