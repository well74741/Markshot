using System.Runtime.InteropServices;

namespace Markshot.Core;

/// <summary>The mouse pointer at the moment of a capture: its picture and where it goes on the screenshot.</summary>
public sealed record CursorShot(BitmapSource Image, int X, int Y);

/// <summary>
/// Grabs the mouse pointer for "显示鼠标" in captures. The pointer is drawn on black and on white:
/// the difference gives its transparency, so plain, coloured and shadowed pointers all come out right.
/// </summary>
public static class CursorCapture
{
    private const int Canvas = 128;

    public static CursorShot? Grab(int originX, int originY)
    {
        try
        {
            var info = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero)
            {
                return null;
            }

            int hotX = 0, hotY = 0;
            if (GetIconInfo(info.Cursor, out var icon))
            {
                hotX = icon.HotspotX;
                hotY = icon.HotspotY;
                if (icon.Mask != IntPtr.Zero)
                {
                    DeleteObject(icon.Mask);
                }

                if (icon.Color != IntPtr.Zero)
                {
                    DeleteObject(icon.Color);
                }
            }

            var black = Draw(info.Cursor, 0x000000);
            var white = Draw(info.Cursor, 0xFFFFFF);
            var pixels = new byte[Canvas * Canvas * 4];
            var any = false;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                // alpha = 1 - (white - black); colour = black / alpha (premultiplied on black).
                var alpha = 255 - Math.Max(white[i + 2] - black[i + 2], Math.Max(white[i + 1] - black[i + 1], white[i] - black[i]));
                alpha = Math.Clamp(alpha, 0, 255);
                if (alpha == 0)
                {
                    continue;
                }

                any = true;
                pixels[i] = black[i];
                pixels[i + 1] = black[i + 1];
                pixels[i + 2] = black[i + 2];
                pixels[i + 3] = (byte)alpha;
            }

            if (!any)
            {
                return null;
            }

            var image = BitmapSource.Create(Canvas, Canvas, 96, 96, PixelFormats.Pbgra32, null, pixels, Canvas * 4);
            image.Freeze();
            return new CursorShot(image, info.Position.X - hotX - originX, info.Position.Y - hotY - originY);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Draw(IntPtr cursor, int background)
    {
        using var bitmap = new System.Drawing.Bitmap(Canvas, Canvas, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(background | unchecked((int)0xFF000000)));
            var dc = graphics.GetHdc();
            try
            {
                DrawIconEx(dc, 0, 0, cursor, 0, 0, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }

        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, Canvas, Canvas), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var pixels = new byte[Canvas * Canvas * 4];
            for (var y = 0; y < Canvas; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * Canvas * 4, Canvas * 4);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The screenshot with the pointer drawn on it.</summary>
    public static BitmapSource Paint(BitmapSource screen, CursorShot cursor)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(screen, new Rect(0, 0, screen.PixelWidth, screen.PixelHeight));
            dc.DrawImage(cursor.Image, new Rect(cursor.X, cursor.Y, cursor.Image.PixelWidth, cursor.Image.PixelHeight));
        }

        var bitmap = new RenderTargetBitmap(screen.PixelWidth, screen.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Native.POINT Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>
/// Keys of the capture window from the settings: a combination ("Ctrl+M") works on press, a lone
/// modifier ("Alt") on release when nothing else was pressed with it (Alt+drag etc. stay free).
/// </summary>
public static class CaptureKey
{
    public static bool IsLoneModifier(string? configured) => ModifierOf(configured) != ModifierKeys.None;

    public static ModifierKeys ModifierOf(string? configured) => (configured ?? "").Trim().ToLowerInvariant() switch
    {
        "alt" => ModifierKeys.Alt,
        "ctrl" or "control" => ModifierKeys.Control,
        "shift" => ModifierKeys.Shift,
        _ => ModifierKeys.None
    };

    public static bool IsModifierKey(Key key, ModifierKeys modifier) => modifier switch
    {
        ModifierKeys.Alt => key is Key.LeftAlt or Key.RightAlt,
        ModifierKeys.Control => key is Key.LeftCtrl or Key.RightCtrl,
        ModifierKeys.Shift => key is Key.LeftShift or Key.RightShift,
        _ => false
    };

    /// <summary>A combination (or a single key) pressed: true when it is the configured one.</summary>
    public static bool Matches(string? configured, Key key, ModifierKeys modifiers)
    {
        if (string.IsNullOrWhiteSpace(configured) || IsLoneModifier(configured))
        {
            return false;
        }

        var wanted = ModifierKeys.None;
        Key? wantedKey = null;
        foreach (var part in configured.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var modifier = ModifierOf(part);
            if (modifier != ModifierKeys.None)
            {
                wanted |= modifier;
            }
            else if (part.Length == 1 && char.IsDigit(part[0]))
            {
                wantedKey = Key.D0 + (part[0] - '0');
            }
            else if (Enum.TryParse<Key>(part, true, out var parsed))
            {
                wantedKey = parsed;
            }
            else
            {
                return false;
            }
        }

        return wantedKey == key && (modifiers & ~ModifierKeys.Windows) == wanted;
    }
}
