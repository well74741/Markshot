using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Markshot.Core;

static class StitchTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    const int W = 320, H = 400, Header = 40, Footer = 30, Doc = 2600;

    // A "page": rows of coloured words with blank lines between paragraphs.
    static byte[] Page()
    {
        var rnd = new Random(7);
        var px = new byte[W * 4 * Doc];
        for (var y = 0; y < Doc; y++)
        {
            var line = y / 18;
            var inLine = y % 18;
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                byte r = 250, g = 250, b = 250;
                var blankLine = line % 7 == 6 || inLine >= 13;
                if (!blankLine && ((x / 9) * 31 + line * 17) % 5 != 0 && x > 12 && x < W - 12)
                {
                    var v = (byte)(40 + ((x * 7 + y * 13 + line * 29) % 90));
                    r = v; g = (byte)(v / 2); b = (byte)(255 - v);
                }
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        }
        return px;
    }

    static byte[] Frame(byte[] page, int offset)
    {
        var f = new byte[W * 4 * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                if (y < Header) { f[i] = 60; f[i + 1] = 40; f[i + 2] = 30; f[i + 3] = 255; continue; }            // sticky header
                if (y >= H - Footer) { f[i] = (byte)(x % 50); f[i + 1] = 90; f[i + 2] = 20; f[i + 3] = 255; continue; } // status bar
                Buffer.BlockCopy(page, ((offset + y - Header) * W + x) * 4, f, i, 4);
            }
        }
        return f;
    }

    public static void Run()
    {
        var page = Page();
        var body = H - Header - Footer;
        var stitcher = new ScrollStitcher(W, H);
        var offsets = new[] { 0, 0, 90, 230, 230, 300, 470, 655, 800, 1000, 1180, 1400, 1555, 1700, 1900, 2050, Doc - body };
        foreach (var o in offsets) stitcher.Add(Frame(page, o));
        var result = stitcher.Result();
        var expected = Header + (Doc) + Footer;
        Check(result.PixelHeight == expected, $"stitched height {result.PixelHeight} = header + whole page + footer ({expected})");
        var px = new byte[W * 4 * result.PixelHeight];
        result.CopyPixels(px, W * 4, 0);
        var bad = 0;
        for (var y = 0; y < Doc && y + Header < result.PixelHeight; y++)
        for (var x = 0; x < W; x++)
        {
            var a = ((y + Header) * W + x) * 4; var b = (y * W + x) * 4;
            if (px[a] != page[b] || px[a + 1] != page[b + 1] || px[a + 2] != page[b + 2]) { bad++; }
        }
        Check(bad == 0, $"page content identical ({bad} wrong pixels)");
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(result));
        using (var f = File.Create("stitched.png")) enc.Save(f);

        // scrolling back up, or nothing moving, adds nothing
        var again = new ScrollStitcher(W, H);
        again.Add(Frame(page, 500));
        again.Add(Frame(page, 600));
        var h = again.Height;
        again.Add(Frame(page, 400));
        Check(again.Height == h, "scrolling back up adds nothing");

        // ---- the session: frames come in on a timer, 完成 hands over the long image
        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var queue = new System.Collections.Generic.Queue<int>(new[] { 0, 150, 320, 500, 700, 900 });
        var current = 0;
        System.Windows.Media.Imaging.BitmapSource done = null;
        var session = new Markshot.UI.ScrollCaptureSession(new System.Windows.Int32Rect(100, 100, W, H), image => done = image)
        {
            Source = () => { if (queue.Count > 0) current = queue.Dequeue(); return Frame(page, current); }
        };
        session.Start();
        var until = DateTime.Now.AddSeconds(3);
        while (queue.Count > 0 && DateTime.Now < until)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            System.Threading.Thread.Sleep(10);
        }
        var panel = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.Title == "Markshot 长截图");
        if (panel?.Content is System.Windows.FrameworkElement root)
        {
            root.UpdateLayout();
            var shot = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(root);
            var e2 = new PngBitmapEncoder(); e2.Frames.Add(BitmapFrame.Create(shot));
            using (var f = File.Create("scroll_panel.png")) e2.Save(f);
        }
        session.Finish();
        Check(done != null && done.PixelHeight == Header + 900 + body + Footer, $"session: 完成 gives the long image ({done?.PixelHeight})");
        Check(!System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().Any(w => w.IsVisible), "session windows closed");
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
