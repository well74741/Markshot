namespace Markshot.Core;

/// <summary>
/// The warp of 网格形变, like Photoshop's 变形 with 交叉拆分: the picture is a grid of patches whose
/// edges are cubic Bézier curves (an anchor at every crossing, two handles on every edge). A patch's
/// inside follows its four edges (a Coons patch). Clicking splits the grid crosswise through a point;
/// dragging an anchor carries its handles along, dragging a handle bends the edge (the handle on the
/// other side of an inner anchor turns with it, so the curve stays smooth). Coordinates are image pixels.
/// </summary>
public sealed class WarpMesh
{
    /// <summary>Where the grid lines are in the original picture (fractions of its width / height).</summary>
    public List<double> U { get; private set; } = new() { 0, 1 };

    public List<double> V { get; private set; } = new() { 0, 1 };

    /// <summary>Crossings [row, column].</summary>
    public Point[,] Anchors { get; private set; }

    /// <summary>Handles of the horizontal edge from anchor [r, c] to [r, c + 1]: [r, c, 0 | 1].</summary>
    public Point[,,] Across { get; private set; }

    /// <summary>Handles of the vertical edge from anchor [r, c] to [r + 1, c]: [r, c, 0 | 1].</summary>
    public Point[,,] Down { get; private set; }

    public int Columns => U.Count - 1;

    public int Rows => V.Count - 1;

    public double Width { get; }

    public double Height { get; }

    /// <summary>Something was split or moved.</summary>
    public bool Changed { get; private set; }

    public WarpMesh(double width, double height)
    {
        Width = width;
        Height = height;
        Anchors = new[,] { { new Point(0, 0), new Point(width, 0) }, { new Point(0, height), new Point(width, height) } };
        Across = new Point[2, 1, 2];
        Down = new Point[1, 2, 2];
        for (var r = 0; r < 2; r++)
        {
            Across[r, 0, 0] = Lerp(Anchors[r, 0], Anchors[r, 1], 1 / 3.0);
            Across[r, 0, 1] = Lerp(Anchors[r, 0], Anchors[r, 1], 2 / 3.0);
        }

        for (var c = 0; c < 2; c++)
        {
            Down[0, c, 0] = Lerp(Anchors[0, c], Anchors[1, c], 1 / 3.0);
            Down[0, c, 1] = Lerp(Anchors[0, c], Anchors[1, c], 2 / 3.0);
        }
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var s = 1 - t;
        return new Point(
            s * s * s * p0.X + 3 * s * s * t * p1.X + 3 * s * t * t * p2.X + t * t * t * p3.X,
            s * s * s * p0.Y + 3 * s * s * t * p1.Y + 3 * s * t * t * p2.Y + t * t * t * p3.Y);
    }

    public Point AcrossAt(int r, int c, double t) => Bezier(Anchors[r, c], Across[r, c, 0], Across[r, c, 1], Anchors[r, c + 1], t);

    public Point DownAt(int r, int c, double t) => Bezier(Anchors[r, c], Down[r, c, 0], Down[r, c, 1], Anchors[r + 1, c], t);

    /// <summary>A point of patch [r, c] at (s, t) in 0–1 (Coons patch of its four edges).</summary>
    public Point Patch(int r, int c, double s, double t)
    {
        var top = AcrossAt(r, c, s);
        var bottom = AcrossAt(r + 1, c, s);
        var left = DownAt(r, c, t);
        var right = DownAt(r, c + 1, t);
        Point a = Anchors[r, c], b = Anchors[r, c + 1], d = Anchors[r + 1, c], e = Anchors[r + 1, c + 1];
        return new Point(
            (1 - t) * top.X + t * bottom.X + (1 - s) * left.X + s * right.X - ((1 - s) * (1 - t) * a.X + s * (1 - t) * b.X + (1 - s) * t * d.X + s * t * e.X),
            (1 - t) * top.Y + t * bottom.Y + (1 - s) * left.Y + s * right.Y - ((1 - s) * (1 - t) * a.Y + s * (1 - t) * b.Y + (1 - s) * t * d.Y + s * t * e.Y));
    }

    private static int Segment(List<double> lines, double value)
    {
        for (var i = 0; i < lines.Count - 2; i++)
        {
            if (value < lines[i + 1])
            {
                return i;
            }
        }

        return lines.Count - 2;
    }

    /// <summary>Where the point (u, v) of the original picture (fractions) is after the warp.</summary>
    public Point Evaluate(double u, double v)
    {
        var c = Segment(U, u);
        var r = Segment(V, v);
        return Patch(r, c, (u - U[c]) / (U[c + 1] - U[c]), (v - V[r]) / (V[r + 1] - V[r]));
    }

    /// <summary>The original-picture point (fractions) that lands nearest <paramref name="p"/>; null when the warp is far from it.</summary>
    public (double U, double V)? Locate(Point p, int samples = 160)
    {
        var best = double.MaxValue;
        (double, double) found = default;
        for (var j = 0; j <= samples; j++)
        {
            for (var i = 0; i <= samples; i++)
            {
                double u = i / (double)samples, v = j / (double)samples;
                var at = Evaluate(u, v);
                var distance = (at - p).LengthSquared;
                if (distance < best)
                {
                    best = distance;
                    found = (u, v);
                }
            }
        }

        // Refine around the best sample.
        var step = 1.0 / samples;
        for (var round = 0; round < 6; round++)
        {
            step /= 2;
            var (bu, bv) = found;
            foreach (var (du, dv) in new[] { (-step, 0.0), (step, 0.0), (0.0, -step), (0.0, step) })
            {
                double u = Math.Clamp(bu + du, 0, 1), v = Math.Clamp(bv + dv, 0, 1);
                var distance = (Evaluate(u, v) - p).LengthSquared;
                if (distance < best)
                {
                    best = distance;
                    found = (u, v);
                }
            }
        }

        var cell = Math.Max(Width, Height) / samples * 3;
        return best <= cell * cell ? found : null;
    }

    /// <summary>
    /// 交叉拆分 at the original-picture point (u, v): a new grid column through u and a new row through v
    /// (a line already close to it is kept). Returns the crossing under the point.
    /// </summary>
    public (int Row, int Column) SplitAt(double u, double v)
    {
        var column = SplitColumn(u);
        var row = SplitRow(v);
        Changed = true;
        return (row, column);
    }

    private const double MinGap = 0.03;

    private int SplitColumn(double u)
    {
        var near = U.FindIndex(x => Math.Abs(x - u) < MinGap);
        if (near >= 0)
        {
            return near;
        }

        var c = Segment(U, u);
        var s = (u - U[c]) / (U[c + 1] - U[c]);
        int rows = Rows, columns = Columns + 1;
        var anchors = new Point[rows + 1, columns + 1];
        var across = new Point[rows + 1, columns, 2];
        var down = new Point[rows, columns + 1, 2];
        int Old(int newColumn) => newColumn <= c ? newColumn : newColumn - 1;
        for (var r = 0; r <= rows; r++)
        {
            for (var k = 0; k <= columns; k++)
            {
                if (k != c + 1)
                {
                    anchors[r, k] = Anchors[r, Old(k)];
                }
            }

            for (var k = 0; k < columns; k++)
            {
                if (k == c || k == c + 1)
                {
                    continue;
                }

                var old = k < c ? k : k - 1;
                across[r, k, 0] = Across[r, old, 0];
                across[r, k, 1] = Across[r, old, 1];
            }

            // The edge that is cut becomes two (de Casteljau).
            var (left1, left2, middle, right1, right2) = Split(Anchors[r, c], Across[r, c, 0], Across[r, c, 1], Anchors[r, c + 1], s);
            anchors[r, c + 1] = middle;
            across[r, c, 0] = left1;
            across[r, c, 1] = left2;
            across[r, c + 1, 0] = right1;
            across[r, c + 1, 1] = right2;
        }

        for (var r = 0; r < rows; r++)
        {
            for (var k = 0; k <= columns; k++)
            {
                if (k == c + 1)
                {
                    // The new line follows the patch it crosses.
                    var (h1, h2) = Through(anchors[r, k], Patch(r, c, s, 1 / 3.0), Patch(r, c, s, 2 / 3.0), anchors[r + 1, k]);
                    down[r, k, 0] = h1;
                    down[r, k, 1] = h2;
                }
                else
                {
                    down[r, k, 0] = Down[r, Old(k), 0];
                    down[r, k, 1] = Down[r, Old(k), 1];
                }
            }
        }

        Anchors = anchors;
        Across = across;
        Down = down;
        U.Insert(c + 1, u);
        return c + 1;
    }

    private int SplitRow(double v)
    {
        var near = V.FindIndex(y => Math.Abs(y - v) < MinGap);
        if (near >= 0)
        {
            return near;
        }

        var r = Segment(V, v);
        var t = (v - V[r]) / (V[r + 1] - V[r]);
        int rows = Rows + 1, columns = Columns;
        var anchors = new Point[rows + 1, columns + 1];
        var across = new Point[rows + 1, columns, 2];
        var down = new Point[rows, columns + 1, 2];
        int Old(int newRow) => newRow <= r ? newRow : newRow - 1;
        for (var c = 0; c <= columns; c++)
        {
            for (var k = 0; k <= rows; k++)
            {
                if (k != r + 1)
                {
                    anchors[k, c] = Anchors[Old(k), c];
                }
            }

            for (var k = 0; k < rows; k++)
            {
                if (k == r || k == r + 1)
                {
                    continue;
                }

                var old = k < r ? k : k - 1;
                down[k, c, 0] = Down[old, c, 0];
                down[k, c, 1] = Down[old, c, 1];
            }

            var (top1, top2, middle, bottom1, bottom2) = Split(Anchors[r, c], Down[r, c, 0], Down[r, c, 1], Anchors[r + 1, c], t);
            anchors[r + 1, c] = middle;
            down[r, c, 0] = top1;
            down[r, c, 1] = top2;
            down[r + 1, c, 0] = bottom1;
            down[r + 1, c, 1] = bottom2;
        }

        for (var c = 0; c < columns; c++)
        {
            for (var k = 0; k <= rows; k++)
            {
                if (k == r + 1)
                {
                    var (h1, h2) = Through(anchors[k, c], Patch(r, c, 1 / 3.0, t), Patch(r, c, 2 / 3.0, t), anchors[k, c + 1]);
                    across[k, c, 0] = h1;
                    across[k, c, 1] = h2;
                }
                else
                {
                    across[k, c, 0] = Across[Old(k), c, 0];
                    across[k, c, 1] = Across[Old(k), c, 1];
                }
            }
        }

        Anchors = anchors;
        Across = across;
        Down = down;
        V.Insert(r + 1, v);
        return r + 1;
    }

    private static (Point, Point, Point, Point, Point) Split(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var a = Lerp(p0, p1, t);
        var b = Lerp(p1, p2, t);
        var c = Lerp(p2, p3, t);
        var d = Lerp(a, b, t);
        var e = Lerp(b, c, t);
        var m = Lerp(d, e, t);
        return (a, d, m, e, c);
    }

    /// <summary>The handles of a cubic from p0 to p3 that passes through q1 at 1/3 and q2 at 2/3.</summary>
    private static (Point, Point) Through(Point p0, Point q1, Point q2, Point p3)
    {
        var r1 = new Point(27 * q1.X - 8 * p0.X - p3.X, 27 * q1.Y - 8 * p0.Y - p3.Y);
        var r2 = new Point(27 * q2.X - p0.X - 8 * p3.X, 27 * q2.Y - p0.Y - 8 * p3.Y);
        return (new Point((2 * r1.X - r2.X) / 18, (2 * r1.Y - r2.Y) / 18), new Point((2 * r2.X - r1.X) / 18, (2 * r2.Y - r1.Y) / 18));
    }

    /// <summary>Moves a crossing; its handles come along.</summary>
    public void MoveAnchor(int r, int c, Point to)
    {
        var delta = to - Anchors[r, c];
        Anchors[r, c] = to;
        if (c > 0)
        {
            Across[r, c - 1, 1] += delta;
        }

        if (c < Columns)
        {
            Across[r, c, 0] += delta;
        }

        if (r > 0)
        {
            Down[r - 1, c, 1] += delta;
        }

        if (r < Rows)
        {
            Down[r, c, 0] += delta;
        }

        Changed = true;
    }

    /// <summary>
    /// Moves a handle (<paramref name="vertical"/>: of a vertical edge [r, c], else a horizontal one;
    /// <paramref name="end"/> 0 = near the first anchor). At an inner anchor the handle across it turns along.
    /// </summary>
    public void MoveHandle(bool vertical, int r, int c, int end, Point to)
    {
        Point anchor;
        (bool Exists, int R, int C, int End) other;
        if (vertical)
        {
            Down[r, c, end] = to;
            anchor = end == 0 ? Anchors[r, c] : Anchors[r + 1, c];
            other = end == 0 ? (r > 0, r - 1, c, 1) : (r + 1 < Rows, r + 1, c, 0);
        }
        else
        {
            Across[r, c, end] = to;
            anchor = end == 0 ? Anchors[r, c] : Anchors[r, c + 1];
            other = end == 0 ? (c > 0, r, c - 1, 1) : (c + 1 < Columns, r, c + 1, 0);
        }

        if (other.Exists)
        {
            var direction = to - anchor;
            if (direction.Length > 0.5)
            {
                ref var opposite = ref (vertical ? ref Down[other.R, other.C, other.End] : ref Across[other.R, other.C, other.End]);
                var length = (opposite - anchor).Length;
                direction.Normalize();
                opposite = anchor - direction * length;
            }
        }

        Changed = true;
    }

    /// <summary>The same warp moved by an offset (the editor edits it where the picture is on the canvas).</summary>
    public WarpMesh Translated(double dx, double dy)
    {
        var copy = new WarpMesh(Width, Height)
        {
            U = new List<double>(U),
            V = new List<double>(V),
            Anchors = (Point[,])Anchors.Clone(),
            Across = (Point[,,])Across.Clone(),
            Down = (Point[,,])Down.Clone(),
            Changed = Changed
        };
        for (var r = 0; r < copy.Anchors.GetLength(0); r++)
        for (var c = 0; c < copy.Anchors.GetLength(1); c++)
        {
            copy.Anchors[r, c] = new Point(Anchors[r, c].X + dx, Anchors[r, c].Y + dy);
        }

        for (var i = 0; i < copy.Across.GetLength(0); i++)
        for (var j = 0; j < copy.Across.GetLength(1); j++)
        for (var k = 0; k < 2; k++)
        {
            copy.Across[i, j, k] = new Point(Across[i, j, k].X + dx, Across[i, j, k].Y + dy);
        }

        for (var i = 0; i < copy.Down.GetLength(0); i++)
        for (var j = 0; j < copy.Down.GetLength(1); j++)
        for (var k = 0; k < 2; k++)
        {
            copy.Down[i, j, k] = new Point(Down[i, j, k].X + dx, Down[i, j, k].Y + dy);
        }

        return copy;
    }

    /// <summary>The warp of a picture <paramref name="scale"/> times this size (the editor previews on a small copy).</summary>
    public WarpMesh Scaled(double scale)
    {
        var copy = new WarpMesh(Width * scale, Height * scale)
        {
            U = new List<double>(U),
            V = new List<double>(V),
            Anchors = (Point[,])Anchors.Clone(),
            Across = (Point[,,])Across.Clone(),
            Down = (Point[,,])Down.Clone(),
            Changed = Changed
        };
        for (var r = 0; r <= Rows; r++)
        {
            for (var c = 0; c <= Columns; c++)
            {
                copy.Anchors[r, c] = new Point(Anchors[r, c].X * scale, Anchors[r, c].Y * scale);
            }
        }

        for (var i = 0; i < copy.Across.GetLength(0); i++)
        for (var j = 0; j < copy.Across.GetLength(1); j++)
        for (var k = 0; k < 2; k++)
        {
            copy.Across[i, j, k] = new Point(Across[i, j, k].X * scale, Across[i, j, k].Y * scale);
        }

        for (var i = 0; i < copy.Down.GetLength(0); i++)
        for (var j = 0; j < copy.Down.GetLength(1); j++)
        for (var k = 0; k < 2; k++)
        {
            copy.Down[i, j, k] = new Point(Down[i, j, k].X * scale, Down[i, j, k].Y * scale);
        }

        return copy;
    }

    /// <summary>Where a fine regular grid of the original picture lands ((n + 1)² points, row by row).</summary>
    public Point[] FineGrid(int n)
    {
        var points = new Point[(n + 1) * (n + 1)];
        for (var j = 0; j <= n; j++)
        {
            for (var i = 0; i <= n; i++)
            {
                points[j * (n + 1) + i] = Evaluate(i / (double)n, j / (double)n);
            }
        }

        return points;
    }
}
