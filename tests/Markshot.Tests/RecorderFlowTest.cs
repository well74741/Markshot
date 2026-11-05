using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.Core.Recording;
using Markshot.UI.Recording;

static class RecorderFlowTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }

    static bool PumpUntil(Func<bool> condition, int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (condition()) return true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        return condition();
    }

    static void Shot(Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        if (root.ActualWidth < 1) return;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        enc.Save(f);
    }

    static T Window<T>() where T : Window => Application.Current.Windows.OfType<T>().FirstOrDefault()!;

    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);

    static uint Affinity(Window w)
    {
        GetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, out var a);
        return a;
    }

    static RECT RectOf(Window w)
    {
        GetWindowRect(new WindowInteropHelper(w).Handle, out var r);
        return r;
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); // as in the running app
        var config = new RecorderConfig { SystemAudio = false, Microphone = false, Fps = 30 };
        var controller = new RecorderController(config, () => { })
        {
            SyntheticFrames = (bits, w, h, n) =>
            {
                unsafe
                {
                    var p = (uint*)bits;
                    var bar = (int)(n * 6 % w);
                    for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        p[y * w + x] = Math.Abs(x - bar) < 8 ? 0xFFFFFFFFu : 0xFF2050A0u;
                }
            }
        };
        string? notice = null;
        controller.Notice += m => notice = m;

        controller.Open(new Int32Rect(200, 150, 641, 361), fromCapture: true);
        Check(!Application.Current.Windows.OfType<Window>().Any(w => w.GetType().Name == "RecordDimWindow"), "from a capture: no dimming flash");
        Pump(400);
        var frameWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.GetType().Name == "RecordFrameWindow");
        var bar = Window<RecordBarWindow>();
        Check(controller.Phase == RecordPhase.Setup && frameWindow?.IsVisible == true && bar?.IsVisible == true, "opens: frame + toolbar, setup");
        Check(controller.Rect.Width % 2 == 0 && controller.Rect.Height % 2 == 0, $"even size {controller.Rect.Width}x{controller.Rect.Height}");
        var rb = RectOf(bar!);
        var area = controller.Rect;
        Check(rb.Top >= area.Y + area.Height && rb.Top - (area.Y + area.Height) < 12, $"toolbar under the area (bar top {rb.Top}, area bottom {area.Y + area.Height})");
        var fr = RectOf(frameWindow!);
        Check(fr.Left < area.X && fr.Right > area.X + area.Width, "frame window surrounds the area");
        Shot(bar!, "rec_bar_setup.png");

        // A size that fits any screen (the CI machine's is small).
        controller.SetSize(600, 340);
        Check(controller.Rect.Width == 600 && controller.Rect.Height == 340, $"typed size applies ({controller.Rect.Width}x{controller.Rect.Height})");
        controller.SetSize(640, 360);
        controller.Recenter();
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = Monitors.At(cursor.X, cursor.Y).WorkArea;
        var c = controller.Rect;
        Check(Math.Abs(c.X + c.Width / 2 - (work.X + work.Width / 2)) <= 2 && Math.Abs(c.Y + c.Height / 2 - (work.Y + work.Height / 2)) <= 2,
            $"录屏区域回中: centred on the monitor ({c.X},{c.Y} {c.Width}x{c.Height})");

        // ---- record 1.2 s, pause 0.4 s, record 0.5 s
        controller.Start();
        Check(PumpUntil(() => controller.Phase == RecordPhase.Recording, 8000), "recording starts (" + notice + ")");
        var wall = System.Diagnostics.Stopwatch.StartNew();
        Pump(1200);
        controller.TogglePause();
        Check(controller.Phase == RecordPhase.Paused, "paused");
        var atPause = controller.Elapsed;
        Pump(400);
        var beforeResume = controller.Elapsed;
        controller.TogglePause();
        Pump(500);
        Shot(bar!, "rec_bar_recording.png");
        Check(Affinity(bar!) == 0, "toolbar outside the area: not hidden from capture / screen sharing");
        // key display: the pills are drawn on the frame (inside the area) and so recorded as shown
        controller.SetKeys(true);
        controller.Pills.Emit("Ctrl + S");
        Pump(200);
        Shot(frameWindow!, "rec_frame_pills.png");
        controller.SetKeys(false);
        var recordedTime = controller.Elapsed;
        var wallTime = wall.Elapsed.TotalSeconds;
        var stopTask = controller.StopAsync();
        Check(PumpUntil(() => controller.Phase == RecordPhase.Preview, 15000), "stops into the preview (" + notice + ")");
        PumpUntil(() => stopTask.IsCompleted, 3000);
        if (stopTask.IsFaulted) Console.WriteLine("stop failed: " + stopTask.Exception);
        var take = controller.Take;
        // The pause is left out: the recording clock stands still through it, and the take is as long as that clock.
        Check(take != null && Math.Abs(take.Duration - recordedTime) < 0.2 && beforeResume - atPause < 0.1 && recordedTime < wallTime,
            $"take {take?.Duration:0.00}s = recording time {recordedTime:0.00}s; the clock stood still in the 0.4 s pause (+{beforeResume - atPause:0.00}s)");
        Pump(800);
        Console.WriteLine("windows: " + string.Join(", ", Application.Current.Windows.OfType<Window>().Select(w => w.GetType().Name + ":" + w.IsVisible)));
        var preview = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.GetType().Name == "RecordPreviewWindow");
        Check(preview?.IsVisible == true && frameWindow?.IsVisible == false, "preview window replaces the frame");
        var pr = RectOf(preview!);
        Check(pr.Left == controller.Rect.X && pr.Right - pr.Left == controller.Rect.Width, "preview sits on the area");
        Console.WriteLine("marker: shot"); Shot(bar!, "rec_bar_preview.png"); Console.WriteLine("marker: shot done");

        // ---- export (trim 0.2–1.2 at 2x)
        var dir = Path.Combine(Path.GetTempPath(), "markshot-rec-flow");
        Directory.CreateDirectory(dir);
        var outFile = Path.Combine(dir, "flow.mp4");
        Console.WriteLine("marker: export");
        var task = controller.ExportAsync(new ExportOptions(0.2, 1.2, 2, false, RecordQuality.Clear, 30, false), outFile);
        PumpUntil(() => task.IsCompleted, 20000);
        Console.WriteLine("marker: export done " + task.Status);
        var probe = FFmpeg.ProbeDurationAsync(outFile);
        PumpUntil(() => probe.IsCompleted, 10000);
        var outDuration = probe.Result;
        Check(task.Result && Math.Abs(outDuration - 0.5) < 0.15, $"exported trimmed 2x: {outDuration:0.00}s");

        // ---- unsaved: closing asks first
        controller.RequestClose();
        Pump(200);
        Check(controller.Phase == RecordPhase.Preview, "unsaved take: exit asks instead of closing");
        controller.MarkSaved(outFile);
        controller.RequestClose();
        Pump(300);
        Check(controller.Phase == RecordPhase.Idle && !Application.Current.Windows.OfType<RecordBarWindow>().Any(), "saved: exit closes everything");

        // ---- countdown, then retake while recording, then stop before start = back to setup
        controller.Open();
        Check(Application.Current.Windows.OfType<Window>().Any(w => w.GetType().Name == "RecordDimWindow"), "opened from the tray: the screen dims once");
        controller.SetDelay(true, 1);
        controller.Start();
        Check(controller.Phase == RecordPhase.Countdown && controller.Count == 1, "countdown 1");
        Check(PumpUntil(() => controller.Phase == RecordPhase.Recording, 6000), "countdown ends in recording");
        controller.Retake();
        Check(controller.Phase == RecordPhase.Setup, "retake: back to setup");
        Pump(600);
        Check(controller.Phase == RecordPhase.Setup, "retake waits for 开始 (does not start by itself)");
        controller.Start();
        Check(controller.Phase == RecordPhase.Countdown, "开始 after retake counts down");
        _ = controller.StopAsync();
        Pump(100);
        Check(controller.Phase == RecordPhase.Setup, "stop during the countdown cancels");
        controller.SetDelay(false, 1);

        // ---- taskbar menu commands arrive through the named events
        // A private prefix: the commands must never reach the Markshot that is really running.
        typeof(RecorderCommands).GetField("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, @"Local\MarkshotTest." + Guid.NewGuid().ToString("N") + ".");
        RecorderCommands.Listen(Dispatcher.CurrentDispatcher, controller.Command);
        Check(RecorderCommands.Send("start"), "command sent");

        Check(PumpUntil(() => controller.Phase == RecordPhase.Recording, 8000), "taskbar 开始录屏 starts");
        RecorderCommands.Send("pause");
        Check(PumpUntil(() => controller.Phase == RecordPhase.Paused, 2000), "taskbar 暂停录屏 pauses");
        RecorderCommands.Send("stop");
        Check(PumpUntil(() => controller.Phase == RecordPhase.Preview, 15000), "taskbar 停止录屏 stops into the preview");
        controller.MarkSaved("x.mp4");
        RecorderCommands.Send("exit");
        Check(PumpUntil(() => controller.Phase == RecordPhase.Idle, 3000), "taskbar 退出录屏 closes");

        // ---- an area filling the screen: the toolbar sits on it and is kept out of the video
        controller.Open(new Int32Rect(0, 0, 100000, 100000));
        Pump(300);
        controller.Start();
        PumpUntil(() => controller.Phase == RecordPhase.Recording, 8000);
        Pump(300);
        var fullBar = Window<RecordBarWindow>();
        Check(Affinity(fullBar) == 0x11, $"toolbar on a full-screen area: excluded from capture (0x{Affinity(fullBar):X})");
        _ = controller.StopAsync();
        PumpUntil(() => controller.Phase == RecordPhase.Preview, 15000);
        controller.MarkSaved("x.mp4");
        controller.Close();
        Pump(300);
        Check(!Application.Current.Windows.OfType<Window>().Any(w => w.GetType().Namespace == "Markshot.UI.Recording"), "all recorder windows gone");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
