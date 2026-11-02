using Microsoft.Win32;

namespace Markshot.Core;

/// <summary>
/// Update by installer package: the update folder holds <c>Markshot-Setup-&lt;版本&gt;.exe</c>
/// files; one newer than the running version can be installed from the settings window.
/// The default folder is where the installer was run from (written by the installer to
/// HKCU\Software\Markshot\SetupDir); it can be changed in the settings.
/// </summary>
public static class UpdateService
{
    public const string SetupPrefix = "Markshot-Setup-";

    public sealed record Package(string Path, string Version);

    /// <summary>Folder the installer ran from, or the repository's dist folder for a development build.</summary>
    public static string DefaultPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Markshot");
            if (key?.GetValue("SetupDir") is string dir && !string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }
        catch (Exception ex)
        {
            Log.Info("读取安装包位置失败：" + ex.Message);
        }

        // Development layout: <repo>\publish\Markshot.exe next to <repo>\dist.
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/'))?.FullName;
        var dist = parent == null ? null : System.IO.Path.Combine(parent, "dist");
        return dist != null && Directory.Exists(dist) ? dist : "";
    }

    public static string EffectivePath(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.UpdatePath)
            ? DefaultPath()
            : Environment.ExpandEnvironmentVariables(config.UpdatePath.Trim());

    /// <summary>The newest installer in <paramref name="folder"/> when it is newer than this build.</summary>
    public static Package? FindNewer(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        Package? best = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, SetupPrefix + "*.exe"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                var version = name[SetupPrefix.Length..];
                if (Parse(version).Length == 0)
                {
                    continue;
                }

                if (best == null || Compare(version, best.Version) > 0)
                {
                    best = new Package(file, version);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("检查更新失败：" + ex.Message);
            return null;
        }

        return best != null && Compare(best.Version, AppVersion.Display) > 0 ? best : null;
    }

    /// <summary>Numeric comparison of dotted versions ("0.8.04" &lt; "0.8.10").</summary>
    public static int Compare(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var left = i < x.Length ? x[i] : 0;
            var right = i < y.Length ? y[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>Starts the installer and quits so it can replace the files.</summary>
    public static void Install(Package package)
    {
        Log.Info($"开始更新到 {package.Version}：{package.Path}");
        Process.Start(new ProcessStartInfo(package.Path) { UseShellExecute = true });
        ((App)Application.Current).ShutdownApp();
    }

    private static int[] Parse(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return Array.Empty<int>();
            }

            numbers.Add(number);
        }

        return numbers.ToArray();
    }
}
