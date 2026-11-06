using ZXing;
using ZXing.Common;

namespace Markshot.Core;

/// <summary>二维码 / 条形码: everything that can be read in a picture (several codes at once), offline.</summary>
public static class CodeReader
{
    /// <summary>One code found: what it says, its kind and whether it is a web address.</summary>
    public sealed record Code(string Text, string Kind)
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
            if (results == null || results.Length == 0)
            {
                // Small codes in a big screenshot: once more, twice the size.
                if (Math.Max(width, height) <= 1600)
                {
                    var big = ImageOps.Resize(image, width * 2, height * 2);
                    var (bp, bw, bh) = ImageOps.ReadPixels(big);
                    results = reader.DecodeMultiple(new RGBLuminanceSource(bp, bw, bh, RGBLuminanceSource.BitmapFormat.BGRA32));
                }
            }

            return (results ?? Array.Empty<Result>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .GroupBy(r => r.Text)
                .Select(g => new Code(g.Key, KindOf(g.First().BarcodeFormat)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("识别二维码 / 条形码失败", ex);
            return Array.Empty<Code>();
        }
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
