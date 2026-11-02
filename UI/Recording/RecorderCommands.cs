// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Windows.Shell;

namespace Markshot.UI.Recording;

/// <summary>
/// The right-click menu of the "Markshot 录屏" taskbar button (a jump list): 开始 / 停止 / 暂停 /
/// 退出录屏. Each item starts Markshot.exe --recorder &lt;command&gt;; that second instance only signals
/// the running one through a named event and quits.
/// </summary>
public static class RecorderCommands
{
    public static readonly string[] Names = { "start", "stop", "pause", "exit" };
    private static readonly List<(EventWaitHandle Handle, RegisteredWaitHandle Wait)> Listeners = new();

    private static string EventName(string command) => @"Local\Markshot.Recorder." + command;

    /// <summary>Second instance: pass the command on. False when no Markshot is listening.</summary>
    public static bool Send(string command)
    {
        if (!Names.Contains(command) || !EventWaitHandle.TryOpenExisting(EventName(command), out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    /// <summary>Running instance: commands arrive on the UI thread.</summary>
    public static void Listen(Dispatcher dispatcher, Action<string> handler)
    {
        foreach (var name in Names)
        {
            var command = name;
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(command));
            var wait = ThreadPool.RegisterWaitForSingleObject(handle,
                (_, _) => dispatcher.BeginInvoke(() => handler(command)), null, Timeout.Infinite, false);
            Listeners.Add((handle, wait));
        }
    }

    /// <summary>The menu items that fit the recorder's state (none when it is closed).</summary>
    public static void UpdateJumpList(RecordPhase phase)
    {
        if (Application.Current == null)
        {
            return;
        }

        try
        {
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };

            void Add(string title, string command) => list.JumpItems.Add(new JumpTask
            {
                Title = title,
                Arguments = "--recorder " + command,
                ApplicationPath = Environment.ProcessPath,
                IconResourcePath = Environment.ProcessPath,
                Description = title
            });

            switch (phase)
            {
                case RecordPhase.Setup:
                    Add("开始录屏", "start");
                    Add("退出录屏", "exit");
                    break;
                case RecordPhase.Countdown or RecordPhase.Starting:
                    Add("取消开始", "stop");
                    Add("退出录屏", "exit");
                    break;
                case RecordPhase.Recording:
                    Add("停止录屏", "stop");
                    Add("暂停录屏", "pause");
                    Add("退出录屏", "exit");
                    break;
                case RecordPhase.Paused:
                    Add("停止录屏", "stop");
                    Add("继续录屏", "pause");
                    Add("退出录屏", "exit");
                    break;
                case RecordPhase.Finishing or RecordPhase.Preview:
                    Add("退出录屏", "exit");
                    break;
            }

            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception ex)
        {
            Log.Error("更新任务栏菜单失败", ex);
        }
    }
}
