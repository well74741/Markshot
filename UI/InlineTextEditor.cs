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

        _box.FontFamily = new FontFamily(TextAnnotation.FontFamilyName);
        _box.FontSize = target.FontSize / scale;
        _box.FontWeight = target.Bold ? FontWeights.Bold : FontWeights.Normal;
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
