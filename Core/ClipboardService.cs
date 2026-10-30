using System.Runtime.InteropServices;

namespace Markshot.Core;

public static class ClipboardService
{
    private const int MaxAttempts = 6;

    public static bool CopyImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
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
                Thread.Sleep(30);
            }
        }

        Log.Error("写入剪贴板失败：剪贴板被其他程序占用。");
        return false;
    }
}
