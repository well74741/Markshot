using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.Services;
using Markshot.UI;

/// <summary>0.8.71: OCR export (text / Markdown / Excel), the formula recognition pack, the toolbar's hover lists.</summary>
static class Round71Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    static BitmapSource Draw(int width, int height, Action<DrawingContext> draw)
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

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- export: text, Markdown, Excel
        {
            var text = "季度报告\n\n产品\t单价\t数量\n键盘\t199\t2\n鼠标\t89.5\t3\n编号\t007\t\n\n合计 | 以上";
            var folder = Path.Combine(Path.GetTempPath(), "markshot-r71");
            Directory.CreateDirectory(folder);
            var txt = Path.Combine(folder, "a.txt");
            OcrExport.Save(txt, text);
            Check(File.ReadAllText(txt).Contains("键盘\t199\t2"), "text: as shown");
            var md = OcrExport.Markdown(text);
            Console.WriteLine(md);
            Check(md.Contains("| 产品 | 单价 | 数量 |") && md.Contains("| --- | --- | --- |") && md.Contains("| 键盘 | 199 | 2 |"), "Markdown: lines with the same tabs are a table");
            Check(md.Contains("季度报告") && md.Contains("合计 \\| 以上") == false && md.Contains("合计 | 以上"), "… other lines stay text");
            var xlsx = Path.Combine(folder, "a.xlsx");
            OcrExport.Save(xlsx, text);
            using (var zip = ZipFile.OpenRead(xlsx))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
                Check(names.Contains("[Content_Types].xml") && names.Contains("xl/workbook.xml") && names.Contains("xl/styles.xml"), "Excel: a workbook");
                Check(sheet.Contains("<c r=\"B4\"><v>199</v></c>") && sheet.Contains("<c r=\"B5\"><v>89.5</v></c>"), "… numbers as numbers, each part in its cell");
                Check(sheet.Contains("<c r=\"B6\" t=\"inlineStr\"><is><t xml:space=\"preserve\">007</t>"), "… a code with a leading zero stays text");
                Check(sheet.Contains("<c r=\"A3\" t=\"inlineStr\" s=\"1\">"), "… a table's first row bold");
                var doc = System.Xml.Linq.XDocument.Parse(sheet);
                Check(doc.Root != null, "… well-formed XML");
            }

            Check(Source("UI/TranslationWindow.xaml").Contains("Click=\"Export_Click\"") && Source("UI/SaveImageDialog.cs").Contains("(string Label, string Extension)[]? formats = null"),
                "the text recognition window: 导出 (Excel / Markdown / 文本, in Markshot's save window)");
        }

        // ---- the formula pack: optional, downloaded in the settings
        {
            Check(OcrPacks.Formula.Repo == "breezedeus/pix2text-mfr-1.5" && OcrPacks.Formula.Files.Length == 3, "公式识别: an optional pack (Pix2Text MFR, MIT)");
            var root = Environment.GetEnvironmentVariable("MARKSHOT_OCR_PACKS");
            if (root != null && Directory.Exists(Path.Combine(root, "formula")))
            {
                OcrPacks.RootOverride = root;
                var formula = Draw(420, 120, dc =>
                {
                    var face = new Typeface(new FontFamily("Cambria Math"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
                    FormattedText T(string s, double size) => new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1);
                    dc.DrawText(T("x =", 30), new Point(20, 40));
                    dc.DrawText(T("−b ± √(b² − 4ac)", 26), new Point(80, 10));
                    dc.DrawLine(new Pen(Brushes.Black, 2), new Point(80, 58), new Point(300, 58));
                    dc.DrawText(T("2a", 26), new Point(170, 64));
                });
                var watch = Stopwatch.StartNew();
                var latex = FormulaRecognizer.Get().Recognize(formula);
                Console.WriteLine($"  latex: {latex} ({watch.ElapsedMilliseconds} ms)");
                Check(latex.Contains("\\frac") && latex.Contains("\\pm") && latex.Contains("\\sqrt"), "a formula becomes LaTeX (\\frac, \\pm, \\sqrt)");
                FormulaRecognizer.Forget();
                OcrPacks.RootOverride = null;
            }
            else
            {
                Console.WriteLine("  (formula pack not here: MARKSHOT_OCR_PACKS not set; recognition skipped)");
            }

            var xaml = Source("UI/SettingsWindow.xaml");
            Check(xaml.Contains("x:Name=\"OcrPackRows\""), "settings → 保存与识别: 识别模型 with download / delete");
            Check(Source("UI/TranslationWindow.xaml").Contains("x:Name=\"FormulaButton\""), "the text recognition window: 公式 (when the pack is there)");
        }

        // ---- the toolbar: one hover list, opened after a short rest, faded rather than made / destroyed
        {
            var code = Source("UI/AnnotationToolbar.xaml.cs");
            Check(code.Contains("_hoverIntent") && code.Contains("Interval = TimeSpan.FromMilliseconds(70)") && code.Contains("private void EnsureHoverPopup()"),
                "a list opens when the pointer rests on a tool (not on every button it sweeps over); one popup reused");
            var app = Source("App.xaml");
            Check(app.Contains("Storyboard.TargetProperty=\"Opacity\" To=\"1\" Duration=\"0:0:0\"") && app.Contains("To=\"0\" Duration=\"0:0:0.09\""), "button highlights follow the pointer at once");

            var config = Markshot.App.Config;
            var before = config.HideToolChevrons;
            config.HideToolChevrons = true;
            var bar = new AnnotationToolbar();
            var window = new Window { Content = bar, Left = -4000, Top = -4000, Width = 900, Height = 200, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            bar.SetMode(AnnotationToolbarMode.Capture);
            bar.ApplyToolOrderFromConfig();
            Pump(100);
            var open = typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", Any)!;
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ToolPen") });
            var popup = typeof(AnnotationToolbar).GetField("_hoverPopup", Any)!.GetValue(bar);
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ToolArrow") });
            Check(ReferenceEquals(popup, typeof(AnnotationToolbar).GetField("_hoverPopup", Any)!.GetValue(bar)) && bar.IsHoverMenuOpen, "moving to the next tool: the same list window, its content changed");
            bar.CloseHoverMenu();
            Pump(250);
            var card = (FrameworkElement)typeof(AnnotationToolbar).GetField("_hoverCard", Any)!.GetValue(bar)!;
            Check(!bar.IsHoverMenuOpen && card.Visibility == Visibility.Collapsed && !((FrameworkElement)((System.Windows.Controls.Primitives.Popup)popup!).Child).IsHitTestVisible,
                "closing fades the list out (clicks go through at once)");
            window.Close();
            Pump(50);
            Check(!((System.Windows.Controls.Primitives.Popup)popup).IsOpen, "the bar gone: the popup is closed too");
            config.HideToolChevrons = before;
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
