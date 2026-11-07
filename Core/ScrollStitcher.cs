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

    /// <summary>The share of the picture that differs between two frames (0–1).</summary>
    private static double ChangedShare(Cells a, Cells b)
    {
        var changed = 0;
        for (var i = 0; i < a.Hash.Length; i++)
        {
            if (a.Hash[i] != b.Hash[i])
            {
                changed++;
            }
        }

        return changed / (double)Math.Max(1, a.Hash.Length);
    }

    /// <summary>The scroll distance of the last frame (content moved up by this many rows; 0 = not placed).</summary>
    public int LastShift { get; private set; }

    private int BodyHeight => _height - _top - _bottom;

    /// <summary>One hash per cell, and whether the cell is a single colour (says nothing about position).</summary>
    private sealed record Cells(ulong[] Hash, bool[] Blank);

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
        var shift = FindShift(_last, cells, out var matched);
        int position;
        if (shift != null)
        {
            CountColumns(frame, shift.Value);
            if (_top < 0)
            {
                Learn(matched, cells);
            }

            position = _viewTop + shift.Value;
        }
        else if (_top >= 0 && Locate(cells) is { } found)
        {
            // Scrolled too far for one step: found again in what was stitched.
            position = found;
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
            added += fresh;
            position = 0;
        }

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
            if (matches < minimum || matches < seen * MinScore)
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

        Score(shift, 1);
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
                    if (value == (before[other + x] & 0x00FFFFFF))
                    {
                        _columnFollowed[x]++;
                    }
                }
            }
        }
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
        var pixels = MemoryMarshal.Cast<byte, uint>(frame);
        for (var y = 0; y < _height; y++)
        {
            var row = y * _width;
            for (var b = 0; b < Bands; b++)
            {
                var value = 14695981039346656037UL;
                var first = pixels[row + _bandStart[b]] & 0x00FFFFFF;
                var uniform = true;
                for (var x = _bandStart[b]; x < _bandStart[b + 1]; x++)
                {
                    var px = pixels[row + x] & 0x00FFFFFF;
                    uniform &= px == first;
                    value = (value ^ px) * 1099511628211UL;
                }

                hash[y * Bands + b] = value;
                blank[y * Bands + b] = uniform;
            }
        }

        return new Cells(hash, blank);
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
