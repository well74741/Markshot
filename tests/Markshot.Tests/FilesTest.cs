using System;
using System.Collections.Generic;
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
using Microsoft.Win32;

// 打开方式 registration, a second start handing files over, the themed save window.
static class FilesTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void SetInternal(Type type, string field, object value) =>
        type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    static void Shot(FrameworkElement root, string file)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        enc.Save(f);
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark);

        // ---- registration, in a key of the test's own (never the real Software\Classes)
        const string root = @"Software\MarkshotTest\Classes";
        SetInternal(typeof(FileAssociation), "ClassesRoot", root);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\MarkshotTest", false);
        var exe = @"C:\Program Files\Markshot\Markshot.exe";
        FileAssociation.Register(exe);
        Check(FileAssociation.RegisteredCommand() == $"\"{exe}\" \"%1\"", "the open command points at the program with the file");
        using (var list = Registry.CurrentUser.OpenSubKey(root + @"\.webp\OpenWithProgids"))
        {
            Check(list?.GetValueNames().Contains(FileAssociation.ProgId) == true, "listed in 打开方式 for .webp");
        }

        using (var types = Registry.CurrentUser.OpenSubKey(root + @"\Applications\Markshot.exe\SupportedTypes"))
        {
            Check(FileAssociation.Extensions.All(e => types?.GetValueNames().Contains(e) == true), "all picture types supported");
        }

        FileAssociation.Register(@"D:\Other\Markshot.exe");
        Check(FileAssociation.RegisteredCommand()!.StartsWith("\"D:\\Other\\Markshot.exe\""), "a moved program refreshes the path");
        FileAssociation.Unregister(@"D:\Other\Markshot.exe");
        using (var list = Registry.CurrentUser.OpenSubKey(root + @"\.png\OpenWithProgids"))
        {
            Check(FileAssociation.RegisteredCommand() == null && list?.GetValueNames().Contains(FileAssociation.ProgId) != true, "turned off: gone again");
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\MarkshotTest", false);

        // ---- a second start hands the pictures to the running Markshot (private pipe name)
        SetInternal(typeof(OpenRequests), "Prefix", "MarkshotTest.Open." + Environment.ProcessId + ".");
        var dir = Path.Combine(Path.GetTempPath(), "markshot-files-test");
        Directory.CreateDirectory(dir);
        var picture = Path.Combine(dir, "图 1.png");
        ImageFile.Save(ImageOps.FromPixels(new byte[] { 0, 0, 255, 255 }, 1, 1), picture);
        var args = new[] { "--capture", picture, Path.Combine(dir, "missing.png"), Path.Combine(dir, "notes.txt") };
        var files = OpenRequests.ImagesIn(args);
        Check(files.Count == 1 && files[0] == picture, "only existing pictures on the command line count");
        Check(!OpenRequests.Send(files), "nobody listening: the second start knows");
        var received = new List<string>();
        using var stop = new CancellationTokenSource();
        OpenRequests.Listen(Dispatcher.CurrentDispatcher, received.Add, stop.Token);
        Pump(200);
        Check(OpenRequests.Send(files), "handed over");
        Pump(500);
        Check(received.Count == 1 && received[0] == picture, "the running one gets the file (on its UI thread)");
        Check(OpenRequests.Send(new[] { picture, picture }), "again (the listener keeps listening)");
        Pump(500);
        Check(received.Count == 3, "every file arrives");
        stop.Cancel();

        // ---- the save window
        var dialog = new SaveImageDialog("保存图片", "Markshot_1.png", dir);
        dialog.SetFormat(".jpg");
        Check(dialog.FileName == "Markshot_1.jpg", "picking a format changes the name's extension");
        var nameBox = (System.Windows.Controls.TextBox)dialog.GetType().GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        nameBox.Text = "截图.webp";
        Check(dialog.CurrentPath == Path.Combine(dir, "截图.webp"), "typing a .webp name picks WebP");
        nameBox.Text = "图 1";
        Check(dialog.CurrentPath == Path.Combine(dir, "图 1.webp") && !dialog.WarningShown, "no extension typed: the format's is added");
        dialog.SetFormat(".png");
        Check(dialog.WarningShown, "an existing file is pointed out (it would be overwritten)");
        nameBox.Text = "a:b.png";
        Check(dialog.WarningShown && !dialog.Confirm() && dialog.ResultPath == null, "a name Windows cannot use is refused");
        var newFolder = Path.Combine(dir, "新文件夹");
        if (Directory.Exists(newFolder)) Directory.Delete(newFolder, true);
        dialog.SetFolder(newFolder);
        nameBox.Text = "结果";
        Check(dialog.Confirm() && dialog.ResultPath == Path.Combine(newFolder, "结果.png") && Directory.Exists(newFolder), "保存: the path, its folder created");
        Shot((FrameworkElement)dialog.Content, "save_dialog_dark.png");
        Theme.Apply(ThemeMode.Light);
        Pump(50);
        Shot((FrameworkElement)new SaveImageDialog("保存图片", "Markshot_2.png", dir).Content, "save_dialog_light.png");
        Theme.Apply(ThemeMode.Dark);
        Directory.Delete(dir, true);

        // ---- the update window: themed, downloads, then installs quietly (here: noted only)
        UpdateService.Package installed = null;
        var update = new UpdateWindow(new UpdateService.Package(Path.Combine(Path.GetTempPath(), "Markshot-Setup-9.9.9.exe"), "9.9.9"))
        {
            Left = -3000, Top = -3000, ShowActivated = false, InstallAction = package => installed = package
        };
        update.Show();
        Pump(100);
        Shot((FrameworkElement)update.Content, "update_dark.png");
        var task = update.InstallAsync();
        var until = DateTime.Now.AddSeconds(5);
        while (!task.IsCompleted && DateTime.Now < until) Pump(50);
        Check(installed?.Version == "9.9.9" && update.Status.Contains("重新打开"), "立即更新: installs quietly, says Markshot reopens");
        update.Close();
        var iss = File.ReadAllText(Repo.File(Path.Combine("installer", "Markshot.iss")));
        Check(iss.Contains("WizardStyle=modern dynamic") && iss.Contains("RestartAfterUpdate"), "the installer follows light / dark and reopens Markshot after a quiet update");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
