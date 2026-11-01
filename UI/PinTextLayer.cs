using Markshot.Services;

namespace Markshot.UI;

/// <summary>
/// Selectable text over a pin, PixPin-style: the pin image is OCR'd once and
/// each line is split into per-character boxes (OCR only returns line boxes,
/// so characters are spread by their typical width: CJK full width, Latin
/// narrower). Dragging over text selects it; the selection is drawn as a
/// highlight. Coordinates are image pixels; <see cref="Scale"/> = px per DIP.
/// </summary>
public sealed class PinTextLayer : FrameworkElement
{
    private static readonly Brush Highlight = Freeze(new SolidColorBrush(Color.FromArgb(90, 0x4C, 0x8D, 0xFF)));

    private readonly List<Glyph> _glyphs = new();
    private readonly List<Rect> _lines = new();
    private int _anchor = -1;
    private int _caret = -1;

    private sealed record Glyph(char Char, Rect Box, int Line);

    public PinTextLayer()
    {
        IsHitTestVisible = false;
    }

    public double Scale { get; set; } = 1.0;

    public bool HasText => _glyphs.Count > 0;

    public bool HasSelection => _anchor >= 0 && _caret >= 0 && _anchor != _caret;

    public string SelectedText
    {
        get
        {
            if (!HasSelection)
            {
                return string.Empty;
            }

            var (from, to) = (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));
            var text = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                if (i > from && _glyphs[i].Line != _glyphs[i - 1].Line)
                {
                    text.AppendLine();
                }

                text.Append(_glyphs[i].Char);
            }

            return text.ToString();
        }
    }

    /// <summary>Replaces the recognised lines (reading order: rows top to bottom, left to right).</summary>
    public void SetLines(IReadOnlyList<OcrTextBlock> blocks)
    {
        _glyphs.Clear();
        _lines.Clear();
        ClearSelection();

        var ordered = blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .OrderBy(b => b.Box.Y + b.Box.Height / 2.0)
            .ToList();
        // Group lines that sit on the same row so selection reads left to right.
        var rows = new List<List<OcrTextBlock>>();
        foreach (var block in ordered)
        {
            var center = block.Box.Y + block.Box.Height / 2.0;
            var row = rows.FirstOrDefault(r =>
                Math.Abs(r[0].Box.Y + r[0].Box.Height / 2.0 - center) < Math.Min(r[0].Box.Height, block.Box.Height) * 0.5);
            if (row == null)
            {
                rows.Add(new List<OcrTextBlock> { block });
            }
            else
            {
                row.Add(block);
            }
        }

        foreach (var block in rows.SelectMany(r => r.OrderBy(b => b.Box.X)))
        {
            var line = _lines.Count;
            var box = new Rect(block.Box.X, block.Box.Y, block.Box.Width, block.Box.Height);
            _lines.Add(box);
            var text = block.Text.Trim();
            var weights = text.Select(Weight).ToList();
            var total = Math.Max(0.001, weights.Sum());
            var x = box.X;
            for (var i = 0; i < text.Length; i++)
            {
                var width = box.Width * weights[i] / total;
                _glyphs.Add(new Glyph(text[i], new Rect(x, box.Y, width, box.Height), line));
                x += width;
            }
        }

        InvalidateVisual();
    }

    public void Reset()
    {
        _glyphs.Clear();
        _lines.Clear();
        ClearSelection();
    }

    public bool IsOverText(Point pixel)
    {
        var tolerance = 2 * Scale;
        return _lines.Any(r =>
        {
            var hit = r;
            hit.Inflate(tolerance, tolerance);
            return hit.Contains(pixel);
        });
    }

    public void BeginSelection(Point pixel)
    {
        _anchor = _caret = CaretAt(pixel);
        InvalidateVisual();
    }

    public void ExtendSelection(Point pixel)
    {
        if (_anchor < 0)
        {
            return;
        }

        _caret = CaretAt(pixel);
        InvalidateVisual();
    }

    public void SelectLineAt(Point pixel)
    {
        var index = CaretAt(pixel);
        if (index < 0 || _glyphs.Count == 0)
        {
            return;
        }

        var line = _glyphs[Math.Min(index, _glyphs.Count - 1)].Line;
        _anchor = _glyphs.FindIndex(g => g.Line == line);
        _caret = _glyphs.FindLastIndex(g => g.Line == line) + 1;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_glyphs.Count == 0)
        {
            return;
        }

        _anchor = 0;
        _caret = _glyphs.Count;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_anchor < 0 && _caret < 0)
        {
            return;
        }

        _anchor = _caret = -1;
        InvalidateVisual();
    }

    /// <summary>Caret position (0..glyph count) nearest to the point, in reading order.</summary>
    private int CaretAt(Point pixel)
    {
        if (_glyphs.Count == 0)
        {
            return -1;
        }

        // The line under the point, else the vertically nearest one.
        var line = -1;
        var best = double.MaxValue;
        for (var i = 0; i < _lines.Count; i++)
        {
            var r = _lines[i];
            var dy = pixel.Y < r.Top ? r.Top - pixel.Y : pixel.Y > r.Bottom ? pixel.Y - r.Bottom : 0;
            var dx = pixel.X < r.Left ? r.Left - pixel.X : pixel.X > r.Right ? pixel.X - r.Right : 0;
            var d = dy * 4 + dx; // prefer the same row
            if (d < best)
            {
                best = d;
                line = i;
            }
        }

        var first = _glyphs.FindIndex(g => g.Line == line);
        var last = _glyphs.FindLastIndex(g => g.Line == line);
        for (var i = first; i <= last; i++)
        {
            var box = _glyphs[i].Box;
            if (pixel.X < box.X + box.Width / 2)
            {
                return i;
            }
        }

        return last + 1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!HasSelection || Scale <= 0)
        {
            return;
        }

        dc.PushTransform(new ScaleTransform(1 / Scale, 1 / Scale));
        var (from, to) = (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));
        var i = from;
        while (i < to)
        {
            // One highlight per line run.
            var line = _glyphs[i].Line;
            var start = _glyphs[i].Box;
            var end = start;
            while (i < to && _glyphs[i].Line == line)
            {
                end = _glyphs[i].Box;
                i++;
            }

            dc.DrawRectangle(Highlight, null, new Rect(start.Left, start.Top, end.Right - start.Left, start.Height));
        }

        dc.Pop();
    }

    private static double Weight(char c)
    {
        if (c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯')
        {
            return 1.0; // CJK, full-width forms
        }

        if (char.IsWhiteSpace(c))
        {
            return 0.3;
        }

        return char.IsUpper(c) || c is 'm' or 'w' or 'M' or 'W' ? 0.65 : 0.5;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
