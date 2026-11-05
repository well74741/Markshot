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

        // On a thread of its own: when another program holds the clipboard, WPF waits (about a second per try, and it
        // flushes with retries too) — on the UI thread that froze the window for many seconds. Here the window waits
        // at most a moment and stays responsive to painting.
        var ok = false;
        var worker = new Thread(() =>
        {
            var data = new DataObject();
            data.SetImage(picture);
            if (file != null)
            {
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
            }

            ok = Retry(() => Clipboard.SetDataObject(data, true), 3);
        })
        {
            IsBackground = true,
            Name = "Markshot clipboard"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(2.5)))
        {
            Log.Error("写入剪贴板超时：剪贴板被其他程序占用。");
            return false;
        }

        Log.Info($"复制图片 {image.PixelWidth}x{image.PixelHeight}：{(ok ? "成功" : "失败")}");
        return ok;
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
