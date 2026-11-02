// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using Markshot.Core.Recording;

namespace Markshot.UI.Recording;

public enum RecordPhase
{
    Idle,
    Setup,
    Countdown,
    Starting,
    Recording,
    Paused,
    Finishing,
    Preview
}

/// <summary>A finished recording waiting in the preview.</summary>
public sealed class RecordTake
{
    public required string File { get; init; }

    public required double Duration { get; set; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Fps { get; init; }

    public required bool HasAudio { get; init; }

    public bool Saved { get; set; }

    public bool Copied { get; set; }
}

/// <summary>
/// 录屏: the one place that owns the recorder state (area, phase, options, the take) and the
/// three windows — the frame around the area, the toolbar, and the preview that replaces the
/// area after stopping. Flow: setup → (countdown) → recording ⇄ paused → finishing → preview.
/// </summary>
public sealed class RecorderController
{
    public const double MinWidthDip = 96;
    public const double MinHeightDip = 64;
    public static readonly int[] FpsChoices = { 15, 24, 30, 60 };
    public static readonly (int W, int H)[] SizePresets = { (640, 360), (1280, 720), (1920, 1080) };
    public static readonly double[] Speeds = { 0.2, 0.5, 0.8, 1, 1.5, 2, 4, 8 };

    private readonly RecorderConfig _config;
    private readonly Action _saveConfig;
    private readonly KeyPills _pills = new();
    private readonly InputHook _hook;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _countdown;
    private RecordFrameWindow? _frame;
    private RecordBarWindow? _bar;
    private RecordPreviewWindow? _preview;
    private RecordClock? _clock;
    private ScreenRecorder? _video;
    private AudioTrack? _system;
    private AudioTrack? _microphone;
    private Task? _prepare;
    private string? _session;
    private int _generation;
    private bool _uiHidden;

    public RecorderController(RecorderConfig config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
        _hook = new InputHook(_pills);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => Changed?.Invoke();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => CountdownTick();
        // The taskbar button's right-click menu follows the state.
        var jumpPhase = RecordPhase.Idle;
        Changed += () =>
        {
            if (Phase != jumpPhase)
            {
                jumpPhase = Phase;
                RecorderCommands.UpdateJumpList(Phase);
            }
        };
    }

    /// <summary>A command from the taskbar menu (start / stop / pause / exit).</summary>
    public void Command(string command)
    {
        if (!IsOpen)
        {
            return;
        }

        switch (command)
        {
            case "start" when Phase == RecordPhase.Setup:
                Start();
                break;
            case "stop":
                _ = StopAsync();
                break;
            case "pause":
                TogglePause();
                break;
            case "exit":
                RequestClose();
                break;
        }
    }

    public event Action? Changed;

    /// <summary>A short message for the toolbar (saved, failed…).</summary>
    public event Action<string>? Notice;

    public RecordPhase Phase { get; private set; } = RecordPhase.Idle;

    public bool IsOpen => Phase != RecordPhase.Idle;

    /// <summary>The area in physical pixels (virtual-desktop coordinates), even width / height.</summary>
    public Int32Rect Rect { get; private set; }

    public RecorderConfig Config => _config;

    public int Count { get; private set; }

    public bool KeysOn { get; private set; }

    public RecordTake? Take { get; private set; }

    /// <summary>Export progress 0–1 while saving / copying, else null.</summary>
    public double? Progress { get; private set; }

    public double Elapsed => _clock?.Elapsed ?? 0;

    public bool IsRecordingPhase => Phase is RecordPhase.Countdown or RecordPhase.Starting or RecordPhase.Recording or RecordPhase.Paused;

    public KeyPills Pills => _pills;

    internal RecordPreviewWindow? Preview => _preview;

    /// <summary>For tests: frames come from here instead of the screen.</summary>
    public Action<IntPtr, int, int, long>? SyntheticFrames { get; set; }

    public double Scale => Monitors.Of(Rect).Scale;

    // ------------------------------------------------------------------ open / close

    /// <summary>Opens the recorder on an area (or the last / a default one); already open: brings it up.</summary>
    public void Open(Int32Rect? area = null)
    {
        if (IsOpen)
        {
            RevealUi();
            if (area is { } a && Phase == RecordPhase.Setup)
            {
                SetRect(a);
            }

            _bar?.Activate();
            return;
        }

        Rect = Clamp(area ?? LastOrDefaultRect());
        Phase = RecordPhase.Setup;
        Take = null;
        KeysOn = false;
        _frame = new RecordFrameWindow(this);
        _bar = new RecordBarWindow(this);
        new RecordDimWindow(Rect).Show(); // first, so the frame and the toolbar stay bright above it
        _frame.Show();
        _bar.Show(); // after the frame: above it
        Layout();
        _frame.PlayOpen();
        if (FFmpeg.Path != null)
        {
            _ = FFmpeg.DetectH264Async(); // warm up: picking the encoder takes a moment the first time
        }
        else
        {
            Notice?.Invoke("找不到 ffmpeg，无法录屏（请重新安装 Markshot）");
        }

        SweepOldSessions();
        Changed?.Invoke();
    }

    /// <summary>Every way of closing goes here: asks first when a recording would be lost.</summary>
    public void RequestClose()
    {
        if (Phase is RecordPhase.Recording or RecordPhase.Paused)
        {
            RevealUi();
            _bar?.Confirm("正在录制，退出会丢弃这段录像。确定退出？", "退出", Close);
            return;
        }

        if (Phase == RecordPhase.Preview && Take is { Saved: false, Copied: false })
        {
            RevealUi();
            _bar?.Confirm("录像还没有保存，确定退出？", "退出", Close);
            return;
        }

        Close();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _generation++;
        _config.LastX = Rect.X;
        _config.LastY = Rect.Y;
        _config.LastWidth = Rect.Width;
        _config.LastHeight = Rect.Height;
        _saveConfig();
        _tick.Stop();
        _countdown.Stop();
        _hook.Stop();
        DropSession();
        Phase = RecordPhase.Idle;
        Take = null;
        Progress = null;
        _uiHidden = false;
        var frame = _frame;
        var bar = _bar;
        var preview = _preview;
        _frame = null;
        _bar = null;
        _preview = null;
        frame?.Close();
        preview?.CloseNow();
        bar?.CloseNow();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ area

    private Int32Rect LastOrDefaultRect()
    {
        var last = new Int32Rect(_config.LastX, _config.LastY, _config.LastWidth, _config.LastHeight);
        if (last.Width >= 16 && last.Height >= 16 && Monitors.Intersects(last))
        {
            return last;
        }

        // 1280 × 720 in the middle of the monitor under the mouse (at most 60 % of its work area).
        var cursor = Native.CursorPosition();
        var monitor = Monitors.At(cursor.X, cursor.Y);
        var work = monitor.WorkArea;
        var k = Math.Min(1, Math.Min(work.Width * 0.6 / 1280, work.Height * 0.6 / 720));
        var width = (int)(1280 * k);
        var height = (int)(720 * k);
        return new Int32Rect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2 - (int)(20 * monitor.Scale), width, height);
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>
    /// Keeps the area on the desktop with at least the minimum size. <paramref name="clip"/>
    /// (dragging an edge) only cuts what sticks out instead of moving the area.
    /// </summary>
    public Int32Rect Clamp(Int32Rect r, bool clip = false)
    {
        var union = Monitors.Union();
        var scale = Monitors.Of(r).Scale;
        var minW = (int)Math.Ceiling(MinWidthDip * scale);
        var minH = (int)Math.Ceiling(MinHeightDip * scale);
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        if (clip)
        {
            var right = Math.Min(union.X + union.Width, x + w);
            var bottom = Math.Min(union.Y + union.Height, y + h);
            x = Math.Max(union.X, x);
            y = Math.Max(union.Y, y);
            w = Math.Max(minW, right - x);
            h = Math.Max(minH, bottom - y);
        }
        else
        {
            w = Math.Clamp(w, minW, union.Width);
            h = Math.Clamp(h, minH, union.Height);
            x = Math.Clamp(x, union.X, union.X + union.Width - w);
            y = Math.Clamp(y, union.Y, union.Y + union.Height - h);
            // The centre in a gap between monitors: move it onto the nearest one.
            var cx = (int)(x + w / 2);
            var cy = (int)(y + h / 2);
            var monitor = Monitors.At(cx, cy);
            var b = monitor.Bounds;
            x += Math.Clamp(cx, b.X + 1, b.X + b.Width - 1) - cx;
            y += Math.Clamp(cy, b.Y + 1, b.Y + b.Height - 1) - cy;
        }

        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), Even(w), Even(h));
    }

    public void SetRect(Int32Rect rect, bool clip = false)
    {
        var next = Clamp(rect, clip);
        if (next.Equals(Rect))
        {
            return;
        }

        Rect = next;
        Layout();
        Changed?.Invoke();
    }

    /// <summary>Typed size in physical pixels, kept centred on the area.</summary>
    public void SetSize(int width, int height)
    {
        if (Phase != RecordPhase.Setup)
        {
            return;
        }

        width = Math.Clamp(width, 16, 16384);
        height = Math.Clamp(height, 16, 16384);
        var cx = Rect.X + Rect.Width / 2;
        var cy = Rect.Y + Rect.Height / 2;
        SetRect(new Int32Rect(cx - width / 2, cy - height / 2, width, height));
    }

    /// <summary>Moves the area (setup) or the whole preview by a distance in physical pixels.</summary>
    public void MoveTo(Int32Rect origin, int dx, int dy)
    {
        if (Phase is not (RecordPhase.Setup or RecordPhase.Preview))
        {
            return;
        }

        SetRect(new Int32Rect(origin.X + dx, origin.Y + dy, origin.Width, origin.Height));
    }

    /// <summary>Places the frame, the preview and the toolbar around the area.</summary>
    public void Layout()
    {
        _frame?.Place();
        _preview?.Place();
        _bar?.Place();
    }

    // ------------------------------------------------------------------ options

    public void SetFps(int fps)
    {
        if (Phase != RecordPhase.Setup)
        {
            return;
        }

        _config.Fps = fps;
        _saveConfig();
        Changed?.Invoke();
    }

    public void SetDelay(bool on, int seconds)
    {
        _config.DelayOn = on;
        _config.Delay = Math.Clamp(seconds, 1, 10);
        _saveConfig();
        Changed?.Invoke();
    }

    public void SetSystemAudio(bool on)
    {
        _config.SystemAudio = on;
        _saveConfig();
        if (on && _system == null && IsRecordingPhase && _clock != null && _session != null)
        {
            _system = OpenTrack(true); // opened on first use
        }

        Changed?.Invoke();
    }

    public void SetMicrophone(bool on)
    {
        _config.Microphone = on;
        _saveConfig();
        if (on && _microphone == null && IsRecordingPhase && _clock != null && _session != null)
        {
            _microphone = OpenTrack(false);
        }

        Changed?.Invoke();
    }

    /// <summary>操作录制: shows the keys / mouse buttons pressed (not remembered).</summary>
    public void SetKeys(bool on)
    {
        KeysOn = on;
        if (on)
        {
            _hook.Start();
        }
        else
        {
            _hook.Stop();
            _pills.Clear();
        }

        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ recording

    public void Start()
    {
        if (Phase != RecordPhase.Setup)
        {
            return;
        }

        if (FFmpeg.Path == null)
        {
            Notice?.Invoke("找不到 ffmpeg，无法录屏（请重新安装 Markshot）");
            return;
        }

        var generation = ++_generation;
        _session = Path.Combine(Path.GetTempPath(), "markshot-rec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_session);
        _clock = new RecordClock();
        _video = new ScreenRecorder(Rect, _config.Fps, _clock, Path.Combine(_session, "video.mkv"), _pills, Scale)
        {
            Synthetic = SyntheticFrames
        };
        _prepare = PrepareAsync(_video, _session);
        if (_config.DelayOn && _config.Delay > 0)
        {
            Phase = RecordPhase.Countdown;
            Count = _config.Delay;
            _countdown.Start();
        }
        else
        {
            Phase = RecordPhase.Starting;
            _ = GoWhenReady(generation);
        }

        _bar?.SetProtected(true); // the toolbar never shows up in the video
        Changed?.Invoke();
    }

    private async Task PrepareAsync(ScreenRecorder video, string session)
    {
        await video.PrepareAsync();
        if (_config.SystemAudio && _session == session)
        {
            _system = OpenTrack(true);
        }

        if (_config.Microphone && _session == session)
        {
            _microphone = OpenTrack(false);
        }
    }

    private AudioTrack? OpenTrack(bool system)
    {
        var track = AudioTrack.Open(system, Path.Combine(_session!, system ? "system.wav" : "mic.wav"), _clock!,
            system ? () => _config.SystemAudio : () => _config.Microphone, out var error);
        if (error != null)
        {
            Notice?.Invoke(error);
        }

        return track;
    }

    private void CountdownTick()
    {
        if (Phase != RecordPhase.Countdown)
        {
            _countdown.Stop();
            return;
        }

        Count--;
        if (Count <= 0)
        {
            _countdown.Stop();
            Phase = RecordPhase.Starting;
            _ = GoWhenReady(_generation);
        }

        Changed?.Invoke();
    }

    private async Task GoWhenReady(int generation)
    {
        try
        {
            if (_prepare != null)
            {
                await _prepare;
            }
        }
        catch (Exception ex)
        {
            Log.Error("录屏准备失败", ex);
            if (generation == _generation)
            {
                Fail("开始录制失败：" + ex.Message);
            }

            return;
        }

        if (generation != _generation || Phase != RecordPhase.Starting)
        {
            return;
        }

        // Let the countdown number disappear from the screen before the first frame.
        await Task.Delay(60);
        if (generation != _generation || Phase != RecordPhase.Starting)
        {
            return;
        }

        _clock!.Start();
        _video!.Go();
        Phase = RecordPhase.Recording;
        _tick.Start();
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (Phase == RecordPhase.Recording)
        {
            _clock?.Pause();
            Phase = RecordPhase.Paused;
        }
        else if (Phase == RecordPhase.Paused)
        {
            _clock?.Resume();
            Phase = RecordPhase.Recording;
        }

        Changed?.Invoke();
    }

    /// <summary>Stop: finishes the files and shows the preview (before recording: back to setup).</summary>
    public async Task StopAsync()
    {
        if (Phase is RecordPhase.Countdown or RecordPhase.Starting)
        {
            _generation++;
            _countdown.Stop();
            DropSession();
            Phase = RecordPhase.Setup;
            _bar?.SetProtected(false);
            Changed?.Invoke();
            return;
        }

        if (Phase is not (RecordPhase.Recording or RecordPhase.Paused))
        {
            return;
        }

        var generation = _generation;
        _clock!.Pause();
        Phase = RecordPhase.Finishing;
        _tick.Stop();
        Changed?.Invoke();

        var video = _video!;
        var session = _session!;
        var result = await video.StopAsync();
        var tracks = new[] { _system, _microphone }.Where(t => t != null).Select(t => t!).ToList();
        await Task.Run(() => tracks.ForEach(t => t.Finish()));
        if (generation != _generation)
        {
            return;
        }

        var raw = Path.Combine(session, "video.mkv");
        if (!result.Ok || !File.Exists(raw) || new FileInfo(raw).Length < 512)
        {
            Fail("没有录到画面" + (string.IsNullOrWhiteSpace(result.Error) ? "" : "：" + FirstLine(result.Error)));
            return;
        }

        var audible = tracks.Where(t => t.Audible).Select(t => t.Path).ToList();
        var preview = Path.Combine(session, "preview.mp4");
        var mux = await FFmpeg.MuxPreviewAsync(raw, audible, preview);
        if (!mux.Ok)
        {
            // Without the sound rather than nothing.
            audible.Clear();
            mux = await FFmpeg.MuxPreviewAsync(raw, audible, preview);
        }

        if (generation != _generation)
        {
            return;
        }

        if (!mux.Ok)
        {
            Fail("录像处理失败：" + FirstLine(mux.Error));
            return;
        }

        var duration = await FFmpeg.ProbeDurationAsync(preview);
        if (generation != _generation)
        {
            return;
        }

        Take = new RecordTake
        {
            File = preview,
            Duration = duration > 0 ? duration : _clock.Elapsed,
            Width = Rect.Width,
            Height = Rect.Height,
            Fps = _config.Fps,
            HasAudio = audible.Count > 0
        };
        DisposeRecorders();
        Phase = RecordPhase.Preview;
        _bar?.SetProtected(false);
        _preview = new RecordPreviewWindow(this);
        _preview.Show();
        _bar?.BringToFront();
        Layout();
        _frame?.Hide();
        Changed?.Invoke();
    }

    /// <summary>重录: throws this take away and records again with the same settings.</summary>
    public void Retake()
    {
        if (Phase == RecordPhase.Preview && Take is { Saved: false, Copied: false })
        {
            _bar?.Confirm("丢弃这段录像，重新录制？", "重录", RetakeNow);
            return;
        }

        RetakeNow();
    }

    private void RetakeNow()
    {
        if (Phase is RecordPhase.Idle or RecordPhase.Finishing)
        {
            return;
        }

        _generation++;
        _countdown.Stop();
        _tick.Stop();
        DropSession();
        Take = null;
        _preview?.CloseNow();
        _preview = null;
        _frame?.Show();
        Phase = RecordPhase.Setup;
        _bar?.SetProtected(false);
        Layout();
        Changed?.Invoke();
        var generation = _generation;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            if (generation == _generation && Phase == RecordPhase.Setup)
            {
                Start();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Fail(string message)
    {
        Log.Info("录屏失败：" + message);
        _generation++;
        _countdown.Stop();
        _tick.Stop();
        DropSession();
        Phase = RecordPhase.Setup;
        _bar?.SetProtected(false);
        _frame?.Show();
        Notice?.Invoke(message);
        Changed?.Invoke();
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;

    private void DisposeRecorders()
    {
        _video?.Dispose();
        _system?.Dispose();
        _microphone?.Dispose();
        _video = null;
        _system = null;
        _microphone = null;
        _prepare = null;
    }

    /// <summary>Stops everything of the current recording and deletes its temporary files (a little later).</summary>
    private void DropSession()
    {
        DisposeRecorders();
        var session = _session;
        _session = null;
        _clock = null;
        if (session != null)
        {
            _ = Task.Delay(1500).ContinueWith(_ => TryDelete(session));
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Leftovers of crashed / killed sessions (older than a day).</summary>
    private static void SweepOldSessions()
    {
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "markshot-rec-*"))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    {
                        TryDelete(dir);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    // ------------------------------------------------------------------ preview: save / copy

    public string SaveDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_config.Directory) && Directory.Exists(_config.Directory))
            {
                return _config.Directory;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Markshot 录屏");
        }
    }

    public static string FileStamp() => "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Writes the take to <paramref name="path"/> with the preview's trim / speed / sound.</summary>
    public async Task<bool> ExportAsync(ExportOptions options, string path)
    {
        if (Take is not { } take || Progress != null)
        {
            return false;
        }

        Progress = 0;
        Changed?.Invoke();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var lastReport = DateTime.MinValue;
        FFmpeg.Result result;
        try
        {
            result = await FFmpeg.ExportAsync(take.File, take.Duration, take.HasAudio, options, path, p =>
            {
                if ((DateTime.Now - lastReport).TotalMilliseconds < 150)
                {
                    return;
                }

                lastReport = DateTime.Now;
                dispatcher.BeginInvoke(() =>
                {
                    Progress = p;
                    Changed?.Invoke();
                });
            });
        }
        catch (Exception ex)
        {
            result = new FFmpeg.Result(-1, ex.Message);
        }

        Progress = null;
        Changed?.Invoke();
        if (!result.Ok)
        {
            Log.Error("录屏导出失败：" + result.Error);
            Notice?.Invoke("导出失败：" + FirstLine(result.Error));
        }

        return result.Ok;
    }

    public void MarkSaved(string path)
    {
        if (Take != null)
        {
            Take.Saved = true;
        }

        _config.Directory = Path.GetDirectoryName(path) ?? _config.Directory;
        _saveConfig();
        Notice?.Invoke("已保存：" + Path.GetFileName(path));
        Changed?.Invoke();
    }

    /// <summary>复制: saves a 流畅 MP4 into the save folder and puts the file on the clipboard.</summary>
    public async Task CopyAsync(ExportOptions options)
    {
        if (Take == null)
        {
            return;
        }

        var directory = SaveDirectory;
        Directory.CreateDirectory(directory);
        var path = UniquePath(Path.Combine(directory, FileStamp() + ".mp4"));
        if (!await ExportAsync(options with { Quality = RecordQuality.Smooth, Gif = false }, path))
        {
            return;
        }

        try
        {
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            Take!.Copied = true;
            Notice?.Invoke("已复制视频文件，可直接粘贴");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notice?.Invoke("已保存，但复制到剪贴板失败");
        }

        Changed?.Invoke();
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // ------------------------------------------------------------------ taskbar: hide / show the UI

    /// <summary>Clicking the taskbar button hides the frame and preview (recording goes on).</summary>
    public void SetUiHidden(bool hidden)
    {
        if (_uiHidden == hidden)
        {
            return;
        }

        _uiHidden = hidden;
        _frame?.SetHidden(hidden);
        _preview?.SetHidden(hidden);
    }

    private void RevealUi()
    {
        if (_uiHidden)
        {
            _bar?.RestoreFromTaskbar();
            SetUiHidden(false);
        }
    }

    /// <summary>The global hotkey: open → start → stop.</summary>
    public void Toggle()
    {
        switch (Phase)
        {
            case RecordPhase.Idle:
                Open();
                break;
            case RecordPhase.Setup:
                Start();
                break;
            case RecordPhase.Countdown or RecordPhase.Starting or RecordPhase.Recording or RecordPhase.Paused:
                _ = StopAsync();
                break;
            default:
                RevealUi();
                _bar?.Activate();
                break;
        }
    }
}
