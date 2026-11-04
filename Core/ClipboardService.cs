using System.Runtime.InteropServices;

namespace Markshot.Core;

public static class ClipboardService
{
    private const int MaxAttempts = 10;

    public static bool CopyImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        if (App.Config?.CopyImageAsFile == true && SaveClipboardFile(image) is { } file)
        {
            // Explorer pastes this as a .png file.
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
        }

        var ok = Retry(() => Clipboard.SetDataObject(data, true));
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

    private static bool Retry(Action action)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
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
