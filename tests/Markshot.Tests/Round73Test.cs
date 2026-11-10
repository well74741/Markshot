using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.73: the hover plate, the floating button cloaked for a capture, the recorder frame (space / right click), editor F glide, memory.</summary>
static class Round73Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    static BitmapSource Picture(int w, int h)
    {
        var px = new byte[w * h * 4];
        new Random(3).NextBytes(px);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the hover plate: fixed width, the lists in place, seen through it
        {
            var config = Markshot.App.Config;
            var before = config.HideToolChevrons;
            config.HideToolChevrons = true;
            var bar = new AnnotationToolbar();
            var window = new Window { Content = bar, Left = -4000, Top = -4000, Width = 900, Height = 200, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            bar.SetMode(AnnotationToolbarMode.Capture);
            bar.ApplyToolOrderFromConfig();
            Pump(150);
            var open = typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", Any)!;
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ToolPen") });
            var card = (FrameworkElement)typeof(AnnotationToolbar).GetField("_hoverCard", Any)!.GetValue(bar)!;
            var lists = (System.Windows.Controls.Canvas)typeof(AnnotationToolbar).GetField("_hoverLists", Any)!.GetValue(bar)!;
            var width = card.Width;
            var count = lists.Children.Count;
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ScrollButton") });
            Pump(300);
            Check(Math.Abs(card.Width - width) < 0.01 && lists.Children.Count == count && count >= 7,
                $"one plate of one width; every tool's list laid out once ({count} lists), not made per hover");
            Check(lists.Clip is RectangleGeometry { Transform: TranslateTransform t } && ReferenceEquals(t, card.RenderTransform),
                "the words are seen only through the plate (the clip moves with it)");
            window.Close();
            config.HideToolChevrons = before;
        }

        // ---- the floating button: cloaked for a capture, not hidden (no old frame shown again)
        {
            var config = new AppConfig { FloatActionHidden = "" };
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false, PointerOnItOverride = () => false };
            button.Show();
            Pump(250);
            button.Suspend();
            Check(button.IsVisible && (bool)button.GetType().GetProperty("Cloaked", Any)!.GetValue(button)!, "a capture: still shown, taken out of the screen's picture (cloaked)");
            button.Resume();
            Pump(300);
            Check(button.IsVisible && !(bool)button.GetType().GetProperty("Cloaked", Any)!.GetValue(button)! && button.GetType().GetProperty("Shown", Any)!.GetValue(button) is double shown && shown > 0.9,
                "after it: back, faded in where it was");
            button.Close();
        }

        // ---- the recorder frame: space moves it while resizing, right click undoes the drag
        {
            var frame = Source("UI/Recording/RecordFrameWindow.cs");
            Check(frame.Contains("if (SpaceHeld && _drag != \"move\")") && frame.Contains("public void CancelDrag()") && frame.Contains("_window.CancelDrag(); // as everywhere in Markshot"),
                "录屏框: 空格 moves it while resizing; a right click puts it back");
        }

        // ---- the image editor: F glides back
        {
            var editor = new ImageEditorWindow(Picture(400, 300)) { Left = 40, Top = 40, Width = 900, Height = 650, ShowActivated = false };
            editor.Show();
            Pump(400);
            editor.PanBy(new Vector(220, 140));
            Pump(50);
            var before = editor.PanOffset;
            editor.FitAnimated();
            Pump(40);
            var midway = editor.PanOffset;
            Pump(400);
            var after = editor.PanOffset;
            Check((midway - before).Length > 1 && (after - midway).Length > 1, $"F: a glide back to the middle (not one frame): {before} → {midway} → {after}");
            editor.CloseWithoutAsking();
        }

        // ---- memory: given back when idle
        {
            var self = System.Diagnostics.Process.GetCurrentProcess();
            for (var i = 0; i < 3; i++)
            {
                var overlay = new OverlayWindow(Picture(1600, 900), new Int32Rect(-6000, -6000, 1600, 900));
                overlay.Show();
                Pump(120);
                overlay.Close();
            }

            Pump(100);
            self.Refresh();
            var before = self.WorkingSet64;
            Markshot.App.TrimNow();
            Pump(100);
            self.Refresh();
            var after = self.WorkingSet64;
            Console.WriteLine($"  working set {before / 1048576} MB → {after / 1048576} MB");
            Check(after < before, "a trim gives memory back (collection with compaction, the working set returned)");
            var app = Source("App.xaml.cs");
            Check(app.Contains("SetProcessWorkingSetSize(") && app.Contains("120L * 1024 * 1024") && Source("UI/OverlayWindow.xaml.cs").Contains("_segmenter = null;") &&
                  Source("Markshot.csproj").Contains("System.GC.ConserveMemory"), "closed windows let go of their pictures at once; a closed pin / editor / text window trims too");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
