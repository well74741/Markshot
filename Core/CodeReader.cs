using ZXing;
using ZXing.Common;

namespace Markshot.Core;

/// <summary>二维码 / 条形码: everything that can be read in a picture (several codes at once), offline.</summary>
public static class CodeReader
{
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
            // Transparent parts read as white (a cut-out pin).
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a < 255)
                {
                    pixels[i] = (byte)(pixels[i] * a / 255 + 255 - a);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255 + 255 - a);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255 + 255 - a);
                }
            }

            var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true }
            };
            var results = reader.DecodeMultiple(source);
            var factor = 1.0;
            if (results == null || results.Length == 0)
            {
                // Small codes in a big screenshot: once more, twice the size.
                if (Math.Max(width, height) <= 1600)
                {
                    var big = ImageOps.Resize(image, width * 2, height * 2);
                    var (bp, bw, bh) = ImageOps.ReadPixels(big);
                    results = reader.DecodeMultiple(new RGBLuminanceSource(bp, bw, bh, RGBLuminanceSource.BitmapFormat.BGRA32));
                    factor = 0.5;
                }
            }

            return (results ?? Array.Empty<Result>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .GroupBy(r => r.Text)
                .Select(g => new Code(g.Key, KindOf(g.First().BarcodeFormat), AreaOf(g.First(), factor, width, height)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("识别二维码 / 条形码失败", ex);
            return Array.Empty<Code>();
        }
    }

    /// <summary>
    /// The code's box from the points the reader reports (a QR code: the centres of its three corner squares, which
    /// lie about 3.5 modules inside the edge; a barcode: two points on its scan line).
    /// </summary>
    private static Rect? AreaOf(Result result, double factor, int width, int height)
    {
        var points = result.ResultPoints?.Where(p => p != null).ToList();
        if (points is not { Count: >= 2 })
        {
            return null;
        }

        var left = points.Min(p => p.X) * factor;
        var right = points.Max(p => p.X) * factor;
        var top = points.Min(p => p.Y) * factor;
        var bottom = points.Max(p => p.Y) * factor;
        double padX, padY;
        if (result.BarcodeFormat == BarcodeFormat.QR_CODE && points.Count >= 3)
        {
            // Finder centres to the edge: 3.5 of the (size − 7) modules between them, plus a little quiet zone.
            var span = Math.Max(right - left, bottom - top);
            // The module count is not reported; the smallest code (21 modules, 14 between the centres) gives the
            // widest margin, bigger codes a little less — 0.27 of the span covers them all with a small quiet zone.
            padX = padY = span * 0.27;
        }
        else
        {
            // A barcode's points lie on one line: give it the height of a usual barcode.
            padX = Math.Max(6, (right - left) * 0.05);
            padY = Math.Max(20, (right - left) * 0.25);
        }

        var area = new Rect(left - padX, top - padY, right - left + padX * 2, bottom - top + padY * 2);
        return Rect.Intersect(area, new Rect(0, 0, width, height)) is { IsEmpty: false } inside ? inside : null;
    }

    private static string KindOf(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "二维码",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.PDF_417 => "PDF417",
        BarcodeFormat.AZTEC => "Aztec",
        _ => "条形码"
    };
}
