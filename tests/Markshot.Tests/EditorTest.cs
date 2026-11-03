using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;
using Markshot.UI;

static class EditorTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    public static void Run()
    {
        const double scale = 1.25; // 125% DPI: 1 DIP = 1.25 image px
        var root = new Grid { Width = 640, Height = 360 };
        var canvasEditor = new AnnotationEditor { Scale = scale };
        var layer = new Canvas();
        var box = new TextBox { Style = (Style)Application.Current.Resources["InlineTextBox"] };
        layer.Children.Add(box);
        root.Children.Add(canvasEditor);
        root.Children.Add(layer);
        var win = new Window
        {
            Content = root, Width = 700, Height = 420, Left = -3000, Top = -3000,
            ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false
        };
        win.Show();

        var editor = new InlineTextEditor(box, canvasEditor);
        var history = new AnnotationHistory();
        editor.Ended += (t, removed) =>
        {
            if (editor.Dirty && !(removed && editor.IsNew)) history.CommitArmed();
            else history.Disarm();
        };

        // (a) new text, typed, committed -> one undo step
        var t1 = new TextAnnotation { Position = new Point(60, 50), FontSize = 32, Color = Colors.Red };
        history.Arm(canvasEditor.Items);
        canvasEditor.Items.Add(t1);
        editor.Begin(t1, true);
        box.SelectedText = "Hello 世界";
        box.CaretIndex = box.Text.Length;
        box.SelectedText = "\r\n第二行 line";
        Check(t1.Text == "Hello 世界\n第二行 line", "live text mirrors box: " + t1.Text.Replace("\n", "\\n"));
        root.UpdateLayout();
        Alignment(root, canvasEditor, box, t1);
        editor.Commit();
        Check(canvasEditor.Items.Count == 1 && history.CanUndo, "commit keeps text and records one step");
        Check(box.Visibility == Visibility.Collapsed && canvasEditor.EditingText == null, "editor hidden after commit");
        history.Undo(canvasEditor.Items);
        Check(canvasEditor.Items.Count == 0, "undo removes new text");
        history.Redo(canvasEditor.Items);
        Check(canvasEditor.Items.Count == 1 && ((TextAnnotation)canvasEditor.Items[0]).Text.StartsWith("Hello"), "redo restores it");

        // (b) empty new text -> discarded, no step
        var t2 = new TextAnnotation { Position = new Point(10, 10), FontSize = 20 };
        history.Arm(canvasEditor.Items);
        canvasEditor.Items.Add(t2);
        editor.Begin(t2, true);
        editor.Commit();
        Check(!canvasEditor.Items.Contains(t2), "empty new text discarded");
        history.Undo(canvasEditor.Items);
        Check(canvasEditor.Items.Count == 0, "empty text left no undo step");
        history.Redo(canvasEditor.Items);
        var restored = (TextAnnotation)canvasEditor.Items[0];

        // (c) re-open unchanged -> not dirty; then change -> one step
        history.Arm(canvasEditor.Items);
        editor.Begin(restored, false, selectAll: true);
        editor.Commit();
        Check(!editor.Dirty, "unchanged re-edit is not dirty");
        history.Arm(canvasEditor.Items);
        editor.Begin(restored, false);
        box.CaretIndex = box.Text.Length;
        box.SelectedText = "!";
        editor.Commit();
        Check(restored.Text.EndsWith("line!"), "re-edit appends at caret");
        history.Undo(canvasEditor.Items);
        Check(((TextAnnotation)canvasEditor.Items[0]).Text.EndsWith("line"), "undo reverts the edit only");

        // (d) clearing an existing text deletes it, undoable
        var existing = (TextAnnotation)canvasEditor.Items[0];
        history.Arm(canvasEditor.Items);
        editor.Begin(existing, false, selectAll: true);
        box.SelectedText = "";
        editor.Commit();
        Check(canvasEditor.Items.Count == 0, "clearing text deletes it");
        history.Undo(canvasEditor.Items);
        Check(canvasEditor.Items.Count == 1, "deletion is undoable");

        // (e) wheel resize while editing + coalesced checkpoints
        var t3 = (TextAnnotation)canvasEditor.Items[0];
        editor.Begin(t3, false);
        var size0 = t3.FontSize;
        editor.AdjustSize(120);
        editor.AdjustSize(120);
        Check(Math.Abs(t3.FontSize - (size0 + 4)) < 0.01 && editor.Dirty, "wheel grows text while editing");
        editor.Commit();
        var h2 = new AnnotationHistory();
        for (var i = 0; i < 10; i++) h2.CheckpointCoalesced(canvasEditor.Items, "slider");
        h2.Undo(canvasEditor.Items);
        Check(!h2.CanUndo, "ten rapid slider ticks make one undo step");

        StyleSheet();
        win.Close();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static void Alignment(Grid root, AnnotationEditor ed, TextBox box, TextAnnotation t)
    {
        ed.EditingText = null;
        ed.InvalidateVisual();
        box.Visibility = Visibility.Hidden;
        root.UpdateLayout();
        var a = Shot(root);

        box.Visibility = Visibility.Visible;
        box.Foreground = Brushes.Red;
        var keep = ed.Items.ToList();
        ed.Items.Clear();
        ed.InvalidateVisual();
        root.UpdateLayout();
        var b = Shot(root);

        ed.Items.AddRange(keep);
        box.ClearValue(TextBox.ForegroundProperty);
        box.Foreground = Brushes.Transparent;
        ed.EditingText = t;
        ed.InvalidateVisual();

        var ra = Program.Bbox(a);
        var rb = Program.Bbox(b);
        Console.WriteLine($"   annotation bbox {ra}  textbox bbox {rb}");
        Check(Math.Abs(ra.l - rb.l) <= 1 && Math.Abs(ra.t - rb.t) <= 1 &&
              Math.Abs(ra.r - rb.r) <= 1 && Math.Abs(ra.b - rb.b) <= 1,
            "editing glyphs overlap committed glyphs within 1 DIP at 125% scale");
    }

    static BitmapSource Shot(FrameworkElement e)
    {
        var bmp = new RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        return bmp;
    }

    static void StyleSheet()
    {
        var bg = new LinearGradientBrush(Color.FromRgb(240, 240, 240), Color.FromRgb(40, 60, 90), 0);
        var items = new Annotation[]
        {
            new TextAnnotation { Position = new Point(20, 16), Text = "普通文字 Plain", FontSize = 24, Color = Color.FromRgb(0xD9, 0x4B, 0x38) },
            new TextAnnotation { Position = new Point(20, 60), Text = "粗体 Bold", FontSize = 24, Bold = true, Color = Color.FromRgb(0x5C, 0x8F, 0xE6) },
            new TextAnnotation { Position = new Point(20, 106), Text = "背景标签 Label", FontSize = 22, Fill = true, Color = Color.FromRgb(0xE4, 0xC4, 0x5A) },
            new TextAnnotation { Position = new Point(20, 156), Text = "描边 Stroke\n第二行", FontSize = 28, Color = Colors.White, StrokeThickness = 2, StrokeColor = Color.FromRgb(20, 20, 20) },
            new TextAnnotation { Position = new Point(300, 60), Text = "旋转 Rotated", FontSize = 22, Bold = true, Fill = true, Color = Color.FromRgb(0xD9, 0x4B, 0x38), Rotation = -12 },
        };
        var ed = new AnnotationEditor { Scale = 1, Width = 520, Height = 260 };
        ed.Items.AddRange(items);
        ed.Selected = items[4];
        var g = new Grid { Width = 520, Height = 260, Background = bg };
        g.Children.Add(ed);
        g.Measure(new Size(520, 260));
        g.Arrange(new Rect(0, 0, 520, 260));
        g.UpdateLayout();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Shot(g)));
        using var f = File.Create("text_styles.png");
        enc.Save(f);
        Console.WriteLine("   wrote text_styles.png");
    }
}
