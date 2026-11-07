using System.Runtime.InteropServices;

namespace Markshot.Core;

public static class ClipboardService
{
    private const int MaxAttempts = 10;

    /// <summary>For tests: takes the picture instead of the clipboard (which is the user's).</summary>
    public static Func<BitmapSource, bool>? CopyOverride { get; set; }

    public static bool CopyImage(BitmapSource image)
    {
        if (CopyOverride is { } copyOverride)
        {
            return copyOverride(image);
        }

        var file = App.Config?.CopyImageAsFile == true ? SaveClipboardFile(image) : null; // Explorer pastes this as a .png file
        var picture = Frozen(image);

        // Written by the raw clipboard API, not WPF's: WPF hands Windows an OLE data object and then "flushes" it,
        // which converts the picture into every format right there and keeps the clipboard open meanwhile —
        // clipboard managers / chat programs that listen (they react to every copy) found it open and it failed
        // ("被其他程序占用", most often after copying a few pins in a row). Here the clipboard is opened for a few
        // milliseconds only: the bitmap is prepared first, then put there in one go.
        var dib = ToDib(picture);
        var png = Png(picture);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (TrySetRaw(dib, png, file))
            {
                Log.Info($"复制图片 {image.PixelWidth}x{image.PixelHeight}：成功{(attempt > 0 ? $"（第 {attempt + 1} 次）" : "")}");
                return true;
            }

            Thread.Sleep(attempt < 10 ? 15 : 40); // another program has it open for a moment: try again soon
        }

        Log.Error($"写入剪贴板失败：剪贴板被其他程序占用（{OpenClipboardOwner()}）。");
        return false;
    }

    public static bool CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException ex)
        {
            Log.Error("复制文本失败", ex);
            return false;
        }
    }

    public static bool HasImage
    {
        get
        {
            try
            {
                return Clipboard.ContainsImage();
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public static BitmapSource? TryGetImage()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return null;
            }

            var image = Clipboard.GetImage();
            image?.Freeze();
            return image;
        }
        catch (COMException ex)
        {
            Log.Error("读取剪贴板图片失败", ex);
            return null;
        }
    }

    public static string? TryGetText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (COMException ex)
        {
            Log.Error("读取剪贴板文本失败", ex);
            return null;
        }
    }

    /// <summary>Writes the image to %TEMP%\Markshot\clipboard (files older than a day are removed).</summary>
    private static string? SaveClipboardFile(BitmapSource image)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Markshot", "clipboard");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, "*.png"))
            {
                try
                {
                    if (File.GetCreationTime(old) < DateTime.Now.AddDays(-1))
                    {
                        File.Delete(old);
                    }
                }
                catch (IOException)
                {
                    // still in use by a paste; next time
                }
            }

            var path = Path.Combine(folder, $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            return path;
        }
        catch (Exception ex)
        {
            Log.Error("保存剪贴板图片文件失败", ex);
            return null;
        }
    }

    // ---- the raw clipboard (user32 / kernel32)

    private const uint CF_DIBV5 = 17;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly uint CfPng = RegisterClipboardFormat("PNG");

    /// <summary>Opens the clipboard (a short wait if another program has it), empties it and puts the picture in.</summary>
    private static bool TrySetRaw(byte[] dib, byte[] png, string? file)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            var ok = Put(CF_DIBV5, dib);
            Put(CfPng, png); // keeps the transparency for programs that read PNG (browsers, Office, chat apps)
            if (file != null)
            {
                Put(CF_HDROP, DropList(file));
            }

            return ok;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Put(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        var target = GlobalLock(memory);
        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        return true; // the clipboard owns the memory now
    }

    /// <summary>A BITMAPV5HEADER + top-down BGRA pixels (with the alpha channel), what Windows calls CF_DIBV5.</summary>
    private static byte[] ToDib(BitmapSource picture)
    {
        var (pixels, width, height) = ImageOps.ReadPixels(picture);
        const int header = 124;
        var bytes = new byte[header + pixels.Length];
        void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(bytes, at);
        void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(bytes, at);
        I32(0, header);
        I32(4, width);
        I32(8, -height); // top-down rows
        BitConverter.GetBytes((short)1).CopyTo(bytes, 12);
        BitConverter.GetBytes((short)32).CopyTo(bytes, 14);
        U32(16, 3); // BI_BITFIELDS
        I32(20, pixels.Length);
        U32(40, 0x00FF0000); // red mask
        U32(44, 0x0000FF00);
        U32(48, 0x000000FF);
        U32(52, 0xFF000000); // alpha mask
        U32(56, 0x73524742); // LCS_sRGB
        U32(108, 4); // LCS_GM_IMAGES
        Buffer.BlockCopy(pixels, 0, bytes, header, pixels.Length);
        return bytes;
    }

    private static byte[] Png(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>DROPFILES + the path (UTF-16, double-zero terminated).</summary>
    private static byte[] DropList(string path)
    {
        var name = System.Text.Encoding.Unicode.GetBytes(path + "\0\0");
        var bytes = new byte[20 + name.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0); // offset of the file list
        BitConverter.GetBytes(1).CopyTo(bytes, 16); // fWide
        name.CopyTo(bytes, 20);
        return bytes;
    }

    /// <summary>Which program has the clipboard open (for the log).</summary>
    private static string OpenClipboardOwner()
    {
        try
        {
            var window = GetOpenClipboardWindow();
            if (window == IntPtr.Zero)
            {
                return "未知程序";
            }

            GetWindowThreadProcessId(window, out var pid);
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch (Exception)
        {
            return "未知程序";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>A copy any thread can read (the picture itself when it already is).</summary>
    private static BitmapSource Frozen(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        var copy = image.CloneCurrentValue();
        if (copy.CanFreeze)
        {
            copy.Freeze();
            return copy;
        }

        var pixels = new WriteableBitmap(image);
        pixels.Freeze();
        return pixels;
    }

    private static bool Retry(Action action, int attempts = MaxAttempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
        }

        Log.Error("写入剪贴板失败：剪贴板被其他程序占用。");
        return false;
    }
}
