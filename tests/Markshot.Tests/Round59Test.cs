using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.59: Alt / Shift drawing, selection by content, long screenshot states, the editor's panel, menus.</summary>
static class Round59Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    static byte[] Stripes(int w, int h, int scroll)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Rows that differ from each other (a scrolling page), not repeating.
                var row = y + scroll;
                var v = (byte)((row * 37 + (row / 7) * 91 + x / 16 * ((row / 11) % 5)) % 251);
                var i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = (byte)(v ^ 0x55); px[i + 2] = (byte)(row % 253); px[i + 3] = 255;
            }
        }

        return px;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the capture area: Alt from the middle, Shift square (Photoshop)
        {
            Rect Area(bool shift, bool alt) => (Rect)typeof(OverlayWindow).GetMethod("CreatingRect", Any, new[] { typeof(Point), typeof(Point), typeof(bool), typeof(bool) })!
                .Invoke(null, new object[] { new Point(100, 100), new Point(130, 150), shift, alt })!;
            Check(Area(false, false) == new Rect(100, 100, 30, 50), "a plain drag: corner to corner");
            Check(Area(false, true) == new Rect(70, 50, 60, 100), "Alt: the press is the middle");
            Check(Area(true, false) == new Rect(100, 100, 50, 50), "Shift: a square");
            Check(Area(true, true) == new Rect(50, 50, 100, 100), "Alt + Shift: a square from the middle");
        }

        // ---- shapes drawn the same way
        {
            var rect = new RectAnnotation();
            var gesture = new CreationGesture(rect, new Point(100, 100));
            gesture.Move(new Point(130, 150), false, false, alt: true);
            Check(rect.Rect == new Rect(70, 50, 60, 100), $"a shape with Alt: from the middle ({rect.Rect})");
            gesture.Move(new Point(130, 150), true, false, alt: true);
            Check(rect.Rect == new Rect(50, 50, 100, 100), $"Alt + Shift: a square from the middle ({rect.Rect})");
            gesture.Move(new Point(130, 150), false, false, alt: false);
            Check(rect.Rect == new Rect(100, 100, 30, 50), "released: corner to corner again");
            var line = new LineAnnotation();
            var lineGesture = new CreationGesture(line, new Point(100, 100));
            lineGesture.Move(new Point(130, 100), false, false, alt: true);
            Check(line.Start == new Point(70, 100) && line.End == new Point(130, 100), "a line with Alt grows both ways");
            foreach (var file in new[] { "UI/OverlayWindow.Input.cs", "UI/PinWindow.Editing.cs", "UI/PinWindow.xaml.cs" })
            {
                Check(!System.Text.RegularExpressions.Regex.IsMatch(Source(file), @"_gesture\.Move\((?![^;]*AltHeld)"), $"{file}: every gesture move passes Alt");
            }
        }

        // ---- selection by what is really at the point
        {
            var outline = new RectAnnotation { Rect = new Rect(20, 20, 100, 100), Thickness = 3, Color = Colors.Red };
            var filled = new RectAnnotation { Rect = new Rect(200, 20, 100, 100), Thickness = 3, Fill = true };
            var ring = new EllipseAnnotation { Rect = new Rect(20, 200, 100, 100), Thickness = 3 };
            var px = new byte[100 * 100 * 4];
            for (var y = 0; y < 100; y++) for (var x = 50; x < 100; x++) px[(y * 100 + x) * 4 + 3] = 255; // the right half shows
            var picture = new ImageAnnotation { Rect = new Rect(0, 0, 400, 400), Image = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Bgra32, null, px, 400) };
            var items = new List<Annotation> { picture, outline, filled, ring };
            Annotation? At(double x, double y, bool content) => AnnotationOps_HitTest(items, new Point(x, y), content);
            Check(ReferenceEquals(At(70, 21, true), outline), "on the outline: the shape");
            Check(ReferenceEquals(At(70, 70, false), outline), "with a tool in hand the whole box still counts");
            Check(At(70, 70, true) == null, "inside an empty outline over a see-through part: nothing");
            Check(ReferenceEquals(At(250, 300, true), picture), "on the visible part of the picture: the picture");
            Check(ReferenceEquals(At(250, 70, true), filled), "a filled shape: its inside counts");
            Check(ReferenceEquals(At(21, 250, true), ring) && At(70, 250, true) == null, "a ring: only its line");
            outline.Rect = new Rect(250, 250, 60, 60);
            Check(ReferenceEquals(At(280, 280, true), picture), "through an empty outline: the picture under it is picked");
        }

        // ---- long screenshot: the state the user sees
        {
            const int w = 200, h = 240;
            var stitcher = new ScrollStitcher(w, h);
            stitcher.Add(Stripes(w, h, 0));
            Check(stitcher.LastOutcome == ScrollStitcher.Outcome.First, "first frame");
            stitcher.Add(Stripes(w, h, 0));
            Check(stitcher.LastOutcome == ScrollStitcher.Outcome.Same, "not moved: waiting");
            stitcher.Add(Stripes(w, h, 40));
            Check(stitcher.LastOutcome == ScrollStitcher.Outcome.Placed && stitcher.PlacedFrames == 1, $"moved with overlap: placed ({stitcher.LastOutcome})");
            stitcher.Add(Stripes(w, h, 5000));
            Check(stitcher.LastOutcome == ScrollStitcher.Outcome.TooFar && stitcher.LostFrames == 1, $"no overlap at all: scrolled too far ({stitcher.LastOutcome})");
            var part = Stripes(w, h, 40);
            for (var i = 0; i < 4 * w * 10; i++) part[i] = (byte)(part[i] ^ 0xFF); // only the top rows change
            stitcher.Add(part);
            Check(stitcher.LastOutcome == ScrollStitcher.Outcome.Same, $"a small change with the page standing still is no error ({stitcher.LastOutcome})");

            var frames = new Queue<byte[]>(new[] { Stripes(200, 200, 0), Stripes(200, 200, 30), Stripes(200, 200, 9000), Stripes(200, 200, 9000) });
            var last = Stripes(200, 200, 0);
            var session = new ScrollCaptureSession(new Int32Rect(-3000, -3000, 200, 200), false, null) { Source = () => frames.Count > 0 ? last = frames.Dequeue() : last };
            session.Start();
            string State() => (string)typeof(ScrollCaptureSession).GetProperty("StateText", Any)!.GetValue(session)!;
            Check(State().Contains("等待滚动"), $"the window says it waits ({State()})");
            typeof(ScrollCaptureSession).GetMethod("Grab", Any)!.Invoke(session, null);
            Check(State().Contains("拼接"), $"… that it stitches ({State()})");
            typeof(ScrollCaptureSession).GetMethod("Grab", Any)!.Invoke(session, null);
            Check(State().Contains("滚动太快"), $"… that it scrolled too fast ({State()})");
            typeof(ScrollCaptureSession).GetMethod("Grab", Any)!.Invoke(session, null);
            Check(State().Contains("滚动太快"), "stopped after losing its place: the message stays");
            session.Finish(ScrollCaptureAction.Cancel);
        }

        // ---- the editor's panel
        {
            var config = Markshot.App.Config;
            config.EditorFoldedSections = "调色";
            var px = Enumerable.Repeat((byte)200, 40 * 40 * 4).ToArray();
            var editor = new ImageEditorWindow(BitmapSource.Create(40, 40, 96, 96, PixelFormats.Bgra32, null, px, 160)) { Left = -4000, Top = -4000, ShowActivated = false };
            editor.Show();
            Pump(300);
            var stack = (Panel)typeof(ImageEditorWindow).GetField("_panelStack", Any)!.GetValue(editor)!;
            var titles = stack.Children.OfType<Border>().Select(card => ((DockPanel)((StackPanel)card.Child).Children[0]).Children.OfType<TextBlock>().First().Text).ToList();
            Check(titles.SequenceEqual(new[] { "画布与尺寸", "裁切与变形", "旋转与镜像", "元素样式", "调色", "抠色与替换颜色" }), "the sections: " + string.Join(" / ", titles));
            var cards = stack.Children.OfType<Border>().ToList();
            var style = cards[titles.IndexOf("元素样式")];
            Check(style.Visibility == Visibility.Visible, "元素样式 is always there");
            var colour = cards[titles.IndexOf("调色")];
            Check(((UIElement)colour.Tag).Visibility == Visibility.Collapsed, "a section folded last time opens folded");
            var fold = ((DockPanel)((StackPanel)cards[0].Child).Children[0]).Children.OfType<Button>().First(b => b.ToolTip as string == "收起 / 展开");
            fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((UIElement)cards[0].Tag).Visibility == Visibility.Collapsed && config.EditorFoldedSections.Contains("画布与尺寸"), "the fold button folds and remembers");
            editor.CloseWithoutAsking();
            config.EditorFoldedSections = "";
        }

        // ---- the tools' tooltips, menus, the watermark row, the cursor, the rest
        {
            var bar = new AnnotationToolbar();
            bar.GetType().GetMethod("SetHideChevrons", Any);
            var src = Source("UI/AnnotationToolbar.xaml.cs");
            Check(src.Contains("tool.ToolTip = hidden ? null : _listToolTips.GetValueOrDefault(tool);"), "buttons with a list show no tooltip over it");
            var i = src.IndexOf("case ToolFamily.Watermark:", StringComparison.Ordinal);
            Check(!src[i..src.IndexOf("case ToolFamily.Mosaic:", i, StringComparison.Ordinal)].Contains("OpacityGroup.Visibility"), "the watermark has no opacity slider");
            Check(src.Contains("(WatermarkActive ? _watermarkOpacity : _opacity)"), "its opacity shows beside the colour");
            Check(Source("UI/OverlayWindow.Input.cs").Contains(": _selection.Contains(point) ? Cursors.SizeAll : Cursors.Arrow"), "outside the area: the plain arrow");
            var button = new FloatButtonWindow(new AppConfig(), _ => { }, () => { }, () => { }) { FullscreenOverride = () => false };
            var headers = (IReadOnlyList<string>)button.GetType().GetMethod("MenuHeaders", Any)!.Invoke(button, null)!;
            Check(headers.Contains("禁用所有快捷键"), "the floating button's menu has 禁用所有快捷键");
            button.Close();
            Check(Source("Core/TrayService.cs").Contains("Add(\"添加贴图…\", addPin);"), "the tray menu has 添加贴图");
            var edited = 0;
            var entry = new PinEntry { Image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16) };
            var history = new PinHistoryWindow(() => new[] { entry }, _ => { }, _ => { }, () => { }, _ => edited++) { Left = -4000, Top = -4000, ShowActivated = false };
            history.Show();
            Pump(150);
            var tiles = (Panel)history.GetType().GetField("_tiles", Any)!.GetValue(history)!;
            var item = ((Border)tiles.Children[0]).ContextMenu!.Items.OfType<MenuItem>().First(m => (string)m.Header == "编辑图片");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(edited == 1, "the pin history's 编辑图片");
            history.Close();
            Check(Source("UI/PinWindow.xaml.cs").Contains("Motion.PopOut(Toolbar, duration: Motion.Bar); // the bar (its own window) goes along"), "closing a pin: its bar goes with a transition");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static Annotation? AnnotationOps_HitTest(List<Annotation> items, Point point, bool content) =>
        (Annotation?)typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("HitTest")!.Invoke(null, new object[] { items, point, 3.0, content });
}
