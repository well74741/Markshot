using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.Core;

/// <summary>
/// Hears every change of the clipboard (any program's copy) through a message-only window
/// (AddClipboardFormatListener); it never writes to the clipboard.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly HwndSource _source;
    private readonly Action _changed;
    private bool _queued;

    public ClipboardWatcher(Action changed)
    {
        _changed = changed;
        _source = new HwndSource(new HwndSourceParameters("Markshot clipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 });
        _source.AddHook(Hook);
        AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate && !_queued)
        {
            // A little later: the program may still be putting its formats there.
            _queued = true;
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                _queued = false;
                try
                {
                    _changed();
                }
                catch (Exception ex)
                {
                    Log.Error("剪贴板变化处理失败", ex);
                }
            };
            later.Start();
        }

        return IntPtr.Zero;
    }

    /// <summary>The picture on the clipboard now (null: none, or it does not read).</summary>
    public static BitmapSource? ReadImage()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return null;
                }

                var image = Clipboard.GetImage();
                if (image is { PixelWidth: > 0, PixelHeight: > 0 })
                {
                    if (image.CanFreeze)
                    {
                        image.Freeze();
                    }

                    return image;
                }
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(30); // another program has it open for a moment
            }
        }

        return null;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
