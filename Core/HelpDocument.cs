using System.Diagnostics;
using System.IO;

namespace Markshot.Core;

/// <summary>
/// The help (功能介绍与使用方法): a web page shipped next to Markshot.exe (help\index.html, with its pictures), opened in
/// the default browser. It works offline; without it (a build run from the source) the project's page opens instead.
/// </summary>
public static class HelpDocument
{
    public const string OnlineUrl = "https://github.com/well74741/Markshot#readme";

    /// <summary>The page next to the program.</summary>
    public static string LocalPath => Path.Combine(AppContext.BaseDirectory, "help", "index.html");

    /// <summary>Tests: what would be opened (instead of a browser).</summary>
    public static Action<string>? OpenOverride { get; set; }

    public static void Open()
    {
        var target = File.Exists(LocalPath) ? new Uri(LocalPath).AbsoluteUri : OnlineUrl;
        if (OpenOverride != null)
        {
            OpenOverride(target);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开帮助失败", ex);
            Markshot.UI.Toast.Notify("无法打开帮助页面", ex.Message);
        }
    }
}
