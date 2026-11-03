// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;

namespace Markshot.Core.Recording;

/// <summary>Recording time: runs while recording, stands still while paused (video and sound share it).</summary>
public sealed class RecordClock
{
    private readonly Stopwatch _watch = new();
    private readonly object _gate = new();

    public bool Started { get; private set; }

    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return Started && !_watch.IsRunning;
            }
        }
    }

    public double Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _watch.Elapsed.TotalSeconds;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            Started = true;
            _watch.Restart();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _watch.Stop();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (Started)
            {
                _watch.Start();
            }
        }
    }
}

/// <summary>Win32 pieces used by the recorder.</summary>
internal static class RecordNative
{
    public const uint WDA_NONE = 0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
    public const int CURSOR_SHOWING = 1;
    public const int DI_NORMAL = 3;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Native.POINT Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    /// <summary>Keeps a window out of screen captures / recordings (Windows 10 2004+). Not for per-pixel transparent windows.</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd, bool exclude) =>
        hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
}

/// <summary>
/// Grabs a screen rectangle (physical pixels, virtual-desktop coordinates — may span monitors)
/// at a steady frame rate, draws the mouse pointer and the key / mouse pills on it, and pipes the
/// frames into ffmpeg. Frames follow the <see cref="RecordClock"/>: when capturing falls behind,
/// the last frame is repeated so the video always lasts as long as the recording time.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private readonly Int32Rect _rect;
    private readonly int _fps;
    private readonly RecordClock _clock;
    private readonly string _output;
    private readonly KeyPills? _keys;
    private readonly double _scale;
    private readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = new();
    private Process? _ffmpeg;
    private Stream? _input;
    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _bits;
    private System.Drawing.Bitmap? _overlayBitmap;
    private long _written;
    private readonly StringBuilder _ffmpegError = new();

    public ScreenRecorder(Int32Rect rect, int fps, RecordClock clock, string output, KeyPills? keys, double scale)
    {
        _rect = rect;
        _fps = fps;
        _clock = clock;
        _output = output;
        _keys = keys;
        _scale = scale;
    }

    /// <summary>Set by the capture thread when ffmpeg stopped taking frames.</summary>
    public string? Error { get; private set; }

    /// <summary>Draw the key / mouse pills into the video (操作录制).</summary>
    public volatile bool DrawKeys;

    /// <summary>For tests: a frame source instead of the screen (fills the BGRA buffer).</summary>
    public Action<IntPtr, int, int, long>? Synthetic { get; init; }

    /// <summary>Starts ffmpeg and prepares the capture buffers (before the countdown ends).</summary>
    public async Task PrepareAsync()
    {
        var encoder = await FFmpeg.DetectH264Async();
        var exe = FFmpeg.Path ?? throw new FileNotFoundException("找不到 ffmpeg.exe");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgr0",
            "-video_size", $"{_rect.Width}x{_rect.Height}",
            "-framerate", _fps.ToString(CultureInfo.InvariantCulture),
            "-i", "-"
        };
        // Recording keeps the best quality; size / quality are chosen when saving.
        args.AddRange(FFmpeg.QualityArgs(encoder, RecordQuality.High).Select(a => a == "veryfast" && encoder.Name == "libx264" ? "ultrafast" : a));
        args.AddRange(new[] { "-g", (_fps * 2).ToString(CultureInfo.InvariantCulture), _output });
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg 启动失败");
        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_ffmpegError)
                {
                    _ffmpegError.AppendLine(e.Data);
                }
            }
        };
        _ffmpeg.BeginErrorReadLine();
        _input = _ffmpeg.StandardInput.BaseStream;

        var header = new RecordNative.BITMAPINFOHEADER
        {
            Size = Marshal.SizeOf<RecordNative.BITMAPINFOHEADER>(),
            Width = _rect.Width,
            Height = -_rect.Height, // top-down rows, as ffmpeg expects
            Planes = 1,
            BitCount = 32
        };
        var screen = RecordNative.GetDC(IntPtr.Zero);
        try
        {
            _memoryDc = RecordNative.CreateCompatibleDC(screen);
            _dib = RecordNative.CreateDIBSection(screen, ref header, 0, out _bits, IntPtr.Zero, 0);
        }
        finally
        {
            RecordNative.ReleaseDC(IntPtr.Zero, screen);
        }

        if (_dib == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法分配录制缓冲区");
        }

        RecordNative.SelectObject(_memoryDc, _dib);
        _overlayBitmap = new System.Drawing.Bitmap(_rect.Width, _rect.Height, _rect.Width * 4,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb, _bits);
        Grab(); // the first frame is ready before recording starts
    }

    /// <summary>Starts writing frames (the clock has just started).</summary>
    public void Go()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Markshot screen recorder" };
        _thread.Start();
    }

    /// <summary>Writes the frames up to the current recording time and lets ffmpeg finish the file.</summary>
    public async Task<FFmpeg.Result> StopAsync()
    {
        _stop = true;
        if (_thread != null)
        {
            await Task.Run(() => _thread.Join());
        }

        try
        {
            _input?.Flush();
            _input?.Dispose();
        }
        catch (IOException)
        {
        }

        if (_ffmpeg != null)
        {
            await _ffmpeg.WaitForExitAsync();
            lock (_ffmpegError)
            {
                return new FFmpeg.Result(_ffmpeg.ExitCode, Error ?? _ffmpegError.ToString().Trim());
            }
        }

        return new FFmpeg.Result(-1, "没有开始录制");
    }

    /// <summary>Throws the recording away (retake / exit while recording).</summary>
    public void Abort()
    {
        _stop = true;
        try
        {
            _thread?.Join(1000);
            _input?.Dispose();
            if (_ffmpeg is { HasExited: false })
            {
                _ffmpeg.Kill(true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void Run()
    {
        RecordNative.timeBeginPeriod(1);
        try
        {
            var frameTime = 1.0 / _fps;
            while (!_stop)
            {
                if (_clock.Paused)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var now = _clock.Elapsed;
                var due = _written * frameTime;
                if (now < due)
                {
                    var wait = (int)((due - now) * 1000);
                    Thread.Sleep(Math.Clamp(wait, 0, 15));
                    continue;
                }

                Grab();
                // Every frame that is due by now; when grabbing was slow the same picture fills the gap.
                var target = (long)Math.Floor(_clock.Elapsed / frameTime) + 1;
                if (!WriteFrames(Math.Max(1, target - _written)))
                {
                    return;
                }
            }

            // Up to the end of the recording time.
            var end = (long)Math.Floor(_clock.Elapsed / frameTime) + 1;
            if (end > _written)
            {
                Grab();
                WriteFrames(end - _written);
            }
        }
        finally
        {
            RecordNative.timeEndPeriod(1);
        }
    }

    private unsafe bool WriteFrames(long count)
    {
        var length = _rect.Width * _rect.Height * 4;
        try
        {
            for (var i = 0; i < count; i++)
            {
                _input!.Write(new ReadOnlySpan<byte>((void*)_bits, length));
                _written++;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            lock (_ffmpegError)
            {
                Error = "ffmpeg 停止接收画面：" + _ffmpegError.ToString().Trim();
            }

            Log.Error("录屏写帧失败", ex);
            return false;
        }
    }

    private void Grab()
    {
        if (Synthetic != null)
        {
            Synthetic(_bits, _rect.Width, _rect.Height, _written);
        }
        else
        {
            var screen = RecordNative.GetDC(IntPtr.Zero);
            try
            {
                // No CAPTUREBLT: it makes Windows hide and re-show the mouse pointer on every grab, and
                // the pointer stuttered / felt slow while recording. With desktop composition the
                // composed screen (layered windows included) is read either way.
                RecordNative.BitBlt(_memoryDc, 0, 0, _rect.Width, _rect.Height, screen, _rect.X, _rect.Y, RecordNative.SRCCOPY);
            }
            finally
            {
                RecordNative.ReleaseDC(IntPtr.Zero, screen);
            }

            DrawCursor();
        }

        if (DrawKeys && _keys != null && _overlayBitmap != null)
        {
            using var graphics = System.Drawing.Graphics.FromImage(_overlayBitmap);
            _keys.Draw(graphics, _scale, _rect.Height);
        }
    }

    private void DrawCursor()
    {
        var info = new RecordNative.CURSORINFO { Size = Marshal.SizeOf<RecordNative.CURSORINFO>() };
        if (!RecordNative.GetCursorInfo(ref info) || (info.Flags & RecordNative.CURSOR_SHOWING) == 0 || info.Cursor == IntPtr.Zero)
        {
            return;
        }

        if (!_hotspots.TryGetValue(info.Cursor, out var hotspot))
        {
            if (RecordNative.GetIconInfo(info.Cursor, out var icon))
            {
                hotspot = (icon.HotspotX, icon.HotspotY);
                if (icon.Mask != IntPtr.Zero)
                {
                    RecordNative.DeleteObject(icon.Mask);
                }

                if (icon.Color != IntPtr.Zero)
                {
                    RecordNative.DeleteObject(icon.Color);
                }
            }

            _hotspots[info.Cursor] = hotspot;
        }

        var x = info.Position.X - _rect.X - hotspot.X;
        var y = info.Position.Y - _rect.Y - hotspot.Y;
        if (x < _rect.Width && y < _rect.Height && x > -64 && y > -64)
        {
            RecordNative.DrawIconEx(_memoryDc, x, y, info.Cursor, 0, 0, 0, IntPtr.Zero, RecordNative.DI_NORMAL);
        }
    }

    public void Dispose()
    {
        Abort();
        _overlayBitmap?.Dispose();
        if (_memoryDc != IntPtr.Zero)
        {
            RecordNative.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            RecordNative.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _ffmpeg?.Dispose();
    }
}
