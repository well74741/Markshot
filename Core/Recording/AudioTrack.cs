// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Markshot.Core.Recording;

/// <summary>
/// One sound source (system sound via WASAPI loopback, or the default microphone) written to a
/// WAV file that follows the <see cref="RecordClock"/>: gaps (loopback sends nothing while the PC
/// is silent) are filled with silence, data while paused is dropped, and a switched-off source
/// writes silence — switching only changes the "gain", the track keeps its timeline.
/// </summary>
public sealed class AudioTrack : IDisposable
{
    private readonly RecordClock _clock;
    private readonly Func<bool> _enabled;
    private readonly object _gate = new();
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private long _frames;
    private byte[] _silence = Array.Empty<byte>();

    private AudioTrack(string path, RecordClock clock, Func<bool> enabled)
    {
        Path = path;
        _clock = clock;
        _enabled = enabled;
    }

    public string Path { get; }

    /// <summary>The source was on (and delivering) at some point: the take has this sound.</summary>
    public bool Audible { get; private set; }

    /// <summary>Opens the system sound (loopback) or the microphone; null (with a message) when it cannot.</summary>
    public static AudioTrack? Open(bool system, string path, RecordClock clock, Func<bool> enabled, out string? error)
    {
        error = null;
        var track = new AudioTrack(path, clock, enabled);
        try
        {
            IWaveIn capture = system ? new WasapiLoopbackCapture() : new WasapiCapture();
            track._capture = capture;
            track._writer = new WaveFileWriter(path, capture.WaveFormat);
            capture.DataAvailable += (_, e) => track.OnData(e.Buffer, e.BytesRecorded);
            capture.StartRecording();
            return track;
        }
        catch (Exception ex)
        {
            Log.Error(system ? "系统声音录制失败" : "麦克风打开失败", ex);
            error = system ? "录不到系统声音：" + ex.Message : "打不开麦克风：" + ex.Message;
            track.Dispose();
            return null;
        }
    }

    private void OnData(byte[] buffer, int bytes)
    {
        lock (_gate)
        {
            if (_writer == null || bytes <= 0 || !_clock.Started || _clock.Paused)
            {
                return;
            }

            var format = _writer.WaveFormat;
            var incoming = bytes / format.BlockAlign;
            var expectedEnd = (long)(_clock.Elapsed * format.SampleRate);
            var gap = expectedEnd - incoming - _frames;
            if (gap > format.SampleRate * 0.06)
            {
                WriteSilence(gap); // the source was quiet: keep the timeline
            }
            else if (_frames - expectedEnd > format.SampleRate * 0.25)
            {
                return; // ahead of the clock (device drift): drop this buffer
            }

            if (_enabled())
            {
                _writer.Write(buffer, 0, bytes);
                Audible = true;
            }
            else
            {
                WriteSilence(incoming);
                return;
            }

            _frames += incoming;
        }
    }

    private void WriteSilence(long frames)
    {
        var format = _writer!.WaveFormat;
        var total = frames * format.BlockAlign;
        if (_silence.Length == 0)
        {
            _silence = new byte[format.AverageBytesPerSecond / 10 / format.BlockAlign * format.BlockAlign];
        }

        while (total > 0)
        {
            var chunk = (int)Math.Min(total, _silence.Length);
            _writer.Write(_silence, 0, chunk);
            total -= chunk;
        }

        _frames += frames;
    }

    /// <summary>Stops the device and pads the file to the end of the recording time.</summary>
    public void Finish()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Error("停止录音失败", ex);
        }

        lock (_gate)
        {
            if (_writer == null)
            {
                return;
            }

            var end = (long)(_clock.Elapsed * _writer.WaveFormat.SampleRate);
            if (end > _frames)
            {
                WriteSilence(end - _frames);
            }

            _writer.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        try
        {
            _capture?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("释放录音设备失败", ex);
        }

        _capture = null;
    }
}
