using Markshot.Annotations;

namespace Markshot.UI;

/// <summary>
/// In-place text editing shared by the capture overlay and pin windows.
///
/// The text is always drawn by <see cref="TextAnnotation.Draw"/> (live, while
/// typing), so what you see during editing is exactly what gets composed. A
/// TextBox with transparent glyphs sits on top in the same font, size and line
/// height; it only supplies the caret, selection and IME.
///
/// While typing, the text has a normal adjustment frame: the band around the
/// text moves it, handles resize, the corner grips rotate. Typing continues
/// and the box follows (the drag captures the mouse on the TextBox itself, so
/// it also works where the box overlaps the frame).
///
/// Keys: Enter = finish, Shift/Ctrl+Enter = new line, Esc = finish.
/// Clicking toolbar controls does not end editing; the host calls
/// <see cref="Refresh"/> after restyling and focus returns to the box.
/// </summary>
public sealed class InlineTextEditor
{
    // WPF's TextBoxView renders glyphs this far right of its origin. Measured
    // against FormattedText offscreen: +2 DIP at 14–60px, regular and bold,
    // with identical vertical metrics when both use BlockLineHeight.
    private const double TextBoxInsetDip = 2;

    private readonly TextBox _box;
    private readonly AnnotationEditor _canvas;
    private bool _syncing;
    private string _originalText = string.Empty;

    // Frame drag while typing.
    private FrameHit _dragHit;
    private bool _framing;
    private Rect _dragStartBounds;
    private Point _dragCenter;
    private Point _dragLast;
    private double _dragStartRotation;
    private double _dragStartAngle;
    private Point _dragPress;
    private Vector _dragGrab;
    private bool _dragStarted;
    private (Point Position, double FontSize, double Rotation) _dragStartState;

    public InlineTextEditor(TextBox box, AnnotationEditor canvas)
    {
        _box = box;
        _canvas = canvas;
        _box.Visibility = Visibility.Collapsed;
        _box.TextChanged += OnTextChanged;
        _box.PreviewKeyDown += OnPreviewKeyDown;
        _box.PreviewMouseWheel += (_, e) =>
        {
            if (Target != null)
            {
                AdjustSize(e.Delta);
                e.Handled = true;
            }
        };
        _box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (TryBeginFrameDrag(PixelOf(e)))
            {
                e.Handled = true;
            }
        };
        _box.PreviewMouseMove += (_, e) =>
        {
            if (_framing)
            {
                DragFrame(PixelOf(e));
                e.Handled = true;
                return;
            }

            _box.Cursor = FrameCursorAt(PixelOf(e)) ?? Cursors.IBeam;
        };
        _box.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_framing)
            {
                EndFrameDrag();
                e.Handled = true;
            }
        };
        _box.LostMouseCapture += (_, _) => _framing = false;
    }

    /// <summary>True while the frame of the text being typed is dragged.</summary>
    public bool IsFraming => _framing;

    /// <summary>Cursor for the editing frame at a point, or null when the point is not on it.</summary>
    public Cursor? FrameCursorAt(Point pixel)
    {
        if (Target is not { } target)
        {
            return null;
        }

        var hit = AnnotationEditor.HitEditFrame(target, pixel, Unit);
        if (hit.IsHandle)
        {
            return AnnotationEditor.ResizeCursor(hit.Handle, target.Rotation);
        }

        if (hit.Rotate)
        {
            return ToolCursors.GetRotationCursor(true);
        }

        return hit.Inside ? Cursors.SizeAll : null;
    }

    /// <summary>
    /// Starts moving / resizing / rotating the text being typed when <paramref name="pixel"/>
    /// is on its frame. Hosts call this for presses that land outside the TextBox.
    /// </summary>
    public bool TryBeginFrameDrag(Point pixel)
    {
        if (Target is not { } target)
        {
            return false;
        }

        var hit = AnnotationEditor.HitEditFrame(target, pixel, Unit);
        if (!hit.Any)
        {
            return false;
        }

        _dragHit = hit;
        _dragStartBounds = target.Bounds;
        _dragCenter = new Point(_dragStartBounds.X + _dragStartBounds.Width / 2, _dragStartBounds.Y + _dragStartBounds.Height / 2);
        _dragLast = pixel;
        _dragStartRotation = target.Rotation;
        _dragStartAngle = Math.Atan2(pixel.Y - _dragCenter.Y, pixel.X - _dragCenter.X) * 180 / Math.PI;
        _dragPress = pixel;
        _dragStarted = false;
        _dragGrab = hit.IsHandle ? AnnotationOps.ResizeGrab(_dragStartBounds, target.Rotation, hit.Handle, pixel) : default;
        _dragStartState = (target.Position, target.FontSize, target.Rotation);
        _framing = _box.CaptureMouse();
        if (_framing)
        {
            _box.Cursor = FrameCursorAt(pixel) ?? Cursors.SizeAll;
        }

        return _framing;
    }

    private void DragFrame(Point pixel)
    {
        if (Target is not { } target)
        {
            return;
        }

        if (!_dragStarted)
        {
            // A click on the frame (no real drag) changes nothing.
            if ((pixel - _dragPress).Length < 3 * Unit)
            {
                return;
            }

            _dragStarted = true;
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_dragHit.Rotate)
        {
            var angle = Math.Atan2(pixel.Y - _dragCenter.Y, pixel.X - _dragCenter.X) * 180 / Math.PI;
            var rotation = _dragStartRotation + angle - _dragStartAngle;
            if (shift)
            {
                rotation = Math.Round(rotation / 15) * 15;
            }

            target.Rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
        }
        else if (_dragHit.IsHandle)
        {
            var rect = AnnotationOps.ResizeFromHandle(_dragStartBounds, target.Rotation, _dragHit.Handle, pixel + _dragGrab, shift);
            AnnotationOps.Resize(target, rect, _dragHit.Handle);
            StyleChanged?.Invoke(target); // the toolbar's size follows
        }
        else
        {
            target.Translate(pixel.X - _dragLast.X, pixel.Y - _dragLast.Y);
            _dragLast = pixel;
        }

        Dirty = true;
        Refresh();
    }

    /// <summary>Right-click during a frame drag: back to where the drag started.</summary>
    public void CancelFrameDrag()
    {
        if (!_framing || Target is not { } target)
        {
            return;
        }

        (target.Position, target.FontSize, target.Rotation) = _dragStartState;
        Refresh();
        StyleChanged?.Invoke(target);
        EndFrameDrag();
    }

    private void EndFrameDrag()
    {
        _framing = false;
        _box.ReleaseMouseCapture();
        _box.Focus();
        Keyboard.Focus(_box);
        _canvas.InvalidateVisual();
    }

    private double Unit => 1 / (_canvas.Scale <= 0 ? 1 : _canvas.Scale);

    /// <summary>Pointer position in image pixels (the canvas draws pixels at Scale DIP each).</summary>
    private Point PixelOf(MouseEventArgs e)
    {
        var dip = e.GetPosition(_canvas);
        var scale = _canvas.Scale <= 0 ? 1 : _canvas.Scale;
        return new Point(dip.X / scale, dip.Y / scale);
    }

    public TextAnnotation? Target { get; private set; }

    public bool IsActive => Target != null;

    public bool IsNew { get; private set; }

    /// <summary>True once the text or its style changed in this session.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Raised when the editor itself restyles the text (wheel size, Ctrl+B) so the toolbar can follow.</summary>
    public event Action<TextAnnotation>? StyleChanged;

    /// <summary>Raised after editing ends. Args: the annotation and whether it was removed (empty).</summary>
    public event Action<TextAnnotation, bool>? Ended;

    public void Begin(TextAnnotation target, bool isNew, Point? caretAtPixel = null, bool selectAll = false)
    {
        if (Target != null)
        {
            Commit();
        }

        Target = target;
        IsNew = isNew;
        Dirty = false;
        _originalText = target.Text;
        _canvas.EditingText = target;

        _syncing = true;
        _box.Text = target.Text.Replace("\n", Environment.NewLine);
        _syncing = false;

        _box.Visibility = Visibility.Visible;
        Refresh();
        _box.UpdateLayout();

        _box.Focus();
        Keyboard.Focus(_box);
        if (selectAll)
        {
            _box.SelectAll();
        }
        else if (caretAtPixel is { } pixel)
        {
            var local = ToBoxPoint(pixel);
            var index = _box.GetCharacterIndexFromPoint(local, snapToText: true);
            if (index >= 0)
            {
                // The index is the character under the point; put the caret on
                // whichever side of that character is nearer.
                var leading = _box.GetRectFromCharacterIndex(index).X;
                var trailing = _box.GetRectFromCharacterIndex(index, trailingEdge: true).X;
                _box.CaretIndex = local.X > (leading + trailing) / 2 ? index + 1 : index;
            }
            else
            {
                _box.CaretIndex = _box.Text.Length;
            }
        }
        else
        {
            _box.CaretIndex = _box.Text.Length;
        }

        _canvas.InvalidateVisual();
    }

    /// <summary>Re-applies position, font and rotation after the target was restyled.</summary>
    public void Refresh()
    {
        if (Target is not { } target)
        {
            return;
        }

        var scale = _canvas.Scale <= 0 ? 1 : _canvas.Scale;
        var size = target.MeasureText();

        _box.FontFamily = TextAnnotation.FamilyOf(target.FontName);
        _box.FontSize = target.FontSize / scale;
        _box.FontWeight = target.Bold ? FontWeights.Bold : FontWeights.Normal;
        _box.FontStyle = target.Italic ? FontStyles.Italic : FontStyles.Normal;
        TextBlock.SetLineHeight(_box, target.LineHeight / scale);
        TextBlock.SetLineStackingStrategy(_box, LineStackingStrategy.BlockLineHeight);
        _box.CaretBrush = new SolidColorBrush(target.TextColor);

        // Leave room for the caret and the next character so the box never scrolls.
        _box.Width = size.Width / scale + target.FontSize / scale + TextBoxInsetDip * 2;
        _box.Height = size.Height / scale + 2;
        Canvas.SetLeft(_box, target.Position.X / scale - TextBoxInsetDip);
        Canvas.SetTop(_box, target.Position.Y / scale);

        if (Math.Abs(target.Rotation) > 0.01)
        {
            var bounds = target.Bounds;
            var center = new Point(
                (bounds.X + bounds.Width / 2) / scale - Canvas.GetLeft(_box),
                (bounds.Y + bounds.Height / 2) / scale - Canvas.GetTop(_box));
            _box.RenderTransform = new RotateTransform(target.Rotation, center.X, center.Y);
        }
        else
        {
            _box.RenderTransform = Transform.Identity;
        }

        if (!_box.IsKeyboardFocusWithin && _box.IsVisible)
        {
            _box.Focus();
        }

        if (_framing)
        {
            // Resized / moved by its frame: the box is laid out now (not a frame later than the text the canvas draws)
            // and never scrolled inside — a box that scrolled while it grew showed the caret beside the text (a jump).
            _box.UpdateLayout();
            _box.ScrollToHorizontalOffset(0);
            _box.ScrollToVerticalOffset(0);
        }

        _canvas.InvalidateVisual();
    }

    public void MarkDirty() => Dirty = true;

    /// <summary>Wheel up/down grows/shrinks the text being edited by 2px.</summary>
    public void AdjustSize(int wheelDelta)
    {
        if (Target is not { } target || wheelDelta == 0)
        {
            return;
        }

        target.FontSize = Math.Clamp(target.FontSize + Math.Sign(wheelDelta) * 2, 6, 400);
        Dirty = true;
        Refresh();
        StyleChanged?.Invoke(target);
    }

    /// <summary>Ends editing and keeps the text (removing it when empty).</summary>
    public void Commit()
    {
        if (Target is not { } target)
        {
            return;
        }

        target.Text = Normalize(_box.Text);
        var removed = target.Text.Trim().Length == 0;
        if (removed)
        {
            _canvas.Items.Remove(target);
        }

        if (target.Text != _originalText)
        {
            Dirty = true;
        }

        if (_framing)
        {
            _framing = false;
            _box.ReleaseMouseCapture();
        }

        Target = null;
        _canvas.EditingText = null;
        _box.Visibility = Visibility.Collapsed;
        _syncing = true;
        _box.Text = string.Empty;
        _syncing = false;
        _canvas.InvalidateVisual();
        Ended?.Invoke(target, removed);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || Target is not { } target)
        {
            return;
        }

        target.Text = Normalize(_box.Text);
        Dirty = true;
        Refresh();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Target == null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Commit();
                e.Handled = true;
                break;

            case Key.Enter when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0:
            {
                var caret = _box.SelectionStart;
                _box.SelectedText = Environment.NewLine;
                _box.CaretIndex = caret + Environment.NewLine.Length;
                _box.SelectionLength = 0;
                e.Handled = true;
                break;
            }

            case Key.Enter:
                Commit();
                e.Handled = true;
                break;

            case Key.B when (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Target is { } target:
                target.Bold = !target.Bold;
                Dirty = true;
                Refresh();
                StyleChanged?.Invoke(target);
                e.Handled = true;
                break;

            case Key.I when (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Target is { } italicTarget:
                italicTarget.Italic = !italicTarget.Italic;
                Dirty = true;
                Refresh();
                StyleChanged?.Invoke(italicTarget);
                e.Handled = true;
                break;

            case Key.Tab:
                // Keep focus inside the editor instead of tabbing to the toolbar.
                e.Handled = true;
                break;
        }
    }

    private Point ToBoxPoint(Point pixel)
    {
        var scale = _canvas.Scale <= 0 ? 1 : _canvas.Scale;
        var dip = new Point(pixel.X / scale - Canvas.GetLeft(_box), pixel.Y / scale - Canvas.GetTop(_box));
        return _box.RenderTransform.Inverse?.Transform(dip) ?? dip;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
