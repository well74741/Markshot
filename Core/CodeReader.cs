namespace Markshot.Core;

/// <summary>
/// 二维码 / 条形码: everything that can be read in a picture (several codes at once), offline. zxing-cpp (native, all common
/// formats, finds many codes in one image and reads damaged / small / rotated / inverted ones); ZXing.Net as a second try.
/// </summary>
public static class CodeReader
{
    static CodeReader()
    {
        // zxing-cpp asks for "ZXing": the native library is shipped as zxingcpp-native.dll (see Markshot.csproj).
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(ZXingCpp.BarcodeReader).Assembly, (name, _, _) =>
            {
                if (!name.StartsWith("ZXing", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                var path = Path.Combine(AppContext.BaseDirectory, "zxingcpp-native.dll");
                return File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // a resolver is already set
        }
    }

    /// <summary>One code found: what it says, its kind and whether it is a web address.</summary>
    /// <param name="Area">Where the code is in the picture (pixels), when the reader could tell.</param>
    public sealed record Code(string Text, string Kind, Rect? Area = null)
    {
        public bool IsLink => Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static IReadOnlyList<Code> Read(BitmapSource image)
    {
        try
        {
            var (pixels, width, height) = ImageOps.ReadPixels(image);
            var gray = Gray(pixels, width, height);
            var found = ReadNative(gray, width, height, 1);
            if (found.Count == 0 && Math.Max(width, height) <= 1400)
            {
                // Tiny codes in a screenshot: once more at twice the size.
                var (big, bw, bh) = Upscale(gray, width, height);
                found = ReadNative(big, bw, bh, 0.5);
            }

            // ZXing.Net as well (it reads some low-contrast codes zxing-cpp gives up on); unless the picture is huge.
            if (found.Count == 0 || (long)width * height <= 2_500_000)
            {
                foreach (var code in ReadManaged(pixels, width, height))
                {
                    if (!found.Any(f => f.Text == code.Text && Near(f.Area, code.Area)))
                    {
                        found.Add(code);
                    }
                }
            }

            return found
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("识别二维码 / 条形码失败", ex);
            return Array.Empty<Code>();
        }
    }

    /// <summary>The same code found twice (by the two readers): its boxes overlap, or one of them is unknown.</summary>
    private static bool Near(Rect? a, Rect? b) => a is not { } x || b is not { } y || x.IntersectsWith(y);

    /// <summary>Loads the native reader ahead of the first use (the capture opens: the first read is quick).</summary>
    public static void WarmUp() => Task.Run(() =>
    {
        try
        {
            ReadNative(new byte[64 * 64], 64, 64, 1);
        }
        catch (Exception)
        {
            // logged when it is really used
        }
    });

    /// <summary>Luminance; transparent parts count as white (a cut-out pin).</summary>
    private static byte[] Gray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, j = 0; j < gray.Length; i += 4, j++)
        {
            var a = bgra[i + 3];
            var l = (bgra[i + 2] * 77 + bgra[i + 1] * 150 + bgra[i] * 29) >> 8;
            gray[j] = (byte)(a == 255 ? l : (l * a + 255 * (255 - a)) / 255);
        }

        return gray;
    }

    private static (byte[] Pixels, int Width, int Height) Upscale(byte[] gray, int width, int height)
    {
        var w = width * 2;
        var h = height * 2;
        var big = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = (y >> 1) * width;
            for (var x = 0; x < w; x++)
            {
                big[y * w + x] = gray[row + (x >> 1)];
            }
        }

        return (big, w, h);
    }

    private static List<Code> ReadNative(byte[] gray, int width, int height, double factor)
    {
        try
        {
            var reader = new ZXingCpp.BarcodeReader
            {
                Formats = ZXingCpp.BarcodeFormat.All,
                TryHarder = true,
                TryRotate = true,
                TryInvert = true,
                TryDownscale = true,
                MaxNumberOfSymbols = 32
            };
            var view = new ZXingCpp.ImageView(gray, width, height, ZXingCpp.ImageFormat.Lum);
            return reader.From(view)
                .Select(b =>
                {
                    var p = b.Position;
                    var xs = new[] { p.TopLeft.X, p.TopRight.X, p.BottomRight.X, p.BottomLeft.X };
                    var ys = new[] { p.TopLeft.Y, p.TopRight.Y, p.BottomRight.Y, p.BottomLeft.Y };
                    var area = new Rect(xs.Min() * factor, ys.Min() * factor, Math.Max(1, (xs.Max() - xs.Min()) * factor), Math.Max(1, (ys.Max() - ys.Min()) * factor));
                    return new Code(b.Text, KindOf(b.Format.ToString()), area);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            Log.Error("zxing-cpp 不可用，改用 ZXing.Net", ex);
            return new List<Code>();
        }
    }

    /// <summary>ZXing.Net (managed) when the native reader finds nothing or is missing.</summary>
    private static List<Code> ReadManaged(byte[] bgra, int width, int height)
    {
        var reader = new ZXing.BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true }
        };
        var results = reader.DecodeMultiple(new ZXing.RGBLuminanceSource(bgra, width, height, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
        return (results ?? Array.Empty<ZXing.Result>())
            .Select(r => new Code(r.Text, KindOf(r.BarcodeFormat.ToString()), ManagedArea(r, width, height)))
            .ToList();
    }

    private static Rect? ManagedArea(ZXing.Result result, int width, int height)
    {
        var points = result.ResultPoints?.Where(p => p != null).ToList();
        if (points is not { Count: >= 2 })
        {
            return null;
        }

        var left = points.Min(p => p.X);
        var right = points.Max(p => p.X);
        var top = points.Min(p => p.Y);
        var bottom = points.Max(p => p.Y);
        var qr = result.BarcodeFormat == ZXing.BarcodeFormat.QR_CODE && points.Count >= 3;
        var span = Math.Max(right - left, bottom - top);
        var padX = qr ? span * 0.27 : Math.Max(6, (right - left) * 0.05);
        var padY = qr ? span * 0.27 : Math.Max(20, (right - left) * 0.25);
        var area = new Rect(left - padX, top - padY, right - left + padX * 2, bottom - top + padY * 2);
        return Rect.Intersect(area, new Rect(0, 0, width, height)) is { IsEmpty: false } inside ? inside : null;
    }

    private static string KindOf(string format)
    {
        // ZXing.Net says QR_CODE, zxing-cpp "QR Code" / "Micro QR Code".
        var f = format.Replace("_", "").Replace(" ", "").Replace("-", "").ToUpperInvariant();
        return f switch
        {
            "QRCODE" or "MICROQRCODE" or "RMQRCODE" => "二维码",
            "DATAMATRIX" => "Data Matrix",
            "PDF417" => "PDF417",
            "AZTEC" => "Aztec",
            "MAXICODE" => "MaxiCode",
            _ => "条形码"
        };
    }
}
