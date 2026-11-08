using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Capture window: annotation tools, selection of elements, text / watermark editing, undo.
public partial class OverlayWindow
{
    private void OnToolbarToolChanged(AnnotationTool tool)
    {
        if (tool == AnnotationTool.Spotlight && Editor.Items.OfType<SpotlightAnnotation>().FirstOrDefault() is { } existingSpot)
        {
            Toolbar.ShowSpotlightDarkness(existingSpot.Darkness); // one darkness for the picture
        }

        _textEditor.Commit();
        _watermarkOff = false; // choosing the watermark tool means: show one
        _tool = tool;
        _thickness = Toolbar.Thickness;
        ClearAnnotationSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        ActivateWatermarkIfNeeded();
        UpdateCursorToPointer();
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        // A selected element of that button switches to the picked style.
        if (_selectedAnnotation != null && AnnotationOps.CanConvert(_selectedAnnotation, tool))
        {
            ConvertSelected(tool);
            return;
        }

        if (!AnnotationOps.SameVariantFamily(_tool, tool))
        {
            OnToolbarToolChanged(tool);
            return;
        }

        _tool = tool;
        _thickness = Toolbar.Thickness;
        ActivateWatermarkIfNeeded();
        UpdateCursorToPointer();
    }

    /// <summary>The watermark tool works on the canvas-wide watermark, creating it on first use.</summary>
    private void ActivateWatermarkIfNeeded()
    {
        if (_tool != AnnotationTool.Watermark || !_hasSelection)
        {
            Editor.HoverWatermark = null;
            return;
        }

        if (_watermarkOff)
        {
            Toolbar.SetWatermarkOn(AnnotationOps.FindWatermark(Editor.Items) != null);
            return;
        }

        var watermark = AnnotationOps.EnsureWatermark(Editor.Items, SnapRect(_selection), Toolbar, _history);
        Toolbar.SyncFrom(watermark);
        Toolbar.SetWatermarkOn(true);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private bool _watermarkOff;

    /// <summary>The watermark switch: off takes the watermark away (undoable), on brings one back.</summary>
    private void OnWatermarkToggled(bool on)
    {
        _watermarkOff = !on;
        if (!on && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(watermark);
            Editor.HoverWatermark = null;
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        ActivateWatermarkIfNeeded();
    }

    private void ApplyToolbarSettings()
    {
        _color = Toolbar.Color;
        _thickness = Toolbar.Thickness;
        _fill = Toolbar.FillEnabled;
        _corner = Toolbar.CornerRadius;
        UpdateCursorToPointer();

        if (_textEditor.Target is { } editing)
        {
            AnnotationStyler.Apply(editing, Toolbar, EffectiveScreen);
            _textEditor.MarkDirty();
            _textEditor.Refresh();
            return;
        }

        if (_tool == AnnotationTool.Watermark && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.CheckpointCoalesced(Editor.Items, watermark);
            AnnotationStyler.Apply(watermark, Toolbar, EffectiveScreen);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, _selectedAnnotation);
        AnnotationStyler.Apply(_selectedAnnotation, Toolbar, EffectiveScreen);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void UpdateCursorToPointer()
    {
        if (_closed || _scale <= 0)
        {
            return;
        }

        UpdateCursor(ToPixels(Mouse.GetPosition(InputLayer)));
    }

    private Annotation? HitAnnotation(Point point) =>
        AnnotationOps.HitTest(Editor.Items, point, 7 * _scale, byContent: _tool is AnnotationTool.None or AnnotationTool.Select);

    private double EraserWidth => Math.Max(2, _thickness);

    private void UpdateWatermarkHover(Point point)
    {
        var watermark = AnnotationOps.FindWatermark(Editor.Items);
        var frameCursor = FrameCursorAt(point);
        var hover = frameCursor == null && watermark != null && watermark.HitTest(point, 2 * _scale) ? watermark : null;
        InputLayer.Cursor = frameCursor ?? (hover != null ? Cursors.Hand : Cursors.Arrow);
        if (!ReferenceEquals(Editor.HoverWatermark, hover))
        {
            Editor.HoverWatermark = hover;
            Editor.InvalidateVisual();
        }
    }

    private void SelectAnnotation(Annotation annotation)
    {
        _selectedAnnotation = annotation;
        _tool = AnnotationTool.Select;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(annotation);
        Toolbar.SetColor(annotation.Color);
        var size = annotation switch
        {
            TextAnnotation text => text.FontSize,
            WatermarkAnnotation watermark => watermark.FontSize,
            NumberAnnotation number => number.Radius,
            _ => annotation.Thickness
        };
        Toolbar.SetThickness(size);
        Toolbar.SetFill(annotation.Fill);
        Toolbar.SetCorner(annotation.CornerRadius);
        if (annotation is TextAnnotation selectedText)
        {
            Toolbar.SetStroke(selectedText.StrokeThickness > 0, selectedText.StrokeColor, selectedText.StrokeThickness);
        }

        _color = annotation.Color;
        _thickness = size;
        _fill = annotation.Fill;
        _corner = annotation.CornerRadius;
        Editor.Selected = annotation;
        Editor.InvalidateVisual();
    }

    private void PasteAnnotation()
    {
        if (AnnotationClipboard.Paste(SnapRect(_selection)) is not { } pasted)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        if (pasted is MosaicAnnotation mosaic)
        {
            MosaicBuilder.Rebuild(mosaic, EffectiveScreen);
        }

        Editor.Items.Add(pasted);
        if (pasted is NumberAnnotation)
        {
            _numberCounter++;
        }

        UpdateUndoButtons();
        SelectQuietly(pasted);
    }

    private void ConvertSelected(AnnotationTool variant)
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        var old = _selectedAnnotation;
        Annotation? replacement = null;

        switch (old, variant)
        {
            case (RectAnnotation rect, AnnotationTool.Ellipse):
                replacement = new EllipseAnnotation
                {
                    Rect = rect.Rect, Color = rect.Color, Thickness = rect.Thickness, Fill = rect.Fill
                };
                break;
            case (EllipseAnnotation ellipse, AnnotationTool.Rectangle):
                replacement = new RectAnnotation
                {
                    Rect = ellipse.Rect, Color = ellipse.Color, Thickness = ellipse.Thickness,
                    Fill = ellipse.Fill, CornerRadius = _corner
                };
                break;
            case (LineAnnotation line, AnnotationTool.Arrow):
                replacement = new ArrowAnnotation
                {
                    Start = line.Start, End = line.End, Color = line.Color, Thickness = line.Thickness, Style = Toolbar.ArrowStyle
                };
                break;
            case (ArrowAnnotation arrow, AnnotationTool.Line):
                replacement = new LineAnnotation
                {
                    Start = arrow.Start, End = arrow.End, Color = arrow.Color, Thickness = arrow.Thickness
                };
                break;
            case (PenAnnotation pen, AnnotationTool.Pen or AnnotationTool.Highlighter):
                _history.Checkpoint(Editor.Items);
                UpdateUndoButtons();
                pen.Highlight = variant == AnnotationTool.Highlighter;
                Editor.InvalidateVisual();
                return;
        }

        if (replacement == null)
        {
            return;
        }

        replacement.Fill = _fill;
        replacement.CornerRadius = _corner;
        replacement.Opacity = old.Opacity;
        replacement.Rotation = old.Rotation;
        var index = Editor.Items.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        UpdateUndoButtons();

        Editor.Items[index] = replacement;
        if (_tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            // Drawing with the button: keep drawing in the new style, the element stays selected.
            _tool = variant;
            Toolbar.SetTool(variant);
            SelectQuietly(replacement);
            _freshSelection = replacement;
            UpdateCursorToPointer();
            return;
        }

        _selectedAnnotation = replacement;
        Editor.Selected = replacement;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(replacement);
        Toolbar.SetColor(replacement.Color);
        Toolbar.SetThickness(replacement.Thickness);
        Toolbar.SetFill(replacement.Fill);
        Toolbar.SetCorner(replacement.CornerRadius);
        Editor.InvalidateVisual();
    }

    private bool TryBeginAnnotationTransform(Point point)
    {
        if (_selectedAnnotation == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selectedAnnotation, point, _scale);
        var bounds = _selectedAnnotation.Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        _annotationCenter = center;
        _annotationStartBounds = bounds;
        _lastAnnotationPoint = point;
        _transformPress = point;
        _transformStarted = false;

        if (hit.IsHandle)
        {
            _resizingAnnotation = true;
            _annotationHandle = hit.Handle;
            _resizeGrab = AnnotationOps.ResizeGrab(bounds, _selectedAnnotation.Rotation, hit.Handle, point);
            return true;
        }

        if (hit.Rotate)
        {
            _rotatingAnnotation = true;
            _annotationStartRotation = _selectedAnnotation.Rotation;
            _annotationRotateStartAngle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
            return true;
        }

        if (hit.Inside && AnnotationOps.InsideMoves(_selectedAnnotation, _tool, point, 7 * _scale))
        {
            _draggingAnnotation = true;
            return true;
        }

        return false;
    }

    private void ResizeAnnotation(Annotation annotation, Rect target) => AnnotationOps.Resize(annotation, target, 4);

    private void ClearAnnotationSelection()
    {
        _freshSelection = null;
        if (_selectedAnnotation == null && Editor.Selected == null)
        {
            return;
        }

        _selectedAnnotation = null;
        Editor.Selected = null;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void RemoveSelectedAnnotation()
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.Remove(_selectedAnnotation);
        _selectedAnnotation = null;
        Editor.Selected = null;
        Toolbar?.SetSelection(null);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void StartAnnotation(Point point)
    {
        _mode = DragMode.None;
        ClearAnnotationSelection();
        _active = AnnotationOps.Create(_tool, point, Toolbar, _numberCounter, EffectiveScreen);
        if (_active == null)
        {
            return;
        }

        if (_active is PolylineAnnotation polyline)
        {
            // Placed click by click, not in one drag.
            _active = null;
            _polyline = new PolylineDraft(polyline, point);
            Editor.Preview = polyline;
            Editor.InvalidateVisual();
            return;
        }

        _gesture = new CreationGesture(_active, point);
        _gesture.Apply();
        Editor.Preview = _active;
        Editor.InvalidateVisual();
    }

    private void CommitAnnotation()
    {
        var annotation = _active;
        _active = null;
        _gesture = null;
        Editor.Preview = null;

        if (annotation == null || AnnotationOps.IsTooSmall(annotation))
        {
            Editor.InvalidateVisual();
            return;
        }

        if (annotation is NumberAnnotation)
        {
            _numberCounter++;
            InputLayer.Cursor = ToolCursors.Get(AnnotationTool.Number, _thickness, _color, 1, _numberCounter); // the next number, at once
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.Add(annotation);
        UpdateUndoButtons();
        // The new element stays selected (wheel / Ctrl+wheel / frame adjust it right away);
        // freehand strokes don't — the next stroke usually follows immediately.
        if (annotation is not PenAnnotation and not MosaicBrushAnnotation)
        {
            SelectQuietly(annotation);
            _freshSelection = annotation;
        }
        else
        {
            Editor.InvalidateVisual();
        }
    }

    /// <summary>The selection is the element just created (see AnnotationOps.SelectionHit).</summary>
    private bool IsFreshSelection => _freshSelection != null && ReferenceEquals(_freshSelection, _selectedAnnotation);

    private void BeginTextEdit(TextAnnotation text, bool isNew, Point? caretAt, bool selectAll = false)
    {
        ClearAnnotationSelection();
        ClearAnnotationHover();
        _history.Arm(Editor.Items);
        if (isNew)
        {
            Editor.Items.Add(text);
        }

        _tool = AnnotationTool.Text;
        Toolbar.SetTool(AnnotationTool.Text);
        Toolbar.SyncFrom(text);
        _textEditor.Begin(text, isNew, caretAt, selectAll);
    }

    private void OnTextEditEnded(TextAnnotation text, bool removed)
    {
        if (_textEditor.Dirty && !(removed && _textEditor.IsNew))
        {
            _history.CommitArmed();
        }
        else
        {
            _history.Disarm();
        }

        UpdateUndoButtons();
        if (!removed && Editor.Items.Contains(text))
        {
            // Keep the finished text selected so its frame can be adjusted right away.
            SelectQuietly(text);
        }

        if (!_closed)
        {
            Focus();
            FocusWindow();
            UpdateCursorToPointer();
        }
    }

    /// <summary>Selects an element without switching tools (the text tool keeps working).</summary>
    private void SelectQuietly(Annotation annotation)
    {
        _selectedAnnotation = annotation;
        Editor.Selected = annotation;
        Toolbar.SyncFrom(annotation);
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void BeginWatermarkEdit(WatermarkAnnotation watermark)
    {
        _editingWatermark = watermark;
        WatermarkEditor.Text = watermark.Text;
        WatermarkEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(WatermarkEditor, watermark.Rect.X / _scale + 4);
        Canvas.SetTop(WatermarkEditor, watermark.Rect.Y / _scale + 4);
        WatermarkEditor.Focus();
        Keyboard.Focus(WatermarkEditor);
        WatermarkEditor.SelectAll();
    }

    private void CommitWatermark()
    {
        if (_editingWatermark is not { } watermark)
        {
            return;
        }

        _editingWatermark = null;
        var text = WatermarkEditor.Text.Trim();
        WatermarkEditor.Visibility = Visibility.Collapsed;
        if (text.Length > 0 && text != watermark.Text)
        {
            _history.Checkpoint(Editor.Items);
            watermark.Text = text;
            UpdateUndoButtons();
            Editor.InvalidateVisual();
        }

        if (!_closed)
        {
            Focus();
            FocusWindow();
        }
    }

    private void CancelWatermark()
    {
        _editingWatermark = null;
        WatermarkEditor.Visibility = Visibility.Collapsed;
        Focus();
        FocusWindow();
    }

    private void WatermarkEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelWatermark();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitWatermark();
            e.Handled = true;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
        FinishPolyline();
        _textEditor.Commit();
        _tool = tool;
        Toolbar.SetTool(tool);
        if (tool != AnnotationTool.Select)
        {
            ClearAnnotationSelection();
        }
        else if (_selectedAnnotation != null)
        {
            Toolbar.SetSelection(_selectedAnnotation);
        }

        _thickness = Toolbar.Thickness;
        ActivateWatermarkIfNeeded();
        InputLayer.Cursor = ToolCursors.Get(tool, _thickness, _color, 1, _numberCounter);
    }

    private void UpdateAnnotationHover(Point point)
    {
        // The frame of the text being typed / of the selected element comes first (resize / rotate arrows).
        if (_textEditor.FrameCursorAt(point) is { } typingFrame)
        {
            ClearAnnotationHover();
            InputLayer.Cursor = typingFrame;
            return;
        }

        if (_selectedAnnotation != null && UpdateAnnotationTransformCursor(point))
        {
            ClearAnnotationHover();
            return;
        }

        if (FrameCursorAt(point) is { } frameCursor)
        {
            // On the capture's edge: it resizes the capture (with any tool), the arrows say so.
            ClearAnnotationHover();
            InputLayer.Cursor = frameCursor;
            return;
        }

        var hit = HitAnnotation(point);
        if (hit != null && SameFamily(hit, _tool))
        {
            var bounds = hit.Bounds;
            bounds.Inflate(4 / _scale, 4 / _scale);
            Editor.HoverBounds = bounds;
            Editor.HoverLabel = _tool == AnnotationTool.Text ? "单击编辑 · 拖动移动" : "点击选中并继续编辑";
            Editor.ShowHover = true;
            InputLayer.Cursor = _tool switch
            {
                AnnotationTool.Text => Cursors.IBeam,
                AnnotationTool.Number => ToolCursors.Get(AnnotationTool.Number, _thickness, _color, 1, _numberCounter), // the badge stays the pointer
                _ => Cursors.Hand
            };
            Editor.InvalidateVisual();
            return;
        }

        ClearAnnotationHover();
        // Outside the area the plain arrow (setting the tool's cursor here and the arrow right after made it flicker).
        InputLayer.Cursor = _hasSelection && !_selection.Contains(point)
            ? Cursors.Arrow
            : ToolCursors.Get(_tool, _thickness, _color, 1, _numberCounter);
    }

    private void ClearAnnotationHover()
    {
        if (!Editor.ShowHover && Editor.HoverBounds == Rect.Empty)
        {
            return;
        }

        Editor.ShowHover = false;
        Editor.HoverBounds = Rect.Empty;
        Editor.HoverLabel = null;
        Editor.InvalidateVisual();
    }

    private static bool SameFamily(Annotation annotation, AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle or AnnotationTool.Ellipse =>
            annotation is RectAnnotation or EllipseAnnotation,
        AnnotationTool.Line or AnnotationTool.Arrow =>
            annotation is LineAnnotation or ArrowAnnotation,
        AnnotationTool.Polyline => annotation is PolylineAnnotation,
        AnnotationTool.Pen or AnnotationTool.Highlighter => false,
        AnnotationTool.Mosaic or AnnotationTool.Watermark =>
            annotation is MosaicAnnotation or WatermarkAnnotation,
        AnnotationTool.Text => annotation is TextAnnotation,
        AnnotationTool.Number => annotation is NumberAnnotation,
        AnnotationTool.Spotlight => annotation is SpotlightAnnotation,
        _ => false
    };

    private bool UpdateAnnotationTransformCursor(Point point)
    {
        if (_selectedAnnotation == null)
        {
            return false;
        }

        var hit = AnnotationOps.SelectionHit(_selectedAnnotation, IsFreshSelection, _tool, point, _scale);
        if (hit.IsHandle)
        {
            InputLayer.Cursor = AnnotationEditor.ResizeCursor(hit.Handle, _selectedAnnotation.Rotation);
            return true;
        }

        if (hit.Rotate)
        {
            InputLayer.Cursor = ToolCursors.GetRotationCursor(true);
            return true;
        }

        if (hit.Inside && _tool is AnnotationTool.None or AnnotationTool.Select)
        {
            InputLayer.Cursor = Cursors.SizeAll;
            return true;
        }

        return false;
    }

    private void UpdateUndoButtons()
    {
        Toolbar.RefreshUndo(_history.CanUndo, _history.CanRedo);
        Toolbar.SetCanClear(Editor.Items.Count > 0);
    }

    /// <summary>清空批注: every annotation goes (one undo step).</summary>
    private void ClearAnnotations()
    {
        _textEditor.Commit();
        if (Editor.Items.Count == 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        ClearAnnotationSelection();
        Editor.Items.Clear();
        _numberCounter = 1;
        if (_tool == AnnotationTool.Number)
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, 1, _numberCounter); // shows 1 again
        }

        UpdateUndoButtons();
        Editor.InvalidateVisual();
        FocusWindow();
    }

    private void Undo() => ApplyHistory(_history.Undo);

    private void Redo() => ApplyHistory(_history.Redo);

    private void ApplyHistory(Func<List<Annotation>, bool> step)
    {
        _textEditor.Commit();
        ClearAnnotationSelection();
        if (!step(Editor.Items))
        {
            return;
        }

        // Restored items are copies; references held for the selection are stale.
        _numberCounter = Editor.Items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();


    /// <summary>折线 done (double-click, Enter, right click, another tool): it becomes an element.</summary>
    private void FinishPolyline()
    {
        if (_polyline is not { } draft)
        {
            return;
        }

        _polyline = null;
        InputLayer.ReleaseMouseCapture();
        _active = draft.Finish() ? draft.Target : null;
        CommitAnnotation();
    }

    private void DropPolyline()
    {
        _polyline = null;
        Editor.Preview = null;
        InputLayer.ReleaseMouseCapture();
        Editor.InvalidateVisual();
    }

    /// <summary>Keys while a polyline is being placed: Enter ends it, Esc drops it, Backspace takes a corner back.</summary>
    private bool PolylineKey(Key key)
    {
        if (_polyline == null)
        {
            return false;
        }

        if (key == Key.Enter)
        {
            FinishPolyline();
        }
        else if (key == Key.Escape || (key == Key.Back && !_polyline.RemoveLast()))
        {
            DropPolyline();
        }

        Editor.InvalidateVisual();
        return !AnnotationOps.IsModifier(key);
    }

    private void CommitPendingText()
    {
        FinishPolyline();
        _textEditor.Commit();
        CommitWatermark();
    }
}
