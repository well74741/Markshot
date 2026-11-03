using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Core;

static class ImageOpsTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    static BitmapSource Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return ImageOps.FromPixels(px, w, h);
    }

    // Left half red, right half blue.
    static BitmapSource Halves(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            if (x < w / 2) { px[i + 2] = 220; } else { px[i] = 220; }
            px[i + 3] = 255;
        }
        return ImageOps.FromPixels(px, w, h);
    }

    static (byte B, byte G, byte R, byte A) At(BitmapSource image, int x, int y)
    {
        var (px, w, _) = ImageOps.ReadPixels(image);
        var i = (y * w + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    static bool Near(int a, int b, int tolerance = 3) => Math.Abs(a - b) <= tolerance;

    public static void Run()
    {
        var c = Solid(4, 4, 200, 100, 50);
        var gray = At(ImageOps.Adjust(c, new ImageAdjust(Gray: true)), 1, 1);
        Check(gray.R == gray.G && gray.G == gray.B && Near(gray.R, (int)(0.299 * 200 + 0.587 * 100 + 0.114 * 50)), $"grey keeps the luma ({gray.R})");
        var inv = At(ImageOps.Adjust(c, new ImageAdjust(Invert: true)), 1, 1);
        Check(inv.R == 55 && inv.G == 155 && inv.B == 205, $"invert ({inv.R},{inv.G},{inv.B})");
        var bright = At(ImageOps.Adjust(c, new ImageAdjust(Brightness: 20)), 1, 1);
        Check(Near(bright.R, 251) && Near(bright.G, 151) && Near(bright.B, 101), $"brightness adds ({bright.R},{bright.G},{bright.B})");
        var flat = At(ImageOps.Adjust(c, new ImageAdjust(Contrast: -100)), 1, 1);
        Check(Near(flat.R, 128) && Near(flat.B, 128), "contrast -100 = flat grey");
        var noSat = At(ImageOps.Adjust(c, new ImageAdjust(Saturation: -100)), 1, 1);
        Check(noSat.R == noSat.B, "saturation -100 = grey");
        var moreSat = At(ImageOps.Adjust(c, new ImageAdjust(Saturation: 50)), 1, 1);
        Check(moreSat.R - moreSat.B > 150, "saturation up spreads the channels");
        Check(At(ImageOps.Adjust(c, new ImageAdjust(Opacity: 50)), 1, 1).A is 127 or 128, "opacity scales alpha");
        var neutral = At(ImageOps.Adjust(c, new ImageAdjust()), 1, 1);
        Check(neutral.R == 200 && neutral.G == 100 && neutral.B == 50 && neutral.A == 255, "neutral changes nothing");

        // 色相: red turns towards green at +120°, grey stays grey, a full turn changes nothing
        var red = Solid(2, 2, 220, 30, 30);
        var green = At(ImageOps.Adjust(red, new ImageAdjust(Hue: 120)), 0, 0);
        Check(green.G > green.R + 80 && green.G > green.B, $"hue +120: red becomes green ({green.R},{green.G},{green.B})");
        var greyHue = At(ImageOps.Adjust(Solid(2, 2, 120, 120, 120), new ImageAdjust(Hue: 77)), 0, 0);
        Check(Near(greyHue.R, 120) && Near(greyHue.G, 120) && Near(greyHue.B, 120), "hue leaves grey alone");
        var full = At(ImageOps.Adjust(c, new ImageAdjust(Hue: 360)), 1, 1);
        Check(Near(full.R, 200) && Near(full.G, 100) && Near(full.B, 50), "hue 360 = as before");

        // 曲线: a straight curve changes nothing, an S-curve darkens shadows and lifts lights, monotone
        Check(ToneCurve.Table(null).Select((v, i) => v == i).All(x => x), "straight curve: identity");
        var sTable = ToneCurve.Table(new double[] { 0, 0, 64, 40, 192, 220, 255, 255 });
        Check(sTable[64] == 40 && sTable[192] == 220 && sTable[32] < 32 && sTable[224] > 224, $"S-curve goes through its points ({sTable[64]}, {sTable[192]})");
        Check(sTable.Zip(sTable.Skip(1)).All(p => p.Second >= p.First), "the curve never goes down");
        var curved = At(ImageOps.Adjust(Solid(2, 2, 64, 64, 64), new ImageAdjust(Curve: new double[] { 0, 0, 64, 40, 192, 220, 255, 255 })), 0, 0);
        Check(curved.R == 40 && curved.G == 40, "the picture follows the curve");
        Check(new ImageAdjust(Curve: new double[] { 0, 0, 255, 255 }).IsNeutral && !new ImageAdjust(Hue: 5).IsNeutral, "neutral knows hue and curve");
        Check(new ImageAdjust(Curve: new double[] { 0, 0, 128, 90, 255, 255 }).SameAs(new ImageAdjust(Curve: new double[] { 0, 0, 128, 90, 255, 255 })), "same curve compares equal");

        // 网格形变: an untouched grid changes nothing; pulling the middle point drags the picture with it
        var grid = new Point[25];
        for (var r = 0; r <= 4; r++) for (var col = 0; col <= 4; col++) grid[r * 5 + col] = new Point(100 * col / 4.0, 60 * r / 4.0);
        var halvesForMesh = Halves(100, 60);
        var same = ImageOps.MeshWarp(halvesForMesh, grid, 4, 4);
        Check(At(same, 10, 30).R == 220 && At(same, 90, 30).B == 220 && At(same, 49, 30).R == 220 && At(same, 51, 30).B == 220, "mesh: untouched grid = the picture");
        var pulled = (Point[])grid.Clone();
        pulled[2 * 5 + 2] = new Point(70, 30); // the centre (50, 30) dragged right
        var warped = ImageOps.MeshWarp(halvesForMesh, pulled, 4, 4);
        Check(At(warped, 60, 30).R == 220 && At(warped, 51, 5).B == 220, "mesh: the red half follows the dragged point, the edge stays");
        var outward = (Point[])grid.Clone();
        outward[4] = new Point(80, 0);
        Check(At(ImageOps.MeshWarp(halvesForMesh, outward, 4, 4), 95, 1).A == 0, "mesh: pulled in from the corner, transparent outside");

        var halves = Halves(100, 60);
        var small = ImageOps.Resize(halves, 50, 30);
        Check(small.PixelWidth == 50 && small.PixelHeight == 30 && At(small, 10, 15).R > 200 && At(small, 40, 15).B > 200, "resize");
        var crop = ImageOps.Crop(halves, new Int32Rect(60, 10, 20, 20));
        Check(crop.PixelWidth == 20 && At(crop, 5, 5).B == 220, "crop");
        var turned = ImageOps.Rotate(halves, 90);
        Check(turned.PixelWidth == 60 && turned.PixelHeight == 100 && At(turned, 30, 10).R > 200 && At(turned, 30, 90).B > 200, $"rotate 90 ({turned.PixelWidth}x{turned.PixelHeight})");
        var tilted = ImageOps.Rotate(halves, 45);
        Check(tilted.PixelWidth > 100 && At(tilted, 1, 1).A == 0, "rotate 45 grows the canvas, corners transparent");

        var triangle = new[] { new Point(10, 10), new Point(90, 10), new Point(50, 50) };
        var cut = ImageOps.PathCrop(halves, triangle, out var origin);
        Check(cut.PixelWidth == 80 && cut.PixelHeight == 40 && origin == new Point(10, 10), $"path crop: bounding box ({cut.PixelWidth}x{cut.PixelHeight})");
        Check(At(cut, 40, 2).A == 255 && At(cut, 1, 38).A == 0 && At(cut, 78, 38).A == 0, "path crop: transparent outside the path");

        // Keystone: a rectangle photographed at an angle (trapezoid drawn into a canvas) comes back square.
        var canvas = new byte[200 * 200 * 4];
        for (var y = 40; y < 160; y++)
        {
            var t = (y - 40) / 120.0;
            var left = 60 - 40 * t; var right = 140 + 40 * t; // wider towards the bottom
            for (var x = 0; x < 200; x++)
            {
                var i = (y * 200 + x) * 4;
                canvas[i + 3] = 255;
                if (x >= left && x < right)
                {
                    var u = (x - left) / (right - left);
                    if (u < 0.5) canvas[i + 2] = 220; else canvas[i] = 220; // red left half, blue right half
                }
            }
        }
        var photo = ImageOps.FromPixels(canvas, 200, 200);
        var fixedImage = ImageOps.Keystone(photo, new[] { new Point(60, 40), new Point(140, 40), new Point(180, 160), new Point(20, 160) });
        var fw = fixedImage.PixelWidth; var fh = fixedImage.PixelHeight;
        Check(fw == 160 && fh >= 120 && fh <= 127, $"keystone: output size from the longer edges ({fw}x{fh})");
        var straight = true;
        foreach (var fy in new[] { 5, fh / 2, fh - 6 })
        {
            straight &= At(fixedImage, fw / 4, fy).R > 180 && At(fixedImage, fw * 3 / 4, fy).B > 180 && At(fixedImage, 3, fy).G < 40;
            straight &= At(fixedImage, fw / 2 - 5, fy).R > 150 && At(fixedImage, fw / 2 + 5, fy).B > 150;
        }
        Check(straight, "keystone: the centre line is straight again, the area is filled edge to edge");
        var identity = ImageOps.Keystone(halves, new[] { new Point(0, 0), new Point(100, 0), new Point(100, 60), new Point(0, 60) });
        Check(identity.PixelWidth == 100 && identity.PixelHeight == 60 && At(identity, 10, 30).R == 220 && At(identity, 90, 30).B == 220, "keystone on the full image changes nothing");

        // Files: every format round-trips; transparency survives where the format has it.
        var dir = Path.Combine(Path.GetTempPath(), "markshot-imageops");
        Directory.CreateDirectory(dir);
        var alpha = Solid(32, 32, 10, 200, 30, 128);
        foreach (var ext in new[] { ".png", ".webp", ".tif" })
        {
            var f = Path.Combine(dir, "a" + ext);
            ImageFile.Save(alpha, f);
            var back = At(ImageFile.Load(f), 5, 5);
            Check(back.A is >= 120 and <= 136 && Near(back.G, 200, 12), $"{ext} keeps colour and alpha ({back.R},{back.G},{back.B},{back.A})");
        }
        foreach (var ext in new[] { ".jpg", ".bmp" })
        {
            var f = Path.Combine(dir, "a" + ext);
            ImageFile.Save(alpha, f);
            var back = At(ImageFile.Load(f), 5, 5);
            Check(back.A == 255 && Near(back.R, 132, 12), $"{ext} goes on white ({back.R},{back.G},{back.B},{back.A})");
        }
        var gif = new GifBitmapEncoder(); gif.Frames.Add(BitmapFrame.Create(Solid(16, 16, 255, 0, 0)));
        var gifPath = Path.Combine(dir, "a.gif");
        using (var s = File.Create(gifPath)) gif.Save(s);
        Check(At(ImageFile.Load(gifPath), 3, 3).R > 240, "gif opens");
        Check(ImageFile.IsImage("x.WEBP") && !ImageFile.IsImage("x.txt"), "file types");
        var bad = Path.Combine(dir, "bad.png");
        File.WriteAllText(bad, "not an image");
        var threw = false;
        try { ImageFile.Load(bad); } catch (Exception) { threw = true; }
        Check(threw, "a broken file reports an error");
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
