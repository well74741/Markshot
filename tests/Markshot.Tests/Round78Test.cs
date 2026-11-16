using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.78: content and clipboard-sequence dedup; desktop icons in the automatic hover chain.</summary>
static class Round78Test
{
    static int _fail;
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");
    static BitmapSource Picture(byte red = 60, byte alpha = 255, int width = 24, int height = 16)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20; pixels[i + 1] = 30; pixels[i + 2] = red; pixels[i + 3] = alpha;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var imageSink = ClipboardService.CopyOverride;
        var textSink = ClipboardService.CopyTextOverride;
        var tableSink = ClipboardService.CopyTablesOverride;
        var sequence = ClipboardService.SequenceOverride;
        var asFile = Markshot.App.Config.CopyImageAsFile;
        uint seq = 10;
        int writes = 0, notified = 0;
        bool succeed = true;
        Action<BitmapSource> note = _ => notified++;
        try
        {
            ClipboardService.ResetCopyCache();
            ClipboardService.SequenceOverride = () => seq;
            ClipboardService.CopyOverride = _ => { if (!succeed) return false; writes++; seq++; return true; };
            ClipboardService.CopyTextOverride = _ => { if (!succeed) return false; writes++; seq++; return true; };
            ClipboardService.CopyTablesOverride = (_, _) => { if (!succeed) return false; writes++; seq++; return true; };
            ClipboardService.ImageCopied += note;
            Markshot.App.Config.CopyImageAsFile = false;
            Check(ClipboardService.CopyImage(Picture()) && writes == 1, "first image writes once (only a test sink, not the user's clipboard)");
            Check(ClipboardService.CopyImage(Picture()) && ClipboardService.CopyImage(Picture()) && writes == 1 && notified == 1,
                "same pixels from fresh image objects: no repeated write or clipboard-image event");
            seq++; // somebody else copied something, even if their payload happened to be identical
            Check(ClipboardService.CopyImage(Picture()) && writes == 2, "another program changes the clipboard: copying our same image again works");
            Check(ClipboardService.CopyImage(Picture(61)) && writes == 3, "a changed pixel must write");
            Check(ClipboardService.CopyImage(Picture(61, 120)) && writes == 4, "a changed alpha channel must write");
            Check(ClipboardService.CopyImage(Picture(61, 120, 16, 24)) && writes == 5, "a changed shape must write");
            Markshot.App.Config.CopyImageAsFile = true;
            Check(ClipboardService.CopyImage(Picture(61, 120, 16, 24)) && writes == 6, "adding the image-file clipboard format is a different copy");
            Check(ClipboardService.CopyText("原文 text") && writes == 7 && ClipboardService.CopyText("原文 text") && writes == 7,
                "same text: no repeated write");
            Check(ClipboardService.CopyText("原文 text\n") && writes == 8, "text changes (including whitespace): write");
            seq++;
            Check(ClipboardService.CopyText("原文 text\n") && writes == 9, "text copied again after a clipboard change: write");
            Check(ClipboardService.CopyTables("A\tB", "<table><tr><td>A</td><td>B</td></tr></table>") && writes == 10,
                "table writes Unicode text and HTML");
            Check(ClipboardService.CopyTables("A\tB", "<table><tr><td>A</td><td>B</td></tr></table>") && writes == 10,
                "same table: no repeated write");
            Check(ClipboardService.CopyText("A\tB") && writes == 11, "same characters as a table but text-only format: write");
            succeed = false;
            Check(!ClipboardService.CopyText("retry"), "a failed write is reported as failed");
            succeed = true;
            Check(ClipboardService.CopyText("retry") && writes == 12, "failure never makes a subsequent copy look already written");
            seq = 0;
            ClipboardService.CopyText("unknown"); seq = 0;
            ClipboardService.CopyText("unknown");
            Check(writes == 14, "unknown sequence cannot suppress a copy");
            seq = 100;
            var box = new System.Windows.Controls.TextBox { Text = "selected text" };
            ClipboardService.BindTextCopy(box);
            box.SelectAll();
            var before = writes;
            System.Windows.Input.ApplicationCommands.Copy.Execute(null, box);
            System.Windows.Input.ApplicationCommands.Copy.Execute(null, box);
            Check(writes == before + 1, "Ctrl+C / context Copy of a selected text box uses the same dedup rule");
        }
        finally
        {
            ClipboardService.ImageCopied -= note;
            ClipboardService.CopyOverride = imageSink;
            ClipboardService.CopyTextOverride = textSink;
            ClipboardService.CopyTablesOverride = tableSink;
            ClipboardService.SequenceOverride = sequence;
            ClipboardService.ResetCopyCache();
            Markshot.App.Config.CopyImageAsFile = asFile;
        }

        // Desktop route and hover chain: synthetic bounds, no clicking/reading the user's desktop.
        {
            var overlay = new OverlayWindow(Picture(width: 640, height: 480), new Int32Rect(0, 0, 640, 480));
            var target = new WindowTarget { Handle = new IntPtr(12345), Desktop = true, Label = "桌面", Bounds = new Rect(0, 0, 640, 480) };
            typeof(OverlayWindow).GetField("_hoverTarget", Any)!.SetValue(overlay, target);
            var config = Markshot.App.Config;
            var previousRange = config.DetectRange;
            var previousWindows = config.DetectWindows;
            try
            {
                config.DetectRange = 2;
                config.DetectWindows = false; // desktop items are elements, not software windows
                var chain = (IList)typeof(OverlayWindow).GetMethod("BuildHoverChain", Any)!.Invoke(overlay, new object[]
                {
                    new List<Int32Rect>(), new List<DetectedElement> { new(new Rect(24, 24, 80, 95), "快捷方式 · 测试") }
                })!;
                Check(chain.Count == 2, "desktop icon plus desktop/screen, even if software-window detection is off");
                var icon = chain[0];
                var screen = chain[1];
                Check((Rect)icon.GetType().GetProperty("Local", Any)!.GetValue(icon)! == new Rect(24, 24, 80, 95) &&
                      (bool)screen.GetType().GetProperty("Screen", Any)!.GetValue(screen)!, "whole icon+caption framed; desktop is a screen range, not an element");
                var start = (int)typeof(OverlayWindow).GetMethod("StartLevel", Any)!.Invoke(overlay, new object[] { chain })!;
                Check(start == 0, "desktop icon offered first rather than discarded as too small for a window-range setting");
            }
            finally
            {
                config.DetectRange = previousRange;
                config.DetectWindows = previousWindows;
                overlay.Close();
            }
            Check(Source("Core/WindowDetector.cs").Contains("DesktopList(handle, screenX, screenY)") &&
                  Source("Core/ElementDetector.cs").Contains("ControlType.ListItem"), "native desktop icon list → UI Automation ListItems (not guessing wallpaper patterns)");
        }

        // Independent pin suites use their own no-clipboard sinks and temporary history folder.
        Round78PinTest.Run();
        Round78ArchiveTest.Run();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
