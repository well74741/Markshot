// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Text.RegularExpressions;

namespace Markshot.Core.Recording;

/// <summary>Export quality: 流畅 / 清晰 / 高清.</summary>
public enum RecordQuality
{
    Smooth,
    Clear,
    High
}

/// <summary>Options for writing a take out (trim, speed, sound, quality, frame rate).</summary>
public sealed record ExportOptions(double In, double Out, double Speed, bool Mute, RecordQuality Quality, int Fps, bool Gif);

/// <summary>
/// Runs the bundled ffmpeg (ffmpeg\ffmpeg.exe next to Markshot.exe; falls back to one on PATH):
/// encoder detection, the post-recording remux and the MP4 / GIF export.
/// </summary>
public static class FFmpeg
{
    private static readonly SemaphoreSlim DetectGate = new(1, 1);
    private static H264Encoder? _encoder;

    /// <summary>An H.264 encoder and its base arguments (quality values are replaced per level).</summary>
    public sealed record H264Encoder(string Name, string[] Args);

    // Order = preference. Hardware first; h264_mf ships with Windows; libx264 is the fallback (not probed).
    private static readonly H264Encoder[] Candidates =
    {
        new("h264_nvenc", new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "20", "-pix_fmt", "yuv420p" }),
        new("h264_qsv", new[] { "-c:v", "h264_qsv", "-global_quality", "20", "-preset", "veryfast", "-pix_fmt", "nv12" }),
        new("h264_amf", new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20", "-pix_fmt", "yuv420p" }),
        new("h264_mf", new[] { "-c:v", "h264_mf", "-rate_control", "quality", "-quality", "80", "-pix_fmt", "yuv420p" })
    };

    public static readonly H264Encoder LibX264 = new("libx264", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p" });

    /// <summary>Full path of ffmpeg.exe, or null when there is none.</summary>
    public static string? Path
    {
        get
        {
            var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }
    }

    public sealed record Result(int ExitCode, string Error)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Runs ffmpeg; <paramref name="onProgress"/> gets 0–1 when <paramref name="total"/> (seconds) is known.</summary>
    public static async Task<Result> RunAsync(IReadOnlyList<string> args, Action<double>? onProgress = null, double total = 0,
        CancellationToken cancellationToken = default)
    {
        var exe = Path ?? throw new FileNotFoundException("找不到 ffmpeg.exe");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var error = new StringBuilder();
        var progressText = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (error)
            {
                error.AppendLine(e.Data);
                if (error.Length > 20000)
                {
                    error.Remove(0, error.Length - 8000); // keep the tail for the message
                }
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            // -progress pipe:1 → key=value lines; out_time_us tells how far the output is.
            if (e.Data is { } line && line.StartsWith("out_time_us=", StringComparison.Ordinal) && total > 0 &&
                long.TryParse(line.AsSpan(12), out var micros))
            {
                onProgress?.Invoke(Math.Clamp(micros / 1e6 / total, 0, 1));
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        lock (error)
        {
            return new Result(process.ExitCode, error.ToString().Trim());
        }
    }

    /// <summary>The first H.264 encoder that really works here (tried once per run, then cached).</summary>
    public static async Task<H264Encoder> DetectH264Async()
    {
        if (_encoder != null)
        {
            return _encoder;
        }

        await DetectGate.WaitAsync();
        try
        {
            if (_encoder != null)
            {
                return _encoder;
            }

            foreach (var candidate in Candidates)
            {
                if (!await IsListedAsync(candidate.Name))
                {
                    continue;
                }

                // Encode a few black frames to nowhere: the driver / GPU may still refuse.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.2" };
                    args.AddRange(candidate.Args);
                    args.AddRange(new[] { "-frames:v", "3", "-f", "null", "-" });
                    if ((await RunAsync(args, cancellationToken: timeout.Token)).Ok)
                    {
                        Log.Info($"录屏编码器：{candidate.Name}");
                        return _encoder = candidate;
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }

            Log.Info("录屏编码器：libx264");
            return _encoder = LibX264;
        }
        finally
        {
            DetectGate.Release();
        }
    }

    private static string? _encoderList;

    private static async Task<bool> IsListedAsync(string name)
    {
        if (_encoderList == null)
        {
            var exe = Path!;
            var info = new ProcessStartInfo(exe, "-hide_banner -encoders")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            _encoderList = output;
        }

        return Regex.IsMatch(_encoderList, @"\s" + Regex.Escape(name) + @"\s");
    }

    /// <summary>The encoder's arguments with the quality values of the level (流畅 / 清晰 / 高清).</summary>
    public static string[] QualityArgs(H264Encoder encoder, RecordQuality quality)
    {
        var level = (int)quality;
        var args = (string[])encoder.Args.Clone();

        void Set(string flag, params int[] values)
        {
            var i = Array.IndexOf(args, flag);
            if (i >= 0 && i + 1 < args.Length)
            {
                args[i + 1] = values[level].ToString(CultureInfo.InvariantCulture);
            }
        }

        Set("-crf", 28, 23, 18);
        Set("-cq", 31, 26, 21);
        Set("-global_quality", 31, 26, 21);
        Set("-qp_i", 31, 26, 21);
        Set("-qp_p", 31, 26, 21);
        if (encoder.Name == "h264_mf")
        {
            Set("-quality", 55, 72, 90);
        }

        return args;
    }

    /// <summary>atempo only takes 0.5–2 per stage: chain stages for other speeds (pitch is kept).</summary>
    public static string Atempo(double speed)
    {
        if (Math.Abs(speed - 1) < 1e-3)
        {
            return "anull";
        }

        var stages = new List<double>();
        var s = speed;
        while (s > 2)
        {
            stages.Add(2);
            s /= 2;
        }

        while (s < 0.5)
        {
            stages.Add(0.5);
            s /= 0.5;
        }

        stages.Add(s);
        return string.Join(",", stages.Select(x => "atempo=" + x.ToString("0.0000", CultureInfo.InvariantCulture)));
    }

    /// <summary>Duration in seconds (from "Duration: hh:mm:ss.xx" of ffmpeg -i), 0 when unknown.</summary>
    public static async Task<double> ProbeDurationAsync(string file)
    {
        var result = await RunAsync(new[] { "-hide_banner", "-i", file });
        var match = Regex.Match(result.Error, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        return match.Success
            ? int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60 +
              double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    /// <summary>
    /// Turns the recorded video (+ the sound tracks) into a seekable MP4 for the preview: the
    /// video is copied as it is, the sound is mixed and encoded to AAC.
    /// </summary>
    public static async Task<Result> MuxPreviewAsync(string video, IReadOnlyList<string> audio, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", video };
        foreach (var track in audio)
        {
            args.AddRange(new[] { "-i", track });
        }

        args.AddRange(new[] { "-map", "0:v", "-c:v", "copy" });
        if (audio.Count == 1)
        {
            args.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-b:a", "192k" });
        }
        else if (audio.Count > 1)
        {
            var inputs = string.Concat(Enumerable.Range(1, audio.Count).Select(i => $"[{i}:a]"));
            args.AddRange(new[]
            {
                "-filter_complex", $"{inputs}amix=inputs={audio.Count}:duration=longest:normalize=0[a]",
                "-map", "[a]", "-c:a", "aac", "-b:a", "192k"
            });
        }

        args.AddRange(new[] { "-movflags", "+faststart", output });
        return await RunAsync(args);
    }

    /// <summary>Writes the take out as MP4 or GIF: trimmed, sped up / down, at the chosen quality and frame rate.</summary>
    public static async Task<Result> ExportAsync(string input, double duration, bool hasAudio, ExportOptions options, string output,
        Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var start = Math.Clamp(options.In, 0, Math.Max(0, duration - 0.05));
        var end = Math.Clamp(options.Out <= 0 ? duration : options.Out, start + 0.05, Math.Max(start + 0.05, duration));
        var speed = Math.Clamp(options.Speed, 0.1, 16);
        var fps = Math.Clamp(options.Fps, 1, 120);
        var filters = new List<string>();
        if (Math.Abs(speed - 1) > 1e-3)
        {
            filters.Add("setpts=PTS/" + speed.ToString("0.####", CultureInfo.InvariantCulture));
        }

        filters.Add("fps=" + fps.ToString(CultureInfo.InvariantCulture));
        var head = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.000", CultureInfo.InvariantCulture),
            "-t", (end - start).ToString("0.000", CultureInfo.InvariantCulture),
            "-i", input
        };
        var tail = new[] { "-progress", "pipe:1", "-nostats", output };
        var total = (end - start) / speed;

        Result result;
        if (options.Gif)
        {
            var dither = options.Quality switch
            {
                RecordQuality.High => "bayer:bayer_scale=3",
                RecordQuality.Smooth => "none",
                _ => "sierra2_4a"
            };
            var graph = string.Join(",", filters) +
                        ",split[a][b];[a]palettegen=stats_mode=diff:max_colors=256:reserve_transparent=0[p];[b][p]paletteuse=dither=" +
                        dither + ":diff_mode=rectangle";
            result = await RunAsync(head.Concat(new[] { "-vf", graph, "-loop", "0" }).Concat(tail).ToList(), onProgress, total, cancellationToken);
        }
        else
        {
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            var sound = hasAudio && !options.Mute
                ? new[] { "-af", Atempo(speed), "-c:a", "aac", "-b:a", "160k" }
                : new[] { "-an" };
            var encoder = await DetectH264Async();
            List<string> Build(H264Encoder e) =>
                head.Concat(new[] { "-vf", string.Join(",", filters) }).Concat(QualityArgs(e, options.Quality)).Concat(sound)
                    .Concat(new[] { "-movflags", "+faststart" }).Concat(tail).ToList();

            result = await RunAsync(Build(encoder), onProgress, total, cancellationToken);
            if (!result.Ok && encoder.Name != LibX264.Name)
            {
                // Hardware encoders can refuse a size / driver state: do it in software.
                result = await RunAsync(Build(LibX264), onProgress, total, cancellationToken);
            }
        }

        if (!result.Ok)
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }

        return result;
    }
}
