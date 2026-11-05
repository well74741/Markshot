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

        // 元素样式 on a free shape: the outline and the shadow follow it
        var disc = new byte[100 * 100 * 4];
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 100; x++)
        {
            var i = (y * 100 + x) * 4;
            disc[i + 2] = 255;
            disc[i + 3] = (byte)(Math.Sqrt((x - 49.5) * (x - 49.5) + (y - 49.5) * (y - 49.5)) <= 45 ? 255 : 0);
        }

        var discImage = ImageOps.FromPixels(disc, 100, 100);
        Check(ImageOps.HasShape(discImage) && !ImageOps.HasShape(Solid(20, 20, 9, 9, 9)), "a picture with see-through parts counts as a shape, a plain one does not");
        var (ring, ringMargin) = ImageOps.ShapeOutline(discImage, 6, Colors.Lime);
        int M(double v) => (int)Math.Round(v + ringMargin);
        Check(At(ring, M(49.5 + 48), M(49.5)).A > 200 && At(ring, M(49.5 + 48), M(49.5)).G == 255, "outline: right outside the shape");
        Check(At(ring, M(49.5 + 55), M(49.5)).A == 0 && At(ring, M(3), M(3)).A == 0, "… nothing further out, nor in the square's corner (it is no rectangle)");
        Check(At(ring, M(49.5), M(49.5)).A == 0, "… and nothing under the solid inside (a see-through picture must not show it)");
        var bigShape = new byte[4000 * 3000 * 4];
        for (var i = 3; i < 4000 * 40 * 4; i += 4) bigShape[i] = 0;
        for (var i = 4000 * 40 * 4 + 3; i < bigShape.Length; i += 4) bigShape[i] = 255;
        var bigImage = ImageOps.FromPixels(bigShape, 4000, 3000);
        var effectClock = System.Diagnostics.Stopwatch.StartNew();
        ImageOps.ShapeOutline(bigImage, 12, Colors.White);
        var outlineMs = effectClock.ElapsedMilliseconds;
        effectClock.Restart();
        ImageOps.ShapeShadow(bigImage, 40);
        Console.WriteLine($"   style on a 4000x3000 shape: outline {outlineMs} ms, shadow {effectClock.ElapsedMilliseconds} ms");
        var (shade, shadeMargin) = ImageOps.ShapeShadow(discImage, 16);
        int N(double v) => (int)Math.Round(v + shadeMargin);
        Check(At(shade, N(49.5), N(49.5)).A > 90 && At(shade, N(49.5 + 50), N(49.5)).A is > 0 and < 90 && At(shade, N(2), N(2)).A < At(shade, N(49.5 + 50), N(49.5)).A,
            $"shadow: strongest inside, fading past the edge, weaker in the square's corner ({At(shade, N(49.5), N(49.5)).A}, {At(shade, N(49.5 + 50), N(49.5)).A}, {At(shade, N(2), N(2)).A})");

        // 色温, 模糊, 锐化, 一键增强
        var mid = Solid(4, 4, 128, 128, 128);
        var warmer = At(ImageOps.Adjust(mid, new ImageAdjust(Temperature: 80)), 1, 1);
        var cooler = At(ImageOps.Adjust(mid, new ImageAdjust(Temperature: -80)), 1, 1);
        Check(warmer.R > 128 && warmer.B < 128 && cooler.B > 128 && cooler.R < 128, $"色温: warm = more red, cool = more blue ({warmer.R}/{warmer.B}, {cooler.R}/{cooler.B})");
        var edge = new byte[40 * 20 * 4];
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 40; x++)
        {
            var v = (byte)(x < 20 ? 40 : 220);
            var i = (y * 40 + x) * 4;
            edge[i] = edge[i + 1] = edge[i + 2] = v;
            edge[i + 3] = 255;
        }

        var step = ImageOps.FromPixels(edge, 40, 20);
        var blurred = ImageOps.Adjust(step, new ImageAdjust(Blur: 20));
        Check(At(blurred, 19, 10).R > 60 && At(blurred, 20, 10).R < 200, $"模糊 softens an edge ({At(blurred, 19, 10).R}, {At(blurred, 20, 10).R})");
        var sharp = ImageOps.Adjust(step, new ImageAdjust(Sharpen: 100));
        Check(At(sharp, 19, 10).R < 40 && At(sharp, 20, 10).R > 220, $"锐化 makes the edge crisper ({At(sharp, 19, 10).R}, {At(sharp, 20, 10).R})");
        Check(At(sharp, 2, 10).R == 40 && At(sharp, 37, 10).R == 220, "… flat areas stay as they are");
        var holey = new byte[20 * 20 * 4];
        for (var i = 0; i < holey.Length; i += 4)
        {
            var inside = (i / 4 % 20) < 10;
            holey[i] = 0; holey[i + 1] = 0; holey[i + 2] = 255; holey[i + 3] = (byte)(inside ? 255 : 0);
        }

        var softHole = ImageOps.Adjust(ImageOps.FromPixels(holey, 20, 20), new ImageAdjust(Blur: 20));
        var rim = At(softHole, 10, 10);
        Check(rim.A > 0 && rim.A < 255 && rim.R > 240 && rim.G < 15, $"模糊 at a see-through edge keeps the colour clean (no dark fringe: {rim.R},{rim.G},{rim.B},{rim.A})");
        var dull = new byte[30 * 10 * 4];
        for (var i = 0; i < dull.Length; i += 4)
        {
            var v = (byte)(90 + (i / 4 % 30) * 2);
            dull[i] = dull[i + 1] = dull[i + 2] = v;
            dull[i + 3] = 255;
        }

        var levels = ImageOps.AutoLevels(ImageOps.FromPixels(dull, 30, 10));
        Check(levels != null && levels[0] >= 85 && levels[2] <= 155 && levels[1] == 0 && levels[3] == 255, $"一键增强 stretches a dull picture over the whole range ({string.Join(",", levels ?? Array.Empty<double>())})");
        var span = new byte[256 * 4 * 4];
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = span[i + 1] = span[i + 2] = (byte)(i / 4 % 256);
            span[i + 3] = 255;
        }

        Check(ImageOps.AutoLevels(ImageOps.FromPixels(span, 256, 4)) == null, "… and leaves one that already spans it");
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

        // 网格形变 like Photoshop: curved edges, a crosswise split, the canvas fits the result
        var plain = new WarpMesh(100, 60);
        var unchanged = ImageOps.Warp(halvesForMesh, plain, out var plainOrigin);
        Check(unchanged.PixelWidth == 100 && unchanged.PixelHeight == 60 && plainOrigin == new Point(0, 0) && At(unchanged, 49, 30).R == 220 && At(unchanged, 51, 30).B == 220,
            "warp: untouched = the picture, same size");
        var split = new WarpMesh(100, 60);
        var (row, column) = split.SplitAt(0.5, 0.5);
        Check(row == 1 && column == 1 && (split.Anchors[1, 1] - new Point(50, 30)).Length < 0.01 && split.Rows == 2 && split.Columns == 2, "split crosswise: a crossing at the point");
        var afterSplit = ImageOps.Warp(halvesForMesh, split, out _);
        Check(At(afterSplit, 48, 30).R == 220 && At(afterSplit, 52, 30).B == 220, "a split alone changes nothing");
        split.MoveAnchor(1, 1, new Point(70, 30));
        Check((split.Across[1, 0, 1] - new Point(70 - 50 / 3.0, 30)).Length < 0.5, "a crossing carries its handles");
        var pulledWarp = ImageOps.Warp(halvesForMesh, split, out _);
        Check(At(pulledWarp, 65, 30).R == 220 && At(pulledWarp, 51, 2).B == 220, "warp: the red half follows the dragged crossing");
        split.MoveHandle(false, 1, 0, 1, new Point(60, 20));
        var mirrored = split.Across[1, 1, 0] - split.Anchors[1, 1];
        var dragged = split.Across[1, 0, 1] - split.Anchors[1, 1];
        Check(Math.Abs(Vector.CrossProduct(mirrored, dragged)) < 0.01 && Vector.Multiply(mirrored, dragged) < 0, "the handle across an inner crossing turns along (smooth curve)");
        var bulge = new WarpMesh(100, 60);
        bulge.MoveHandle(false, 0, 0, 0, new Point(33, -30));
        bulge.MoveHandle(false, 0, 0, 1, new Point(66, -30));
        var bulged = ImageOps.Warp(halvesForMesh, bulge, out var bulgeOrigin);
        Check(bulgeOrigin.Y < -10 && bulged.PixelHeight > 70 && bulged.PixelWidth == 100, $"the canvas grows to hold a bulging edge ({bulged.PixelWidth}×{bulged.PixelHeight} at {bulgeOrigin})");
        Check(split.Locate(split.Evaluate(0.3, 0.7)) is { } found && Math.Abs(found.U - 0.3) < 0.01 && Math.Abs(found.V - 0.7) < 0.01, "a warped point is found back");

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
