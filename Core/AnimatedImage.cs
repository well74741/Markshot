using SkiaSharp;
using System.Windows.Media.Animation;
using Drawing = System.Drawing;

namespace Markshot.Core;

/// <summary>
/// A picture that may move (GIF / WebP / APNG frames with their own durations), small enough for an icon.
/// Played on an <see cref="System.Windows.Controls.Image"/> at the file's own speed.
/// </summary>
public sealed class AnimatedImage
{
    private AnimatedImage(IReadOnlyList<BitmapSource> frames, IReadOnlyList<TimeSpan> durations)
    {
        Frames = frames;
        Durations = durations;
    }

    public IReadOnlyList<BitmapSource> Frames { get; }

    public IReadOnlyList<TimeSpan> Durations { get; }

    public bool IsAnimated => Frames.Count > 1;

    public TimeSpan Length => TimeSpan.FromTicks(Durations.Sum(d => d.Ticks));

    public static AnimatedImage FromBitmap(BitmapSource image)
    {
        var frame = image.IsFrozen ? image : (BitmapSource)image.GetCurrentValueAsFrozen();
        return new AnimatedImage(new[] { frame }, new[] { TimeSpan.Zero });
    }

    /// <summary>Reads a picture file; every frame shrunk to at most <paramref name="maxSide"/> pixels. Null when it does not read.</summary>
    public static AnimatedImage? Load(string path, int maxSide = 192)
    {
        try
        {
            return Decode(File.ReadAllBytes(path), maxSide);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("读取图片失败：" + path, ex);
            return null;
        }
    }

    public static AnimatedImage? Decode(byte[] bytes, int maxSide = 192)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec != null)
            {
                return DecodeFrames(codec, maxSide);
            }
        }
        catch (Exception ex)
        {
            Log.Error("解码图片失败", ex);
        }

        try
        {
            // Formats SkiaSharp does not read (ICO, TIFF …): WPF's decoder, the first frame.
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var largest = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            return FromBitmap(Shrink(largest, maxSide));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static AnimatedImage? DecodeFrames(SKCodec codec, int maxSide)
    {
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var count = Math.Clamp(codec.FrameCount, 1, 400);
        var frameInfo = codec.FrameInfo;
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
        // All frames stay in memory while it plays: at most ~600 MB of them (a long, large GIF plays smaller).
        var budget = 600.0 * 1024 * 1024 / (count * 4.0);
        scale = Math.Min(scale, Math.Sqrt(budget / Math.Max(1.0, (double)width * height)));
        var needed = new HashSet<int>();
        for (var i = 0; i < count && i < frameInfo.Length; i++)
        {
            if (frameInfo[i].RequiredFrame >= 0)
            {
                needed.Add(frameInfo[i].RequiredFrame);
            }
        }

        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));
        var decoded = new SKBitmap?[count];
        var frames = new List<BitmapSource>();
        var durations = new List<TimeSpan>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var bitmap = new SKBitmap(info);
                var prior = codec.FrameCount > 1 && i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;
                if (prior >= 0 && prior < i && decoded[prior] is { } before)
                {
                    before.CopyTo(bitmap); // the frame draws over the one it depends on
                }
                else
                {
                    bitmap.Erase(SKColors.Transparent);
                    prior = -1;
                }

                var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, prior));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bitmap.Dispose();
                    if (i == 0)
                    {
                        return null;
                    }

                    break;
                }

                frames.Add(ToSource(bitmap, outWidth, outHeight));
                if (needed.Contains(i))
                {
                    decoded[i] = bitmap; // a later frame draws over it
                }
                else
                {
                    bitmap.Dispose();
                }
                var milliseconds = codec.FrameCount > 1 && i < frameInfo.Length ? frameInfo[i].Duration : 0;
                durations.Add(TimeSpan.FromMilliseconds(milliseconds <= 10 ? 100 : milliseconds)); // like browsers: 0 / 10 ms = 100 ms
            }
        }
        finally
        {
            foreach (var bitmap in decoded)
            {
                bitmap?.Dispose();
            }
        }

        return frames.Count == 0 ? null : new AnimatedImage(frames, durations);
    }

    private static BitmapSource ToSource(SKBitmap bitmap, int width, int height)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        BitmapSource source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, bitmap.Width * 4);
        if (width != bitmap.Width || height != bitmap.Height)
        {
            source = Shrink(source, Math.Max(width, height));
        }

        source.Freeze();
        return source;
    }

    /// <summary>A smooth downscale (several halving steps would be finer, one high-quality pass is enough for icons).</summary>
    public static BitmapSource Shrink(BitmapSource source, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale >= 0.999)
        {
            if (source.CanFreeze && !source.IsFrozen)
            {
                source.Freeze();
            }

            return source;
        }

        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Shows the picture on <paramref name="image"/>; a moving one loops at its own speed.</summary>
    public void Play(System.Windows.Controls.Image image)
    {
        image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, null);
        image.Source = Frames[0];
        if (CreateAnimation() is { } animation)
        {
            image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, animation);
        }
    }

    private ObjectAnimationUsingKeyFrames? _animation;

    /// <summary>The frames as an endless animation of Image.Source (null for a still picture).</summary>
    public ObjectAnimationUsingKeyFrames? CreateAnimation()
    {
        if (!IsAnimated)
        {
            return null;
        }

        if (_animation != null)
        {
            return _animation;
        }

        var animation = new ObjectAnimationUsingKeyFrames { Duration = Length, RepeatBehavior = RepeatBehavior.Forever };
        var at = TimeSpan.Zero;
        for (var i = 0; i < Frames.Count; i++)
        {
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(Frames[i], KeyTime.FromTimeSpan(at)));
            at += Durations[i];
        }

        animation.Freeze();
        return _animation = animation;
    }

    /// <summary>The first frame as a tray icon (square, the picture centred).</summary>
    public Drawing.Icon ToIcon(int size)
    {
        var frame = Frames[0];
        var scale = Math.Min(size / (double)frame.PixelWidth, size / (double)frame.PixelHeight);
        var width = frame.PixelWidth * scale;
        var height = frame.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(frame, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var bitmap = new Drawing.Bitmap(stream);
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }
}

/// <summary>The floating button's picture (also the tray icon): a copy in the config folder, or the Markshot logo.</summary>
public static class FloatIcon
{
    public static string Directory => Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppConfig.ConfigDirectory;

    public static string? CustomPath(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.FloatIcon) ? null : Path.Combine(Directory, Path.GetFileName(config.FloatIcon));

    /// <summary>The picture in use: the chosen one when it still reads, else the logo.</summary>
    public static AnimatedImage Load(AppConfig config)
    {
        if (CustomPath(config) is { } path && File.Exists(path) && AnimatedImage.Load(path) is { } custom)
        {
            return custom;
        }

        return Logo();
    }

    public static bool IsCustom(AppConfig config) => CustomPath(config) is { } path && File.Exists(path);

    public static AnimatedImage Logo()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Markshot;component/Assets/markshot.ico"));
        if (resource != null)
        {
            using var stream = resource.Stream;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (AnimatedImage.Decode(memory.ToArray()) is { } logo)
            {
                return logo;
            }
        }

        var empty = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
        return AnimatedImage.FromBitmap(empty);
    }

    /// <summary>Takes a picture file (copied, so the original may move); false when it does not read.</summary>
    public static bool SetFile(AppConfig config, string source)
    {
        if (AnimatedImage.Load(source) == null)
        {
            return false;
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension.Length is < 2 or > 6)
        {
            extension = ".png";
        }

        var name = "float_icon_" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + extension;
        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(source, Path.Combine(Directory, name), true);
        Replace(config, name);
        return true;
    }

    /// <summary>Takes a picture (pasted): saved as PNG.</summary>
    public static void SetImage(AppConfig config, BitmapSource image)
    {
        var name = "float_icon_" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".png";
        System.IO.Directory.CreateDirectory(Directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(AnimatedImage.Shrink(image, 512)));
        using (var file = File.Create(Path.Combine(Directory, name)))
        {
            encoder.Save(file);
        }

        Replace(config, name);
    }

    /// <summary>重置: the Markshot logo again.</summary>
    public static void Reset(AppConfig config) => Replace(config, "");

    private static void Replace(AppConfig config, string name)
    {
        var old = CustomPath(config);
        config.FloatIcon = name;
        if (old != null && !string.Equals(old, CustomPath(config), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (being read): it stays, a new start uses the new one anyway.
            }
        }
    }
}
