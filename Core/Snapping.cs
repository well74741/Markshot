namespace Markshot.Core;

/// <summary>
/// 吸附 while moving elements on the editor's canvas: the moved box's edges and middle line up with the
/// canvas' edges and middle and with the other elements' edges and middles when they come close.
/// </summary>
public static class Snapping
{
    /// <summary>A line to show: vertical (X) or horizontal (Y) at <see cref="At"/>, from <see cref="From"/> to <see cref="To"/>.</summary>
    public readonly record struct Guide(bool Vertical, double At, double From, double To);

    /// <summary>
    /// How far to shift <paramref name="moving"/> (canvas pixels) to snap it, and the guides that show why.
    /// <paramref name="reach"/>: the snapping distance in canvas pixels.
    /// </summary>
    public static (Vector Shift, List<Guide> Guides) Snap(Rect moving, Rect canvas, IEnumerable<Rect> others, double reach)
    {
        var targets = new List<Rect> { canvas };
        targets.AddRange(others.Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0));
        var (dx, xGuide) = Best(new[] { moving.Left, moving.Left + moving.Width / 2, moving.Right },
            targets.SelectMany(t => new[] { t.Left, t.Left + t.Width / 2, t.Right }.Select(v => (v, t))), reach);
        var (dy, yGuide) = Best(new[] { moving.Top, moving.Top + moving.Height / 2, moving.Bottom },
            targets.SelectMany(t => new[] { t.Top, t.Top + t.Height / 2, t.Bottom }.Select(v => (v, t))), reach);
        var guides = new List<Guide>();
        var moved = new Rect(moving.X + dx, moving.Y + dy, moving.Width, moving.Height);
        if (xGuide is { } x)
        {
            guides.Add(new Guide(true, x.Value, Math.Min(moved.Top, x.Target.Top), Math.Max(moved.Bottom, x.Target.Bottom)));
        }

        if (yGuide is { } y)
        {
            guides.Add(new Guide(false, y.Value, Math.Min(moved.Left, y.Target.Left), Math.Max(moved.Right, y.Target.Right)));
        }

        return (new Vector(dx, dy), guides);
    }

    private static (double Shift, (double Value, Rect Target)? Line) Best(double[] own, IEnumerable<(double Value, Rect Target)> lines, double reach)
    {
        var best = reach + 1;
        (double Value, Rect Target)? found = null;
        var shift = 0.0;
        foreach (var line in lines)
        {
            foreach (var value in own)
            {
                var distance = Math.Abs(line.Value - value);
                if (distance <= reach && distance < best)
                {
                    best = distance;
                    shift = line.Value - value;
                    found = line;
                }
            }
        }

        return (shift, found);
    }

    /// <summary>对齐 of several boxes: how far each moves (left / centre / right, top / middle / bottom, spread evenly).</summary>
    public static List<Vector> Align(IReadOnlyList<Rect> boxes, string how)
    {
        var all = boxes.Aggregate(Rect.Empty, (union, box) => { union.Union(box); return union; });
        var moves = boxes.Select(_ => new Vector()).ToList();
        switch (how)
        {
            case "Left": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(all.Left - boxes[i].Left, 0); break;
            case "Right": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(all.Right - boxes[i].Right, 0); break;
            case "Center": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(all.Left + all.Width / 2 - (boxes[i].Left + boxes[i].Width / 2), 0); break;
            case "Top": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(0, all.Top - boxes[i].Top); break;
            case "Bottom": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(0, all.Bottom - boxes[i].Bottom); break;
            case "Middle": for (var i = 0; i < boxes.Count; i++) moves[i] = new Vector(0, all.Top + all.Height / 2 - (boxes[i].Top + boxes[i].Height / 2)); break;
            case "SpreadX" or "SpreadY":
            {
                // Equal gaps between neighbours; the outermost two stay.
                var horizontal = how == "SpreadX";
                var order = Enumerable.Range(0, boxes.Count).OrderBy(i => horizontal ? boxes[i].Left : boxes[i].Top).ToList();
                if (order.Count < 3)
                {
                    break;
                }

                var span = horizontal ? all.Width : all.Height;
                var filled = order.Sum(i => horizontal ? boxes[i].Width : boxes[i].Height);
                var gap = (span - filled) / (order.Count - 1);
                var at = horizontal ? all.Left : all.Top;
                foreach (var i in order)
                {
                    moves[i] = horizontal ? new Vector(at - boxes[i].Left, 0) : new Vector(0, at - boxes[i].Top);
                    at += (horizontal ? boxes[i].Width : boxes[i].Height) + gap;
                }

                break;
            }
        }

        return moves;
    }
}
