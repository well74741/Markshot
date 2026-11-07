using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Capture window: mouse and keyboard input.
public partial class OverlayWindow
{
    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var point = ToPixels(e.GetPosition(InputLayer));
        if (PenEyedropper.IsPen(_tool) && PenEyedropper.AltDown && _hasSelection)
        {
            // 吸色: Alt + click with the pen takes the colour under the cross.
            if (_eyedropper.Pick(point) is { } picked)
            {
                Toolbar.PickColor(picked);
            }

            e.Handled = true;
            return;
        }

        if (_textEditor.Target is { } editingText)
        {
            // The frame of the text being typed moves / resizes / rotates it; typing continues.
            if (_textEditor.TryBeginFrameDrag(point))
            {
                e.Handled = true;
                return;
            }

            _textEditor.Commit(); // a non-empty text stays selected (OnTextEditEnded)

            // A click elsewhere only finishes typing — unless the text tool is
            // pointed at another text, which then opens for editing, or the click is on the capture's edge
            // (which then resizes the capture).
            if (!(_tool == AnnotationTool.Text && HitAnnotation(point) is TextAnnotation other &&
                  !ReferenceEquals(other, editingText)) && HitHandle(point) < 0)
            {
                e.Handled = true;
                return;
            }
        }

        Focus();

        if (_editingWatermark != null)
        {
            CommitWatermark();
        }

        if (_polyline != null)
        {
            // Each click is a corner; a double-click ends the line.
            if (e.ClickCount == 2)
            {
                FinishPolyline();
            }
            else
            {
                _polyline.Press(point, IsShiftDown);
                InputLayer.CaptureMouse(); // dragging from here draws the next segment
            }

            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _dragOrigin = _selection;
        _dragging = true;
        _pendingWindow = Rect.Empty;

        if (e.ClickCount == 2 &&
            (_tool == AnnotationTool.None || _tool == AnnotationTool.Select) &&
            HitAnnotation(point) is TextAnnotation doubleClicked)
        {
            _dragging = false;
            _draggingAnnotation = false;
            InputLayer.ReleaseMouseCapture();
            BeginTextEdit(doubleClicked, isNew: false, caretAt: point);
            e.Handled = true;
            return;
        }

        // The adjustment frame of the selected element works with any tool:
        // handles/edges resize, the band outside the corners rotates.
        if (_selectedAnnotation != null && _hasSelection)
        {
            var frame = AnnotationOps.SelectionHit(_selectedAnnotation, IsFreshSelection, _tool, point, _scale);
            if (frame.IsHandle || frame.Rotate)
            {
                TryBeginAnnotationTransform(point);
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (frame.Inside && _selectedAnnotation is TextAnnotation &&
                _tool is AnnotationTool.Select or AnnotationTool.None or AnnotationTool.Text)
            {
                // Drag moves the selected text; a plain click opens it for editing.
                TryBeginAnnotationTransform(point);
                _pendingTextClick = true;
                _dragMoved = false;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // The capture's edges and corners resize it with any tool in hand (a pen, a shape, the eraser…).
        if (_hasSelection && DrawingTool && HitHandle(point) is var edge and >= 0)
        {
            _mode = DragMode.Resizing;
            _handle = edge;
            _recentRegionIndex = -1;
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Text && _hasSelection)
        {
            if (HitAnnotation(point) is TextAnnotation pressed)
            {
                _textPress = pressed;
                _textPressPoint = point;
                _textPressMoved = false;
                _lastAnnotationPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _dragging = false;
                if (e.ClickCount >= 2)
                {
                    // A double-click on the blank: a new text (the mouse-up lands on its box, not on this layer).
                    BeginTextEdit(AnnotationStyler.NewText(point, Toolbar), isNew: true, caretAt: null);
                }
                else
                {
                    ClearAnnotationSelection(); // a single click on the blank only lets go of the selected text
                }

                e.Handled = true;
                return;
            }
        }

        // Before "double-click = copy": a double-click on a watermark edits its text.
        if (e.ClickCount == 2 && _tool == AnnotationTool.Watermark &&
            AnnotationOps.FindWatermark(Editor.Items) is { } watermark && watermark.HitTest(point, 4 * _scale))
        {
            _dragging = false;
            _draggingAnnotation = false;
            InputLayer.ReleaseMouseCapture();
            BeginWatermarkEdit(watermark);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 &&
            (_tool == AnnotationTool.None || _tool == AnnotationTool.Select) &&
            _hasSelection && _selection.Contains(point))
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (_tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser and not AnnotationTool.Cutout)
        {
            var candidate = HitAnnotation(point);
            if (candidate != null && SameFamily(candidate, _tool))
            {
                SelectAnnotation(candidate);
                ClearAnnotationHover();
                e.Handled = true;
                return;
            }
        }

        if (_selectedAnnotation != null && (_tool == AnnotationTool.Select || _tool == AnnotationTool.None) &&
            TryBeginAnnotationTransform(point))
        {
            _history.Arm(Editor.Items);
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _draggingAnnotation = true;
                _dragMoved = false;
                _pendingTextClick = false;
                _lastAnnotationPoint = point;
                _annotationStartBounds = hit.Bounds;
                _annotationScaleStartPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearAnnotationSelection();
        }

        if (_tool == AnnotationTool.Cutout)
        {
            // 抠除: the hole is an element of its own, there (and cut) while it is drawn.
            var cutout = new CutoutAnnotation { Thickness = EraserWidth };
            cutout.Points.Add(point);
            _history.Arm(Editor.Items);
            _history.CommitArmed();
            Editor.Items.Add(cutout);
            _eraserPath = cutout;
            _gesture = new CreationGesture(cutout, point);
            _erasing = true;
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Eraser)
        {
            // The stroke erases live and is bound to the elements it touched on release.
            _eraserPath = new PenAnnotation { Thickness = EraserWidth };
            _eraserPath.Points.Add(point);
            _gesture = new CreationGesture(_eraserPath, point);
            _erasing = true;
            Editor.LiveEraser = _eraserPath.Points;
            Editor.LiveEraserWidth = _eraserPath.Thickness;
            Editor.LiveEraserStrength = Toolbar.EraserOpacity;
            Editor.LiveEraserSoftness = Toolbar.EraserSoftness;
            Editor.InvalidateVisual();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Watermark)
        {
            if (AnnotationOps.FindWatermark(Editor.Items) is { } dragged)
            {
                _draggingWatermark = dragged;
                _watermarkLast = point;
                _dragStart = point;
                _dragMoved = false;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (HitCornerDot(point) is var dot and >= 0 && _selectedAnnotation == null)
        {
            // Drag a corner dot towards the centre: rounded corners for the capture.
            _mode = DragMode.Rounding;
            _roundingCorner = dot;
            _cornerRadiusOrigin = _cornerRadius;
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool is not (AnnotationTool.None or AnnotationTool.Select or AnnotationTool.Eraser or AnnotationTool.Cutout) &&
            _hasSelection && _selection.Contains(point))
        {
            StartAnnotation(point);
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_hasSelection)
        {
            var handle = HitHandle(point);
            if (handle >= 0)
            {
                _mode = DragMode.Resizing;
                _handle = handle;
                _recentRegionIndex = -1;
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Moving;
                _recentRegionIndex = -1;
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        ClearAnnotationSelection();

        // Clicking a highlighted window / element selects it; dragging starts a manual selection.
        if (_hoverTarget != null && !_hoverLocal.IsEmpty && !_hasSelection)
        {
            _pendingWindow = _hoverLocal;
            _mode = DragMode.Creating;
            _cornerRadius = 0;
            _lasso = null;
            _handle = -1;
            _selection = Rect.Empty;
            _hasSelection = false;
            _recentRegionIndex = -1;
            Editor.Items.Clear();
            _history.Clear();
            ClearHover();
            UpdateUndoButtons();
            UpdateSelectionVisuals();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        _mode = DragMode.Creating;
        _cornerRadius = 0;
        _lasso = null;
        _handle = -1;
        _pendingWindow = Rect.Empty;
        _recentRegionIndex = -1;
        _selection = new Rect(point, point);
        _hasSelection = false;
        Editor.Items.Clear();
        _history.Clear();
        ClearHover();
        UpdateUndoButtons();
        UpdateSelectionVisuals();
        InputLayer.CaptureMouse();
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var point = ToPixels(e.GetPosition(InputLayer));
        if (_hasSelection && _eyedropper.Update(_tool, point, _gesture != null))
        {
            Magnifier.Visibility = Visibility.Collapsed;
            InputLayer.Cursor = Cursors.None; // the cross is the pointer
            return;
        }

        if (InputLayer.Cursor == Cursors.None && _gesture?.Target is not NumberAnnotation)
        {
            UpdateCursorToPointer(); // (dragging out a number's arrow keeps the pointer hidden: no flashing badge)
        }

        if (ShouldShowMagnifier(point))
        {
            UpdateMagnifier(point);
        }
        else
        {
            Magnifier.Visibility = Visibility.Collapsed;
        }


        if (_spaceDrag && _mode is DragMode.Creating or DragMode.Resizing)
        {
            var dx = point.X - _spaceAnchor.X;
            var dy = point.Y - _spaceAnchor.Y;
            var moved = new Rect(
                _spaceSelectionOrigin.X + dx,
                _spaceSelectionOrigin.Y + dy,
                _spaceSelectionOrigin.Width,
                _spaceSelectionOrigin.Height);

            moved.X = Math.Clamp(moved.X, 0, Math.Max(0, _screen.PixelWidth - moved.Width));
            moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, _screen.PixelHeight - moved.Height));
            _selection = moved;
            _dragStart = new Point(_spaceDragStartOrigin.X + dx, _spaceDragStartOrigin.Y + dy);
            _hasSelection = true;
            UpdateSelectionVisuals();
            return;
        }

        if (_polyline != null)
        {
            _polyline.Move(point, IsShiftDown);
            Editor.InvalidateVisual();
            return;
        }

        if (_gesture != null)
        {
            _gesture.Pressure = PenEyedropper.Pressure(e, InputLayer); // tablet pen: the width follows the pressure
            _gesture.Move(point, IsShiftDown, IsCtrlDown);
            if (_gesture.Target is NumberAnnotation && InputLayer.Cursor != Cursors.None)
            {
                InputLayer.Cursor = Cursors.None; // dragging out the arrow: its head is the pointer (a cursor would hide it)
            }

            Editor.InvalidateVisual();
            return;
        }

        if (_draggingWatermark != null)
        {
            if (!_dragMoved && (point - _dragStart).Length < 3 * _scale)
            {
                return;
            }

            if (!_dragMoved)
            {
                _dragMoved = true;
                _history.CommitArmed();
                UpdateUndoButtons();
                InputLayer.Cursor = Cursors.SizeAll;
            }

            _draggingWatermark.Translate(point.X - _watermarkLast.X, point.Y - _watermarkLast.Y);
            _watermarkLast = point;
            Editor.InvalidateVisual();
            return;
        }

        if ((_rotatingAnnotation || _resizingAnnotation) && !_transformStarted)
        {
            // A click on a handle / grip without a real drag changes nothing.
            if ((point - _transformPress).Length < 3 * _scale)
            {
                return;
            }

            _transformStarted = true;
        }

        if (_textPress != null)
        {
            if (!_textPressMoved && (point - _textPressPoint).Length * 1 / _scale < 3)
            {
                return;
            }

            if (!_textPressMoved)
            {
                _textPressMoved = true;
                _history.CommitArmed();
                UpdateUndoButtons();
                SelectQuietly(_textPress);
                InputLayer.Cursor = Cursors.SizeAll;
            }

            _textPress.Translate(point.X - _lastAnnotationPoint.X, point.Y - _lastAnnotationPoint.Y);
            _lastAnnotationPoint = point;
            Editor.InvalidateVisual();
            return;
        }

        if ((_rotatingAnnotation || _resizingAnnotation || _draggingAnnotation) && _selectedAnnotation != null)
        {
            _history.CommitArmed();
            UpdateUndoButtons();
        }

        if ((_rotatingAnnotation || (_draggingAnnotation && _dragRotate)) && _selectedAnnotation != null)
        {
            var angle = Math.Atan2(point.Y - _annotationCenter.Y, point.X - _annotationCenter.X) * 180 / Math.PI;
            var rotation = _annotationStartRotation + angle - _annotationRotateStartAngle;
            if (IsShiftDown)
            {
                rotation = Math.Round(rotation / 15) * 15;
            }

            _selectedAnnotation.Rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
            Editor.InvalidateVisual();
            return;
        }

        if (_resizingAnnotation && _selectedAnnotation != null)
        {
            var target = AnnotationOps.ResizeFromHandle(
                _annotationStartBounds, _selectedAnnotation.Rotation, _annotationHandle, point + _resizeGrab, IsShiftDown);
            AnnotationOps.Resize(_selectedAnnotation, target, _annotationHandle);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingAnnotation && _dragScale && _selectedAnnotation != null)
        {
            var center = new Point(
                _dragScaleBounds.X + _dragScaleBounds.Width / 2,
                _dragScaleBounds.Y + _dragScaleBounds.Height / 2);
            var startDistance = Math.Max(1, (_dragScalePointer - center).Length);
            var factor = Math.Max(0.05, (point - center).Length / startDistance);
            AnnotationOps.Resize(_selectedAnnotation, AnnotationOps.ScaleAround(_dragScaleBounds, center, factor), 4);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingAnnotation && _selectedAnnotation != null)
        {
            var dx = point.X - _lastAnnotationPoint.X;
            var dy = point.Y - _lastAnnotationPoint.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                if (!_dragMoved && (point - _dragStart).Length < 3 * _scale)
                {
                    return; // a click on a selected text should not nudge it
                }

                _dragMoved = true;
                ClearAnnotationHover();
                _selectedAnnotation.Translate(dx, dy);
                _lastAnnotationPoint = point;
                Editor.InvalidateVisual();
            }

            return;
        }

        if (!_dragging)
        {
            if (!_hasSelection)
            {
                UpdateHover(point);
            }

            UpdateSizeBox(point);
            UpdateCursor(point);
            if (_tool is AnnotationTool.None or AnnotationTool.Select && _selectedAnnotation == null && UpdateCodeHover(point))
            {
                InputLayer.Cursor = Cursors.Hand; // on a code: its box and card show
                return;
            }

            if (_hasSelection && _tool == AnnotationTool.Watermark)
            {
                UpdateWatermarkHover(point);
            }
            else if (_hasSelection && _tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser and not AnnotationTool.Cutout)
            {
                UpdateAnnotationHover(point);
            }

            return;
        }

        if (_pendingWindow != Rect.Empty)
        {
            if ((point - _dragStart).Length > 4)
            {
                _pendingWindow = Rect.Empty;
                _selection = ClampToScreen(NormalizeRect(_dragStart, point));
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
                ApplyRememberedCorner();
                UpdateSelectionVisuals();
            }

            return;
        }

        switch (_mode)
        {
            case DragMode.Creating:
            {
                var rect = NormalizeRect(_dragStart, point);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    rect = MakeSquare(_dragStart, point);
                }

                _selection = ClampToScreen(rect);
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
                ApplyRememberedCorner();
                RequestSelectionVisuals(); // drawn once per frame, however fast the mouse reports
                break;
            }

            case DragMode.Moving:
            {
                var delta = point - _dragStart;
                var moved = new Rect(
                    _dragOrigin.X + delta.X,
                    _dragOrigin.Y + delta.Y,
                    _dragOrigin.Width,
                    _dragOrigin.Height);
                moved.X = Math.Clamp(moved.X, 0, Math.Max(0, _screen.PixelWidth - moved.Width));
                moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, _screen.PixelHeight - moved.Height));
                _selection = moved;
                RequestSelectionVisuals(); // drawn once per frame, however fast the mouse reports
                break;
            }

            case DragMode.Resizing:
            {
                _selection = ClampToScreen(ResizeRect(_dragOrigin, _handle, point));
                _cornerRadius = Math.Min(_cornerRadius, Math.Min(_selection.Width, _selection.Height) / 2);
                RequestSelectionVisuals(); // drawn once per frame, however fast the mouse reports
                break;
            }

            case DragMode.Rounding:
            {
                _cornerRadius = AnnotationEditor.CornerRadiusFor(_selection, _roundingCorner, point, _scale);
                UpdateSelectionVisuals();
                break;
            }
        }
    }

    /// <summary>
    /// Right-click while the left button is still down undoes the gesture in progress:
    /// drawing, erasing, moving / resizing / rotating an element, dragging a text or the
    /// watermark, and moving / resizing the capture area.
    /// </summary>
    private bool CancelGesture()
    {
        if (_textEditor.IsFraming)
        {
            _textEditor.CancelFrameDrag();
            return true;
        }

        if (_polyline != null)
        {
            DropPolyline();
            return true;
        }

        if (_active != null || _erasing)
        {
            Editor.Preview = null;
            Editor.LiveEraser = null;
            _active = null;
            _gesture = null;
            DropCutout();
            _erasing = false;
            _eraserPath = null;
            _dragging = false;
            _spaceDrag = false;
            _mode = DragMode.None;
            InputLayer.ReleaseMouseCapture();
            Editor.InvalidateVisual();
            return true;
        }

        if (_resizingAnnotation || _rotatingAnnotation || _draggingAnnotation || _textPress != null || _draggingWatermark != null)
        {
            var target = _selectedAnnotation ?? _textPress;
            var index = target != null ? Editor.Items.IndexOf(target) : -1;
            var reverted = _history.CancelGesture(Editor.Items);
            _resizingAnnotation = false;
            _rotatingAnnotation = false;
            _draggingAnnotation = false;
            _textPress = null;
            _draggingWatermark = null;
            _dragRotate = false;
            _dragScale = false;
            _pendingTextClick = false;
            _dragMoved = false;
            _dragging = false;
            _mode = DragMode.None;
            InputLayer.ReleaseMouseCapture();
            if (reverted)
            {
                ClearAnnotationSelection();
                if (index >= 0 && index < Editor.Items.Count && Editor.Items[index] is not WatermarkAnnotation)
                {
                    SelectQuietly(Editor.Items[index]);
                }
            }

            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return true;
        }

        if (_lassoing)
        {
            _lassoing = false;
            _lassoDraft = null;
            Editor.LassoDraft = null;
            Editor.LassoPreview = null;
            InputLayer.ReleaseMouseCapture();
            Editor.InvalidateVisual();
            UpdateSelectionVisuals();
            return true;
        }

        if (_mode == DragMode.Rounding)
        {
            _cornerRadius = _cornerRadiusOrigin;
            _mode = DragMode.None;
            _dragging = false;
            InputLayer.ReleaseMouseCapture();
            UpdateSelectionVisuals();
            return true;
        }

        if (_dragging && _mode is DragMode.Creating or DragMode.Moving or DragMode.Resizing)
        {
            _selection = _dragOrigin;
            _hasSelection = !_dragOrigin.IsEmpty && _dragOrigin.Width > 0 && _dragOrigin.Height > 0;
            _dragging = false;
            _spaceDrag = false;
            _mode = DragMode.None;
            Magnifier.Visibility = Visibility.Collapsed;
            InputLayer.ReleaseMouseCapture();
            UpdateSelectionVisuals();
            return true;
        }

        return false;
    }

    private bool _visualsQueued;

    /// <summary>
    /// While the area is dragged: its visuals are brought up to date right before the next frame is drawn, once,
    /// instead of on every mouse report (a fast mouse reports far more often than the screen refreshes).
    /// </summary>
    private void RequestSelectionVisuals()
    {
        if (_visualsQueued)
        {
            return;
        }

        _visualsQueued = true;
        CompositionTarget.Rendering += FlushSelectionVisuals;
    }

    private void FlushSelectionVisuals(object? sender, EventArgs e)
    {
        CancelQueuedVisuals();
        if (!_closed)
        {
            UpdateSelectionVisuals();
        }
    }

    private void CancelQueuedVisuals()
    {
        if (_visualsQueued)
        {
            _visualsQueued = false;
            CompositionTarget.Rendering -= FlushSelectionVisuals;
        }
    }

    private void Input_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_active == null && !_erasing)
        {
            return;
        }

        // Right click while drawing cancels the element being drawn.
        Editor.Preview = null;
        Editor.LiveEraser = null;
        _active = null;
        _gesture = null;
        DropCutout();
        _erasing = false;
        _eraserPath = null;
        _dragging = false;
        _spaceDrag = false;
        _suppressContextMenu = true;
        InputLayer.ReleaseMouseCapture();
        Editor.InvalidateVisual();
        e.Handled = true;
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        CancelQueuedVisuals(); // the release draws the final state itself

        InputLayer.ReleaseMouseCapture();
        _dragging = false;
        Magnifier.Visibility = Visibility.Collapsed;
        _spaceDrag = false;
        _dragRotate = false;
        _dragScale = false;
        if (_polyline != null)
        {
            _polyline.Release(ToPixels(e.GetPosition(InputLayer)), IsShiftDown, 4 * _scale);
            _mode = DragMode.None;
            Editor.InvalidateVisual();
            return;
        }
        if (_draggingWatermark != null)
        {
            _draggingWatermark = null;
            _history.Disarm();
            _dragMoved = false;
            _mode = DragMode.None;
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_mode == DragMode.Rounding)
        {
            _mode = DragMode.None;
            RememberCorner();
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        var endedTransform = _resizingAnnotation || _rotatingAnnotation || _draggingAnnotation;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;

        if (_textPress != null)
        {
            var pressed = _textPress;
            _textPress = null;
            _mode = DragMode.None;
            if (_textPressMoved)
            {
                UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            }
            else
            {
                _history.Disarm();
                BeginTextEdit(pressed, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
            }

            return;
        }

        if (endedTransform)
        {
            _history.Disarm();
        }

        if (_erasing)
        {
            _erasing = false;
            _gesture = null;
            Editor.LiveEraser = null;
            if (_eraserPath is CutoutAnnotation)
            {
                UpdateUndoButtons(); // the hole was cut while drawing; its undo step exists
            }
            else if (_eraserPath != null)
            {
                // The state before the stroke becomes the undo step, but only if it erased something.
                var before = Editor.Items.Select(item => item.Clone()).ToList();
                if (AnnotationOps.BindEraser(Editor.Items, _eraserPath.Points, _eraserPath.Thickness, Toolbar.EraserOpacity, Toolbar.EraserSoftness))
                {
                    _history.Checkpoint(before);
                    UpdateUndoButtons();
                }
            }

            _eraserPath = null;
            Editor.InvalidateVisual();
            _mode = DragMode.None;
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_draggingAnnotation)
        {
            _draggingAnnotation = false;
            _mode = DragMode.None;
            var clicked = _pendingTextClick && !_dragMoved;
            _pendingTextClick = false;
            _dragMoved = false;
            if (clicked && _selectedAnnotation is TextAnnotation selectedText)
            {
                BeginTextEdit(selectedText, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
                return;
            }

            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
            _mode = DragMode.None;
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_pendingWindow != Rect.Empty)
        {
            _selection = _pendingWindow;
            _hasSelection = true;
            _pendingWindow = Rect.Empty;
            ApplyRememberedCorner();
            _mode = DragMode.None;
            _handle = -1;
            ClearHover();
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_mode == DragMode.Creating)
        {
            if (_selection.Width < 2 || _selection.Height < 2)
            {
                _hasSelection = false;
                _selection = Rect.Empty;
            }
            else
            {
                _hasSelection = true;
            }
        }

        _mode = DragMode.None;
        _handle = -1;
        ClearHover();
        UpdateSelectionVisuals();
        UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
    }

    private void Input_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_textEditor.IsActive)
        {
            _textEditor.AdjustSize(e.Delta);
            e.Handled = true;
            return;
        }

        if (_hasSelection || _active != null || _closed)
        {
            // Selected element first: wheel = thickness / size, Ctrl+wheel = its opacity.
            // Otherwise with a tool active: the same for the tool's next element.
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _active == null &&
                (_selectedAnnotation != null ? AnnotationOps.HasSoftEdge(_selectedAnnotation) : PenEyedropper.IsPen(_tool) || _tool == AnnotationTool.Eraser))
            {
                Toolbar.NudgeSoftness(e.Delta); // Shift + wheel: the pen's / the eraser's 软边
                e.Handled = true;
            }
            else if (_selectedAnnotation != null)
            {
                AdjustSelectedByWheel(e.Delta, ctrl);
                e.Handled = true;
            }
            else if (_active == null && _tool is not AnnotationTool.None and not AnnotationTool.Select)
            {
                if (ctrl)
                {
                    Toolbar.NudgeOpacity(e.Delta);
                }
                else
                {
                    AdjustActiveToolByWheel(e.Delta);
                }

                e.Handled = true;
            }
            else if (_active == null && _hasSelection && !_dragging)
            {
                // No tool in hand: the wheel rounds the capture's corners (up = rounder), remembered for the next one.
                WheelCorner(e.Delta);
                e.Handled = true;
            }

            return;
        }

        // PixPin: the wheel steps between the element and what is around it (up = outer).
        if (StepHoverLevel(e.Delta))
        {
            e.Handled = true;
            return;
        }

        if (App.Config.DetectElements && WheelInWindow(e.Delta))
        {
            e.Handled = true;
            return;
        }

        // Without element detection: the window or its child control.
        _preferChild = !_preferChild;
        ClearHover();
        UpdateHover(ToPixels(e.GetPosition(InputLayer)));
        if (_hoverTarget != null)
        {
            ShowWheelTip(_preferChild ? "识别范围：窗口里的控件" : "识别范围：整个窗口");
        }

        e.Handled = true;
    }

    private void Input_MouseLeave(object sender, MouseEventArgs e)
    {
        Magnifier.Visibility = Visibility.Collapsed;
        ClearHover();
        ClearAnnotationHover();
    }

    /// <summary>The corners last adjusted, as far as this area allows (a lasso has none).</summary>
    /// <summary>One wheel notch: the capture's corner radius by 6 DIP (a lasso capture has no corners).</summary>
    private void WheelCorner(int delta)
    {
        if (_lasso != null)
        {
            ShowWheelTip("手绘截图没有圆角");
            return;
        }

        var most = Math.Floor(Math.Min(_selection.Width, _selection.Height) / 2);
        var step = 6 * _scale;
        var radius = Math.Clamp(Math.Round((_cornerRadius + Math.Sign(delta) * step) / step) * step, 0, most);
        _cornerRadius = radius;
        RememberCorner();
        UpdateSelectionVisuals();
        ShowWheelTip(radius <= 0 ? "圆角：无 · 滚轮：上 变圆，下 变方" : $"圆角：{Math.Round(radius / _scale)} · 滚轮：上 变圆，下 变方");
    }

    private void ApplyRememberedCorner()
    {
        if (_lasso == null && _hasSelection)
        {
            _cornerRadius = Math.Min(Math.Max(0, App.Config.CaptureCornerRadius), Math.Min(_selection.Width, _selection.Height) / 2);
        }
    }

    /// <summary>A corner dot was let go: the next capture starts with this.</summary>
    private void RememberCorner() => App.Config.CaptureCornerRadius = _cornerRadius;

    private int _quickDepth;

    /// <summary>快速双击 (setting): whatever tool is in hand, a double-click inside the area copies and closes.</summary>
    private void OnQuickDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            _quickDepth = _history.Depth;
            return;
        }

        if (e.ClickCount != 2 || !App.Config.QuickDoubleClickCopy || !_hasSelection || _closed || _finishing ||
            _tool == AnnotationTool.Text || // the text tool's double-click makes a new text
            e.OriginalSource is not DependencyObject source || PenEyedropper.AltDown ||
            !_selection.Contains(ToPixels(e.GetPosition(InputLayer))))
        {
            return;
        }

        // On the canvas only (not the bar, not the size label); the empty box the first click just opened counts as canvas.
        if (!ReferenceEquals(source, InputLayer) && !(IsInside(source, TextEditor) && TextEditor.Text.Length == 0))
        {
            return;
        }

        QuickCopyAndClose();
        e.Handled = true;
    }

    /// <summary>Drops what the double-click's first click drew, copies, closes.</summary>
    private void QuickCopyAndClose()
    {
        _textEditor.Commit();
        DropPolyline();
        while (_history.Depth > _quickDepth && _history.CanUndo)
        {
            _history.Undo(Editor.Items);
        }

        _dragging = false;
        _mode = DragMode.None;
        InputLayer.ReleaseMouseCapture();
        ClearAnnotationSelection();
        CopyAndClose();
    }

    /// <summary>Esc or a right click in the capture: out at once (默认), or step back first (先放下工具 / 取消选中、再取消选区，再退出).</summary>
    private void HandleEscape()
    {
        if (App.Config.EscExitsCapture)
        {
            CancelCapture(); // one Esc: out (the setting can make it step back first)
        }
        else if (_selectedAnnotation != null)
        {
            ClearAnnotationSelection();
        }
        else if (_tool != AnnotationTool.None && _tool != AnnotationTool.Select)
        {
            SetTool(AnnotationTool.Select);
        }
        else if (_hasSelection)
        {
            ResetCapture(); // the area and what was drawn in it: choose again
        }
        else
        {
            CancelCapture();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_finishing)
        {
            e.Handled = true;
            return;
        }

        if (_closed || _textEditor.IsActive || _editingWatermark != null || WatermarkEditor.IsKeyboardFocusWithin ||
            SizeBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var key = AnnotationOps.RealKey(e);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (CaptureKeyDown(key, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        if (_polyline != null)
        {
            e.Handled = PolylineKey(key);
            return;
        }

        // While a selected element is being dragged, E rotates and R scales it;
        // every other key belongs to the drag and must not switch tools.
        if (_draggingAnnotation || _resizingAnnotation || _rotatingAnnotation || _textPress != null)
        {
            if (key == Key.E && _draggingAnnotation && !_dragRotate && _selectedAnnotation != null)
            {
                var bounds = _selectedAnnotation.Bounds;
                _annotationCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                _annotationStartRotation = _selectedAnnotation.Rotation;
                _annotationRotateStartAngle = Math.Atan2(_lastAnnotationPoint.Y - _annotationCenter.Y,
                    _lastAnnotationPoint.X - _annotationCenter.X) * 180 / Math.PI;
                _dragScale = false;
                _dragRotate = true;
            }
            else if (key == Key.R && _draggingAnnotation && !_dragScale && _selectedAnnotation != null)
            {
                _dragScaleBounds = _selectedAnnotation.Bounds;
                _dragScalePointer = _lastAnnotationPoint;
                _dragRotate = false;
                _dragScale = true;
            }

            if (!AnnotationOps.IsModifier(key))
            {
                e.Handled = true;
            }

            return;
        }

        // While drawing: Space moves the element, E rotates it about its start
        // point, Shift/Ctrl refresh the constrained shape immediately.
        if (_gesture != null)
        {
            if (key == Key.Space)
            {
                _gesture.BeginMove();
                InputLayer.Cursor = Cursors.SizeAll;
            }
            else if (key == Key.E)
            {
                _gesture.BeginRotate();
            }
            else if (AnnotationOps.IsModifier(key))
            {
                _gesture.Move(_gesture.LastPointer, shift, control);
                Editor.InvalidateVisual();
                return;
            }

            e.Handled = true;
            return;
        }

        // Space while drawing or resizing the area: the whole area moves with the mouse; released, the drag goes on.
        if (key == Key.Space && _dragging && _mode is DragMode.Creating or DragMode.Resizing && !_spaceDrag)
        {
            _spaceDrag = true;
            var cursor = Native.CursorPosition();
            _spaceAnchor = new Point(cursor.X - _bounds.X, cursor.Y - _bounds.Y);
            _spaceSelectionOrigin = _selection;
            _spaceDragStartOrigin = _dragStart;
            InputLayer.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (!control && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            ToolHotkeys.TryMap(key, App.Config, out var toolShortcut))
        {
            SetTool(Toolbar.ChoiceFor(toolShortcut));
            e.Handled = true;
            return;
        }

        if (key == Key.Tab && !control && _hasSelection)
        {
            // Tab: the other style of the current button (or of the selected element).
            var current = _selectedAnnotation != null && _tool is AnnotationTool.None or AnnotationTool.Select
                ? AnnotationOps.ToolOf(_selectedAnnotation)
                : _tool;
            if (AnnotationOps.NextVariant(current) is { } next)
            {
                Toolbar.PickVariant(next);
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.C && _selectedAnnotation != null && AnnotationClipboard.CanCopy(_selectedAnnotation))
        {
            AnnotationClipboard.Copy(_selectedAnnotation);
            e.Handled = true;
            return;
        }

        if (control && key == Key.V && AnnotationClipboard.HasContent && _hasSelection)
        {
            PasteAnnotation();
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back && _selectedAnnotation == null && _tool == AnnotationTool.Watermark &&
            AnnotationOps.FindWatermark(Editor.Items) is { } watermarkToDelete)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(watermarkToDelete);
            Editor.HoverWatermark = null;
            SetTool(AnnotationTool.Select);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (key is Key.LeftShift or Key.RightShift)
        {
            _hexColor = !_hexColor;
            Magnifier.HexFormat = _hexColor;
            if (Magnifier.Visibility == Visibility.Visible)
            {
                Magnifier.InvalidateVisual();
            }
        }

        if (shift && key >= Key.D1 && key <= Key.D9)
        {
            SetTool(key switch
            {
                Key.D1 => AnnotationTool.Rectangle,
                Key.D2 => AnnotationTool.Line,
                Key.D3 => AnnotationTool.Arrow,
                Key.D4 => AnnotationTool.Number,
                Key.D5 => AnnotationTool.Pen,
                Key.D6 => AnnotationTool.Highlighter,
                Key.D7 => AnnotationTool.Mosaic,
                Key.D8 => AnnotationTool.Text,
                Key.D9 => AnnotationTool.Eraser,
                _ => AnnotationTool.Select
            });
            e.Handled = true;
            return;
        }

        if (!control && key == Key.C)
        {
            CopyCurrentColor();
            e.Handled = true;
            return;
        }

        if (!control && key == Key.R)
        {
            ApplyRecentRegion(!shift);
            e.Handled = true;
            return;
        }

        if (!control && key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            HandleEscape();
            e.Handled = true;
            return;
        }

        if (control && key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && (key == Key.Y || (shift && key == Key.Z)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (!_hasSelection)
        {
            return;
        }

        if (control && key == Key.B && _selectedAnnotation is TextAnnotation)
        {
            Toolbar.ToggleBold();
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation is TextAnnotation enterText && key == Key.F2)
        {
            BeginTextEdit(enterText, isNew: false, caretAt: null, selectAll: true);
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation != null && key is Key.Delete or Key.Back)
        {
            RemoveSelectedAnnotation();
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation != null &&
            key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = key == Key.Left ? -amount : key == Key.Right ? amount : 0;
            var dy = key == Key.Up ? -amount : key == Key.Down ? amount : 0;
            _history.CheckpointCoalesced(Editor.Items, ("nudge", _selectedAnnotation));
            _selectedAnnotation.Translate(dx, dy);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = shift ? 10.0 : 1.0;
            var selection = _selection;
            switch (key)
            {
                case Key.Left:
                    selection.X = Math.Max(0, selection.X - step);
                    break;
                case Key.Right:
                    selection.X = Math.Min(_screen.PixelWidth - selection.Width, selection.X + step);
                    break;
                case Key.Up:
                    selection.Y = Math.Max(0, selection.Y - step);
                    break;
                case Key.Down:
                    selection.Y = Math.Min(_screen.PixelHeight - selection.Height, selection.Y + step);
                    break;
            }

            _selection = selection;
            UpdateSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.C)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.S)
        {
            if (shift)
            {
                QuickSaveAndClose();
            }
            else
            {
                SaveAndClose();
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.A)
        {
            _selection = new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight);
            _hasSelection = true;
            ApplyRememberedCorner();
            UpdateSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (key == Key.F3)
        {
            PinAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.Q)
        {
            TranslateAndClose();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.O:
                    SetTool(AnnotationTool.Ellipse);
                    e.Handled = true;
                    break;
                case Key.A:
                    SetTool(AnnotationTool.Arrow);
                    e.Handled = true;
                    break;
                case Key.L:
                    SetTool(AnnotationTool.Line);
                    e.Handled = true;
                    break;
                case Key.E:
                    SetTool(AnnotationTool.Eraser);
                    e.Handled = true;
                    break;
                case Key.P:
                    SetTool(AnnotationTool.Pen);
                    e.Handled = true;
                    break;
                case Key.H:
                    SetTool(AnnotationTool.Highlighter);
                    e.Handled = true;
                    break;
                case Key.M:
                    SetTool(AnnotationTool.Mosaic);
                    e.Handled = true;
                    break;
                case Key.T:
                    SetTool(AnnotationTool.Text);
                    e.Handled = true;
                    break;
                case Key.N:
                    SetTool(AnnotationTool.Number);
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = AnnotationOps.RealKey(e);
        if (!_closed && CaptureKeyUp(key))
        {
            e.Handled = true;
            return;
        }

        var pointer = ToPixels(Mouse.GetPosition(InputLayer));

        if (_gesture != null)
        {
            if (key == Key.Space)
            {
                _gesture.EndMove();
                InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, 1, _numberCounter);
            }
            else if (key == Key.E)
            {
                _gesture.EndRotate();
            }
            else if (AnnotationOps.IsModifier(key))
            {
                _gesture.Move(_gesture.LastPointer, IsShiftDown, IsCtrlDown);
                Editor.InvalidateVisual();
            }

            e.Handled = !AnnotationOps.IsModifier(key);
            return;
        }

        if (key == Key.E && _dragRotate)
        {
            // Continue moving from here without a jump.
            _dragRotate = false;
            _lastAnnotationPoint = pointer;
            e.Handled = true;
        }

        if (key == Key.R && _dragScale)
        {
            _dragScale = false;
            _lastAnnotationPoint = pointer;
            e.Handled = true;
        }

        if (key == Key.Space && _spaceDrag)
        {
            _spaceDrag = false;
            if (_mode == DragMode.Resizing)
            {
                // The grabbed edge / corner moved with the mouse: resizing goes on from here.
                _dragOrigin = _selection;
            }

            e.Handled = true;
        }
    }

    private void UpdateCursor(Point point)
    {
        if (_textEditor.FrameCursorAt(point) is { } frameCursor)
        {
            InputLayer.Cursor = frameCursor; // frame of the text being typed
            return;
        }

        if (_hasSelection)
        {
            var frameHandle = HitHandle(point);
            if (frameHandle >= 0)
            {
                InputLayer.Cursor = frameHandle switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    3 or 7 => Cursors.SizeWE,
                    _ => Cursors.SizeAll
                };
                return;
            }
        }

        if (_selectedAnnotation == null && HitCornerDot(point) is var roundDot and >= 0)
        {
            InputLayer.Cursor = roundDot is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW; // the rounding dots
            return;
        }

        // The frame of the selected element (any tool in hand): its handles resize, its edge rotates.
        if (_selectedAnnotation != null && _tool is not (AnnotationTool.Select or AnnotationTool.None) && UpdateAnnotationTransformCursor(point))
        {
            return;
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            if (_selectedAnnotation != null && UpdateAnnotationTransformCursor(point))
            {
                return;
            }

            if (HitAnnotation(point) != null)
            {
                InputLayer.Cursor = Cursors.SizeAll;
                return;
            }

            var selectedHandle = HitHandle(point);
            if (selectedHandle >= 0)
            {
                InputLayer.Cursor = selectedHandle switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    3 or 7 => Cursors.SizeWE,
                    _ => Cursors.SizeAll
                };
                return;
            }

            InputLayer.Cursor = _hasSelection && _selection.Contains(point)
                ? Cursors.SizeAll
                : _hasSelection ? Cursors.Arrow : Cursors.Cross;
            return;
        }

        if (_tool != AnnotationTool.None && _hasSelection && _selection.Contains(point))
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, 1, _numberCounter);
            return;
        }

        if (HitCornerDot(point) is var cornerDot and >= 0)
        {
            InputLayer.Cursor = cornerDot is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return;
        }

        var handle = HitHandle(point);
        InputLayer.Cursor = handle switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => _hasSelection && _selection.Contains(point) ? Cursors.SizeAll : Cursors.Cross
        };
    }

    private void AdjustActiveToolByWheel(int delta)
    {
        var step = Math.Sign(delta) * (_tool is AnnotationTool.Text or AnnotationTool.Number ? 2 : 1);
        var value = _tool switch
        {
            AnnotationTool.Text => Math.Clamp(_thickness + step, 8, 64),
            AnnotationTool.Number => Math.Clamp(_thickness + step, 8, 40),
            AnnotationTool.MosaicBrush => Math.Clamp(_thickness + step * 2, 4, 80),
            AnnotationTool.Watermark => Math.Clamp(_thickness + step * 2, 10, 120),
            AnnotationTool.Eraser or AnnotationTool.Cutout => Math.Clamp(_thickness + step * 2, 4, 80),
            _ => Math.Clamp(Math.Round(_thickness) + step, 1, 16) // one notch = one size (it was 0.5: 2–3 notches per visible step)
        };

        _thickness = value;
        Toolbar.SetThickness(value);
        UpdateCursorToPointer();
    }

    private void AdjustSelectedByWheel(int delta, bool opacity)
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, (opacity ? "opacity" : "wheel", _selectedAnnotation));
        UpdateUndoButtons();
        if (opacity)
        {
            if (_selectedAnnotation is WatermarkAnnotation)
            {
                _selectedAnnotation.Opacity = Math.Clamp(Math.Round(_selectedAnnotation.Opacity + Math.Sign(delta) * 0.05, 2), 0.05, 1);
                Toolbar.SyncFrom(_selectedAnnotation);
            }
            else
            {
                _selectedAnnotation.Opacity = AnnotationOps.WheelOpacity(_selectedAnnotation.Opacity, delta);
                Toolbar.SetOpacity(_selectedAnnotation.Opacity);
            }
        }
        else if (AnnotationOps.WheelSize(_selectedAnnotation, delta, EffectiveScreen) is { } size)
        {
            _thickness = size;
            Toolbar.SetThickness(size);
        }
        else
        {
            Toolbar.SyncFrom(_selectedAnnotation);
        }

        Editor.InvalidateVisual();
    }

    private static bool IsShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static bool IsCtrlDown => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    /// <summary>A cut-out being drawn is cancelled (right click / Esc): the hole goes again.</summary>
    private void DropCutout()
    {
        if (_erasing && _eraserPath is CutoutAnnotation)
        {
            _history.CancelGesture(Editor.Items);
            UpdateUndoButtons();
        }
    }
}
