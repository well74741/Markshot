using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Pin window: editing (annotation tools, input, selection, text / watermark, undo).
public partial class PinWindow
{
    private void InitToolbar()
    {
        Toolbar.SetMode(IsEditor ? AnnotationToolbarMode.Editor : AnnotationToolbarMode.Pin);
        if (!IsEditor)
        {
            Toolbar.EditRequested += OpenInEditor;
        }

        Toolbar.ToolChanged += OnToolbarToolChanged;
        Toolbar.WatermarkToggled += OnWatermarkToggled;
        Toolbar.VariantChanged += OnToolbarVariantChanged;
        Toolbar.SettingsChanged += ApplyToolbarSettings;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.CopyRequested += Copy;
        Toolbar.SaveRequested += Save;
        Toolbar.TranslateRequested += Translate;
        Toolbar.ApplyRequested += () => ExitEditMode(true);
        Toolbar.RefreshUndo(false, false);
        Toolbar.SizeChanged += (_, _) =>
        {
            if (_editMode)
            {
                Dispatcher.BeginInvoke(new Action(PlaceToolbar), DispatcherPriority.Loaded);
            }
        };

        _tool = AnnotationTool.None;
        Toolbar.SetTool(AnnotationTool.None);
        Toolbar.SetSelection(null);
        InputLayer.IsHitTestVisible = false;
        InputLayer.Cursor = Cursors.Arrow;
    }

    private void OnToolbarToolChanged(AnnotationTool tool)
    {
        _textEditor.Commit();
        _watermarkOff = false; // choosing the watermark tool means: show one
        _tool = tool;
        _thickness = Toolbar.Thickness;
        ClearSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ActivateWatermarkIfNeeded();
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        // A selected element of that button switches to the picked style.
        if (_selected != null && AnnotationOps.CanConvert(_selected, tool))
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
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ClearAnnotationHover();
        ActivateWatermarkIfNeeded();
    }

    /// <summary>The watermark tool works on the canvas-wide watermark, creating it on first use.</summary>
    private void ActivateWatermarkIfNeeded()
    {
        if (_tool != AnnotationTool.Watermark)
        {
            Editor.HoverWatermark = null;
            return;
        }

        var canvas = new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight);
        if (_watermarkOff)
        {
            Toolbar.SetWatermarkOn(AnnotationOps.FindWatermark(Editor.Items) != null);
            return;
        }

        var watermark = AnnotationOps.EnsureWatermark(Editor.Items, canvas, Toolbar, _history);
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
        if (_tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
        }

        if (_textEditor.Target is { } editing)
        {
            AnnotationStyler.Apply(editing, Toolbar, _baseImage);
            _textEditor.MarkDirty();
            _textEditor.Refresh();
            return;
        }

        if (_tool == AnnotationTool.Watermark && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.CheckpointCoalesced(Editor.Items, watermark);
            AnnotationStyler.Apply(watermark, Toolbar, _baseImage);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        if (_selected == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, _selected);
        AnnotationStyler.Apply(_selected, Toolbar, _baseImage);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void PasteAnnotation()
    {
        if (AnnotationClipboard.Paste(new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight)) is not { } pasted)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        if (pasted is MosaicAnnotation mosaic)
        {
            MosaicBuilder.Rebuild(mosaic, _baseImage);
        }

        Editor.Items.Add(pasted);
        if (pasted is NumberAnnotation)
        {
            _number++;
        }

        UpdateUndoButtons();
        SelectQuietly(pasted);
    }

    private void ConvertSelected(AnnotationTool variant)
    {
        if (_selected == null)
        {
            return;
        }

        var old = _selected;
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
                    Start = line.Start, End = line.End, Color = line.Color, Thickness = line.Thickness
                };
                break;
            case (ArrowAnnotation arrow, AnnotationTool.Line):
                replacement = new LineAnnotation
                {
                    Start = arrow.Start, End = arrow.End, Color = arrow.Color, Thickness = arrow.Thickness
                };
                break;
            case (PenAnnotation pen, AnnotationTool.Pen):
                pen.Highlight = false;
                SelectAnnotation(pen);
                return;
            case (PenAnnotation pen, AnnotationTool.Highlighter):
                pen.Highlight = true;
                SelectAnnotation(pen);
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
            InputLayer.Cursor = ToolCursors.Get(variant, _thickness, _color, CursorScale);
            SelectQuietly(replacement);
            _freshSelection = replacement;
            return;
        }

        _selected = replacement;
        Editor.Selected = replacement;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(replacement);
        Toolbar.SetColor(replacement.Color);
        Toolbar.SetThickness(replacement.Thickness);
        Toolbar.SetFill(replacement.Fill);
        Toolbar.SetCorner(replacement.CornerRadius);
        Editor.InvalidateVisual();
    }

    private void ToggleEditing()
    {
        if (_editMode)
        {
            ExitEditMode(true);
        }
        else
        {
            EnterEditMode();
        }
    }

    private protected void EnterEditMode()
    {
        if (_editMode || _locked || _clickThrough)
        {
            return;
        }

        _editMode = true;
        TextLayer.ClearSelection();
        TextLayer.Visibility = Visibility.Collapsed;
        _active = null;
        _selected = null;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Source = _baseImage;
        Img.Source = _baseImage;
        Editor.Scale = 1.0 / _zoom;
        Editor.Visibility = Visibility.Visible;
        InputLayer.Visibility = Visibility.Visible;
        // Reset the tool while the toolbar is hidden, so it opens in its final shape.
        SetTool(AnnotationTool.None);
        Toolbar.SetSelection(null);
        if (Motion.IsHiding(Toolbar))
        {
            Motion.PopIn(Toolbar); // still fading out: turn around from where it is
        }

        Toolbar.Visibility = Visibility.Visible;
        ShowToolbarWindow();
        UpdateUndoButtons();
        ApplyBounds();
        InputLayer.Focus();
    }

    /// <summary>编辑: the image editor opens on this pin (image and editable annotations); re-pinning from there replaces it.</summary>
    private void OpenInEditor()
    {
        ExitEditMode(true);
        ((App)Application.Current).OpenEditor(_baseImage, annotations: Editor.Items.Select(item => item.Clone()).ToList(), sourcePin: this);
        _parked = true;
        Hide();
    }

    /// <summary>Back on the screen after the editor it went into was closed.</summary>
    public void ShowAgain()
    {
        _parked = false;
        if (!_fadingOut)
        {
            Show();
            ApplyBounds();
        }
    }

    private void ExitEditMode(bool apply)
    {
        if (!_editMode || IsEditor)
        {
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }

        _textEditor.Commit();
        CommitWatermark();
        if (apply)
        {
            var edited = ComposeEditedImage();
            SourceImage = edited;
            Img.Source = edited;
            _pixelWidth = edited.PixelWidth;
            _pixelHeight = edited.PixelHeight;
        }

        _editMode = false;
        _active = null;
        _selected = null;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Visibility = Visibility.Collapsed;
        InputLayer.Visibility = Visibility.Collapsed;
        Motion.PopOut(Toolbar, completed: () =>
        {
            if (!_editMode)
            {
                _toolbarWindow?.Hide();
            }
        });
        _history.Clear();
        UpdateUndoButtons();
        ApplyBounds();
        TextLayer.Visibility = Visibility.Visible;
        if (apply)
        {
            RefreshTextLayer();
        }
    }

    private protected virtual BitmapSource ComposeEditedImage()
    {
        if (Editor.Items.Count == 0)
        {
            return _baseImage;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Rounded pins keep their corners transparent, annotations included.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight), CornerRadius, CornerRadius));
            dc.DrawImage(_baseImage, new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight));
            foreach (var annotation in AnnotationEditor.DrawOrder(Editor.Items))
            {
                AnnotationEditor.DrawItem(dc, annotation);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(
            _baseImage.PixelWidth, _baseImage.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Rotate / resize / E-rotate / R-scale of the selected element. True when it consumed the move.</summary>
    private bool TransformSelected(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        if ((_rotatingAnnotation || _resizingAnnotation) && !_transformStarted)
        {
            // A click on a handle / grip without a real drag changes nothing.
            if ((point - _transformPress).Length * _zoom < 3)
            {
                return true;
            }

            _transformStarted = true;
        }

        if (_rotatingAnnotation || _resizingAnnotation || (_movingSelected && (_dragRotate || _dragScale)))
        {
            _history.CommitArmed();
            UpdateUndoButtons();
        }

        if (_rotatingAnnotation || (_movingSelected && _dragRotate))
        {
            var angle = Math.Atan2(point.Y - _annotationCenter.Y, point.X - _annotationCenter.X) * 180 / Math.PI;
            var rotation = _annotationStartRotation + angle - _annotationRotateStartAngle;
            if (IsShiftDown)
            {
                rotation = Math.Round(rotation / 15) * 15;
            }

            _selected.Rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
            Editor.InvalidateVisual();
            return true;
        }

        if (_resizingAnnotation)
        {
            var target = AnnotationOps.ResizeFromHandle(
                _annotationStartBounds, _selected.Rotation, _annotationHandle, point + _resizeGrab, IsShiftDown);
            AnnotationOps.Resize(_selected, target, _annotationHandle);
            Editor.InvalidateVisual();
            return true;
        }

        if (_movingSelected && _dragScale)
        {
            var center = new Point(
                _dragScaleBounds.X + _dragScaleBounds.Width / 2,
                _dragScaleBounds.Y + _dragScaleBounds.Height / 2);
            var startDistance = Math.Max(1, (_dragScalePointer - center).Length);
            var factor = Math.Max(0.05, (point - center).Length / startDistance);
            AnnotationOps.Resize(_selected, AnnotationOps.ScaleAround(_dragScaleBounds, center, factor), 4);
            Editor.InvalidateVisual();
            return true;
        }

        return false;
    }

    private void OnEditKeyDown(KeyEventArgs e)
    {
        var key = AnnotationOps.RealKey(e);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (IsEditor && EditorKey(key, control))
        {
            e.Handled = true;
            return;
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // While a selected element is being dragged, E rotates and R scales it;
        // every other key belongs to the drag and must not switch tools.
        if (_movingSelected || _resizingAnnotation || _rotatingAnnotation || _textPress != null)
        {
            if (key == Key.E && _movingSelected && !_dragRotate && _selected != null)
            {
                var bounds = _selected.Bounds;
                _annotationCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                _annotationStartRotation = _selected.Rotation;
                _annotationRotateStartAngle = Math.Atan2(_lastPoint.Y - _annotationCenter.Y,
                    _lastPoint.X - _annotationCenter.X) * 180 / Math.PI;
                _dragScale = false;
                _dragRotate = true;
            }
            else if (key == Key.R && _movingSelected && !_dragScale && _selected != null)
            {
                _dragScaleBounds = _selected.Bounds;
                _dragScalePointer = _lastPoint;
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

        if (!control && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            ToolHotkeys.TryMap(key, App.Config, out var toolShortcut))
        {
            SetTool(Toolbar.ChoiceFor(toolShortcut));
            e.Handled = true;
            return;
        }

        if (key == Key.Tab && !control)
        {
            var current = _selected != null && _tool is AnnotationTool.None or AnnotationTool.Select
                ? AnnotationOps.ToolOf(_selected)
                : _tool;
            if (AnnotationOps.NextVariant(current) is { } next)
            {
                Toolbar.PickVariant(next);
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.C)
        {
            if (_selected != null && AnnotationClipboard.CanCopy(_selected))
            {
                AnnotationClipboard.Copy(_selected);
            }
            else
            {
                ClipboardService.CopyImage(ComposeEditedImage());
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.V && AnnotationClipboard.HasContent)
        {
            PasteAnnotation();
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back && _selected == null && _tool == AnnotationTool.Watermark &&
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

        if (key == Key.Escape)
        {
            if (_selected != null)
            {
                ClearSelection();
            }
            else
            {
                ExitEditMode(true);
            }

            e.Handled = true;
            return;
        }

        if (key == Key.Space)
        {
            _spaceExitGuard = true;

            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (control && key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (control && key == Key.S)
        {
            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (!control && key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
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

        if (control && key == Key.B && _selected is TextAnnotation)
        {
            Toolbar.ToggleBold();
            e.Handled = true;
            return;
        }

        if (_selected != null && key is Key.Delete or Key.Back)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(_selected);
            ClearSelection();
            UpdateUndoButtons();
            e.Handled = true;
            return;
        }

        if (_selected != null && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = key == Key.Left ? -amount : key == Key.Right ? amount : 0;
            var dy = key == Key.Up ? -amount : key == Key.Down ? amount : 0;
            _history.CheckpointCoalesced(Editor.Items, ("nudge", _selected));
            _selected.Translate(dx, dy);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
        }
    }

    private protected void SetTool(AnnotationTool tool)
    {
        _textEditor.Commit();
        _tool = tool;
        Toolbar.SetTool(tool);
        if (tool != AnnotationTool.Select && tool != AnnotationTool.None)
        {
            ClearSelection();
        }
        else if (_selected != null)
        {
            Toolbar.SetSelection(_selected);
        }

        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ClearAnnotationHover();
    }

    private protected void Undo() => ApplyHistory(_history.Undo);

    private protected void Redo() => ApplyHistory(_history.Redo);

    private void ApplyHistory(Func<List<Annotation>, bool> step)
    {
        _textEditor.Commit();
        ClearSelection();
        if (!step(Editor.Items))
        {
            return;
        }

        _number = Editor.Items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private protected void UpdateUndoButtons()
    {
        Toolbar.RefreshUndo(_history.CanUndo, _history.CanRedo);
    }

    private Point ToPixels(Point dip) => new(dip.X / _zoom, dip.Y / _zoom);

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = ToPixels(e.GetPosition(InputLayer));
        if (_textEditor.Target is { } editingText)
        {
            // The frame of the text being typed moves / resizes / rotates it; typing continues.
            if (_textEditor.TryBeginFrameDrag(point))
            {
                e.Handled = true;
                return;
            }

            _textEditor.Commit();

            if (!(_tool == AnnotationTool.Text && HitAnnotation(point) is TextAnnotation other &&
                  !ReferenceEquals(other, editingText)))
            {
                e.Handled = true;
                return;
            }
        }

        if (_selected != null)
        {
            var frame = AnnotationOps.SelectionHit(_selected, IsFreshSelection, _tool, point, 1 / _zoom);
            if (frame.IsHandle || frame.Rotate || (frame.Inside && _selected is TextAnnotation && _tool == AnnotationTool.Text))
            {
                TryBeginAnnotationTransform(point);
                _pendingTextClick = _movingSelected && _selected is TextAnnotation;
                _dragMoved = false;
                _pressPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        Focus();
        CommitWatermark();

        _lastPoint = point;

        if (_tool == AnnotationTool.Watermark)
        {
            if (e.ClickCount == 2 && AnnotationOps.FindWatermark(Editor.Items) is { } watermark &&
                watermark.HitTest(point, 4 / _zoom))
            {
                BeginWatermarkEdit(watermark);
            }
            else if (AnnotationOps.FindWatermark(Editor.Items) is { } dragged)
            {
                // Press and drag moves the watermark pattern.
                _draggingWatermark = dragged;
                _watermarkLast = point;
                _pressPoint = point;
                _dragMoved = false;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Text)
        {
            if (HitAnnotation(point) is TextAnnotation pressed)
            {
                BeginTextPress(pressed, point, InputLayer);
            }
            else
            {
                BeginTextEdit(AnnotationStyler.NewText(point, Toolbar), isNew: true, caretAt: null);
            }

            e.Handled = true;
            return;
        }

        if (_tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser)
        {
            var candidate = HitAnnotation(point);
            if (candidate != null && SameFamily(candidate, _tool))
            {
                SelectAnnotation(candidate);
                _movingSelected = true;
                _lastPoint = point;
                _history.Arm(Editor.Items);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _movingSelected = true;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearSelection();
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
            Editor.InvalidateVisual();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        StartAnnotation(point);
        InputLayer.CaptureMouse();
        e.Handled = true;
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ToPixels(e.GetPosition(InputLayer));
        if (UpdateTextPress(point))
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Released && _textEditor.FrameCursorAt(point) is { } frameCursor)
        {
            InputLayer.Cursor = frameCursor; // frame of the text being typed
            return;
        }

        if (_gesture != null)
        {
            _gesture.Move(point, IsShiftDown, IsCtrlDown);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingWatermark != null)
        {
            if (!_dragMoved && (point - _pressPoint).Length * _zoom < 3)
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

        if (TransformSelected(point))
        {
            return;
        }

        if (_movingSelected && _selected != null)
        {
            var dx = point.X - _lastPoint.X;
            var dy = point.Y - _lastPoint.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                if (!_dragMoved && (point - _pressPoint).Length * _zoom < 3)
                {
                    return;
                }

                _dragMoved = true;
                ClearAnnotationHover();
                _history.CommitArmed();
                UpdateUndoButtons();
                _selected.Translate(dx, dy);
                _lastPoint = point;
                Editor.InvalidateVisual();
            }

            return;
        }

        UpdateCursor(point);
        if (_tool == AnnotationTool.Watermark)
        {
            var watermark = AnnotationOps.FindWatermark(Editor.Items);
            var hover = watermark != null && watermark.HitTest(point, 2 / _zoom) ? watermark : null;
            InputLayer.Cursor = hover != null ? Cursors.Hand : Cursors.Arrow;
            if (!ReferenceEquals(Editor.HoverWatermark, hover))
            {
                Editor.HoverWatermark = hover;
                Editor.InvalidateVisual();
            }

            return;
        }

        UpdateAnnotationHover(point);
    }

    /// <summary>
    /// Right-click while the left button is still down undoes the gesture in progress
    /// (drawing, erasing, moving / resizing / rotating, dragging a text or the watermark).
    /// </summary>
    private bool CancelGesture()
    {
        if (_textEditor.IsFraming)
        {
            _textEditor.CancelFrameDrag();
            return true;
        }

        if (_active != null || _erasing)
        {
            Editor.Preview = null;
            Editor.LiveEraser = null;
            _active = null;
            _gesture = null;
            _erasing = false;
            _eraserPath = null;
            InputLayer.ReleaseMouseCapture();
            Editor.InvalidateVisual();
            return true;
        }

        if (_resizingAnnotation || _rotatingAnnotation || _movingSelected || _textPress != null || _draggingWatermark != null)
        {
            var target = _selected ?? _textPress;
            var index = target != null ? Editor.Items.IndexOf(target) : -1;
            var reverted = _history.CancelGesture(Editor.Items);
            _resizingAnnotation = false;
            _rotatingAnnotation = false;
            _movingSelected = false;
            _textPress = null;
            _draggingWatermark = null;
            _dragRotate = false;
            _dragScale = false;
            _pendingTextClick = false;
            _dragMoved = false;
            InputLayer.ReleaseMouseCapture();
            ReleaseMouseCapture();
            if (reverted)
            {
                ClearSelection();
                if (index >= 0 && index < Editor.Items.Count && Editor.Items[index] is not WatermarkAnnotation)
                {
                    SelectQuietly(Editor.Items[index]);
                }
            }

            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return true;
        }

        return false;
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
        _erasing = false;
        _eraserPath = null;
        _suppressContextMenu = true;
        InputLayer.ReleaseMouseCapture();
        Editor.InvalidateVisual();
        e.Handled = true;
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EndTextPress(ToPixels(e.GetPosition(InputLayer)), selectAll: false))
        {
            return;
        }

        InputLayer.ReleaseMouseCapture();
        _dragRotate = false;
        _dragScale = false;

        if (_draggingWatermark != null)
        {
            _draggingWatermark = null;
            _history.Disarm();
            _dragMoved = false;
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            _gesture = null;
            Editor.LiveEraser = null;
            if (_eraserPath != null)
            {
                // The state before the stroke becomes the undo step, but only if it erased something.
                var before = Editor.Items.Select(item => item.Clone()).ToList();
                if (AnnotationOps.BindEraser(Editor.Items, _eraserPath.Points, _eraserPath.Thickness, Toolbar.EraserOpacity))
                {
                    _history.Checkpoint(before);
                    UpdateUndoButtons();
                }
            }

            _eraserPath = null;
            Editor.InvalidateVisual();
            return;
        }

        if (_resizingAnnotation || _rotatingAnnotation)
        {
            _resizingAnnotation = false;
            _rotatingAnnotation = false;
            _history.Disarm();
            return;
        }

        if (_movingSelected)
        {
            _movingSelected = false;
            _history.Disarm();
            var clicked = _pendingTextClick && !_dragMoved;
            _pendingTextClick = false;
            _dragMoved = false;
            if (clicked && _selected is TextAnnotation selectedText)
            {
                BeginTextEdit(selectedText, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
            }

            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }
    }

    private void StartAnnotation(Point point)
    {
        ClearSelection();
        _active = AnnotationOps.Create(_tool, point, Toolbar, _number, _baseImage);
        if (_active == null)
        {
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
            _number++;
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

        if (_editMode)
        {
            InputLayer.Focus();
        }
    }

    /// <summary>Selects an element without switching tools (the text tool keeps working).</summary>
    private void SelectQuietly(Annotation annotation)
    {
        _selected = annotation;
        Editor.Selected = annotation;
        Toolbar.SyncFrom(annotation);
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void BeginTextPress(TextAnnotation text, Point point, UIElement capture)
    {
        _textPress = text;
        _textPressPoint = point;
        _textPressMoved = false;
        _lastPoint = point;
        _textPressCapture = capture;
        _history.Arm(Editor.Items);
        capture.CaptureMouse();
    }

    /// <returns>True while a text press is in progress (the move is consumed).</returns>
    private bool UpdateTextPress(Point point)
    {
        if (_textPress == null)
        {
            return false;
        }

        if (!_textPressMoved && (point - _textPressPoint).Length * _zoom < 3)
        {
            return true;
        }

        if (!_textPressMoved)
        {
            _textPressMoved = true;
            _history.CommitArmed();
            UpdateUndoButtons();
            SelectQuietly(_textPress);
        }

        _textPress.Translate(point.X - _lastPoint.X, point.Y - _lastPoint.Y);
        _lastPoint = point;
        Editor.InvalidateVisual();
        return true;
    }

    private bool EndTextPress(Point point, bool selectAll)
    {
        if (_textPress is not { } pressed)
        {
            return false;
        }

        _textPress = null;
        _textPressCapture?.ReleaseMouseCapture();
        _textPressCapture = null;
        if (!_textPressMoved)
        {
            _history.Disarm();
            BeginTextEdit(pressed, isNew: false, caretAt: selectAll ? null : point, selectAll: selectAll);
        }

        return true;
    }

    private void BeginWatermarkEdit(WatermarkAnnotation watermark)
    {
        _editingWatermark = watermark;
        WatermarkEditor.Text = watermark.Text;
        WatermarkEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(WatermarkEditor, watermark.Rect.X * _zoom + 4);
        Canvas.SetTop(WatermarkEditor, watermark.Rect.Y * _zoom + 4);
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

        InputLayer.Focus();
    }

    private void WatermarkEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _editingWatermark = null;
            WatermarkEditor.Visibility = Visibility.Collapsed;
            InputLayer.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitWatermark();
            e.Handled = true;
        }
    }

    private Annotation? HitAnnotation(Point point) => AnnotationOps.HitTest(Editor.Items, point, 7 / _zoom);

    private void SelectAnnotation(Annotation annotation)
    {
        _selected = annotation;
        _tool = AnnotationTool.None;
        Toolbar.SetTool(AnnotationTool.None);
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
        InputLayer.IsHitTestVisible = false;
        InputLayer.Cursor = Cursors.Arrow;
        ImageHost.Cursor = Cursors.Arrow;
        ClearAnnotationHover();
        Editor.Selected = annotation;
        Editor.InvalidateVisual();
    }

    private bool TryBeginAnnotationTransform(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selected, point, 1 / _zoom);
        var bounds = _selected.Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        _annotationCenter = center;
        _annotationStartBounds = bounds;
        _lastPoint = point;
        _transformPress = point;
        _transformStarted = false;

        if (hit.IsHandle)
        {
            _resizingAnnotation = true;
            _annotationHandle = hit.Handle;
            _resizeGrab = AnnotationOps.ResizeGrab(bounds, _selected.Rotation, hit.Handle, point);
            return true;
        }

        if (hit.Rotate)
        {
            _rotatingAnnotation = true;
            _annotationStartRotation = _selected.Rotation;
            _annotationRotateStartAngle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
            return true;
        }

        if (hit.Inside && AnnotationOps.InsideMoves(_selected, _tool, point, 7 / _zoom))
        {
            _movingSelected = true;
            return true;
        }

        return false;
    }

    private protected void ClearSelection()
    {
        _freshSelection = null;
        _selected = null;
        Editor.Selected = null;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        ImageHost.Cursor = Cursors.Arrow;
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private protected void UpdateCursor(Point point)
    {
        if (_tool is AnnotationTool.Select or AnnotationTool.None)
        {
            InputLayer.Cursor = HitAnnotation(point) != null ? Cursors.SizeAll : Cursors.Arrow;
        }
        else
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
        }
    }

    private void UpdateAnnotationHover(Point point)
    {
        if (_tool is AnnotationTool.None or AnnotationTool.Select or AnnotationTool.Eraser)
        {
            ClearAnnotationHover();
            return;
        }

        var hit = HitAnnotation(point);
        if (hit != null && SameFamily(hit, _tool))
        {
            var bounds = hit.Bounds;
            bounds.Inflate(4 / (1.0 / _zoom), 4 / (1.0 / _zoom));
            Editor.HoverBounds = bounds;
            Editor.HoverLabel = _tool == AnnotationTool.Text ? "单击编辑 · 拖动移动" : "点击选中并继续编辑";
            Editor.ShowHover = true;
            InputLayer.Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Hand;
            Editor.InvalidateVisual();
            return;
        }

        ClearAnnotationHover();
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
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
        AnnotationTool.Pen or AnnotationTool.Highlighter => false,
        AnnotationTool.Mosaic or AnnotationTool.Watermark =>
            annotation is MosaicAnnotation or WatermarkAnnotation,
        AnnotationTool.Text => annotation is TextAnnotation,
        AnnotationTool.Number => annotation is NumberAnnotation,
        _ => false
    };

    private bool UpdateAnnotationTransformCursor(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        var hit = AnnotationOps.SelectionHit(_selected, IsFreshSelection, _tool, point, 1 / _zoom);
        if (hit.IsHandle)
        {
            ImageHost.Cursor = AnnotationEditor.ResizeCursor(hit.Handle, _selected.Rotation);
            return true;
        }

        if (hit.Rotate)
        {
            ImageHost.Cursor = ToolCursors.GetRotationCursor(true);
            return true;
        }

        if (hit.Inside && _tool is AnnotationTool.None or AnnotationTool.Select)
        {
            ImageHost.Cursor = Cursors.SizeAll;
            return true;
        }

        return false;
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
            AnnotationTool.Eraser => Math.Clamp(_thickness + step * 2, 4, 80),
            _ => Math.Clamp(_thickness + step * 0.5, 1, 16)
        };

        _thickness = value;
        Toolbar.SetThickness(value);
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
    }

    private void AdjustSelectedByWheel(int delta, bool opacity)
    {
        if (_selected == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, (opacity ? "opacity" : "wheel", _selected));
        UpdateUndoButtons();
        if (opacity)
        {
            if (_selected is WatermarkAnnotation)
            {
                _selected.Opacity = Math.Clamp(Math.Round(_selected.Opacity + Math.Sign(delta) * 0.05, 2), 0.05, 1);
                Toolbar.SyncFrom(_selected);
            }
            else
            {
                _selected.Opacity = AnnotationOps.WheelOpacity(_selected.Opacity, delta);
                Toolbar.SetOpacity(_selected.Opacity);
            }
        }
        else if (AnnotationOps.WheelSize(_selected, delta, _baseImage) is { } size)
        {
            _thickness = size;
            Toolbar.SetThickness(size);
        }
        else
        {
            Toolbar.SyncFrom(_selected);
        }

        Editor.InvalidateVisual();
    }

    private static bool IsTextBypassHeld()
    {
        var setting = App.Config.TextBypassModifier ?? "Alt";
        var modifiers = Keyboard.Modifiers;
        return (setting.Contains("Alt", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Alt) != 0)
               || (setting.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Control) != 0)
               || (setting.Contains("Shift", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Shift) != 0);
    }

    /// <summary>Removes every annotation from the pin (undoable while editing).</summary>
    private void ClearAnnotations()
    {
        if (Editor.Items.Count == 0)
        {
            return;
        }

        _textEditor.Commit();
        if (_editMode)
        {
            _history.Checkpoint(Editor.Items);
        }

        Editor.Items.Clear();
        ClearSelection();
        UpdateUndoButtons();
        Editor.InvalidateVisual();
        if (!_editMode)
        {
            SourceImage = _baseImage;
            Img.Source = _baseImage;
            RefreshTextLayer();
        }
    }

    /// <summary>
    /// After a click on the toolbar (its own, never-activated window) the keys — Tab, Esc,
    /// tool letters — must reach the pin: make sure it is the active window and the canvas has
    /// the keyboard focus (unless a text is being typed).
    /// </summary>
    private protected void ReclaimKeyboard()
    {
        if (!_editMode || _textEditor.IsActive || _editingWatermark != null || Toolbar.IsMenuOpen)
        {
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        InputLayer.Focus();
        Keyboard.Focus(InputLayer);
    }
}
