using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Markshot.Core;

/// <summary>
/// Markshot in Explorer's 打开方式 for pictures: a ProgID with an open command, listed under each
/// image type (current user only, no admin rights). Making it the default needs the user's choice in
/// Windows' own chooser (<see cref="ChooseDefault"/>), which Windows 10 / 11 insist on.
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "Markshot.Image";

    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico" };

    /// <summary>Where the classes go under HKEY_CURRENT_USER (tests use a key of their own).</summary>
    internal static string ClassesRoot = @"Software\Classes";

    private static string ExeName(string exePath) => Path.GetFileName(exePath);

    /// <summary>Writes (or refreshes, e.g. after the program moved) the registration.</summary>
    public static void Register(string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        using (var prog = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{ProgId}"))
        {
            prog.SetValue("", "Markshot 图片");
            prog.SetValue("FriendlyTypeName", "Markshot 图片");
            using (var icon = prog.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue("", $"\"{exePath}\",0");
            }

            using var open = prog.CreateSubKey(@"shell\open");
            open.SetValue("", "用 Markshot 编辑");
            using var openCommand = open.CreateSubKey("command");
            openCommand.SetValue("", command);
        }

        using (var app = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\Applications\{ExeName(exePath)}"))
        {
            app.SetValue("FriendlyAppName", "Markshot 图片编辑器");
            using (var openCommand = app.CreateSubKey(@"shell\open\command"))
            {
                openCommand.SetValue("", command);
            }

            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var extension in Extensions)
            {
                types.SetValue(extension, "");
            }
        }

        foreach (var extension in Extensions)
        {
            using var list = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{extension}\OpenWithProgids");
            list.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void Unregister(string exePath)
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{ProgId}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\Applications\{ExeName(exePath)}", false);
        foreach (var extension in Extensions)
        {
            using var list = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{extension}\OpenWithProgids", true);
            list?.DeleteValue(ProgId, false);
        }

        NotifyShell();
    }

    /// <summary>The open command registered now (null when not registered).</summary>
    public static string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{ProgId}\shell\open\command");
        return key?.GetValue("") as string;
    }

    /// <summary>Windows' "how do you want to open .png files" chooser, with "always" ticked by the user.</summary>
    public static void ChooseDefault(IntPtr owner, string extension = ".png")
    {
        // The chooser needs a file of the type; an empty one in the temp folder is enough.
        var sample = Path.Combine(Path.GetTempPath(), "Markshot 打开方式" + extension);
        if (!File.Exists(sample))
        {
            File.WriteAllBytes(sample, Array.Empty<byte>());
        }

        var info = new OPENASINFO { File = sample, Class = null, Flags = 0x1 | 0x2 | 0x8 /* allow + register ext + force registration */ };
        SHOpenWithDialog(owner, ref info);
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string File;
        public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OPENASINFO info);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}

/// <summary>
/// "Open this picture" from a second start (Explorer's 打开方式 runs Markshot.exe with the file) to
/// the Markshot already running, through a named pipe of this user's session.
/// </summary>
public static class OpenRequests
{
    /// <summary>Pipe name prefix (tests use their own so they never reach a running Markshot).</summary>
    internal static string Prefix = "Markshot.Open.";

    private static string PipeName => $"{Prefix}{Process.GetCurrentProcess().SessionId}.{Environment.UserName}";

    /// <summary>The pictures named on a command line (existing image files only).</summary>
    public static List<string> ImagesIn(IEnumerable<string> arguments) =>
        arguments.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal) && ImageFile.IsImage(argument) && File.Exists(argument))
            .Select(Path.GetFullPath)
            .ToList();

    /// <summary>Second start: hands the files over. False when no Markshot is listening.</summary>
    public static bool Send(IReadOnlyList<string> files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            foreach (var file in files)
            {
                writer.WriteLine(file);
            }

            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Running Markshot: each file handed over arrives on the UI thread.</summary>
    public static void Listen(Dispatcher dispatcher, Action<string> open, CancellationToken stop = default)
    {
        var name = PipeName;
        Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(stop);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (await reader.ReadLineAsync(stop) is { } line)
                    {
                        var file = line.Trim();
                        if (file.Length > 0)
                        {
                            await dispatcher.BeginInvoke(() => open(file));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Any failure (the pipe name taken, a file that does not open …) and it listens again.
                    Log.Error("接收打开请求失败", ex);
                    await Task.Delay(500, stop).ContinueWith(_ => { });
                }
            }
        }, stop);
    }
}
