using System;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>0.8.70: long screenshots scrolled back and forth / in big steps, the settings window (title bar, detected), Win + mouse 圆角, the help page.</summary>
static class Round70Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    /// <summary>A page like text: word blocks on lines, paragraphs, now and then a list of look-alike items.</summary>
    static double[,] Page(int width, int height, int seed, bool list)
    {
        var random = new Random(seed);
        var page = new double[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            page[y, x] = 246;
        var line = 6;
        while (line + 20 < height)
        {
            if (list)
            {
                // A chat / list item: the same avatar, the same separator, a different name and text.
                for (var y = line; y < line + 36 && y < height; y++)
                for (var x = 10; x < 46; x++)
                    page[y, x] = 120;
                for (var row = 0; row < 2; row++)
                {
                    var x = 56;
                    var top = line + 4 + row * 16;
                    while (x < width - 30)
                    {
                        var word = 6 + random.Next(36);
                        var shade = 40 + random.Next(110);
                        for (var y = top; y < top + 10; y++)
                        for (var i = x; i < Math.Min(width - 10, x + word); i++)
                            page[y, i] = shade;
                        x += word + 4 + random.Next(10);
                        if (random.Next(9) == 0) break;
                    }
                }

                for (var x = 0; x < width; x++)
                    if (line + 44 < height) page[line + 44, x] = 225;
                line += 48;
                continue;
            }

            var lines = 2 + random.Next(6);
            for (var l = 0; l < lines && line + 14 < height; l++, line += 18)
            {
                var x = 8 + (l == 0 ? 20 : 0);
                var end = l == lines - 1 ? width / 3 + random.Next(width / 2) : width - 12;
                while (x < end)
                {
                    var word = 6 + random.Next(30);
                    var shade = 30 + random.Next(100);
                    var tall = 9 + random.Next(3);
                    for (var y = line; y < line + tall; y++)
                    for (var i = x; i < Math.Min(end, x + word); i++)
                        page[y, i] = shade;
                    x += word + 5 + random.Next(6);
                }
            }

            line += 14 + random.Next(20);
        }

        return page;
    }

    static double At(double[,] page, double y, int x)
    {
        var low = (int)Math.Floor(y);
        var t = y - low;
        return t == 0 ? page[low, x] : page[low, x] * (1 - t) + page[low + 1, x] * t;
    }

    static byte[] Frame(double[,] page, int width, int height, double offset)
    {
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var b = (byte)Math.Round(At(page, y + offset, x));
            var i = (y * width + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = b;
            px[i + 3] = 255;
        }

        return px;
    }

    /// <summary>Rows of the result that are not the page's row there (a duplicate, a gap, a wrong place).</summary>
    static int WrongRows(BitmapSource result, double[,] page)
    {
        var width = result.PixelWidth;
        var px = new byte[width * result.PixelHeight * 4];
        result.CopyPixels(px, width * 4, 0);
        var wrong = 0;
        for (var y = 0; y < result.PixelHeight && y + 2 < page.GetLength(0); y++)
        {
            var best = double.MaxValue;
            foreach (var d in new[] { -0.75, -0.5, -0.25, 0, 0.25, 0.5, 0.75 })
            {
                if (y + d < 0) continue;
                var sum = 0.0;
                for (var x = 0; x < width; x++) sum += Math.Abs(px[(y * width + x) * 4] - At(page, y + d, x));
                best = Math.Min(best, sum / width);
            }

            if (best > 6) wrong++;
        }

        return wrong;
    }

    static (int Placed, int Lost, int Height, int Wrong, double Ms) Run(double[,] page, int width, int height, IEnumerable<double> offsets)
    {
        var stitcher = new ScrollStitcher(width, height);
        var watch = new Stopwatch();
        var count = 0;
        foreach (var offset in offsets)
        {
            var frame = Frame(page, width, height, offset);
            watch.Start();
            stitcher.Add(frame);
            watch.Stop();
            count++;
            if (Environment.GetEnvironmentVariable("R70") == "trace")
            {
                var cells = typeof(ScrollStitcher).GetMethod("HashCells", Any)!.Invoke(stitcher, new object[] { frame });
                var vt = (int)typeof(ScrollStitcher).GetField("_viewTop", Any)!.GetValue(stitcher)!;
                var mis = string.Join(" ", Enumerable.Range(-2, 5).Select(k => ((double)typeof(ScrollStitcher).GetMethod("Misfit", Any)!.Invoke(stitcher, new object[] { cells!, vt + k, 1 })!).ToString("0.00")));
                Console.WriteLine($"      misfit around {vt}: {mis}");
            }
            if (Environment.GetEnvironmentVariable("R70") == "trace")
                Console.WriteLine($"    {offset,8:0.##}: {stitcher.LastOutcome} {stitcher.LastShift} at {(int)typeof(ScrollStitcher).GetField("_viewTop", Any)!.GetValue(stitcher)! + (double)typeof(ScrollStitcher).GetField("_viewFraction", Any)!.GetValue(stitcher)!:0.00} phase {(double)typeof(ScrollStitcher).GetField("_phase", Any)!.GetValue(stitcher)!:0.00}");
        }

        var result = stitcher.Result();
        if (Environment.GetEnvironmentVariable("R70") == "trace")
            Console.WriteLine($"    kept {stitcher.KeptColumns()}");
        return (stitcher.PlacedFrames, stitcher.LostFrames, stitcher.Height, WrongRows(result, page), watch.Elapsed.TotalMilliseconds / count);
    }

    static IEnumerable<double> Path(params (double To, double Step)[] legs)
    {
        var at = 0.0;
        yield return at;
        foreach (var (to, step) in legs)
        {
            var sign = Math.Sign(to - at);
            while (Math.Abs(to - at) > 1e-9)
            {
                at = sign > 0 ? Math.Min(to, at + step) : Math.Max(to, at - step);
                yield return at;
            }
        }
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- long screenshots: back and forth, big wheel steps, fractions of a pixel, look-alike list items
        {
            const int width = 480, height = 360;
            var cases = new (string Name, bool List, (double, double)[] Legs)[]
            {
                ("down, up, down again (whole pixels)", false, new[] { (900.0, 60.0), (300.0, 90.0), (1500.0, 75.0) }),
                ("down, up, down again (fractions)", false, new[] { (900.0, 57.25), (300.0, 83.5), (1500.0, 71.75) }),
                ("big wheel steps (most of the area)", false, new[] { (1800.0, 250.0) }),
                ("big steps back and forth", false, new[] { (1200.0, 230.5), (200.0, 260.25), (1900.0, 240.75) }),
                ("a list of look-alike items, back and forth", true, new[] { (1000.0, 96.0), (300.0, 120.0), (1600.0, 144.0) }),
                ("a list, fractions", true, new[] { (1000.0, 97.5), (300.0, 121.25), (1600.0, 143.75) }),
            };
            foreach (var (name, list, legs) in cases)
            {
                var page = Page(width, 2600, name.Length, list);
                var offsets = Path(legs).ToList();
                var end = offsets.Max();
                var (placed, lost, total, wrong, ms) = Run(page, width, height, offsets);
                Check(Math.Abs(total - (height + end)) <= 2 && wrong <= 3,
                    $"{name}: {total} rows for {height + end}, {wrong} wrong, {placed} placed / {lost} lost, {ms:0.0} ms a frame");
            }

            // A big area, wheel steps of most of its height, fractions, back and forth: right, and quick enough for
            // ten frames a second.
            {
                const int bigWidth = 1100, bigHeight = 860;
                var page = Page(bigWidth, 5200, 3, false);
                var step = double.TryParse(Environment.GetEnvironmentVariable("R70STEP"), out var st) ? st : 690.5;
                var offsets = Path((3000.0, step), (900.0, 640.25), (4000.0, 700.75)).ToList();
                var (placed, lost, total, wrong, ms) = Run(page, bigWidth, bigHeight, offsets);
                Check(Math.Abs(total - (bigHeight + 4000)) <= 2 && wrong <= 3 && lost == 0,
                    $"a big area in big steps: {total} rows for {bigHeight + 4000}, {wrong} wrong, {placed} placed / {lost} lost");
                // (the shared machines of GitHub Actions run a Debug build a few times slower: a loose bound there)
                var bound = Environment.GetEnvironmentVariable("CI") == "true" ? 150 : 40;
                Check(ms < bound, $"… {ms:0.0} ms a frame (under {bound})");
            }
        }

        // ---- the settings window: its own title bar (theme colours, 帮助, the window buttons), found by window detection
        {
            var opened = new List<string>();
            HelpDocument.OpenOverride = opened.Add;
            var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(400);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(settings);
            Check(chrome != null && chrome.CaptionHeight > 30 && settings.FindName("TitleBar") is FrameworkElement,
                "the settings window draws its own title bar (no system one keeping the old colours)");
            var help = (System.Windows.Controls.Button)settings.FindName("HelpButton");
            Check(help != null && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(help), "a ? button in the title bar, clickable");
            help!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(opened.Count == 1 && (opened[0].EndsWith("help/index.html") || opened[0] == HelpDocument.OnlineUrl), $"… it opens the help ({opened.FirstOrDefault()})");

            BitmapSource Shot()
            {
                var root = (FrameworkElement)settings.Content;
                var scale = VisualTreeHelper.GetDpi(settings).DpiScaleX;
                var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * 1.15 * scale), (int)(root.ActualHeight * 1.15 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                return bitmap;
            }

            Color TitleColor(BitmapSource shot)
            {
                var px = new byte[4];
                shot.CopyPixels(new Int32Rect(shot.PixelWidth / 2, 6, 1, 1), px, 4, 0);
                return Color.FromRgb(px[2], px[1], px[0]);
            }

            ((System.Windows.Controls.RadioButton)settings.FindName("ThemeDark")).IsChecked = true; // as the user switches it
            Pump(200);
            var dark = Shot();
            ((System.Windows.Controls.RadioButton)settings.FindName("ThemeLight")).IsChecked = true;
            Pump(200);
            var light = Shot();
            var (d, l) = (TitleColor(dark), TitleColor(light));
            Check(d.R < 80 && l.R > 200, $"the title bar follows the theme at once (dark {d}, light {l})");
            if (Environment.GetEnvironmentVariable("R70SHOT") is { } folder)
            {
                foreach (var (shot, name) in new[] { (dark, "settings_dark.png"), (light, "settings_light.png") })
                {
                    using var file = File.Create(System.IO.Path.Combine(folder, name));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(shot));
                    encoder.Save(file);
                }
            }

            settings.Topmost = true; // above whatever else is open at that spot while the tests run
            Pump(100);
            var handle = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            var bounds = WindowDetector.BoundsOf(handle);
            var found = WindowDetector.Detect((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2), false);
            Check(found?.Handle == handle && found.Own, $"window detection frames Markshot's own settings window ({found?.Label})");
            settings.Close();
            HelpDocument.OpenOverride = null;
            Check(Source("Core/TrayService.cs").Contains("Add(\"帮助\", HelpDocument.Open);"), "帮助 in the tray menu");
        }

        // ---- Win + mouse: the capture's 圆角
        {
            var square = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 40 * 30 * 4).ToArray(), 160);
            var rounded = ImageOps.RoundCorners(square, 10);
            var px = new byte[40 * 30 * 4];
            rounded.CopyPixels(px, 160, 0);
            Check(px[3] == 0 && px[(15 * 40 + 20) * 4 + 3] == 200, "a picture gets the 圆角 (corners see-through, the middle kept)");
            var app = Source("App.xaml.cs");
            var done = app[app.IndexOf("private void WinMouseDone", StringComparison.Ordinal)..];
            done = done[..done.IndexOf("public void ApplyHotkeys", StringComparison.Ordinal)];
            Check(done.Contains("Config.CaptureCornerRadius") && done.Contains("ImageOps.RoundCorners(image, corner)") && done.Contains("cornerRadius: corner"),
                "Win + mouse copies and pins with the capture's 圆角");
            Check(Source("UI/WinMouseSelect.cs").Contains("border.CornerRadius = new CornerRadius(radius / scale);"), "… its frame shows the 圆角 while drawn");
        }

        // ---- the help page: shipped next to the program, up to date, every part there
        {
            var page = Source("help/index.html");
            var version = System.Text.RegularExpressions.Regex.Match(Source("Markshot.csproj"), "<Version>([^<]+)</Version>").Groups[1].Value;
            Check(page.Contains($"适用于 Markshot {version}"), $"the help page is built for this version ({version}; run tools/make_help.py after a bump)");
            var parts = new[] { "认识 Markshot", "快速上手", "截图", "批注", "贴图", "长截图", "文字识别与翻译", "Win + 鼠标", "悬浮按钮", "录屏", "图片编辑器", "使用案例", "快捷键速查", "常见问题", "关于" };
            Check(parts.All(part => page.Contains($">{part}</h2>")), "introduction, every function, use cases, shortcuts and questions");
            var demos = System.Text.RegularExpressions.Regex.Matches(page, "<svg class=\"demo\"").Count;
            Check(demos >= 12 && page.Contains("<animate ") && !page.Contains("src=\"http"), $"{demos} animated pictures (lasso, text recognition, recording, editor too), nothing loaded from the internet");
            foreach (var demo in new[] { "lasso", "ocr", "record", "editor" })
            {
                Check(page.Contains($"data-demo=\"{demo}\""), $"… the {demo} picture");
            }

            var stars = System.Text.RegularExpressions.Regex.Matches(page, "<strong class=\"star\">([^<]+)</strong>").Select(m => m.Groups[1].Value).ToList();
            Check(stars.Count >= 15 && stars.Any(s => s.Contains("右键")) && stars.Any(s => s.Contains("抠除")) && stars.Any(s => s.Contains("空格")),
                $"what sets Markshot apart is in bold ({stars.Count}: right click undoes, 抠除, space moves …)");
            Check(page.Contains("文字识别：识别长图里的文字") || page.Contains("工具条上的"), "the long screenshot's 文字识别 is described");
            Check(page.Contains("作者 <b>guoqing.wu</b>（GitHub 账户") && page.Contains("https://github.com/well74741/Markshot") && page.Contains("Copyright © 2026 guoqing.wu (well74741)") &&
                  page.Contains("alt=\"作者微信二维码\""), "about: the author, copyright, GitHub and the WeChat code");
            Check(Source("tools/make_help.py").Contains("rotate=\"auto\" path=\"M{x1} {y1} L{x2} {y2}\""), "the drawn arrow's head rides on its end, turned along it");
            Check(Source("Markshot.csproj").Contains("<Content Include=\"help\\index.html\">"), "… copied next to Markshot.exe (installer and portable)");
        }

        // ---- text recognition: tables, symbols and formulas, columns, long pictures (the real recogniser)
        if (Environment.GetEnvironmentVariable("R70") != "noocr")
        {
            BitmapSource Draw(int width, int height, Action<DrawingContext> draw)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    draw(dc);
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return bitmap;
            }

            var face = new Typeface("Microsoft YaHei UI");
            void Text(DrawingContext dc, string text, double x, double y, double size = 16) =>
                dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1), new Point(x, y));
            Markshot.Services.OcrDocument Read(BitmapSource image) => Markshot.Services.OcrService.RecognizeDocumentAsync(image).GetAwaiter().GetResult();

            // a table with lines
            var data = new[] { new[] { "姓名", "部门", "工号", "入职日期" }, new[] { "张三", "设计部", "1024", "2023-05-12" }, new[] { "李四", "研发部", "2048", "2021-11-03" }, new[] { "王五", "市场部", "4096", "2024-02-28" } };
            var table = Draw(560, 190, dc =>
            {
                var pen = new Pen(Brushes.Gray, 1);
                for (var r = 0; r <= data.Length; r++) dc.DrawLine(pen, new Point(20, 20 + r * 40), new Point(540, 20 + r * 40));
                foreach (var x in new[] { 20, 150, 280, 400, 540 }) dc.DrawLine(pen, new Point(x, 20), new Point(x, 20 + data.Length * 40));
                for (var r = 0; r < data.Length; r++)
                for (var c = 0; c < 4; c++)
                    Text(dc, data[r][c], new[] { 30, 160, 290, 410 }[c], 28 + r * 40);
            });
            var read = Read(table);
            var cells = read.Tables.FirstOrDefault()?.Rows;
            Console.WriteLine("  table: " + (cells == null ? "none" : string.Join(" | ", cells.Select(row => string.Join(",", row)))));
            var same = cells == null ? 0 : Enumerable.Range(0, 4).Sum(r => Enumerable.Range(0, 4).Count(c => r < cells.Count && c < cells[r].Count && cells[r][c] == data[r][c]));
            Check(cells is { Count: 4 } && cells.All(row => row.Count == 4) && same >= 14, $"a table is read as rows and columns ({same} of 16 cells exact)");

            // symbols and a formula
            var math = Draw(620, 200, dc =>
            {
                Text(dc, "面积 S = π r²，误差 ±0.5%，α ≥ β，x ≠ 0", 20, 20, 20);
                Text(dc, "∑ aᵢ ≤ 100，√2 ≈ 1.414，温度 25℃，角度 30°", 20, 70, 20);
                Text(dc, "y =", 20, 140, 20);
                Text(dc, "a + b", 70, 122, 18);
                dc.DrawLine(new Pen(Brushes.Black, 1.5), new Point(66, 147), new Point(120, 147));
                Text(dc, "2c", 82, 150, 18);
                Text(dc, "+ 1", 130, 140, 20);
            });
            if (Environment.GetEnvironmentVariable("R70") == "ocr")
                foreach (var b in Markshot.Services.OcrService.RecognizeBlocksAsync(math).GetAwaiter().GetResult()) Console.WriteLine($"    block {b.Text} {b.Box.X},{b.Box.Y} {b.Box.Width}x{b.Box.Height}");
            var formula = Read(math).Text;
            Console.WriteLine("  math: " + formula.Replace(Environment.NewLine, " ⏎ "));
            var symbols = new[] { "π", "±", "≥", "≠", "∑", "≤", "√", "≈", "℃", "°" };
            var hit = symbols.Count(formula.Contains);
            Check(hit >= 8, $"symbols are recognised ({hit} of {symbols.Length})");
            Check(formula.Contains("(a+b)/(2c)") || formula.Contains("(a + b)/(2c)") || formula.Contains("(a+b)/2c") || formula.Contains("(a + b)/2c"), "a stacked fraction becomes (a + b)/(2c)");

            // two columns of text: read column by column
            var left = new[] { "第一栏第一行的文字比较长一些", "第一栏第二行的文字也比较长", "第一栏第三行的文字继续写下" };
            var right = new[] { "第二栏第一行的文字比较长一些", "第二栏第二行的文字也比较长", "第二栏第三行的文字继续写下" };
            var columns = Draw(760, 150, dc =>
            {
                for (var i = 0; i < 3; i++)
                {
                    Text(dc, left[i], 20, 20 + i * 32);
                    Text(dc, right[i], 420, 20 + i * 32);
                }
            });
            var columnText = Read(columns).Text;
            Console.WriteLine("  columns: " + columnText.Replace(Environment.NewLine, " ⏎ "));
            var firstRight = columnText.IndexOf("第二栏第一行", StringComparison.Ordinal);
            var lastLeft = columnText.IndexOf("第一栏第三行", StringComparison.Ordinal);
            Check(firstRight > 0 && lastLeft >= 0 && lastLeft < firstRight, "two columns of text are read one after the other");

            // a long picture (a long screenshot): lines at the bottom are found too
            var lines = 160;
            var tall = Draw(520, 30 * lines + 40, dc =>
            {
                for (var i = 0; i < lines; i++) Text(dc, $"第 {i + 1} 行：长截图里的文字识别测试", 20, 20 + i * 30);
            });
            var watch = Stopwatch.StartNew();
            var tallText = Read(tall).Text;
            var flat = tallText.Replace(" ", "");
            var found = Enumerable.Range(1, lines).Count(i => flat.Contains($"第{i}行"));
            if (Environment.GetEnvironmentVariable("R70") == "ocr")
            {
                Console.WriteLine("  missing: " + string.Join(",", Enumerable.Range(1, lines).Where(i => !flat.Contains($"第{i}行"))));
                Console.WriteLine(string.Join(" ⏎ ", tallText.Split(Environment.NewLine).Take(30)));
            }
            Check(found >= lines * 0.95 && flat.IndexOf("第1行", StringComparison.Ordinal) < flat.IndexOf($"第{lines}行", StringComparison.Ordinal),
                $"a long picture ({tall.PixelHeight} px) is read top to bottom: {found} of {lines} lines, {watch.ElapsedMilliseconds} ms");

            // the window shows tables as tables
            var window = new TranslationWindow(null, autoTranslate: false) { Left = -4000, Top = -4000, ShowActivated = false };
            window.Show();
            window.ShowTables(new[] { new Markshot.Services.OcrTable(data) });
            Pump(200);
            Check(((FrameworkElement)window.FindName("TableToggle")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("TableView")).Visibility == Visibility.Visible &&
                  ((System.Windows.Controls.Panel)window.FindName("TablePanel")).Children.Count == 1, "the text recognition window shows the table as a table (表格 switch)");
            window.Close();
            var html = Markshot.Core.ClipboardService.HtmlFragment(new Markshot.Services.OcrTable(data).ToHtml());
            var start = int.Parse(System.Text.RegularExpressions.Regex.Match(html, "StartFragment:(\\d+)").Groups[1].Value);
            Check(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(html)[start..]).StartsWith("<table"), "copied as an HTML table too (pastes into Excel / Word as a table)");
        }

        // ---- the long screenshot bar has 文字识别; pins: text selection switch, the drag key
        {
            Check(Source("UI/ScrollCaptureSession.cs").Contains("Action(\"IconOcrMask\", \"文字识别：识别长图里的文字\", ScrollCaptureAction.Ocr)") &&
                  Source("App.xaml.cs").Contains("case UI.ScrollCaptureAction.Ocr when image != null:"), "长截图 → 文字识别");
            var config = Markshot.App.Config;
            var saved = (config.PinTextSelect, config.TextBypassModifier);
            config.PinTextSelect = false;
            var bypass = typeof(PinWindow).GetMethod("IsTextBypassHeld", Any)!;
            Check((bool)bypass.Invoke(null, null)!, "text selection off: a drag on text moves the pin");
            config.PinTextSelect = true;
            config.TextBypassModifier = "Ctrl";
            Check(!(bool)bypass.Invoke(null, null)!, "on: text is selected unless the key is held");
            (config.PinTextSelect, config.TextBypassModifier) = saved;
            var xaml = Source("UI/SettingsWindow.xaml");
            Check(xaml.Contains("x:Name=\"PinTextSelectCheck\"") && xaml.Contains("x:Name=\"TextBypassBox\" Style=\"{StaticResource HotkeyField}\""),
                "settings → 贴图窗口: the switch and the key (pressed, not typed)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
