using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>Editable closed pins, isolated persistence, real erased strokes/undo and lazy resource lifetimes.
/// No Markshot.App startup, global hotkeys, clipboard access or real input is used.</summary>
static class Round78ArchiveTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static AnnotationHistory History(PinWindow pin) => (AnnotationHistory)typeof(PinWindow).GetField("_history", Any).GetValue(pin);
    static AnnotationEditor Editor(PinWindow pin) => (AnnotationEditor)pin.FindName("Editor");
    static void Call(PinWindow pin, string method) => typeof(PinWindow).GetMethod(method, Any).Invoke(pin, null);
    static bool Erase(List<Annotation> items, IReadOnlyList<Point> points, double width, double strength = 1, double softness = 0) =>
        (bool)typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps").GetMethod("BindEraser", Any).Invoke(null, new object[] { items, points, width, strength, softness });
    static BitmapSource Picture(int width = 320, int height = 160, bool premultiplied = false)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = premultiplied ? (byte)1 : (byte)255; data[i + 1] = premultiplied ? (byte)2 : (byte)255;
            data[i + 2] = premultiplied ? (byte)3 : (byte)255; data[i + 3] = premultiplied ? (byte)5 : (byte)255;
        }
        var image = BitmapSource.Create(width, height, 120, 144, premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, data, width * 4); image.Freeze(); return image;
    }
    static byte[] Pixels(BitmapSource image) { var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8; var result = new byte[stride * image.PixelHeight]; image.CopyPixels(result, stride, 0); return result; }
    static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    static PenAnnotation Stroke()
    {
        var pen = new PenAnnotation { Thickness = 12, Color = Colors.Red };
        pen.Points.AddRange(new[] { new Point(30, 80), new Point(290, 80) }); return pen;
    }
    static PinWindow Reopen(PinArchive archive)
    {
        var state = archive.Restore(); var s = state.Snapshot;
        var pin = new PinWindow(s.Image, s.Left, s.Top, s.BaseImage, s.Annotations)
        { CornerRadius = state.CornerRadius, ShapedShadow = state.ShapedShadow, ShowActivated = false, ShowInTaskbar = false };
        pin.AdoptArchive(state); pin.RestoreView(s.Zoom, s.Opacity); pin.Show(); return pin;
    }
    static PinArchive Close(PinWindow pin)
    {
        PinArchive result = null;
        // The test's editable marks were created directly, without real pointer input. Compose
        // them just as ExitEditMode does, before the real Closed callback persists the pin.
        typeof(PinWindow).GetProperty("SourceImage", Any).SetValue(pin, Compose(pin));
        pin.Closed += (_, _) => result = pin.CaptureArchive();
        pin.CloseImmediately();
        if (result == null) throw new InvalidOperationException("Closed archive callback was not run"); return result;
    }
    static BitmapSource Compose(PinWindow pin) => (BitmapSource)typeof(PinWindow).GetMethod("ComposeEditedImage", Any).Invoke(pin, null);

    public static void Run()
    {
        _fail = 0;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.Combine(Path.GetTempPath(), "Markshot-Round78-Archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var store = PinStore.UseDirectoryForTests(Path.Combine(directory, "pins"));
        try
        {
            ClosedStroke(directory);
            AllFields(directory);
            RetentionAndLegacy(directory);
            Lifetimes(directory);
            Timing(directory);
            QuotaAndInvalidStep(directory);
        }
        finally
        {
            // Only windows created by this suite, in this test process.
            foreach (var pin in Application.Current.Windows.OfType<PinWindow>().ToArray()) pin.CloseImmediately();
            Collect();
            var sessionPath = (string)typeof(PinArchive).GetField("_sessionDirectory", Any).GetValue(null);
            PinArchive.CleanupSession();
            Check(sessionPath == null || !Directory.Exists(sessionPath), "exit cleanup removes this process's session archives and journals");
            try { Directory.Delete(directory, true); } catch { }
        }
        Console.WriteLine(_fail == 0 ? "ARCHIVE ALL PASS" : $"ARCHIVE {_fail} FAILED");
        if (_fail != 0) Environment.ExitCode = 1;
    }

    static void ClosedStroke(string directory)
    {
        var image = Picture(); var pen = Stroke();
        var pin = new PinWindow(image, -6000, -6000, image, new List<Annotation> { pen })
        { ShowActivated = false, ShowInTaskbar = false, CornerRadius = 7, ShapedShadow = true };
        pin.Show(); pin.RestoreView(0.75, 0.6);
        var history = History(pin);
        history.Checkpoint(Array.Empty<Annotation>()); // the drawing can be undone after reopening
        history.Checkpoint(Editor(pin).Items);
        Check(Erase(Editor(pin).Items, new[] { new Point(160, 55), new Point(160, 105) }, 24), "production eraser binds a real pen stroke");
        Check(pen.IsErasedAt(new Point(160, 80)), "real stroke has an erased gap before closing");
        var archive = Close(pin);
        var entry = new PinEntry { Archive = archive };
        Check(PinStore.Save(entry, archive), "closed pin persists an editable archive");
        Check(entry.Image == null && entry.Archive != null, "closed entry retains a path, not a full-size pixel array");
        Check(Path.GetDirectoryName(entry.FilePath) == Path.Combine(directory, "pins"), "PinStore save stays in this suite's directory");

        var restored = Reopen(archive);
        var restoredPen = (PenAnnotation)Editor(restored).Items.Single();
        Check(restoredPen.IsErasedAt(new Point(160, 80)), "closed→restored preserves the mask on editable pen strokes");
        Check(restored.Snapshot().Zoom == 0.75 && restored.Snapshot().Opacity == 0.6 && restored.CornerRadius == 7 && restored.ShapedShadow, "place/view/corner/shape survive restoration");
        Call(restored, "Undo");
        Check(Editor(restored).Items.Count == 1 && ((PenAnnotation)Editor(restored).Items[0]).EraseMasks == null, "real pin Undo removes the pre-close erase mask");
        Check(Pixel(Compose(restored), 160, 80).R > 240 && Pixel(Compose(restored), 160, 80).G < 30, "undo visibly restores original red ink, not flattened PNG");
        Call(restored, "Redo");
        Check(((PenAnnotation)Editor(restored).Items[0]).IsErasedAt(new Point(160, 80)), "real pin Redo restores its erased gap");
        Check(Pixel(Compose(restored), 160, 80).G > 240, "redo visibly exposes the white base in the gap");
        Call(restored, "Undo");
        var before = Editor(restored).Items.Select(a => a.Clone()).ToList();
        Check(Erase(Editor(restored).Items, new[] { new Point(100, 60), new Point(100, 100) }, 20), "restored real ink remains erasable at another position");
        History(restored).Checkpoint(before);
        Check(((PenAnnotation)Editor(restored).Items[0]).IsErasedAt(new Point(100, 80)), "new erase edits the restored element");
        Call(restored, "Undo");
        Call(restored, "Undo");
        Check(Editor(restored).Items.Count == 0, "full pre-close drawing undo survives additional edits");
        var again = Close(restored);
        var redoRestored = Reopen(again);
        Check(History(redoRestored).CanRedo, "close/reopen preserves the redo stack as well as undo");
        Call(redoRestored, "Redo");
        Check(Editor(redoRestored).Items.Count == 1, "restored redo stack order restores the drawing first");
        typeof(PinWindow).GetMethod("ApplyOrientation", Any).Invoke(redoRestored, new object[] { 'H', true });
        Check(((PenAnnotation)Editor(redoRestored).Items[0]).Points[0].X == 290, "actual flip transforms restored editable stroke points");
        Call(redoRestored, "Redo");
        Check(((PenAnnotation)Editor(redoRestored).Items[0]).IsErasedAt(new Point(220, 80)), "flipped lazy redo preserves the eraser mask in the new coordinate space");
        Call(redoRestored, "Undo");
        Check(((PenAnnotation)Editor(redoRestored).Items[0]).EraseMasks == null, "flipped lazy undo returns the un-erased editable stroke");
        redoRestored.CloseImmediately();
    }

    static void AllFields(string directory)
    {
        var image = Picture(96, 64, premultiplied: true);
        var pen = Stroke(); pen.Corners.Add(1); pen.Pressures = new List<float> { 0.2f, 0.9f }; pen.Highlight = true; pen.Softness = 0.4;
        var poly = new PolylineAnnotation(); poly.Points.AddRange(pen.Points); poly.Corners.Add(0); poly.Bends[0] = new Vector(9, -3);
        var cut = new CutoutAnnotation { Baked = true, Thickness = 9 }; cut.Points.AddRange(pen.Points);
        var brush = new MosaicBrushAnnotation { Pattern = image, PatternRect = new Rect(1, 2, 96, 64), BlockSize = 14, Blur = 6, Gaussian = true, Feather = 8 };
        brush.Points.AddRange(pen.Points); brush.Corners.Add(1);
        var items = new List<Annotation>
        {
            new RectAnnotation { Rect = new Rect(1, 2, 30, 40) }, new EllipseAnnotation { Rect = new Rect(2, 3, 40, 20) },
            new ArrowAnnotation { Start = new Point(3, 4), End = new Point(60, 40), Style = ArrowStyle.Double, Bend = new Vector(-2, 8) },
            new LineAnnotation { Start = new Point(4, 5), End = new Point(50, 30), Bend = new Vector(7, -4) }, pen, cut, poly,
            new ImageAnnotation { Image = image, Origin = image.Clone(), Rect = new Rect(5, 6, 90, 60), Radius = 3, ShadowSize = 5, BorderWidth = 2, BorderColor = Colors.Lime,
                Adjust = new ImageAdjust(10, 20, -8, 70, true, true, 30, new double[] { 0, 5, 128, 160, 255, 250 }, 4, 3, 2) },
            new MosaicAnnotation { Rect = new Rect(7, 8, 50, 20), BlockSize = 11, Blur = 7, Gaussian = true, Feather = 9, Pattern = image, PatternRect = new Rect(1, 2, 96, 64) },
            new WatermarkAnnotation { Rect = new Rect(0, 0, 96, 64), Text = "水印", FontSize = 18, Angle = -30, Shift = new Vector(11, 12) },
            new TextAnnotation { Position = new Point(8, 9), Text = "笔迹\ntext", FontSize = 21, Bold = true, Italic = true, FontName = "Segoe UI", StrokeColor = Colors.Blue, StrokeThickness = 2 },
            new NumberAnnotation { Center = new Point(30, 20), Number = 12, Radius = 18, Hollow = true, Outlined = false, Tail = new Point(60, 50) }, brush,
            new SpotlightAnnotation { Rect = new Rect(10, 11, 40, 30), Ellipse = true, Darkness = 0.7, Feather = 15 }
        };
        foreach (var a in items)
        {
            a.Color = Colors.Orange; a.Fill = true; a.CornerRadius = 2; a.Opacity = 0.72; a.Rotation = 13; a.Hidden = true;
            a.EraseMasks = new List<EraseStroke> { new(new List<Point> { new(2, 3), new(6, 9) }, 4, new Size(30, 20), 0.45, 0.6) };
        }
        var history = new AnnotationHistory(); history.Checkpoint(items); history.Checkpoint(items); history.Undo(items);
        // The current data itself keeps Hidden; undo clones deliberately reset it, as in the editor.
        foreach (var a in items) a.Hidden = true;
        var archive = PinArchive.Create(new PinArchiveState(new PinSnapshot(image, image, items, -6000, -6000, 1, 1), history,
            5, true, "HVR", image, image, "译文", "English", "中文", true), Path.Combine(directory, "all-fields.markpin"));
        var state = archive.Restore(); var restored = state.Snapshot.Annotations;
        Check(restored.Count == 14 && restored.Select(a => a.GetType()).SequenceEqual(items.Select(a => a.GetType())), "all 14 known annotation types round-trip explicitly");
        Check(restored.All(a => a.Color == Colors.Orange && a.Fill && a.CornerRadius == 2 && a.Opacity == 0.72 && a.Rotation == 13 && a.Hidden), "all common style/hidden fields survive");
        Check(restored.All(a => a.EraseMasks is { Count: 1 } && a.EraseMasks[0].Width == 4 && a.EraseMasks[0].RecordedSize == new Size(30, 20) && a.EraseMasks[0].Strength == 0.45 && a.EraseMasks[0].Softness == 0.6 && a.EraseMasks[0].Points.SequenceEqual(new[] { new Point(2, 3), new Point(6, 9) })), "all erase mask points/size/width/strength/softness survive");
        var rp = (PenAnnotation)restored[4]; Check(rp.Points.SequenceEqual(pen.Points) && rp.Corners.SetEquals(pen.Corners) && rp.Pressures.SequenceEqual(pen.Pressures) && rp.Highlight && rp.Softness == 0.4, "pen points/corners/pressure/highlight/softness survive");
        Check(((CutoutAnnotation)restored[5]).Baked && ((CutoutAnnotation)restored[5]).Points.SequenceEqual(cut.Points), "cutout baked state and stroke survive");
        Check(((PolylineAnnotation)restored[6]).Bends[0] == new Vector(9, -3) && ((PolylineAnnotation)restored[6]).Corners.Contains(0), "polyline bends and corners survive");
        var ri = (ImageAnnotation)restored[7]; var ai = (ImageAnnotation)items[7];
        Check(ri.Adjust.SameAs(ai.Adjust) && ri.Rect == ai.Rect && ri.Radius == 3 && ri.ShadowSize == 5 && ri.BorderWidth == 2 && ri.BorderColor == Colors.Lime && ReferenceEquals(ri.Image, ri.Origin), "image/origin/style and every adjustment including tone curve survive and deduplicate");
        Check(((MosaicAnnotation)restored[8]).PatternRect == new Rect(1, 2, 96, 64) && ((MosaicAnnotation)restored[8]).Blur == 7 && ((MosaicAnnotation)restored[8]).Gaussian && ((MosaicAnnotation)restored[8]).Feather == 9, "mosaic pattern/blur/feather survive");
        var wm = (WatermarkAnnotation)restored[9]; Check(wm.Text == "水印" && wm.FontSize == 18 && wm.Angle == -30 && wm.Shift == new Vector(11, 12), "watermark text/font/angle/shift survive");
        var tx = (TextAnnotation)restored[10]; Check(tx.Text == "笔迹\ntext" && tx.Bold && tx.Italic && tx.FontName == "Segoe UI" && tx.StrokeColor == Colors.Blue && tx.StrokeThickness == 2, "text formatting/font/outline survive");
        var no = (NumberAnnotation)restored[11]; Check(no.Number == 12 && no.Radius == 18 && no.Hollow && !no.Outlined && no.Tail == new Point(60, 50), "number badge/tail/style survive");
        var mb = (MosaicBrushAnnotation)restored[12]; Check(mb.Points.SequenceEqual(brush.Points) && mb.Corners.SetEquals(brush.Corners) && mb.Gaussian && mb.Blur == 6 && mb.Feather == 8 && mb.BlockSize == 14, "mosaic brush path/corners/style survive");
        var sp = (SpotlightAnnotation)restored[13]; Check(sp.Ellipse && sp.Darkness == 0.7 && sp.Feather == 15, "spotlight shape/darkness/feather survive");
        Check(state.Orientation == "HVR" && state.TranslationText == "译文" && state.SourceLabel == "English" && state.TargetLabel == "中文" && state.ShowsOriginal, "orientation and original/translation switching metadata survive");
        Check(state.Snapshot.Image.Format == PixelFormats.Pbgra32 && state.Snapshot.Image.DpiX == 120 && state.Snapshot.Image.DpiY == 144 && Pixels(state.Snapshot.Image).SequenceEqual(Pixels(image)), "premultiplied low-alpha pixels and DPI are exactly lossless");
        using (var zip = ZipFile.OpenRead(archive.FilePath)) Check(zip.Entries.Count(e => e.FullName.StartsWith("images/")) == 1, "same bitmap content across layers/current/undo/redo stored once");
        var restoredHistory = state.History; var live = restored;
        Check(restoredHistory.CanUndo && restoredHistory.CanRedo && restoredHistory.ChangeCount == history.ChangeCount, "both stack directions and ChangeCount survive");
        restoredHistory.Undo(live); Check(live.All(a => !a.Hidden), "history clones retain normal non-hidden snapshot semantics");
    }

    static void RetentionAndLegacy(string directory)
    {
        var first = PinStore.Load().First();
        var firstArchive = first.LoadArchive().KeepForSession(); var persistent = first.FilePath;
        var seed = firstArchive.Restore();
        for (var i = 0; i < 32; i++)
        {
            var entry = new PinEntry { Created = DateTime.Now.AddMinutes(i + 1) };
            PinStore.Save(entry, firstArchive);
        }
        Check(PinStore.Load().Count == 30 && !File.Exists(persistent), "persistent index/file eviction keeps its existing 30-entry range");
        var restored = Reopen(firstArchive); Call(restored, "Undo");
        Check(Editor(restored).Items.Count == 1 && ((PenAnnotation)Editor(restored).Items[0]).EraseMasks == null, "session copy survives persistent file eviction with real stroke undo");
        restored.CloseImmediately();
        var diskOnly = PinStore.Load().Last();
        Check(diskOnly.Image == null && diskOnly.Archive == null && diskOnly.Thumbnail().PixelWidth <= 320, "restart/disk-only entries lazily decode only small thumbnails");
        var fromDisk = Reopen(diskOnly.LoadArchive().KeepForSession()); Call(fromDisk, "Undo");
        Check(Editor(fromDisk).Items.Count == 1 && ((PenAnnotation)Editor(fromDisk).Items[0]).EraseMasks == null, "evict all in-memory snapshots and restore disk archive with undo");
        fromDisk.CloseImmediately();
        var legacy = new PinEntry(); var legacyImage = Picture(40, 20); PinStore.Save(legacy, legacyImage);
        var old = PinStore.Load().Single(e => e.FilePath == legacy.FilePath);
        Check(old.LoadArchive() == null && old.LoadImage().PixelWidth == 40 && Pixels(old.LoadImage()).SequenceEqual(Pixels(legacyImage)), "pre-archive PNG records still load as flattened pictures");
        var outside = Path.Combine(directory, "outside.png"); File.WriteAllText(outside, "sentinel");
        PinStore.Delete(new PinEntry { FilePath = outside }); Check(File.Exists(outside), "Delete cannot touch a supplied file outside the isolated store");
        using (PinStore.UseDirectoryForTests(Path.Combine(directory, "malicious")))
        {
            File.WriteAllText(Path.Combine(directory, "malicious", "history.json"), "[{\"File\":\"../outside.png\"}]");
            Check(PinStore.Load().Count == 0, "untrusted index cannot traverse outside PinStore root");
        }
        var bad = Path.Combine(directory, "unknown.markpin");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("current").Open())
        using (var w = new BinaryWriter(stream)) w.Write(999);
        bool rejected = false; try { new PinArchive(bad).Restore(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "unknown archive versions are rejected without dynamic type construction");
        // Exercise production session retention without constructing Application/starting tray or hotkeys.
        var app = (Markshot.App)RuntimeHelpers.GetUninitializedObject(typeof(Markshot.App));
        var closed = new List<PinEntry>(); var ph = new PinHistory();
        typeof(Markshot.App).GetField("_closedPins", Any).SetValue(app, closed);
        typeof(Markshot.App).GetField("<History>k__BackingField", Any).SetValue(app, ph);
        for (var i = 0; i < 35; i++) closed.Add(new PinEntry { Archive = firstArchive });
        var evicted = closed[0]; typeof(Markshot.App).GetMethod("TrimClosedPinArchives", Any).Invoke(app, null);
        Check(closed.Count == 30 && evicted.Archive == null, "production closed-session retention releases handles beyond 30 entries");
        Check(File.Exists(firstArchive.FilePath) && seed.History.CanUndo, "retention does not delete an archive still leased by live undo readers");
    }

    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (string Path, AnnotationHistory History) LeasedHistory(string directory)
    {
        var image = Picture(); var pen = Stroke(); var history = new AnnotationHistory(); history.Checkpoint(new List<Annotation> { pen });
        var archive = PinArchive.CreateSession(new PinArchiveState(new PinSnapshot(image, image, new List<Annotation> { pen }, -6000, -6000, 1, 1), history));
        return (archive.FilePath, archive.Restore().History);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string ReleaseLease(string directory)
    {
        var leased = LeasedHistory(directory); Collect();
        Check(File.Exists(leased.Path), "GC cannot delete the compressed source of a live lazy undo stack");
        var items = new List<Annotation>(); leased.History.Undo(items);
        Check(items.Count == 1 && items[0] is PenAnnotation, "leased lazy source remains readable after GC");
        return leased.Path;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (AnnotationHistory History, WeakReference Bitmap) LazyImage(string directory)
    {
        var image = Picture(160, 90); var old = Picture(164, 94);
        var history = new AnnotationHistory(); history.Checkpoint(new List<Annotation> { new ImageAnnotation { Image = old, Rect = new Rect(0, 0, 164, 94) } });
        var archive = PinArchive.CreateSession(new PinArchiveState(new PinSnapshot(image, image, new List<Annotation> { new ImageAnnotation { Image = image, Rect = new Rect(0, 0, 160, 90) } }, -6000, -6000, 1, 1), history));
        var state = archive.Restore(); state.History.Undo(state.Snapshot.Annotations);
        var weak = new WeakReference(((ImageAnnotation)state.Snapshot.Annotations[0]).Image);
        state.Snapshot.Annotations.Clear(); return (state.History, weak);
    }
    sealed class TransformOwner { public BitmapSource Bitmap = Picture(500, 300); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (AnnotationHistory History, WeakReference Owner) TransformedHistory(string directory)
    {
        var leased = LeasedHistory(directory); var owner = new TransformOwner(); var weak = new WeakReference(owner);
        leased.History.ForEachStored(a => { a.Translate(12, 0); GC.KeepAlive(owner.Bitmap); });
        return (leased.History, weak);
    }
    static void Lifetimes(string directory)
    {
        var transformed = TransformedHistory(directory); Collect();
        Check(!transformed.Owner.IsAlive, "batch-mapped archive history does not retain the owner's bitmap-bearing transform closure");
        var mappedItems = new List<Annotation>(); transformed.History.Undo(mappedItems);
        Check(((PenAnnotation)mappedItems[0]).Points[0].X == 42, "batch-mapped history remains lazy and restores transformed coordinates");
        var path = ReleaseLease(directory); Collect();
        Check(!File.Exists(path), "unreferenced session source is cleaned after history is released");
        var lazy = LazyImage(directory); Collect();
        Check(!lazy.Bitmap.IsAlive && lazy.History.CanRedo, "undo reader does not permanently cache a decoded full-size image");
    }

    static void Timing(string directory)
    {
        var image = Picture(1600, 900); var items = new List<Annotation>(); var history = new AnnotationHistory();
        for (var i = 0; i < 50; i++)
        {
            history.Checkpoint(items);
            var pen = new PenAnnotation { Thickness = 8, Color = Colors.Red };
            for (var j = 0; j < 48; j++) pen.Points.Add(new Point(20 + j * 30, 30 + i * 16));
            pen.Corners.Add(12); items.Add(pen);
            Erase(items, new[] { new Point(700, 0), new Point(700, 900) }, 18, 0.7, 0.2);
        }
        Collect(); var before = GC.GetTotalMemory(true); var watch = Stopwatch.StartNew();
        var archive = PinArchive.Create(new PinArchiveState(new PinSnapshot(image, image, items, -6000, -6000, 1, 1), history), Path.Combine(directory, "timing.markpin"));
        var save = watch.ElapsedMilliseconds; watch.Restart(); var state = archive.Restore(); var restore = watch.ElapsedMilliseconds;
        var after = GC.GetTotalMemory(true);
        Check(state.History.Depth == 50, "all 50 existing history steps survive a 1600×900 archive");
        using (var zip = ZipFile.OpenRead(archive.FilePath)) Check(zip.Entries.Count(e => e.FullName.StartsWith("images/")) == 1, "50 erase snapshots do not duplicate the full-size base image");
        for (var i = 0; i < 50; i++) state.History.Undo(state.Snapshot.Annotations);
        Check(state.Snapshot.Annotations.Count == 0, "all 50 lazy history steps undo in correct order");
        Console.WriteLine($"ARCHIVE PERF 1600x900, 50 strokes/erase snapshots: save={save}ms restore={restore}ms compressed={archive.StoredBytes / 1024}KiB managed-retained-delta={(after - before) / 1024}KiB");

        // An intentionally high-entropy synthetic background, never a screenshot of the user's
        // screen. Distinct composed/base images give a more conservative archival benchmark.
        var pixels = new byte[1600 * 900 * 4]; uint random = 17;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            pixels[i] = (byte)random; pixels[i + 1] = (byte)(random >> 8); pixels[i + 2] = (byte)(random >> 16); pixels[i + 3] = 255;
        }
        var texture = BitmapSource.Create(1600, 900, 96, 96, PixelFormats.Bgra32, null, pixels, 6400); texture.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawImage(texture, new Rect(0, 0, 1600, 900)); AnnotationEditor.DrawLayers(dc, items); }
        var composed = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32); composed.Render(visual); composed.Freeze();
        Collect(); var retainedBefore = GC.GetTotalMemory(true); var allocatedBefore = GC.GetTotalAllocatedBytes();
        var processBefore = Process.GetCurrentProcess().PrivateMemorySize64; watch.Restart();
        var texturedArchive = PinArchive.Create(new PinArchiveState(new PinSnapshot(composed, texture, items, -6000, -6000, 1, 1), history), Path.Combine(directory, "texture.markpin"));
        var texturedSave = watch.ElapsedMilliseconds; watch.Restart(); var texturedState = texturedArchive.Restore(); var texturedRestore = watch.ElapsedMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
        var retained = GC.GetTotalMemory(true) - retainedBefore;
        var processDelta = Process.GetCurrentProcess().PrivateMemorySize64 - processBefore;
        Check(texturedState.History.Depth == 50 && Pixels(texturedState.Snapshot.BaseImage).SequenceEqual(pixels), "high-entropy 1600×900 bottom/composed images remain lossless with 50 lazy steps");
        Console.WriteLine($"ARCHIVE PERF high-entropy 1600x900 + distinct composed output, 50 steps: save={texturedSave}ms restore={texturedRestore}ms compressed={texturedArchive.StoredBytes / 1024}KiB managed-allocated={allocated / 1024}KiB managed-retained-delta={retained / 1024}KiB process-private-delta={processDelta / 1024}KiB");
    }

    static void QuotaAndInvalidStep(string directory)
    {
        var image = Picture(); var history = new AnnotationHistory(); history.Checkpoint(new List<Annotation> { Stroke() });
        var state = new PinArchiveState(new PinSnapshot(image, image, new List<Annotation> { Stroke() }, -6000, -6000, 1, 1), history);
        var good = PinArchive.Create(state, Path.Combine(directory, "good.markpin"));
        var badPath = Path.Combine(directory, "bad-tag.markpin"); File.Copy(good.FilePath, badPath);
        using (var zip = ZipFile.Open(badPath, ZipArchiveMode.Update))
        {
            zip.GetEntry("undo/0").Delete();
            using var output = zip.CreateEntry("undo/0").Open(); using var w = new BinaryWriter(output); w.Write(1); w.Write((byte)255);
        }
        var bad = new PinArchive(badPath).Restore(); var live = bad.Snapshot.Annotations;
        bool rejected = false; try { bad.History.Undo(live); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && bad.History.Depth == 1 && !bad.History.CanRedo && live.Count == 1, "unknown annotation tags reject transactionally without clearing scene/stacks");

        var session = (string)typeof(PinArchive).GetProperty("SessionDirectory", Any).GetValue(null);
        var reservation = Path.Combine(session, "quota-reservation");
        // SetLength reserves logical length, not a 1 GB managed array. The file belongs only to
        // this process's random session directory and is removed immediately after the check.
        var namesBefore = Directory.GetFiles(session).Select(Path.GetFileName).ToHashSet();
        using (var file = File.Create(reservation)) file.SetLength(PinArchive.MaxSessionBytes);
        try
        {
            bool limited = false; try { PinArchive.CreateSession(state); } catch (IOException) { limited = true; }
            Check(limited && Directory.GetFiles(session).Where(p => p != reservation).All(p => namesBefore.Contains(Path.GetFileName(p))), "1 GB session bound rejects excess and immediately removes failed archive/staging files");
            var restored = good.Restore(); restored.History.Checkpoint(new List<Annotation> { new ImageAnnotation { Image = image, Rect = new Rect(0, 0, 320, 160) } });
            var scene = new List<Annotation>();
            Check(restored.History.Undo(scene) && scene[0] is ImageAnnotation, "disk-full active checkpoint safely falls back to bounded shared-image history");
        }
        finally { File.Delete(reservation); }
    }
}
