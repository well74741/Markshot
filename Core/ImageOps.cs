using System.Runtime.InteropServices;
using SkiaSharp;

namespace Markshot.Core;

/// <summary>Colour adjustments of the image editor; all neutral at their defaults.</summary>
public sealed record ImageAdjust(double Brightness = 0, double Contrast = 0, double Saturation = 0, double Opacity = 100, bool Gray = false, bool Invert = false)
{
    public bool IsNeutral => Brightness == 0 && Contrast == 0 && Saturation == 0 && Opacity >= 100 && !Gray && !Invert;
}

/// <summary>Pixel operations of the image editor. Everything takes and returns frozen bitmaps.</summary>
public static class ImageOps
{
    /// <summary>A frozen 96-dpi straight-alpha BGRA copy (what the editor works on).</summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        var (pixels, width, height) = ReadPixels(source);
        return FromPixels(pixels, width, height);
    }

    public static (byte[] Pixels, int Width, int Height) ReadPixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    public static BitmapSource FromPixels(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Brightness, contrast, saturation (−100…100), opacity (0…100), grey and invert.</summary>
    public static BitmapSource Adjust(BitmapSource source, ImageAdjust adjust)
    {
        var (pixels, width, height) = ReadPixels(source);
        // A lookup table per channel value: brightness, contrast and invert (in that order).
        var table = new byte[256];
        var brightness = adjust.Brightness / 100 * 255;
        var contrast = adjust.Contrast >= 0 ? 1 + adjust.Contrast / 100 * 2 : 1 + adjust.Contrast / 100;
        for (var i = 0; i < 256; i++)
        {
            var value = (i - 127.5) * contrast + 127.5 + brightness;
            value = Math.Clamp(value, 0, 255);
            table[i] = (byte)Math.Round(adjust.Invert ? 255 - value : value);
        }

        var saturation = adjust.Gray ? 0 : 1 + adjust.Saturation / 100;
        var opacity = Math.Clamp(adjust.Opacity, 0, 100) / 100;
        var changeColor = Math.Abs(saturation - 1) > 1e-6;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (changeColor)
                {
                    var luma = 0.114 * b + 0.587 * g + 0.299 * r;
                    b = Math.Clamp(luma + (b - luma) * saturation, 0, 255);
                    g = Math.Clamp(luma + (g - luma) * saturation, 0, 255);
                    r = Math.Clamp(luma + (r - luma) * saturation, 0, 255);
                }

                pixels[i] = table[(int)Math.Round(b)];
                pixels[i + 1] = table[(int)Math.Round(g)];
                pixels[i + 2] = table[(int)Math.Round(r)];
                pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * opacity);
            }
        });
        return FromPixels(pixels, width, height);
    }

    /// <summary>A corner is see-through (a cut-out shape).</summary>
    public static bool HasTransparentCorner(BitmapSource image)
    {
        var (pixels, width, height) = ReadPixels(image);
        return new[] { 0, width - 1, (height - 1) * width, height * width - 1 }.Any(i => pixels[i * 4 + 3] < 250);
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(source, rect);
        return Normalize(cropped);
    }

    /// <summary>Scaled to a new size (high-quality resampling).</summary>
    public static BitmapSource Resize(BitmapSource source, int width, int height) =>
        Draw(width, height, dc => dc.DrawImage(source, new Rect(0, 0, width, height)));

    /// <summary>Turned by any angle (degrees, clockwise); the canvas grows to fit, corners stay transparent.</summary>
    public static BitmapSource Rotate(BitmapSource source, double degrees)
    {
        double w = source.PixelWidth, h = source.PixelHeight;
        var radians = degrees * Math.PI / 180;
        var sin = Math.Abs(Math.Sin(radians));
        var cos = Math.Abs(Math.Cos(radians));
        // Rounded first: at 90° the sine / cosine are off by a hair, which would add a pixel.
        var width = Math.Max(1, (int)Math.Ceiling(Math.Round(w * cos + h * sin, 6)));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Round(w * sin + h * cos, 6)));
        return Draw(width, height, dc =>
        {
            dc.PushTransform(new TranslateTransform((width - w) / 2, (height - h) / 2));
            dc.PushTransform(new RotateTransform(degrees, w / 2, h / 2));
            dc.DrawImage(source, new Rect(0, 0, w, h));
            dc.Pop();
            dc.Pop();
        });
    }

    /// <summary>Cut out along a closed path (image pixels): the bounding box, transparent outside the path.</summary>
    public static BitmapSource PathCrop(BitmapSource source, IReadOnlyList<Point> path, out Point origin)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(path[0], true, true);
            context.PolyLineTo(path.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        var bounds = Rect.Intersect(geometry.Bounds, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Right) - left);
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - top);
        origin = new Point(left, top);
        return Draw(width, height, dc =>
        {
            dc.PushTransform(new TranslateTransform(-left, -top));
            dc.PushClip(geometry);
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.Pop();
            dc.Pop();
        });
    }

    /// <summary>
    /// 梯形校正: the quadrilateral (top-left, top-right, bottom-right, bottom-left in image pixels)
    /// is straightened into a rectangle, as wide / high as its longer opposite edges.
    /// </summary>
    public static BitmapSource Keystone(BitmapSource source, IReadOnlyList<Point> quad)
    {
        static double Length(Point a, Point b) => (a - b).Length;
        var width = Math.Max(1, (int)Math.Round(Math.Max(Length(quad[0], quad[1]), Length(quad[3], quad[2]))));
        var height = Math.Max(1, (int)Math.Round(Math.Max(Length(quad[0], quad[3]), Length(quad[1], quad[2]))));
        var (input, sw, sh) = ReadPixels(source);
        var map = Homography(new[] { new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height) }, quad);
        var output = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var px = x + 0.5;
                var py = y + 0.5;
                var d = map[6] * px + map[7] * py + 1;
                var sx = (map[0] * px + map[1] * py + map[2]) / d - 0.5;
                var sy = (map[3] * px + map[4] * py + map[5]) / d - 0.5;
                Sample(input, sw, sh, sx, sy, output, (y * width + x) * 4);
            }
        });
        return FromPixels(output, width, height);
    }

    /// <summary>Bilinear sample (premultiplied while mixing, so transparent pixels do not bleed); outside = transparent.</summary>
    private static void Sample(byte[] pixels, int width, int height, double x, double y, byte[] output, int at)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double b = 0, g = 0, r = 0, a = 0;
        for (var j = 0; j <= 1; j++)
        {
            for (var i = 0; i <= 1; i++)
            {
                var sx = x0 + i;
                var sy = y0 + j;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                {
                    continue;
                }

                var weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                var p = (sy * width + sx) * 4;
                var alpha = pixels[p + 3] * weight;
                b += pixels[p] * alpha;
                g += pixels[p + 1] * alpha;
                r += pixels[p + 2] * alpha;
                a += alpha;
            }
        }

        if (a <= 0)
        {
            return;
        }

        output[at] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
        output[at + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
        output[at + 2] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
        output[at + 3] = (byte)Math.Clamp(Math.Round(a), 0, 255);
    }

    /// <summary>The projective transform taking four points to four points (eight coefficients; the ninth is 1).</summary>
    private static double[] Homography(IReadOnlyList<Point> from, IReadOnlyList<Point> to)
    {
        var m = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            var row = i * 2;
            m[row, 0] = x;
            m[row, 1] = y;
            m[row, 2] = 1;
            m[row, 6] = -x * u;
            m[row, 7] = -y * u;
            m[row, 8] = u;
            m[row + 1, 3] = x;
            m[row + 1, 4] = y;
            m[row + 1, 5] = 1;
            m[row + 1, 6] = -x * v;
            m[row + 1, 7] = -y * v;
            m[row + 1, 8] = v;
        }

        // Gaussian elimination with partial pivoting.
        for (var c = 0; c < 8; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < 8; r++)
            {
                if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                {
                    pivot = r;
                }
            }

            for (var k = 0; k < 9; k++)
            {
                (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
            }

            for (var r = 0; r < 8; r++)
            {
                if (r == c || Math.Abs(m[c, c]) < 1e-12)
                {
                    continue;
                }

                var factor = m[r, c] / m[c, c];
                for (var k = c; k < 9; k++)
                {
                    m[r, k] -= factor * m[c, k];
                }
            }
        }

        var result = new double[8];
        for (var i = 0; i < 8; i++)
        {
            result[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : m[i, 8] / m[i, i];
        }

        return result;
    }

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            draw(dc);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Normalize(bitmap);
    }
}

/// <summary>Opening and saving image files in the editor.</summary>
public static class ImageFile
{
    public const string OpenFilter = "图片文件|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico|所有文件|*.*";
    public const string SaveFilter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|WebP 图片|*.webp|BMP 图片|*.bmp|TIFF 图片|*.tif;*.tiff";

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico" };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads an image (the first / largest frame, EXIF orientation applied). Throws when it cannot.</summary>
    public static BitmapSource Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            return Orient(ImageOps.Normalize(frame), Orientation(frame));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException)
        {
            // No Windows codec for it (WebP on many PCs…): SkiaSharp reads the common ones.
            using var decoded = SKBitmap.Decode(bytes) ?? throw new NotSupportedException("无法识别的图片格式", ex);
            using var bgra = decoded.Copy(SKColorType.Bgra8888);
            var pixels = new byte[bgra.Width * bgra.Height * 4];
            using (var unpremul = new SKBitmap(new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                decoded.CopyTo(unpremul, SKColorType.Bgra8888);
                System.Runtime.InteropServices.Marshal.Copy(unpremul.GetPixels(), pixels, 0, pixels.Length);
            }

            return ImageOps.FromPixels(pixels, bgra.Width, bgra.Height);
        }
    }

    /// <summary>Writes by the file's extension; formats without transparency go on white.</summary>
    public static void Save(BitmapSource image, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (extension == ".webp")
        {
            var (pixels, width, height) = ImageOps.ReadPixels(image);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using var data = bitmap.Encode(SKEncodedImageFormat.Webp, 92) ?? throw new InvalidOperationException("WebP 编码失败");
            File.WriteAllBytes(path, data.ToArray());
            return;
        }

        BitmapEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        var output = extension is ".jpg" or ".jpeg" or ".jfif" or ".bmp" ? OnWhite(image) : image;
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static BitmapSource OnWhite(BitmapSource image)
    {
        var (pixels, width, height) = ImageOps.ReadPixels(image);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 255)
            {
                continue;
            }

            pixels[i] = (byte)((pixels[i] * a + 255 * (255 - a)) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 255 * (255 - a)) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 255 * (255 - a)) / 255);
            pixels[i + 3] = 255;
        }

        return ImageOps.FromPixels(pixels, width, height);
    }

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort value)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
        }

        return 1;
    }

    /// <summary>Applies the EXIF orientation (1–8) so a photo shows the way it was taken.</summary>
    private static BitmapSource Orient(BitmapSource image, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null
        };
        return transform == null ? image : ImageOps.Normalize(new TransformedBitmap(image, transform));
    }
}
