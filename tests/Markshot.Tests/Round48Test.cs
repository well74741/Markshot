using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.48: number badges, fonts, marquee, pins, Win + mouse, QR codes, the hover list, the settings.</summary>
static class Round48Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }

    static RenderTargetBitmap Draw(Annotation a, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            AnnotationEditor.DrawItem(dc, a);
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    static Color At(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark); // the themed colours ({DynamicResource T_…}) as in the app
        var config = Markshot.App.Config;

        // ---- 序号: styles, bigger figures, the arrow dragged out of it
        var solid = new NumberAnnotation { Center = new Point(40, 40), Radius = 20, Number = 3, Color = Color.FromRgb(255, 30, 30), Outlined = false };
        var hollow = new NumberAnnotation { Center = new Point(40, 40), Radius = 20, Number = 3, Color = Color.FromRgb(255, 30, 30), Hollow = true, Outlined = false };
        var s = Draw(solid, 80, 80);
        var h = Draw(hollow, 80, 80);
        Check(At(s, 28, 52).R > 200 && At(s, 28, 52).G < 80, "序号 solid: a filled disc");
        Check(At(h, 27, 40).R < 60 && At(h, 40, 21).R > 200, "序号 空心: a ring, its inside open");
        var outlined = Draw(new NumberAnnotation { Center = new Point(40, 40), Radius = 20, Number = 3, Color = Color.FromRgb(255, 30, 30), Outlined = true }, 80, 80);
        Check(At(outlined, 40, 20).G > 200 && At(s, 40, 20).G < 120, "序号 描边: a light outline around it (none without)");
        var gesture = new CreationGesture(new NumberAnnotation { Center = new Point(40, 40), Radius = 14, Number = 1 }, new Point(40, 40));
        gesture.Move(new Point(140, 90), false, false);
        gesture.Apply();
        var placed = (NumberAnnotation)gesture.Target;
        Check(placed.Center == new Point(40, 40) && placed.Tail == new Point(140, 90) && placed.HitTest(new Point(90, 65), 1),
            "序号 dragged before release: the badge stays, an arrow points to the pointer");
        gesture.Move(new Point(44, 42), false, false);
        gesture.Apply();
        Check(placed.Tail == null, "… back over the badge: no arrow");
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 60, 60)), null, new Rect(0, 0, 420, 90));
                var x = 35;
                foreach (var (hollowStyle, outline, n) in new[] { (false, true, 1), (false, false, 2), (true, true, 3), (true, false, 12) })
                {
                    new NumberAnnotation { Center = new Point(x, 35), Radius = 22, Number = n, Color = Color.FromRgb(255, 30, 30), Hollow = hollowStyle, Outlined = outline }.Draw(dc);
                    x += 70;
                }

                new NumberAnnotation { Center = new Point(320, 30), Radius = 16, Number = 5, Color = Color.FromRgb(255, 30, 30), Tail = new Point(400, 75) }.Draw(dc);
            }

            var shot = new RenderTargetBitmap(420, 90, 96, 96, PixelFormats.Pbgra32);
            shot.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(shot));
            using var file = System.IO.File.Create("number_styles.png");
            enc.Save(file);
        }

        var cursor = ToolCursors.Get(AnnotationTool.Number, 14, Colors.Red, 1, 5);
        var next = ToolCursors.Get(AnnotationTool.Number, 14, Colors.Red, 1, 6);
        Check(cursor != Cursors.Cross && next != cursor, "序号 cursor: the next badge itself, a new one for the next number");

        // ---- 字体: the computer's fonts
        var fonts = FontLibrary.All();
        Check(fonts.Count > 5 && fonts.Any(f => f.Name == "Microsoft YaHei UI" || f.Name == "Microsoft YaHei"), $"字体: the installed fonts are listed ({fonts.Count})");
        var text = new TextAnnotation { Text = "Abc 字体", FontSize = 30, FontName = "Times New Roman" };
        var plain = new TextAnnotation { Text = "Abc 字体", FontSize = 30 };
        Check(Math.Abs(text.MeasureText().Width - plain.MeasureText().Width) > 1, "a text in another font measures differently (the font is used)");
        var bar = new AnnotationToolbar();
        var barWin = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        barWin.Show();
        bar.SetTool(AnnotationTool.Text, notify: false);
        Pump(150);
        Check(((FrameworkElement)bar.FindName("FontButton")).IsVisible, "the text tool has a 字体 button");
        bar.SetTool(AnnotationTool.Number, notify: false);
        Pump(100);
        Check(((FrameworkElement)bar.FindName("NumberStyleGroup")).IsVisible, "the number tool has 空心 / 描边");
        var fill = (ToggleButton)bar.FindName("NumberHollowToggle");
        Check(fill.Style == (Style)Application.Current.FindResource("PlateToggle") && fill.Background is SolidColorBrush { Color.A: > 0 },
            "switch buttons (空心, 填充, 背景…) have a faint plate");

        // ---- the hover list closes when its tool is clicked
        config.HideToolChevrons = true;
        bar.ApplyToolOrderFromConfig();
        var pen = (ButtonBase)bar.FindName("ToolPen");
        typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bar, new object[] { pen });
        Check(bar.IsHoverMenuOpen, "hover list open");
        pen.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Check(!bar.IsHoverMenuOpen, "clicking the tool closes its list (the settings bar opens there)");
        barWin.Close();

        // ---- 滚动播放
        var block = new TextBlock { Text = string.Concat(Enumerable.Repeat("很长的提示文字 ", 20)), Width = 120, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Marquee.SetEnabled(block, true);
        var host = new Window { Content = block, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        host.Show();
        Pump(200);
        Check(Marquee.Overflow(block) > 100 && block.TextTrimming == TextTrimming.None, $"a hint that does not fit scrolls ({Marquee.Overflow(block):0} DIP to travel)");
        block.Text = "短";
        Pump(200);
        Check(Marquee.Overflow(block) == 0, "… one that fits stays still");
        host.Close();

        // ---- pins: Delete leaves no history; the shadow is a ring when see-through
        var picture = BitmapSource.Create(60, 40, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 60 * 40 * 4).ToArray(), 240);
        var pin = new PinWindow(picture, -4000, -4000) { Left = -4000, Top = -4000, ShowActivated = false };
        pin.Show();
        Pump(150);
        pin.RestoreView(1, 0.5);
        var shadow = (Border)pin.FindName("ImageShadowLayer");
        var shadowHost = (FrameworkElement)pin.FindName("ShadowHost");
        Check(shadowHost.Clip is CombinedGeometry clip && !clip.FillContains(new Point(30, 20)) && clip.FillContains(new Point(-5, 20)) &&
              shadow.Margin == new Thickness(0) && shadow.BorderThickness == new Thickness(0),
            "a see-through pin: no shadow colour under the picture, the shadow outside as wide as before");
        pin.RestoreView(1, 1);
        Check(shadowHost.Clip == null, "… opaque again: the plain shadow");
        typeof(PinWindow).GetMethod("DeleteWithGroup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pin, null);
        Pump(300);
        Check(pin.SkipHistory, "Delete: the pin goes and is not kept in the history");
        Check(new AppConfig().CloseAllPinsHotkey == "Shift+Esc" && HotkeyGesture.TryParse("Shift+Esc", out _, out _), "关闭所有贴图: Shift+Esc by default");

        // ---- Win + mouse: draws an area, the action per button
        var done = new List<(MouseButton, Int32Rect)>();
        var win = new WinMouseSelect((b, r) => done.Add((b, r)));
        var winDown = typeof(WinMouseSelect).GetField("WinDown", BindingFlags.NonPublic | BindingFlags.Static)!;
        var realWin = winDown.GetValue(null);
        winDown.SetValue(null, new Func<bool>(() => true));
        bool Send(int message, int x, int y) => (bool)typeof(WinMouseSelect).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(win, new object[] { message, new Point(x, y) })!;
        var swallowedDown = Send(0x0207, -3900, -3900); // middle down
        Send(0x0200, -3700, -3800);
        var swallowedUp = Send(0x0208, -3700, -3800);
        Pump(150);
        Pump(500);
        winDown.SetValue(null, realWin);
        Check(swallowedDown && swallowedUp && done.Count == 1 && done[0].Item1 == MouseButton.Middle && done[0].Item2 == new Int32Rect(-3900, -3900, 200, 100),
            $"Win + 中键: the press is the program's no more, the area is drawn ({(done.Count > 0 ? done[0].Item2.ToString() : "-")})");

        var defaults = new AppConfig();
        Check(defaults.WinMouseLeft == "Ocr" && defaults.WinMouseMiddle == "Pin" && defaults.WinMouseRight == "Copy" && defaults.WinMouseEnabled && !defaults.HotkeysDisabled,
            "Win + 左 / 中 / 右 = 识别文字 / 贴图 / 复制 by default; hotkeys on");

        // ---- 二维码
        var writer = new ZXing.BarcodeWriterPixelData { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 180, Height = 180, Margin = 2 } };
        var data = writer.Write("https://github.com/well74741/Markshot");
        var qr = BitmapSource.Create(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null, data.Pixels, data.Width * 4);
        var codes = CodeReader.Read(qr);
        Check(codes.Count == 1 && codes[0].IsLink && codes[0].Kind == "二维码", $"二维码: read, the link recognised ({string.Join(",", codes.Select(c => c.Text))})");
        var ocr = new TranslationWindow(null, autoTranslate: false) { Left = -4000, Top = -4000, ShowActivated = false };
        ocr.Show();
        Pump(150);
        ocr.ShowCodes(codes);
        var panel = (StackPanel)ocr.FindName("CodePanel");
        var link = panel.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<TextBlock>()).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).FirstOrDefault();
        Check(panel.IsVisible && link?.NavigateUri?.Host == "github.com", "the 文字识别 window lists it with a clickable link");
        ocr.Close();

        // ---- the settings: 关闭所有贴图, 禁用所有快捷键, Win + mouse rows
        var settings = new SettingsWindow(new AppConfig { WinMouseLeft = "Copy", WinMouseRight = "Copy" }) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        Pump(200);
        var rows = (StackPanel)settings.FindName("WinMouseRows");
        var picked = rows.Children.OfType<StackPanel>().Select(r => r.Children.OfType<Border>().First().Child).Cast<StackPanel>()
            .Select(choices => choices.Children.OfType<RadioButton>().First(b => b.IsChecked == true).Tag).ToList();
        Check(picked.SequenceEqual(new object[] { "Copy", "Pin", "Copy" }), $"Win + mouse rows: any action per button, the same one twice too ({string.Join(",", picked)})");
        Check(settings.FindName("HotkeysDisabledCheck") is CheckBox && settings.FindName("CloseAllPinsHotkeyBox") is TextBox { Text: "Shift+Esc" }, "settings: 禁用所有快捷键, 关闭所有贴图");
        settings.Close();

        // ==== 0.8.49
        // the shadow of a see-through pin is lighter alike
        {
            var shadowPin = new PinWindow(picture, -4000, -4000) { Left = -4000, Top = -4000, ShowActivated = false };
            shadowPin.Show();
            Pump(150);
            var layer = (Border)shadowPin.FindName("ImageShadowLayer");
            byte Alpha() => ((SolidColorBrush)layer.Background).Color.A;
            var full = Alpha();
            shadowPin.RestoreView(1, 0.4);
            var faint = Alpha();
            Check(faint < full * 0.6 && faint > 0, $"a pin at 40 %: its shadow at about 40 % too ({full} -> {faint})");
            shadowPin.CloseImmediately();
        }

        // the colour panel: a click elsewhere closes it (as 取消)
        {
            var picker = new ColorPickerWindow(Colors.Red) { Left = -4000, Top = -4000 };
            picker.Show();
            picker.Activate();
            Pump(200);
            var other = new Window { Left = -3600, Top = -4000, Width = 50, Height = 50, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            other.Show();
            other.Activate(); // a pin taking the keyboard back: no click
            Pump(300);
            Check(picker.IsVisible, "colour panel: another window taking the focus (the pin) does not close it");
            var click = typeof(ColorPickerWindow).GetMethod("SimulateClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var inside = picker.PointToScreen(new Point(20, 20));
            click.Invoke(picker, new object[] { inside });
            Check(picker.IsVisible, "… a click on it keeps it");
            click.Invoke(picker, new object[] { new Point(inside.X + 2000, inside.Y + 2000) });
            Pump(450); // its closing fade
            Check(!picker.IsVisible && !picker.Accepted, "… a click outside closes it, nothing chosen");
            other.Close();
        }

        // 二维码 in the capture area: found where they are; the pointer over one shows its box and card
        {
            var qrA = writer.Write("https://example.com/a");
            var qrB = writer.Write("第二个二维码");
            var screenPx = new byte[640 * 300 * 4];
            for (var i = 0; i < screenPx.Length; i++) screenPx[i] = 255;
            void Put(ZXing.Rendering.PixelData code, int ox, int oy)
            {
                for (var y = 0; y < code.Height; y++)
                    Array.Copy(code.Pixels, y * code.Width * 4, screenPx, ((oy + y) * 640 + ox) * 4, code.Width * 4);
            }

            Put(qrA, 40, 60);
            Put(qrB, 380, 60);
            var screenShot = BitmapSource.Create(640, 300, 96, 96, PixelFormats.Bgra32, null, screenPx, 640 * 4);
            screenShot.Freeze();
            var overlay = new OverlayWindow(screenShot, new Int32Rect(-4000, -4000, 640, 300));
            typeof(OverlayWindow).GetField("_hasSelection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(overlay, true);
            typeof(OverlayWindow).GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(overlay, new Rect(10, 10, 620, 280));
            overlay.Show();
            Pump(150);
            typeof(OverlayWindow).GetMethod("ReadCodesNow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(overlay, null);
            var found = (IReadOnlyList<CodeReader.Code>)typeof(OverlayWindow).GetProperty("CodesInArea", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
            for (var i = 0; i < 60 && found.Count < 2; i++)
            {
                Pump(50);
                found = (IReadOnlyList<CodeReader.Code>)typeof(OverlayWindow).GetProperty("CodesInArea", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
            }

            var a = found.FirstOrDefault(c => c.Text.EndsWith("/a"));
            Check(found.Count == 2 && a?.Area is { } boxA && boxA.Contains(new Point(130, 150)) && !boxA.Contains(new Point(470, 150)) &&
                  boxA.Left > 20 && boxA.Right < 250, $"two codes in the area, each where it is ({string.Join(" | ", found.Select(c => c.Text + " " + c.Area))})");
            var hover = typeof(OverlayWindow).GetMethod("UpdateCodeHover", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var onCode = (bool)hover.Invoke(overlay, new object[] { new Point(130, 150) })!;
            var card = (Border?)typeof(OverlayWindow).GetField("_codeCard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay);
            var frame = (Border?)typeof(OverlayWindow).GetField("_codeFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay);
            var cardLink = card == null ? null : FindLink(card);
            Check(onCode && frame?.Visibility == Visibility.Visible && frame.Child is Canvas { Children.Count: 1 } && frame.BorderThickness == new Thickness(0) &&
                  card?.Visibility == Visibility.Visible && cardLink?.NavigateUri?.Host == "example.com",
                "pointer on the first code: four corner marks and a card with the clickable link");
            hover.Invoke(overlay, new object[] { new Point(240, 150) }); // off the code, on the way to the card
            Pump(150);
            Check(card?.Visibility == Visibility.Visible, "… leaving the code towards the card: the card waits for the pointer");
            hover.Invoke(overlay, new object[] { new Point(470, 150) });
            Check(card?.Visibility == Visibility.Visible && card is not null && FindLink(card) == null, "on the second code: its own card (plain text, no link)");
            hover.Invoke(overlay, new object[] { new Point(320, 20) });
            Pump(600);
            Check(frame?.Visibility != Visibility.Visible && card?.Visibility != Visibility.Visible, "off the codes (and not on the card): marks and card go after a moment");
            overlay.Close();
        }

        // 二维码 reading: many codes, low contrast (the second reader), barcodes
        {
            var many = new byte[1200 * 800 * 4];
            for (var i = 0; i < many.Length; i++) many[i] = 255;
            for (var k = 0; k < 6; k++)
            {
                var code = writer.Write("https://example.com/code" + k);
                for (var y = 0; y < code.Height; y++)
                    Array.Copy(code.Pixels, y * code.Width * 4, many, ((60 + (k / 3) * 380 + y) * 1200 + 60 + (k % 3) * 380) * 4, code.Width * 4);
            }

            var gray = new byte[1200 * 800];
            for (var i = 0; i < gray.Length; i++) gray[i] = many[i * 4];
            var native = (System.Collections.IList)typeof(CodeReader).GetMethod("ReadNative", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { gray, 1200, 800, 1.0 })!;
            Check(native.Count == 6, $"zxing-cpp (shipped as zxingcpp-native.dll) loads and reads all six itself ({native.Count})");
            var sixWatch = System.Diagnostics.Stopwatch.StartNew();
            var six = CodeReader.Read(BitmapSource.Create(1200, 800, 96, 96, PixelFormats.Bgra32, null, many, 1200 * 4));
            Check(six.Count == 6 && six.All(c => c.Area != null), $"six codes in one area: all six, each with its place ({six.Count}, {sixWatch.ElapsedMilliseconds} ms)");
            var low = writer.Write("https://low.example/a");
            for (var i = 0; i < low.Pixels.Length; i += 4)
            {
                var v = (byte)(low.Pixels[i] < 128 ? 120 : 200);
                low.Pixels[i] = low.Pixels[i + 1] = low.Pixels[i + 2] = v;
            }

            Check(CodeReader.Read(BitmapSource.Create(low.Width, low.Height, 96, 96, PixelFormats.Bgra32, null, low.Pixels, low.Width * 4)).Count == 1, "a low-contrast code is read too");
            var ean = new ZXing.BarcodeWriterPixelData { Format = ZXing.BarcodeFormat.EAN_13, Options = new ZXing.Common.EncodingOptions { Width = 300, Height = 100, Margin = 10 } }.Write("6901234567892");
            var eanCodes = CodeReader.Read(BitmapSource.Create(ean.Width, ean.Height, 96, 96, PixelFormats.Bgra32, null, ean.Pixels, ean.Width * 4));
            Check(eanCodes.Count == 1 && eanCodes[0].Kind == "条形码" && eanCodes[0].Text == "6901234567892", "a barcode (EAN-13)");
        }

        // 序号: dragging out the arrow shows the plain cross, not the badge over it
        {
            var source = System.IO.File.ReadAllText(Repo.File("UI/OverlayWindow.Input.cs")) + System.IO.File.ReadAllText(Repo.File("UI/PinWindow.Editing.cs"));
            Check(System.Text.RegularExpressions.Regex.Matches(source, @"NumberAnnotation \{ Tail: not null \} && InputLayer.Cursor != Cursors.None").Count == 2,
                "序号 drag: no cursor over the arrow (its head is the pointer; capture and pins)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static System.Windows.Documents.Hyperlink? FindLink(DependencyObject root)
    {
        foreach (var block in Descendants(root).OfType<TextBlock>())
        {
            if (block.Inlines.OfType<System.Windows.Documents.Hyperlink>().FirstOrDefault() is { } link)
            {
                return link;
            }
        }

        return null;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
