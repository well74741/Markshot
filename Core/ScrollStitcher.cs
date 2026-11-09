using System.Runtime.InteropServices;

namespace Markshot.Core;

/// <summary>
/// Stitches screenshots of one screen area taken while its content scrolls (长截图), either way:
/// rows that come into view below are appended, rows that come into view above (a chat window
/// scrolled up) go on top. Each frame is split into cells (rows × <see cref="Bands"/> vertical
/// bands); only cells that changed say anything, and a band counts only when most of its cells
/// agree on the scroll distance. So side panels, scroll bars and small animations inside the area do
/// not stop the stitching. Rows that never move at the top / bottom (a sticky header, a status bar)
/// are kept once, and fixed side panels are left out of the result. Works on 32-bit BGRA pixels.
/// </summary>
public sealed class ScrollStitcher
{
    private const int Bands = 16;
    private const double MinScore = 0.85;

    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly int[] _bandStart = new int[Bands + 1];
    private readonly List<byte[]> _bodyRows = new();
    private readonly List<ulong[]> _bodyCells = new();
    private readonly List<byte[]> _bodyMeans = new(); // each stitched row's cells by brightness (tolerant matching)
    private readonly bool[] _movingBands = new bool[Bands];
    private readonly int[] _columnChanged;
    private readonly int[] _columnFollowed;
    private byte[]? _lastFrame;
    private Cells? _last;
    private byte[]? _headerFrame;
    private byte[]? _footerFrame;
    private int _top = -1;
    private int _bottom = -1;
    private int _viewTop; // where the last frame's body starts in the stitched body
    private double _viewFraction; // … and the part of a row it is off by (pages scrolled by fractions of a pixel)
    private double _phase; // the fraction found by the last tolerant match
    private double _exactRatio; // how many of the cells the last exact match explained

    public ScrollStitcher(int width, int height, int maxHeight = 30000)
    {
        _width = width;
        _height = height;
        _maxHeight = maxHeight;
        _columnChanged = new int[width];
        _columnFollowed = new int[width];
        for (var b = 0; b <= Bands; b++)
        {
            _bandStart[b] = width * b / Bands;
        }
    }

    /// <summary>Height of the stitched image so far (pixels).</summary>
    public int Height => _lastFrame == null ? 0 : (_top < 0 ? _height : _top + _bodyRows.Count + _bottom);

    public bool Full => Height >= _maxHeight;

    /// <summary>What happened to a frame (the preview tells the user).</summary>
    public enum Outcome
    {
        /// <summary>The first frame, nothing to compare with yet.</summary>
        First,

        /// <summary>Nothing moved.</summary>
        Same,

        /// <summary>Placed (rows added, or scrolled back over what is there).</summary>
        Placed,

        /// <summary>Most of the picture changed and no overlap with the last frame: scrolled too far between two frames.</summary>
        TooFar,

        /// <summary>Only a part changed, nothing moved as a whole: an animation, a video, a hover effect.</summary>
        Animating,

        /// <summary>Found, but it would leave a gap in the image.</summary>
        Gap,

        /// <summary>The maximum length is reached.</summary>
        Full
    }

    public Outcome LastOutcome { get; private set; } = Outcome.First;

    /// <summary>Frames placed / frames that changed but could not be placed.</summary>
    public int PlacedFrames { get; private set; }

    public int LostFrames { get; private set; }

    /// <summary>The body rows one frame shows: a step close to this leaves almost no overlap.</summary>
    public int ViewHeight => _top < 0 ? _height : BodyHeight;

    /// <summary>
    /// The share of what is drawn that differs between two frames (0–1). Empty cells (one colour in both) are not
    /// counted: on a page with much white space a real scroll changed less than half of all cells and was reported as
    /// 只有局部在变.
    /// </summary>
    private static double ChangedShare(Cells a, Cells b)
    {
        int changed = 0, drawn = 0;
        for (var i = 0; i < a.Hash.Length; i++)
        {
            if (a.Blank[i] && b.Blank[i] && a.Hash[i] == b.Hash[i])
            {
                continue;
            }

            drawn++;
            if (a.Hash[i] != b.Hash[i])
            {
                changed++;
            }
        }

        return changed / (double)Math.Max(1, drawn);
    }

    /// <summary>The scroll distance of the last frame (content moved up by this many rows; 0 = not placed).</summary>
    public int LastShift { get; private set; }

    private int BodyHeight => _height - _top - _bottom;

    /// <summary>One hash per cell, and whether the cell is a single colour (says nothing about position).</summary>
    private sealed record Cells(ulong[] Hash, bool[] Blank, byte[] Mean);

    /// <summary>Adds a frame (stride = width × 4). Returns the rows it added.</summary>
    public int Add(byte[] frame)
    {
        var cells = HashCells(frame);
        if (_last == null)
        {
            _lastFrame = frame;
            _last = cells;
            return 0;
        }

        if (cells.Hash.AsSpan().SequenceEqual(_last.Hash))
        {
            LastShift = 0;
            LastOutcome = Outcome.Same;
            return 0;
        }

        // How far did the content move since the last placed frame?
        // Pixel-exact first; then by brightness, with some tolerance: many programs scroll by a fraction of a pixel
        // (display scaling, smooth scrolling), so the moved rows are resampled and never equal exactly.
        _phase = 0;
        var shift = FindShift(_last, cells, out var matched);
        // Not quite exact: a page scrolled by a fraction, its flat insides (the middle rows of text) equal one row off
        // as well. The tolerant match tells the fraction (lost otherwise: half a row each time adds up to a doubled row).
        if (shift is { } whole && _exactRatio < 0.97 && FindShiftLoosely(_last, cells, out var loose) is { } refined && Math.Abs(refined - whole) <= 1)
        {
            shift = refined;
            matched = loose;
        }
        else if (shift != null)
        {
            _phase = 0;
        }

        shift ??= FindShiftLoosely(_last, cells, out matched);
        int position;
        var fraction = 0.0;
        if (shift != null)
        {
            CountColumns(frame, shift.Value);
            if (_top < 0)
            {
                Learn(matched, cells);
            }

            // Fractions add up over the frames (each frame is compared with the one before); then the place is checked
            // against what was stitched (a fixed reference: an error of a fraction of a row does not add up, and a
            // frame of a page scrolled by fractions that matched one row off exactly is put right).
            var exact = _viewTop + _viewFraction + shift.Value + _phase;
            position = Anchor(cells, (int)Math.Round(exact), 2);
            fraction = Math.Clamp(exact - position, -0.5, 0.5);
        }
        else if (_top >= 0 && (Locate(cells) ?? LocateLoosely(cells)) is { } found)
        {
            // Scrolled too far for one step (or back over what was stitched): found again in what was stitched.
            position = Anchor(cells, found, 2);
        }
        else
        {
            // Only something animating, or a jump that cannot be placed: wait for a frame that can.
            LastShift = 0;
            LastOutcome = ChangedShare(_last, cells) >= 0.5 ? Outcome.TooFar : Outcome.Animating;
            LostFrames++;
            return 0;
        }

        var body = BodyHeight;
        if (position > _bodyRows.Count || position < -body)
        {
            LastShift = 0; // it would leave a gap
            LastOutcome = Outcome.Gap;
            LostFrames++;
            return 0;
        }

        LastShift = position - _viewTop;
        var added = 0;
        if (position < 0)
        {
            // Scrolled up past the start: the new rows go on top.
            var fresh = -position;
            if (Height + fresh > _maxHeight)
            {
                LastShift = 0;
                LastOutcome = Outcome.Full;
                return 0;
            }

            _bodyRows.InsertRange(0, Enumerable.Range(0, fresh).Select(j => Row(frame, _top + j)));
            _bodyCells.InsertRange(0, Enumerable.Range(0, fresh).Select(j => RowCells(cells.Hash, _top + j)));
            _bodyMeans.InsertRange(0, Enumerable.Range(0, fresh).Select(j => RowCells(cells.Mean, _top + j)));
            added += fresh;
            position = 0;
        }

        _viewFraction = position == _viewTop + LastShift ? fraction : 0;
        _viewTop = position;
        for (var j = _bodyRows.Count - position; j < body && !Full; j++)
        {
            AddRow(frame, cells, _top + j);
            added++;
        }

        // The rows just beyond the ends of the body are continuous with these frames: header and footer come from them.
        if (position == 0)
        {
            _headerFrame = frame;
        }

        if (position + body >= _bodyRows.Count)
        {
            _footerFrame = frame;
        }

        _lastFrame = frame;
        _last = cells;
        // A blinking caret or a small animation with the page standing still: no move, nothing wrong either.
        if (LastShift != 0 || added > 0)
        {
            PlacedFrames++;
            LastOutcome = Full ? Outcome.Full : Outcome.Placed;
        }
        else
        {
            LastOutcome = Outcome.Same;
        }

        return added;
    }

    private void AddRow(byte[] frame, Cells cells, int y)
    {
        _bodyRows.Add(Row(frame, y));
        _bodyCells.Add(RowCells(cells.Hash, y));
        _bodyMeans.Add(RowCells(cells.Mean, y));
    }

    /// <summary>A row stayed where it was (a spinner or a caret in it is tolerated).</summary>
    private static bool Unchanged(Cells a, Cells b, int y)
    {
        var changed = 0;
        for (var band = 0; band < Bands; band++)
        {
            if (a.Hash[y * Bands + band] != b.Hash[y * Bands + band])
            {
                changed++;
            }
        }

        return changed <= 3;
    }

    /// <summary>
    /// The first time the content moves: rows at the top / bottom that stayed put are the header /
    /// footer, and the frame before the move starts the stitched body.
    /// </summary>
    private void Learn(bool[] matchedRows, Cells current)
    {
        int Run(int start, int step)
        {
            var run = 0;
            for (var y = start; y >= 0 && y < _height && !matchedRows[y] && Unchanged(current, _last!, y); y += step)
            {
                run++;
            }

            return run;
        }

        _top = Math.Min(Run(0, 1), _height / 3);
        _bottom = Math.Min(Run(_height - 1, -1), _height / 3);
        _headerFrame = _lastFrame;
        _footerFrame = _lastFrame;
        for (var y = _top; y < _height - _bottom; y++)
        {
            AddRow(_lastFrame!, _last!, y);
        }

        _viewTop = 0;
    }

    /// <summary>
    /// The distance d (−height &lt; d &lt; height, not 0) for which the cells that changed since the
    /// previous frame equal the previous frame's cells d rows lower; null when no distance fits well.
    /// <paramref name="matchedRows"/>: the rows that took part in the match.
    /// </summary>
    private int? FindShift(Cells previous, Cells current, out bool[] matchedRows)
    {
        matchedRows = new bool[_height];
        // Rows that stay put at the edges (a header) cannot match anything that scrolled.
        var low = 0;
        while (low < _height && Unchanged(current, previous, low))
        {
            low++;
        }

        var high = _height;
        while (high > low && Unchanged(current, previous, high - 1))
        {
            high--;
        }

        // Cells that changed in place and are not a flat colour: only they say where the content went.
        var informative = new bool[_height * Bands];
        var count = 0;
        for (var i = 0; i < informative.Length; i++)
        {
            if (current.Hash[i] != previous.Hash[i] && !current.Blank[i])
            {
                informative[i] = true;
                count++;
            }
        }

        if (count < 4)
        {
            return null;
        }

        var explained = Explained(informative);
        var bandMatches = new int[Bands];
        var bandSeen = new int[Bands];
        (int Matches, int Seen) Score(int d, int step)
        {
            Array.Clear(bandMatches);
            Array.Clear(bandSeen);
            var from = Math.Max(low, low - d);
            var to = Math.Min(high, high - d);
            for (var y = from; y < to; y += step)
            {
                var row = y * Bands;
                var other = (y + d) * Bands;
                for (var b = 0; b < Bands; b++)
                {
                    if (informative[row + b])
                    {
                        bandSeen[b]++;
                        if (current.Hash[row + b] == previous.Hash[other + b])
                        {
                            bandMatches[b]++;
                        }
                    }
                }
            }

            // A band part panel, part page never agrees: it does not count against the others.
            int matches = 0, seen = 0;
            for (var b = 0; b < Bands; b++)
            {
                if (bandSeen[b] >= 2 && bandMatches[b] >= bandSeen[b] * 0.6)
                {
                    matches += bandMatches[b];
                    seen += bandSeen[b];
                }
            }

            return (matches, seen);
        }

        static double Rank(int matches, int seen)
        {
            var ratio = matches / (double)Math.Max(1, seen);
            return matches * ratio * ratio * ratio * ratio;
        }

        // A coarse pass on every 4th row, then the best few checked on every row.
        var candidates = new List<(int D, double Rank)>();
        for (var d = -(_height - 8); d <= _height - 8; d++)
        {
            if (d != 0)
            {
                var (matches, seen) = Score(d, 4);
                if (matches >= 2)
                {
                    candidates.Add((d, Rank(matches, seen)));
                }
            }
        }

        int? best = null;
        var bestRank = 0.0;
        var minimum = Math.Min(12, Math.Max(4, count / 2));
        foreach (var (d, _) in candidates.OrderByDescending(c => c.Rank).Take(8))
        {
            var (matches, seen) = Score(d, 1);
            if (matches < minimum || matches < seen * MinScore || !explained(d, low, high, matches))
            {
                continue;
            }

            var rank = Rank(matches, seen);
            if (rank > bestRank + 1e-9 || (Math.Abs(rank - bestRank) <= 1e-9 && best is { } other && Math.Abs(d - LastShift) < Math.Abs(other - LastShift)))
            {
                best = d;
                bestRank = rank;
            }
        }

        if (best is not { } shift)
        {
            return null;
        }

        var (bestMatches, bestSeen) = Score(shift, 1);
        _exactRatio = bestMatches / (double)Math.Max(1, bestSeen);
        var agreeing = new bool[Bands];
        for (var b = 0; b < Bands; b++)
        {
            agreeing[b] = bandSeen[b] >= 2 && bandMatches[b] >= bandSeen[b] * 0.6;
            _movingBands[b] |= agreeing[b];
        }

        for (var y = Math.Max(0, -shift); y < Math.Min(_height, _height - shift); y++)
        {
            for (var b = 0; b < Bands; b++)
            {
                if (agreeing[b] && informative[y * Bands + b] && current.Hash[y * Bands + b] == previous.Hash[(y + shift) * Bands + b])
                {
                    matchedRows[y] = true;
                    break;
                }
            }
        }

        return shift;
    }

    /// <summary>
    /// <see cref="FindShift"/> by each cell's mean brightness (a difference of a few levels counts as the same):
    /// content moved by a fraction of a pixel is redrawn slightly differently.
    /// </summary>
    private static readonly double[] Phases = { 0, 0.25, 0.5, 0.75 };

    private int? FindShiftLoosely(Cells previous, Cells current, out bool[] matchedRows)
    {
        const int Same = 6;
        matchedRows = new bool[_height];
        bool Still(int y)
        {
            var changed = 0;
            for (var b = 0; b < Bands; b++)
            {
                if (Math.Abs(current.Mean[y * Bands + b] - previous.Mean[y * Bands + b]) > Same)
                {
                    changed++;
                }
            }

            return changed <= 3;
        }

        var low = 0;
        while (low < _height && Still(low))
        {
            low++;
        }

        var high = _height;
        while (high > low && Still(high - 1))
        {
            high--;
        }

        if (high - low < 16)
        {
            return null;
        }

        // Cells that changed and have something in them (a flat cell matches anywhere).
        var informative = new bool[_height * Bands];
        var count = 0;
        for (var y = low; y < high; y++)
        {
            for (var b = 0; b < Bands; b++)
            {
                var i = y * Bands + b;
                if (!current.Blank[i] && Math.Abs(current.Mean[i] - previous.Mean[i]) > Same)
                {
                    informative[i] = true;
                    count++;
                }
            }
        }

        if (count < 24)
        {
            return null;
        }

        var explained = Explained(informative);

        var bandMatches = new int[Bands];
        var bandSeen = new int[Bands];
        // Moved by d + t rows (0 <= t < 1): a row now is the previous rows d and d + 1 blended (the brightness of a
        // cell is linear in its pixels, so the blend of the cells' brightness).
        // t < 0: any blend (the value between the two rows) — the previous frame may be a blend itself, the two
        // blends of blends do not equal exactly.
        bool Fits(int i, int other, double t)
        {
            if (t >= 0)
            {
                return Math.Abs(current.Mean[i] - (t == 0 ? previous.Mean[other] : previous.Mean[other] * (1 - t) + previous.Mean[other + Bands] * t)) <= Same;
            }

            int a = previous.Mean[other], b = previous.Mean[other + Bands], value = current.Mean[i];
            return value >= Math.Min(a, b) - Same && value <= Math.Max(a, b) + Same;
        }

        (int Matches, int Seen) Score(int d, int step, double t = 0)
        {
            Array.Clear(bandMatches);
            Array.Clear(bandSeen);
            var from = Math.Max(low, low - d);
            var to = Math.Min(high, high - d - (t != 0 ? 1 : 0));
            for (var y = from; y < to; y += step)
            {
                for (var b = 0; b < Bands; b++)
                {
                    var i = y * Bands + b;
                    if (informative[i])
                    {
                        bandSeen[b]++;
                        if (Fits(i, (y + d) * Bands + b, t))
                        {
                            bandMatches[b]++;
                        }
                    }
                }
            }

            int matches = 0, seen = 0;
            for (var b = 0; b < Bands; b++)
            {
                if (bandSeen[b] >= 4 && bandMatches[b] >= bandSeen[b] * 0.7)
                {
                    matches += bandMatches[b];
                    seen += bandSeen[b];
                }
            }

            return (matches, seen);
        }

        int? best = null;
        var bestRank = 0.0;
        var candidates = new List<(int D, double Rank)>();
        for (var d = -(_height - 16); d <= _height - 16; d++)
        {
            if (d == 0)
            {
                continue;
            }

            var (matches, seen) = Score(d, 3, -1); // (any fraction: one with no fraction drops the true distance when the rows are blended half and half)
            if (matches >= 4)
            {
                var ratio = matches / (double)Math.Max(1, seen);
                candidates.Add((d, matches * Math.Pow(ratio, 6)));
            }
        }

        // (and the row before each: moved by d - 1 and most of a row looks like d)
        foreach (var d in candidates.OrderByDescending(c => c.Rank).Take(6).SelectMany(c => new[] { c.D, c.D - 1 }).Where(d => d != 0).Distinct())
        {
            var (matches, seen) = Score(d, 1, -1);
            if (matches < 16 || matches < seen * 0.9 || !explained(d, low, high, matches))
            {
                continue;
            }

            var ratio = matches / (double)Math.Max(1, seen);
            var rank = matches * Math.Pow(ratio, 6);
            if (rank > bestRank)
            {
                best = d;
                bestRank = rank;
            }
        }

        if (best is not { } shift)
        {
            return null;
        }

        // The fraction t (for where the rows go; the match itself is settled): a row now ≈ a + t·(b − a) for the
        // previous rows a, b at the distance; least squares over the cells that match and have an edge to tell by.
        // (Counting cells within the tolerance could not tell 0.25 from 0.5: rows went one off every few frames.)
        double sumXy = 0, sumXx = 0;
        for (var y = Math.Max(low, low - shift); y < Math.Min(high, high - shift - 1); y++)
        {
            for (var b = 0; b < Bands; b++)
            {
                var i = y * Bands + b;
                var other = (y + shift) * Bands + b;
                if (!informative[i] || !Fits(i, other, -1))
                {
                    continue;
                }

                double a = previous.Mean[other], step = previous.Mean[other + Bands] - a;
                if (Math.Abs(step) >= 3)
                {
                    sumXy += (current.Mean[i] - a) * step;
                    sumXx += step * step;
                }
            }
        }

        var bestPhase = sumXx > 0 ? Math.Clamp(sumXy / sumXx, 0, 0.999) : 0;
        _phase = bestPhase;
        Score(shift, 1, -1);
        for (var b = 0; b < Bands; b++)
        {
            _movingBands[b] |= bandSeen[b] >= 4 && bandMatches[b] >= bandSeen[b] * 0.7;
        }

        for (var y = Math.Max(low, low - shift); y < Math.Min(high, high - shift); y++)
        {
            for (var b = 0; b < Bands; b++)
            {
                if (informative[y * Bands + b] && y + shift + 1 < _height && Fits(y * Bands + b, (y + shift) * Bands + b, -1))
                {
                    matchedRows[y] = true;
                    break;
                }
            }
        }

        return shift;
    }

    /// <summary>
    /// Whether a distance accounts for most of what changed where the two frames overlap. Bands that disagree are left
    /// out of a match (a fixed side panel, a scroll bar), so without this a few look-alike columns (the same avatar
    /// on every item of a list) could carry a wrong distance against all the rest: the page then stitched twice.
    /// </summary>
    private Func<int, int, int, int, bool> Explained(bool[] informative)
    {
        var before = new int[_height + 1];
        for (var y = 0; y < _height; y++)
        {
            var n = 0;
            for (var b = 0; b < Bands; b++)
            {
                if (informative[y * Bands + b])
                {
                    n++;
                }
            }

            before[y + 1] = before[y] + n;
        }

        return (d, low, high, matches) =>
        {
            var from = Math.Max(low, low - d);
            var to = Math.Min(high, high - d);
            return to <= from || matches >= (before[to] - before[from]) * 0.6;
        };
    }

    /// <summary>
    /// How far the frame's body differs from the stitched rows when its top is at row p of the stitched body (mean
    /// brightness difference per cell; NaN when they overlap by too little to tell).
    /// </summary>
    private double Misfit(Cells current, int p, int step = 1)
    {
        var body = BodyHeight;
        var count = _bodyMeans.Count;
        double sum = 0;
        var seen = 0;
        for (var j = Math.Max(0, -p); j < body && p + j < count; j += step)
        {
            var row = (_top + j) * Bands;
            var stored = _bodyMeans[p + j];
            for (var band = 0; band < Bands; band++)
            {
                if (_movingBands[band])
                {
                    sum += Math.Abs(current.Mean[row + band] - stored[band]);
                    seen++;
                }
            }
        }

        return seen < 8 * Bands / Math.Max(1, step) / 2 ? double.NaN : sum / seen;
    }

    /// <summary>The place near <paramref name="position"/> where the frame fits what was stitched best.</summary>
    private int Anchor(Cells current, int position, int reach)
    {
        var here = Misfit(current, position);
        if (double.IsNaN(here))
        {
            return position;
        }

        var best = position;
        var bestMisfit = here;
        for (var p = position - reach; p <= position + reach; p++)
        {
            var misfit = Misfit(current, p);
            if (p != position && !double.IsNaN(misfit) && misfit < bestMisfit * 0.8)
            {
                best = p;
                bestMisfit = misfit;
            }
        }

        return best;
    }

    /// <summary>
    /// <see cref="Locate"/> by brightness (pages scrolled by fractions of a pixel never repeat a row exactly): the place
    /// within two screens where nearly every cell is as stitched (or between two stitched rows), and no other place
    /// fits about as well (look-alike content: rather wait for the next frame than stitch it twice).
    /// </summary>
    private int? LocateLoosely(Cells current)
    {
        const int Same = 6;
        var body = BodyHeight;
        var count = _bodyMeans.Count;
        var minOverlap = Math.Max(16, body / 4);
        var from = Math.Max(-(body - minOverlap), _viewTop - body * 2);
        var to = Math.Min(count - minOverlap, _viewTop + body * 2);
        (double Score, int Seen) Fit(int p, int step)
        {
            int matches = 0, seen = 0;
            for (var j = Math.Max(0, -p); j < body && p + j + 1 < count; j += step)
            {
                var row = (_top + j) * Bands;
                var stored = _bodyMeans[p + j];
                var next = _bodyMeans[p + j + 1];
                for (var band = 0; band < Bands; band++)
                {
                    if (_movingBands[band] && !current.Blank[row + band])
                    {
                        seen++;
                        int value = current.Mean[row + band], a = stored[band], b = next[band];
                        if (value >= Math.Min(a, b) - Same && value <= Math.Max(a, b) + Same)
                        {
                            matches++;
                        }
                    }
                }
            }

            return (matches / (double)Math.Max(1, seen), seen);
        }

        var scores = new List<(int P, double Score)>();
        for (var p = from; p <= to; p += 2)
        {
            var (score, seen) = Fit(p, 3);
            if (seen >= 24)
            {
                scores.Add((p, score));
            }
        }

        if (scores.Count == 0)
        {
            return null;
        }

        var (near, top) = scores.MaxBy(s => s.Score);
        // Another place far from it fitting nearly as well: look-alike content, not to be trusted.
        if (scores.Any(s => Math.Abs(s.P - near) > 6 && s.Score > top - 0.04))
        {
            return null;
        }

        int? best = null;
        var bestScore = 0.0;
        for (var p = Math.Max(from, near - 3); p <= Math.Min(to, near + 3); p++)
        {
            var (score, seen) = Fit(p, 1);
            if (seen >= 24 && score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return bestScore >= 0.9 ? best : null;
    }

    /// <summary>
    /// The frame's body found in the stitched body (within two screens of the last position, above
    /// the start too); null when it does not fit well. Only bands seen moving count.
    /// </summary>
    private int? Locate(Cells current)
    {
        var body = BodyHeight;
        var count = _bodyCells.Count;
        var minOverlap = Math.Max(8, body / 4);
        var from = Math.Max(-(body - minOverlap), _viewTop - body * 2);
        var to = Math.Min(count - minOverlap, _viewTop + body * 2);
        (double Score, int Seen) Fit(int p, int step)
        {
            int matches = 0, seen = 0;
            for (var j = Math.Max(0, -p); j < body && p + j < count; j += step)
            {
                var row = (_top + j) * Bands;
                var stored = _bodyCells[p + j];
                for (var band = 0; band < Bands; band++)
                {
                    if (_movingBands[band] && !current.Blank[row + band])
                    {
                        seen++;
                        if (current.Hash[row + band] == stored[band])
                        {
                            matches++;
                        }
                    }
                }
            }

            return (matches / (double)Math.Max(1, seen), seen);
        }

        int? coarse = null;
        var coarseScore = 0.0;
        for (var p = from; p <= to; p++)
        {
            var (score, seen) = Fit(p, 4);
            if (seen >= 8 && score > coarseScore)
            {
                coarseScore = score;
                coarse = p;
            }
        }

        if (coarse is not { } near)
        {
            return null;
        }

        int? best = null;
        var bestScore = 0.0;
        for (var p = Math.Max(from, near - 4); p <= Math.Min(to, near + 4); p++)
        {
            var (score, seen) = Fit(p, 1);
            if (seen >= 8 && score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return bestScore >= 0.9 ? best : null;
    }

    /// <summary>For each column: did pixels change in place, and did they follow the scroll? (Fixed panels do neither; a scroll bar only changes.)</summary>
    private void CountColumns(byte[] frame, int shift)
    {
        var now = MemoryMarshal.Cast<byte, uint>(frame);
        var before = MemoryMarshal.Cast<byte, uint>(_lastFrame);
        for (var y = Math.Max(0, -shift); y < Math.Min(_height, _height - shift); y += 2)
        {
            var row = y * _width;
            var other = (y + shift) * _width;
            for (var x = 0; x < _width; x++)
            {
                var value = now[row + x] & 0x00FFFFFF;
                if (value != (before[row + x] & 0x00FFFFFF))
                {
                    _columnChanged[x]++;
                    if (value == (before[other + x] & 0x00FFFFFF) || Between(value, before[other + x], other + _width < before.Length ? before[other + _width + x] : before[other + x]))
                    {
                        _columnFollowed[x]++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// A pixel of a page scrolled by a fraction of a row: between the two pixels it was resampled from (green, which
    /// carries most of the brightness, with a little tolerance). Without this the columns of such a page did not count
    /// as following the scroll, and its edges (an avatar column, a margin) were cut off as a fixed panel.
    /// </summary>
    private static bool Between(uint value, uint a, uint b)
    {
        int g = (int)((value >> 8) & 0xFF), ga = (int)((a >> 8) & 0xFF), gb = (int)((b >> 8) & 0xFF);
        return g >= Math.Min(ga, gb) - 6 && g <= Math.Max(ga, gb) + 6;
    }

    /// <summary>
    /// The columns of the result: the scrolling part, without fixed panels and scroll bars beside it
    /// (they would repeat all the way down). Empty margins stay.
    /// </summary>
    public (int Left, int Width) KeptColumns()
    {
        bool Moving(int x) => _columnChanged[x] >= 3 && _columnFollowed[x] >= _columnChanged[x] * 0.8;
        var left = 0;
        while (left < _width && !Moving(left))
        {
            left++;
        }

        if (left == _width || _bodyRows.Count == 0)
        {
            return (0, _width);
        }

        var right = _width - 1;
        while (right > left && !Moving(right))
        {
            right--;
        }

        // A side is cut only when something stands there; a little of the empty edge stays with the content.
        var keepLeft = HasContent(0, left) ? Margin(left, -1) : 0;
        var keepRight = HasContent(right + 1, _width) ? Margin(right, 1) : _width - 1;
        return (keepLeft, keepRight - keepLeft + 1);
    }

    private int Margin(int from, int step)
    {
        var x = from;
        for (var i = 0; i < 12 && x + step >= 0 && x + step < _width && Flat(x + step, x + step + 1); i++)
        {
            x += step;
        }

        return x;
    }

    private bool HasContent(int from, int to) => from < to && !Flat(from, to);

    /// <summary>The columns hold one colour in the first rows of the stitched body.</summary>
    private bool Flat(int from, int to)
    {
        var first = BitConverter.ToUInt32(_bodyRows[0], from * 4) & 0x00FFFFFF;
        foreach (var row in _bodyRows.Take(_height))
        {
            for (var x = from; x < to; x++)
            {
                if ((BitConverter.ToUInt32(row, x * 4) & 0x00FFFFFF) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private Cells HashCells(byte[] frame)
    {
        var hash = new ulong[_height * Bands];
        var blank = new bool[_height * Bands];
        var mean = new byte[_height * Bands];
        var pixels = MemoryMarshal.Cast<byte, uint>(frame);
        for (var y = 0; y < _height; y++)
        {
            var row = y * _width;
            for (var b = 0; b < Bands; b++)
            {
                var value = 14695981039346656037UL;
                var first = pixels[row + _bandStart[b]] & 0x00FFFFFF;
                var uniform = true;
                var light = 0L;
                for (var x = _bandStart[b]; x < _bandStart[b + 1]; x++)
                {
                    var px = pixels[row + x] & 0x00FFFFFF;
                    uniform &= px == first;
                    value = (value ^ px) * 1099511628211UL;
                    light += ((px >> 16) & 0xFF) * 3 + ((px >> 8) & 0xFF) * 6 + (px & 0xFF);
                }

                hash[y * Bands + b] = value;
                blank[y * Bands + b] = uniform;
                mean[y * Bands + b] = (byte)(light / (10 * Math.Max(1, _bandStart[b + 1] - _bandStart[b])));
            }
        }

        return new Cells(hash, blank, mean);
    }

    private static T[] RowCells<T>(T[] cells, int y) => cells.AsSpan(y * Bands, Bands).ToArray();

    private byte[] Row(byte[] frame, int y)
    {
        var row = new byte[_width * 4];
        Buffer.BlockCopy(frame, y * _width * 4, row, 0, row.Length);
        return row;
    }

    /// <summary>The stitched image: header, body, footer (the frames at the two ends of the body supply those).</summary>
    public BitmapSource Result()
    {
        if (_lastFrame == null)
        {
            throw new InvalidOperationException("no frames");
        }

        if (_top < 0)
        {
            return Bitmap(_lastFrame, _width, _height);
        }

        var (left, width) = KeptColumns();
        var height = Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        void Copy(byte[] source, int sourceRow, int rows, int targetRow)
        {
            for (var y = 0; y < rows; y++)
            {
                Buffer.BlockCopy(source, (sourceRow + y) * _width * 4 + left * 4, pixels, (targetRow + y) * stride, stride);
            }
        }

        Copy(_headerFrame!, 0, _top, 0);
        for (var i = 0; i < _bodyRows.Count; i++)
        {
            Buffer.BlockCopy(_bodyRows[i], left * 4, pixels, (_top + i) * stride, stride);
        }

        Copy(_footerFrame!, _height - _bottom, _bottom, _top + _bodyRows.Count);
        return Bitmap(pixels, width, height);
    }

    private static BitmapSource Bitmap(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
