namespace Markshot.Core;

/// <summary>
/// 抠色 and 替换颜色 (like Photoshop's Color Range / Replace Color): pixels near a chosen colour are made transparent,
/// or moved to another colour keeping their own light and shade. "Near" is a tolerance with a soft falloff.
/// </summary>
public static class ColorOps
{
    /// <summary>How much a pixel belongs to the chosen colour: 1 inside the tolerance, fading to 0 just past it.</summary>
    /// <param name="tolerance">0–100: how different a colour may be and still count.</param>
    public static double Weight(byte r, byte g, byte b, Color key, double tolerance)
    {
        // Weighted RGB distance (the eye is most sensitive to green), 0 … ~100 for black ↔ white.
        var dr = r - key.R;
        var dg = g - key.G;
        var db = b - key.B;
        var distance = Math.Sqrt(0.30 * dr * dr + 0.59 * dg * dg + 0.11 * db * db) / 2.55;
        var inner = Math.Clamp(tolerance, 0, 100);
        var soft = 4 + inner * 0.5;
        if (distance <= inner)
        {
            return 1;
        }

        return distance >= inner + soft ? 0 : 1 - (distance - inner) / soft;
    }

    /// <summary>抠色: the colour (and what is close to it) becomes transparent; edges fade.</summary>
    public static BitmapSource Key(BitmapSource source, Color key, double tolerance)
    {
        var (pixels, width, height) = ImageOps.ReadPixels(source);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue;
            }

            var w = Weight(pixels[i + 2], pixels[i + 1], pixels[i], key, tolerance);
            if (w > 0)
            {
                pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * (1 - w));
            }
        }

        return ImageOps.FromPixels(pixels, width, height);
    }

    /// <summary>替换颜色: what is close to <paramref name="from"/> takes the hue, saturation and lightness of <paramref name="to"/>, keeping its shading.</summary>
    public static BitmapSource Replace(BitmapSource source, Color from, Color to, double tolerance)
    {
        var (pixels, width, height) = ImageOps.ReadPixels(source);
        var (fh, fs, fl) = ToHsl(from);
        var (th, ts, tl) = ToHsl(to);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue;
            }

            var r = pixels[i + 2];
            var g = pixels[i + 1];
            var b = pixels[i];
            var w = Weight(r, g, b, from, tolerance);
            if (w <= 0)
            {
                continue;
            }

            var (ph, ps, pl) = ToHsl(Color.FromRgb(r, g, b));
            var hue = fs < 0.05 ? th : th + (ph - fh); // a grey has no hue to shift from: it simply takes the new one
            var saturation = Math.Clamp(fs < 0.05 ? ts * (0.5 + ps) : ps + (ts - fs), 0, 1);
            var lightness = Math.Clamp(pl + (tl - fl), 0, 1);
            var shifted = FromHsl(hue, saturation, lightness);
            pixels[i + 2] = (byte)Math.Round(r + (shifted.R - r) * w);
            pixels[i + 1] = (byte)Math.Round(g + (shifted.G - g) * w);
            pixels[i] = (byte)Math.Round(b + (shifted.B - b) * w);
        }

        return ImageOps.FromPixels(pixels, width, height);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = (g - b) / d + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = (b - r) / d + 2;
        }
        else
        {
            h = (r - g) / d + 4;
        }

        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        if (s <= 0)
        {
            var grey = (byte)Math.Round(l * 255);
            return Color.FromRgb(grey, grey, grey);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Channel(double t)
        {
            t = (t % 1 + 1) % 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }

        return Color.FromRgb((byte)Math.Round(Channel(h + 1 / 3.0) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1 / 3.0) * 255));
    }
}
