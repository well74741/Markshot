using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Markshot.Core.Recording;

static class RecordCoreTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    public static void Run()
    {
        Check(FFmpeg.Path != null, "ffmpeg found: " + FFmpeg.Path);
        var encoder = FFmpeg.DetectH264Async().GetAwaiter().GetResult();
        Check(encoder != null, "encoder: " + encoder?.Name);
        Check(FFmpeg.Atempo(4) == "atempo=2.0000,atempo=2.0000" && FFmpeg.Atempo(0.2) == "atempo=0.5000,atempo=0.5000,atempo=0.8000",
            "atempo chain: " + FFmpeg.Atempo(0.2));

        var dir = Path.Combine(Path.GetTempPath(), "markshot-rec-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // ---- synthetic frames: magenta with a white bar moving right; 1.5 s at 30 fps with a 0.5 s pause
        var clock = new RecordClock();
        var raw = Path.Combine(dir, "video.mkv");
        var pills = new KeyPills();
        var recorder = new ScreenRecorder(new Int32Rect(0, 0, 320, 180), 30, clock, raw, pills, 1.0)
        {
            Synthetic = (bits, w, h, n) =>
            {
                unsafe
                {
                    var p = (uint*)bits;
                    var bar = (int)(n * 4 % w);
                    for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        p[y * w + x] = Math.Abs(x - bar) < 6 ? 0xFFFFFFFFu : 0xFFFF00FFu;
                }
            }
        };
        recorder.PrepareAsync().GetAwaiter().GetResult();
        clock.Start();
        recorder.Go();
        recorder.DrawKeys = true;
        pills.Emit("Ctrl + C");
        Thread.Sleep(700);
        clock.Pause();
        Thread.Sleep(500); // not part of the video
        clock.Resume();
        Thread.Sleep(800);
        clock.Pause();
        var elapsed = clock.Elapsed;
        var stopped = recorder.StopAsync().GetAwaiter().GetResult();
        recorder.Dispose();
        Check(stopped.Ok && File.Exists(raw) && new FileInfo(raw).Length > 1000, $"recorded {new FileInfo(raw).Length} bytes ({stopped.Error})");
        var rawDuration = FFmpeg.ProbeDurationAsync(raw).GetAwaiter().GetResult();
        Check(Math.Abs(rawDuration - elapsed) < 0.2, $"video lasts the recording time without the pause: {rawDuration:0.00}s vs {elapsed:0.00}s");

        // ---- a silent 'system sound' track the length of the clock, then the preview remux
        var wav = Path.Combine(dir, "sys.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wav, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
        {
            var samples = new float[(int)(elapsed * 48000) * 2];
            for (var i = 0; i < samples.Length; i += 2) samples[i] = samples[i + 1] = (float)Math.Sin(i / 2 * 2 * Math.PI * 440 / 48000) * 0.2f;
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var preview = Path.Combine(dir, "preview.mp4");
        var mux = FFmpeg.MuxPreviewAsync(raw, new[] { wav, wav }, preview).GetAwaiter().GetResult();
        var previewDuration = FFmpeg.ProbeDurationAsync(preview).GetAwaiter().GetResult();
        Check(mux.Ok && Math.Abs(previewDuration - elapsed) < 0.25, $"preview mp4 with mixed sound: {previewDuration:0.00}s {mux.Error}");

        // ---- export: trimmed + 2x speed MP4, and a GIF
        var mp4 = Path.Combine(dir, "out.mp4");
        double last = 0;
        var export = FFmpeg.ExportAsync(preview, previewDuration, true, new ExportOptions(0.2, 1.2, 2, false, RecordQuality.Clear, 30, false), mp4,
            p => last = p).GetAwaiter().GetResult();
        var outDuration = FFmpeg.ProbeDurationAsync(mp4).GetAwaiter().GetResult();
        Check(export.Ok && Math.Abs(outDuration - 0.5) < 0.15, $"trimmed 1.0s at 2x -> {outDuration:0.00}s ({export.Error}) progress {last:0.00}");
        var gif = Path.Combine(dir, "out.gif");
        var gifResult = FFmpeg.ExportAsync(preview, previewDuration, true, new ExportOptions(0, 0, 1, false, RecordQuality.Smooth, 10, true), gif)
            .GetAwaiter().GetResult();
        Check(gifResult.Ok && File.Exists(gif) && File.ReadAllBytes(gif).Take(3).SequenceEqual("GIF"u8.ToArray()), $"gif written ({gifResult.Error})");

        // a frame from the middle: magenta background, the key pill bottom-left
        var png = Path.Combine(dir, "frame.png");
        FFmpeg.RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", "0.3", "-i", raw, "-frames:v", "1", png }).GetAwaiter().GetResult();
        File.Copy(png, "record_frame.png", true);

        // ---- key display rules
        var keys = new KeyPills();
        var hook = new InputHook(keys);
        var down = typeof(InputHook).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var up = typeof(InputHook).GetMethod("OnKeyUp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Press(params int[] vks) { foreach (var vk in vks) down.Invoke(hook, new object[] { vk }); foreach (var vk in vks.Reverse()) up.Invoke(hook, new object[] { vk }); }
        Press(0xA2, 0x43);       // Ctrl + C
        Press(0xA2, 0x43);       // again: ×2
        Press(0xA0);             // Shift alone
        down.Invoke(hook, new object[] { 0x41 }); down.Invoke(hook, new object[] { 0x41 }); up.Invoke(hook, new object[] { 0x41 }); // A with auto-repeat
        var labels = keys.Snapshot().Select(p => p.Label).ToList();
        Check(labels.SequenceEqual(new[] { "Ctrl + C  ×2", "Shift", "A" }), "key pills: " + string.Join(" | ", labels));

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
