namespace Markshot.Core;

/// <summary>
/// Picks out what is under the mouse from the screenshot itself — a run of text, a picture, a button,
/// a block of content — for programs whose UI Automation tree is empty (Electron / Chromium apps with
/// accessibility off, games, remote desktops). Content shows up as edges (a pixel unlike its right or
/// lower neighbour); edges close together are joined (more loosely at each level), and the joined
/// piece under the mouse is the element. Everything is worked out once per window region and kept,
/// so moving the mouse only looks up labels.
/// </summary>
public sealed class ScreenSegmenter
{
    private const int EdgeThreshold = 28;
    private readonly byte[] _pixels; // BGRA of the whole screenshot
    private readonly int _width;
    private readonly int _height;
    private readonly double _scale; // physical pixels per DIP: gaps grow with the display scale
    private readonly object _gate = new();
    private Int32Rect _region = Int32Rect.Empty;
    private Level[]? _levels;

    public ScreenSegmenter(byte[] pixels, int width, int height, double scale)
    {
        _pixels = pixels;
        _width = width;
        _height = height;
        _scale = Math.Max(1, scale);
    }

    /// <summary>One way of joining edges: every pixel's piece and each piece's box.</summary>
    private sealed class Level
    {
        public required int[] Labels;
        public required List<Int32Rect> Boxes;
    }

    /// <summary>Gaps bridged at each level (DIP): characters into a run of text, runs into a block, blocks into a section.</summary>
    private static readonly (double X, double Y)[] Gaps = { (13, 1.5), (22, 9), (40, 28) };

    /// <summary>
    /// The pieces under <paramref name="x"/>, <paramref name="y"/> (screenshot pixels) within
    /// <paramref name="region"/>, smallest first. Pieces that fill most of the region are left out (the window is that).
    /// </summary>
    /// <summary>The pieces of this region are worked out (looking them up is then instant).</summary>
    public bool IsReady(Int32Rect region)
    {
        region = Clip(region);
        lock (_gate)
        {
            return _levels != null && _region.Equals(region);
        }
    }

    /// <summary>Works out the pieces of a region (~100 ms for a big window: off the UI thread).</summary>
    public void Prepare(Int32Rect region) => At(region, region.X, region.Y);

    public List<Int32Rect> At(Int32Rect region, int x, int y)
    {
        region = Clip(region);
        var result = new List<Int32Rect>();
        if (region.Width < 8 || region.Height < 8 || x < region.X || y < region.Y || x >= region.X + region.Width || y >= region.Y + region.Height)
        {
            return result;
        }

        Level[] levels;
        lock (_gate)
        {
            if (_levels == null || !_region.Equals(region))
            {
                _region = region;
                _levels = Build(region);
            }

            levels = _levels;
        }

        var index = (y - region.Y) * region.Width + (x - region.X);
        var regionArea = (double)region.Width * region.Height;
        foreach (var level in levels)
        {
            var label = level.Labels[index];
            Int32Rect box;
            if (label > 0)
            {
                box = level.Boxes[label];
            }
            else
            {
                // Inside a piece but on no edge (the middle of a picture, a card's padding): the smallest piece around.
                box = Int32Rect.Empty;
                var best = double.MaxValue;
                foreach (var candidate in level.Boxes)
                {
                    var area = candidate.Width * (double)candidate.Height;
                    if (area > 0 && area < best && x >= candidate.X && y >= candidate.Y && x < candidate.X + candidate.Width && y < candidate.Y + candidate.Height)
                    {
                        box = candidate;
                        best = area;
                    }
                }

                if (box.IsEmpty)
                {
                    continue;
                }
            }

            if (box.Width * (double)box.Height > regionArea * 0.7 || (box.Width < 4 && box.Height < 4))
            {
                continue;
            }

            if (!result.Any(seen => Similar(seen, box)))
            {
                result.Add(box);
            }
        }

        return result;
    }

    private Int32Rect Clip(Int32Rect r)
    {
        var left = Math.Clamp(r.X, 0, _width);
        var top = Math.Clamp(r.Y, 0, _height);
        var right = Math.Clamp(r.X + r.Width, 0, _width);
        var bottom = Math.Clamp(r.Y + r.Height, 0, _height);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static bool Similar(Int32Rect a, Int32Rect b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 4 && Math.Abs(a.Height - b.Height) <= 4;

    private Level[] Build(Int32Rect region)
    {
        var edges = Edges(region);
        return Gaps.Select(gap => Join(edges, region, (int)Math.Round(gap.X * _scale), (int)Math.Round(gap.Y * _scale))).ToArray();
    }

    /// <summary>Content pixels: brightness differs from the right or lower neighbour (flat backgrounds and soft gradients do not count).</summary>
    private bool[] Edges(Int32Rect region)
    {
        int w = region.Width, h = region.Height;
        var luma = new int[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = ((region.Y + y) * _width + region.X) * 4;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 4;
                luma[y * w + x] = (_pixels[i] * 29 + _pixels[i + 1] * 150 + _pixels[i + 2] * 77) >> 8;
            }
        }

        var edges = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var v = luma[i];
                if ((x + 1 < w && Math.Abs(v - luma[i + 1]) > EdgeThreshold) || (y + 1 < h && Math.Abs(v - luma[i + w]) > EdgeThreshold))
                {
                    edges[i] = true;
                    if (x + 1 < w) edges[i + 1] = true;
                    if (y + 1 < h) edges[i + w] = true;
                }
            }
        }

        // The region's own border (window frame) is no content.
        for (var x = 0; x < w; x++)
        {
            edges[x] = edges[(h - 1) * w + x] = false;
        }

        for (var y = 0; y < h; y++)
        {
            edges[y * w] = edges[y * w + w - 1] = false;
        }

        return edges;
    }

    /// <summary>
    /// Joins edge pixels closer than the gap (a box blur of the mask, done with running sums), labels the
    /// joined pieces and keeps each piece's box around its real edge pixels (not the widened mask).
    /// </summary>
    private static Level Join(bool[] edges, Int32Rect region, int gapX, int gapY)
    {
        int w = region.Width, h = region.Height;
        var rx = Math.Max(0, gapX / 2);
        var ry = Math.Max(0, gapY / 2);

        // Widen horizontally, then vertically.
        var wide = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var count = 0;
            var row = y * w;
            for (var x = -rx; x < w + rx; x++)
            {
                var add = x + rx;
                if (add < w && edges[row + add]) count++;
                var drop = x - rx - 1;
                if (drop >= 0 && edges[row + drop]) count--;
                if (x >= 0 && x < w) wide[row + x] = count > 0;
            }
        }

        var mask = new bool[w * h];
        for (var x = 0; x < w; x++)
        {
            var count = 0;
            for (var y = -ry; y < h + ry; y++)
            {
                var add = y + ry;
                if (add < h && wide[add * w + x]) count++;
                var drop = y - ry - 1;
                if (drop >= 0 && wide[drop * w + x]) count--;
                if (y >= 0 && y < h) mask[y * w + x] = count > 0;
            }
        }

        // Label with a flood fill per piece.
        var labels = new int[w * h];
        var boxes = new List<Int32Rect> { Int32Rect.Empty };
        var stack = new Stack<int>();
        for (var start = 0; start < labels.Length; start++)
        {
            if (!mask[start] || labels[start] != 0)
            {
                continue;
            }

            var label = boxes.Count;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int px = i % w, py = i / w;
                if (edges[i])
                {
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);
                }

                if (px > 0 && mask[i - 1] && labels[i - 1] == 0) { labels[i - 1] = label; stack.Push(i - 1); }
                if (px + 1 < w && mask[i + 1] && labels[i + 1] == 0) { labels[i + 1] = label; stack.Push(i + 1); }
                if (py > 0 && mask[i - w] && labels[i - w] == 0) { labels[i - w] = label; stack.Push(i - w); }
                if (py + 1 < h && mask[i + w] && labels[i + w] == 0) { labels[i + w] = label; stack.Push(i + w); }
            }

            // A little room around the content, as a hand-drawn frame would have.
            const int pad = 2;
            boxes.Add(maxX < 0
                ? Int32Rect.Empty
                : new Int32Rect(
                    region.X + Math.Max(0, minX - pad),
                    region.Y + Math.Max(0, minY - pad),
                    Math.Min(w - 1, maxX + pad) - Math.Max(0, minX - pad) + 1,
                    Math.Min(h - 1, maxY + pad) - Math.Max(0, minY - pad) + 1));
        }

        return new Level { Labels = labels, Boxes = boxes };
    }
}
