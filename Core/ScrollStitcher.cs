namespace Markshot.Core;

/// <summary>
/// Stitches screenshots of one screen area taken while its content scrolls (长截图). Each new
/// frame is compared row by row with the previous one: rows that stay put at the top / bottom
/// (a sticky header, a status bar) are found first, then the scroll offset of the part in between;
/// the rows that came into view are appended. Works on 32-bit BGRA pixels.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly List<byte[]> _bodyRows = new();
    private readonly List<ulong> _bodyHashes = new();
    private int _viewTop; // where the current frame's body starts inside the stitched body
    private ulong[]? _lastHashes;
    private byte[]? _lastFrame;
    private byte[]? _firstFrame;
    private int _top = -1;
    private int _bottom = -1;

    public ScrollStitcher(int width, int height, int maxHeight = 30000)
    {
        _width = width;
        _height = height;
        _maxHeight = maxHeight;
    }

    /// <summary>Height of the stitched image so far (pixels).</summary>
    public int Height => _firstFrame == null ? 0 : (_top < 0 ? _height : _top + _bodyRows.Count + _bottom);

    public bool Full => Height >= _maxHeight;

    /// <summary>How the last frame was taken in: the scroll distance (0 = no movement / not found).</summary>
    public int LastShift { get; private set; }

    /// <summary>Adds a frame (stride = width × 4). Returns the rows it added.</summary>
    public int Add(byte[] frame)
    {
        var hashes = Hash(frame);
        if (_firstFrame == null)
        {
            _firstFrame = frame;
            _lastFrame = frame;
            _lastHashes = hashes;
            return 0;
        }

        var previous = _lastHashes!;
        if (hashes.AsSpan().SequenceEqual(previous))
        {
            LastShift = 0;
            return 0;
        }

        // Fixed rows at the top / bottom between the two frames.
        var top = 0;
        while (top < _height && hashes[top] == previous[top])
        {
            top++;
        }

        var bottom = 0;
        while (bottom < _height - top && hashes[_height - 1 - bottom] == previous[_height - 1 - bottom])
        {
            bottom++;
        }

        // Keep the narrowest header / footer seen, the first time it is learned.
        if (_top < 0)
        {
            _top = Math.Min(top, _height / 3);
            _bottom = Math.Min(bottom, _height / 3);
            // The first frame's body is the start of the image.
            var firstHashes = Hash(_firstFrame);
            for (var y = _top; y < _height - _bottom; y++)
            {
                _bodyRows.Add(Row(_firstFrame, y));
                _bodyHashes.Add(firstHashes[y]);
            }

            _viewTop = 0;
        }

        var bodyTop = _top;
        var bodyHeight = _height - _top - _bottom;
        // Where does this frame's body sit in what was stitched so far? Looked up around the last
        // position in both directions, so scrolling back up (and down again) never adds rows twice.
        var position = Locate(hashes, bodyTop, bodyHeight);
        if (position < 0)
        {
            LastShift = 0;
            _lastFrame = frame;
            _lastHashes = hashes;
            return 0;
        }

        LastShift = position - _viewTop;
        _viewTop = position;
        var added = 0;
        for (var i = _bodyRows.Count - position; i < bodyHeight && !Full; i++)
        {
            _bodyRows.Add(Row(frame, bodyTop + i));
            _bodyHashes.Add(hashes[bodyTop + i]);
            added++;
        }

        _lastFrame = frame;
        _lastHashes = hashes;
        return added;
    }

    /// <summary>
    /// The offset p in the stitched body where the frame's body row i equals body row p + i over
    /// the most non-blank rows (searched within one screen of the last position); -1 when unsure.
    /// </summary>
    private int Locate(ulong[] now, int top, int height)
    {
        var count = _bodyHashes.Count;
        var minOverlap = Math.Max(8, height / 8);
        var from = Math.Max(0, _viewTop - height);
        var to = Math.Min(count - minOverlap, _viewTop + height);
        var best = -1;
        var bestScore = 0.0;
        var bestDistance = int.MaxValue;
        for (var p = from; p <= to; p++)
        {
            var overlap = Math.Min(height, count - p);
            int matches = 0, informative = 0;
            for (var i = 0; i < overlap; i++)
            {
                var a = now[top + i];
                if (IsBlank(a))
                {
                    continue;
                }

                informative++;
                if (a == _bodyHashes[p + i])
                {
                    matches++;
                }
            }

            if (informative < Math.Min(8, overlap / 4) || matches < Math.Min(12, informative))
            {
                continue;
            }

            var score = matches / (double)informative;
            var distance = Math.Abs(p - _viewTop);
            // Clear agreement; equal scores go to the position nearest the last one.
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance))
            {
                bestScore = score;
                best = p;
                bestDistance = distance;
            }
        }

        return bestScore >= 0.92 ? best : -1;
    }

    private readonly HashSet<ulong> _blank = new();

    private bool IsBlank(ulong hash) => _blank.Contains(hash);

    /// <summary>One hash per row; rows of a single colour are remembered as blank (they match anything).</summary>
    private ulong[] Hash(byte[] frame)
    {
        var hashes = new ulong[_height];
        var stride = _width * 4;
        for (var y = 0; y < _height; y++)
        {
            var span = frame.AsSpan(y * stride, stride);
            var hash = 14695981039346656037UL;
            var first = BitConverter.ToUInt32(span[..4]);
            var uniform = true;
            for (var x = 0; x < stride; x += 4)
            {
                var px = BitConverter.ToUInt32(span.Slice(x, 4)) & 0x00FFFFFF;
                uniform &= px == (first & 0x00FFFFFF);
                hash = (hash ^ px) * 1099511628211UL;
            }

            hashes[y] = hash;
            if (uniform)
            {
                _blank.Add(hash);
            }
        }

        return hashes;
    }

    private byte[] Row(byte[] frame, int y)
    {
        var row = new byte[_width * 4];
        Buffer.BlockCopy(frame, y * _width * 4, row, 0, row.Length);
        return row;
    }

    /// <summary>The stitched image: header of the first frame, the body, footer of the last frame.</summary>
    public BitmapSource Result()
    {
        if (_firstFrame == null)
        {
            throw new InvalidOperationException("no frames");
        }

        if (_top < 0)
        {
            return Bitmap(_firstFrame, _height);
        }

        var height = Height;
        var stride = _width * 4;
        var pixels = new byte[stride * height];
        Buffer.BlockCopy(_firstFrame, 0, pixels, 0, _top * stride);
        for (var i = 0; i < _bodyRows.Count; i++)
        {
            Buffer.BlockCopy(_bodyRows[i], 0, pixels, (_top + i) * stride, stride);
        }

        Buffer.BlockCopy(_lastFrame!, (_height - _bottom) * stride, pixels, (_top + _bodyRows.Count) * stride, _bottom * stride);
        return Bitmap(pixels, height);
    }

    private BitmapSource Bitmap(byte[] pixels, int height)
    {
        var bitmap = BitmapSource.Create(_width, height, 96, 96, PixelFormats.Bgr32, null, pixels, _width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
