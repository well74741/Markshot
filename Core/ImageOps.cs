using System.Runtime.InteropServices;
using SkiaSharp;

namespace Markshot.Core;

/// <summary>
/// Colour adjustments of the image editor; all neutral at their defaults. <see cref="Hue"/> turns the
/// colours (degrees); <see cref="Curve"/> is the tone curve's points as x, y pairs (0–255), null = straight.
/// </summary>
public sealed record ImageAdjust(double Brightness = 0, double Contrast = 0, double Saturation = 0, double Opacity = 100, bool Gray = false, bool Invert = false,
    double Hue = 0, double[]? Curve = null, double Temperature = 0, double Sharpen = 0, double Blur = 0)
{
    public bool IsNeutral => Brightness == 0 && Contrast == 0 && Saturation == 0 && Opacity >= 100 && !Gray && !Invert && Hue == 0 && ToneCurve.IsStraight(Curve) &&
                             Temperature == 0 && Sharpen == 0 && Blur == 0;

    /// <summary>Same values (the curve compared point by point).</summary>
    public bool SameAs(ImageAdjust? other) => other is not null && this with { Curve = null } == other with { Curve = null } &&
        (Curve ?? ToneCurve.Straight).AsSpan().SequenceEqual(other.Curve ?? ToneCurve.Straight);
}

/// <summary>The tone curve of 曲线: points through which a smooth curve (that never turns back) maps input to output.</summary>
public static class ToneCurve
{
    public static readonly double[] Straight = { 0, 0, 255, 255 };

    public static bool IsStraight(double[]? curve) =>
        curve == null || Enumerable.Range(0, curve.Length / 2).All(i => Math.Abs(curve[i * 2] - curve[i * 2 + 1]) < 0.5);

    /// <summary>
    /// 256 output values: monotone cubic (Fritsch–Carlson) through the points sorted by x; flat beyond
    /// the first / last point.
    /// </summary>
    public static byte[] Table(double[]? curve)
    {
        var table = new byte[256];
        var points = Enumerable.Range(0, (curve ?? Straight).Length / 2)
            .Select(i => (X: Math.Clamp((curve ?? Straight)[i * 2], 0, 255), Y: Math.Clamp((curve ?? Straight)[i * 2 + 1], 0, 255)))
            .OrderBy(p => p.X).ToList();
        for (var i = points.Count - 1; i > 0; i--)
        {
            if (points[i].X - points[i - 1].X < 0.5)
            {
                points.RemoveAt(i);
            }
        }

        if (points.Count == 1)
        {
            Array.Fill(table, (byte)Math.Round(points[0].Y));
            return table;
        }

        var n = points.Count;
        var slopes = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            slopes[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        }

        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            tangents[i] = slopes[i - 1] * slopes[i] <= 0 ? 0 : (slopes[i - 1] + slopes[i]) / 2;
        }

        for (var i = 0; i < n - 1; i++)
        {
            if (Math.Abs(slopes[i]) < 1e-9)
            {
                tangents[i] = tangents[i + 1] = 0;
                continue;
            }

            var a = tangents[i] / slopes[i];
            var b = tangents[i + 1] / slopes[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                tangents[i] = t * a * slopes[i];
                tangents[i + 1] = t * b * slopes[i];
            }
        }

        for (var x = 0; x < 256; x++)
        {
            double y;
            if (x <= points[0].X)
            {
                y = points[0].Y;
            }
            else if (x >= points[n - 1].X)
            {
                y = points[n - 1].Y;
            }
            else
            {
                var k = 0;
                while (x > points[k + 1].X)
                {
                    k++;
                }

                var h = points[k + 1].X - points[k].X;
                var t = (x - points[k].X) / h;
                var t2 = t * t;
                var t3 = t2 * t;
                y = (2 * t3 - 3 * t2 + 1) * points[k].Y + (t3 - 2 * t2 + t) * h * tangents[k] +
                    (-2 * t3 + 3 * t2) * points[k + 1].Y + (t3 - t2) * h * tangents[k + 1];
            }

            table[x] = (byte)Math.Clamp(Math.Round(y), 0, 255);
        }

        return table;
    }
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
        // 模糊 / 锐化 first (on the picture as it is), then the colours.
        if (adjust.Blur > 0.01 || adjust.Sharpen > 0.01)
        {
            Spatial(pixels, width, height, adjust.Blur / 20 * Math.Max(width, height) * 0.02, adjust.Sharpen / 100 * 1.5);
        }

        // A lookup table per channel value: brightness, contrast, invert, then the tone curve.
        var table = new byte[256];
        var curve = ToneCurve.Table(adjust.Curve);
        var brightness = adjust.Brightness / 100 * 255;
        var contrast = adjust.Contrast >= 0 ? 1 + adjust.Contrast / 100 * 2 : 1 + adjust.Contrast / 100;
        for (var i = 0; i < 256; i++)
        {
            var value = (i - 127.5) * contrast + 127.5 + brightness;
            value = Math.Clamp(value, 0, 255);
            table[i] = curve[(int)Math.Round(adjust.Invert ? 255 - value : value)];
        }

        var saturation = adjust.Gray ? 0 : 1 + adjust.Saturation / 100;
        var opacity = Math.Clamp(adjust.Opacity, 0, 100) / 100;
        // 色相: the colours turn around the grey axis keeping their brightness (as CSS hue-rotate).
        var hue = adjust.Hue * Math.PI / 180;
        double cos = Math.Cos(hue), sin = Math.Sin(hue);
        var m = new[]
        {
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072
        };
        var turn = Math.Abs(adjust.Hue) > 1e-6;
        // 色温: warmer = more red, less blue (cooler the other way).
        var warm = Math.Clamp(adjust.Temperature, -100, 100) / 100 * 0.2;
        var changeColor = Math.Abs(saturation - 1) > 1e-6 || turn || Math.Abs(warm) > 1e-6;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (changeColor)
                {
                    if (Math.Abs(warm) > 1e-6)
                    {
                        r = Math.Clamp(r * (1 + warm) + 255 * Math.Max(0, warm) * 0.08, 0, 255);
                        b = Math.Clamp(b * (1 - warm) + 255 * Math.Max(0, -warm) * 0.08, 0, 255);
                    }

                    if (turn)
                    {
                        var r2 = m[0] * r + m[1] * g + m[2] * b;
                        var g2 = m[3] * r + m[4] * g + m[5] * b;
                        var b2 = m[6] * r + m[7] * g + m[8] * b;
                        r = r2;
                        g = g2;
                        b = b2;
                    }

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

    /// <summary>An empty (transparent) canvas.</summary>
    public static BitmapSource Blank(int width, int height) => FromPixels(new byte[width * height * 4], width, height);

    /// <summary>Whatever is drawn, on a transparent bitmap of that size.</summary>
    public static BitmapSource Render(int width, int height, Action<DrawingContext> draw) => Draw(width, height, draw);

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(source, rect);
        return Normalize(cropped);
    }

    /// <summary>Scaled to a new size (high-quality resampling).</summary>
    /// <summary>
    /// 模糊 (box blur three times ≈ Gaussian, <paramref name="radius"/> pixels) and 锐化 (unsharp mask of the
    /// picture's fine detail by <paramref name="sharpen"/>), on premultiplied values so see-through edges stay clean.
    /// </summary>
    private static void Spatial(byte[] pixels, int width, int height, double radius, double sharpen)
    {
        var count = width * height * 4;
        var buffer = new float[count];
        for (var i = 0; i < count; i += 4)
        {
            // Premultiplied with the alpha as 0…255: colour = value × alpha, so value = colour / alpha later.
            var a = pixels[i + 3];
            buffer[i] = pixels[i] * a;
            buffer[i + 1] = pixels[i + 1] * a;
            buffer[i + 2] = pixels[i + 2] * a;
            buffer[i + 3] = a;
        }

        // Any blur at all moves at least a pixel (a small picture's radius would round to nothing).
        var r = radius > 0.001 ? Math.Max(1, (int)Math.Round(radius)) : 0;
        if (r >= 1)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(buffer, width, height, r);
            }
        }

        if (sharpen > 0.001)
        {
            var soft = (float[])buffer.Clone();
            BoxBlur(soft, width, height, 1);
            BoxBlur(soft, width, height, 1);
            var amount = (float)sharpen;
            Parallel.For(0, height, y =>
            {
                for (var i = y * width * 4; i < (y + 1) * width * 4; i++)
                {
                    buffer[i] += amount * (buffer[i] - soft[i]);
                }
            });
        }

        Parallel.For(0, height, y =>
        {
            for (var i = y * width * 4; i < (y + 1) * width * 4; i += 4)
            {
                var a = Math.Clamp(buffer[i + 3], 0, 255);
                pixels[i + 3] = (byte)Math.Round(a);
                if (a < 0.5)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                    continue;
                }

                pixels[i] = (byte)Math.Round(Math.Clamp(buffer[i] / a, 0, 255));
                pixels[i + 1] = (byte)Math.Round(Math.Clamp(buffer[i + 1] / a, 0, 255));
                pixels[i + 2] = (byte)Math.Round(Math.Clamp(buffer[i + 2] / a, 0, 255));
            }
        });
    }

    /// <summary>One box blur pass (horizontal then vertical) of radius <paramref name="r"/> over 4-channel floats.</summary>
    private static void BoxBlur(float[] data, int width, int height, int r)
    {
        var window = 2 * r + 1;
        var temp = new float[data.Length];
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var c = 0; c < 4; c++)
            {
                float sum = 0;
                for (var k = -r; k <= r; k++)
                {
                    sum += data[row + Math.Clamp(k, 0, width - 1) * 4 + c];
                }

                for (var x = 0; x < width; x++)
                {
                    temp[row + x * 4 + c] = sum / window;
                    sum += data[row + Math.Min(x + r + 1, width - 1) * 4 + c] - data[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
        });
        Parallel.For(0, width, x =>
        {
            for (var c = 0; c < 4; c++)
            {
                float sum = 0;
                for (var k = -r; k <= r; k++)
                {
                    sum += temp[(Math.Clamp(k, 0, height - 1) * width + x) * 4 + c];
                }

                for (var y = 0; y < height; y++)
                {
                    data[(y * width + x) * 4 + c] = sum / window;
                    sum += temp[(Math.Min(y + r + 1, height - 1) * width + x) * 4 + c] - temp[(Math.Max(y - r, 0) * width + x) * 4 + c];
                }
            }
        });
    }

    /// <summary>一键增强: the tone curve that stretches the picture's brightness over the full range (null: nothing to gain).</summary>
    public static double[]? AutoLevels(BitmapSource source)
    {
        var (pixels, _, _) = ReadPixels(source);
        var histogram = new long[256];
        long total = 0;
        for (var i = 0; i < pixels.Length; i += 8)
        {
            if (pixels[i + 3] < 16)
            {
                continue;
            }

            histogram[(int)Math.Round(0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2])]++;
            total++;
        }

        if (total == 0)
        {
            return null;
        }

        var clip = total * 0.005;
        int low = 0, high = 255;
        for (long seen = 0; low < 255 && (seen += histogram[low]) <= clip; low++)
        {
        }

        for (long seen = 0; high > 0 && (seen += histogram[high]) <= clip; high--)
        {
        }

        return high - low >= 235 || high <= low ? null : new double[] { low, 0, high, 255 };
    }

    /// <summary>A canvas of one colour.</summary>
    public static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        return FromPixels(pixels, width, height);
    }

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

    /// <summary>
    /// 网格形变: the picture divided into a grid of <paramref name="columns"/> × <paramref name="rows"/>
    /// cells, each cell stretched onto the quadrilateral of its four (moved) grid points. Points are in
    /// output pixels, row by row ((columns + 1) per row). Same size as the source; outside = transparent.
    /// </summary>
    public static BitmapSource MeshWarp(BitmapSource source, IReadOnlyList<Point> points, int columns, int rows, int outputWidth = 0, int outputHeight = 0)
    {
        var (input, sourceWidth, sourceHeight) = ReadPixels(source);
        var width = outputWidth > 0 ? outputWidth : sourceWidth;
        var height = outputHeight > 0 ? outputHeight : sourceHeight;
        var output = new byte[width * height * 4];
        double cellW = sourceWidth / (double)columns, cellH = sourceHeight / (double)rows;
        Point P(int c, int r) => points[r * (columns + 1) + c];
        var cells = new List<(int C, int R)>();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                cells.Add((c, r));
            }
        }

        foreach (var (c, r) in cells)
        {
            Point a = P(c, r), b = P(c + 1, r), cc = P(c + 1, r + 1), d = P(c, r + 1);
            var left = (int)Math.Floor(Math.Min(Math.Min(a.X, b.X), Math.Min(cc.X, d.X)));
            var right = (int)Math.Ceiling(Math.Max(Math.Max(a.X, b.X), Math.Max(cc.X, d.X)));
            var top = (int)Math.Floor(Math.Min(Math.Min(a.Y, b.Y), Math.Min(cc.Y, d.Y)));
            var bottom = (int)Math.Ceiling(Math.Max(Math.Max(a.Y, b.Y), Math.Max(cc.Y, d.Y)));
            left = Math.Max(0, left);
            top = Math.Max(0, top);
            right = Math.Min(width, right);
            bottom = Math.Min(height, bottom);
            Parallel.For(top, bottom, y =>
            {
                for (var x = left; x < right; x++)
                {
                    if (InverseBilinear(new Point(x + 0.5, y + 0.5), a, b, cc, d, out var u, out var v))
                    {
                        Sample(input, sourceWidth, sourceHeight, (c + u) * cellW - 0.5, (r + v) * cellH - 0.5, output, (y * width + x) * 4);
                    }
                }
            });
        }

        return FromPixels(output, width, height);
    }

    /// <summary>
    /// 网格形变 (Photoshop-style warp): the picture follows the curved grid of <paramref name="mesh"/>.
    /// The canvas is exactly as big as the warped picture (<paramref name="origin"/>: where its corner is
    /// in the original's pixels), so nothing is cut off and no empty border is added.
    /// </summary>
    public static BitmapSource Warp(BitmapSource source, WarpMesh mesh, out Point origin, int fine = 64)
    {
        var points = mesh.FineGrid(fine);
        double w = source.PixelWidth, h = source.PixelHeight;
        var left = Math.Floor(Math.Clamp(points.Min(p => p.X), -w * 2, w * 3));
        var top = Math.Floor(Math.Clamp(points.Min(p => p.Y), -h * 2, h * 3));
        var right = Math.Ceiling(Math.Clamp(points.Max(p => p.X), -w * 2, w * 3));
        var bottom = Math.Ceiling(Math.Clamp(points.Max(p => p.Y), -h * 2, h * 3));
        origin = new Point(left, top);
        var shifted = points.Select(p => new Point(p.X - left, p.Y - top)).ToList();
        return MeshWarp(source, shifted, fine, fine, Math.Max(1, (int)(right - left)), Math.Max(1, (int)(bottom - top)));
    }

    /// <summary>The (u, v) in [0, 1]² for which the bilinear patch a-b-c-d (clockwise from top-left) passes through p.</summary>
    private static bool InverseBilinear(Point p, Point a, Point b, Point c, Point d, out double u, out double v)
    {
        static double Cross(Vector x, Vector y) => x.X * y.Y - x.Y * y.X;
        Vector e = b - a, f = d - a, g = a - b + (c - d), h = p - a;
        var k2 = Cross(g, f);
        var k1 = Cross(e, f) + Cross(h, g);
        var k0 = Cross(h, e);
        u = v = -1;
        const double slack = 1e-6;

        bool Solve(double vv, out double uu)
        {
            var dx = e.X + g.X * vv;
            var dy = e.Y + g.Y * vv;
            uu = Math.Abs(dx) >= Math.Abs(dy) ? (h.X - f.X * vv) / dx : (h.Y - f.Y * vv) / dy;
            return vv >= -slack && vv <= 1 + slack && uu >= -slack && uu <= 1 + slack && double.IsFinite(uu);
        }

        if (Math.Abs(k2) < 1e-9)
        {
            if (Math.Abs(k1) < 1e-12)
            {
                return false;
            }

            v = -k0 / k1;
            return Solve(v, out u);
        }

        var w = k1 * k1 - 4 * k0 * k2;
        if (w < 0)
        {
            return false;
        }

        w = Math.Sqrt(w);
        var v1 = (-k1 - w) / (2 * k2);
        if (Solve(v1, out var u1))
        {
            u = u1;
            v = v1;
            return true;
        }

        var v2 = (-k1 + w) / (2 * k2);
        if (Solve(v2, out var u2))
        {
            u = u2;
            v = v2;
            return true;
        }

        return false;
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
        if (IsWebp(bytes))
        {
            // Always SkiaSharp, so a WebP opens the same whether or not Windows has a WebP codec.
            return LoadWithSkia(bytes, null);
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            return Orient(ImageOps.Normalize(frame), Orientation(frame));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException)
        {
            // No Windows codec for it: SkiaSharp reads the common ones.
            return LoadWithSkia(bytes, ex);
        }
    }

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
        bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';

    private static BitmapSource LoadWithSkia(byte[] bytes, Exception? inner)
    {
        // Decoded straight into unpremultiplied BGRA: copying a decoded (premultiplied) bitmap kept the
        // premultiplied values, and half-transparent pixels came out too dark.
        var size = SKBitmap.DecodeBounds(bytes);
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new NotSupportedException("无法识别的图片格式", inner);
        }

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var decoded = SKBitmap.Decode(bytes, info) ?? throw new NotSupportedException("无法识别的图片格式", inner);
        var pixels = new byte[info.Width * info.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return ImageOps.FromPixels(pixels, info.Width, info.Height);
    }

    /// <summary>JPEG / WebP quality (1…100) used by <see cref="Save"/>; the save window sets it.</summary>
    public static int Quality { get; set; } = 92;

    public static bool Lossy(string extension) => extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".webp";

    /// <summary>Formats that cannot hold transparency (it goes on white there).</summary>
    public static bool Opaque(string extension) => extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".bmp";

    /// <summary>The file as it would be written (for the size estimate).</summary>
    public static byte[] Encode(BitmapSource image, string extension, int quality)
    {
        var temp = Path.Combine(Path.GetTempPath(), "markshot-estimate-" + Guid.NewGuid().ToString("N") + extension);
        var before = Quality;
        try
        {
            Quality = quality;
            Save(image, temp);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            Quality = before;
            try { File.Delete(temp); } catch (IOException) { }
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
            using var data = bitmap.Encode(SKEncodedImageFormat.Webp, Math.Clamp(Quality, 1, 100)) ?? throw new InvalidOperationException("WebP 编码失败");
            File.WriteAllBytes(path, data.ToArray());
            return;
        }

        BitmapEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(Quality, 1, 100) },
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
