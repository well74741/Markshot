using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Markshot.Annotations;
using Microsoft.Win32.SafeHandles;

namespace Markshot.Core;

/// <summary>
/// Small, original crosshair cursors with a tool badge. Generated at runtime so
/// the app does not depend on external cursor files.
/// </summary>
public static class ToolCursors
{
    private static readonly Dictionary<string, Cursor> Cache = new();
    private static readonly List<CursorHandle> Handles = new();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadCursorFromFile(string fileName);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    private sealed class CursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }

    public static Cursor Get(AnnotationTool tool, double thickness = 3, Color? color = null)
    {
        if (tool == AnnotationTool.Select || tool == AnnotationTool.None)
        {
            return Cursors.Arrow;
        }

        if (tool == AnnotationTool.Text)
        {
            return Cursors.IBeam;
        }

        if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line
            or AnnotationTool.Arrow or AnnotationTool.Mosaic or AnnotationTool.Watermark or AnnotationTool.Number)
        {
            return Cursors.Cross;
        }

        var diameter = Math.Clamp((int)Math.Round(thickness * 2), 6, 48);
        var ink = tool == AnnotationTool.Eraser
            ? Color.FromRgb(0xFF, 0x6B, 0x6B)
            : color ?? Color.FromRgb(0xF5, 0xF5, 0xF5);
        var key = $"{tool}-{diameter}-{ink.R:X2}{ink.G:X2}{ink.B:X2}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var cursor = Build(diameter, ink, key) ?? Cursors.Cross;
        Cache[key] = cursor;
        return cursor;
    }

    private static Cursor? Build(int diameter, Color ink, string key)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 0, 0, 0)), 3.0);
                var line = new Pen(new SolidColorBrush(ink), 1.5);
                var radius = diameter / 2.0;
                dc.DrawEllipse(null, outline, new Point(32, 32), radius, radius);
                dc.DrawEllipse(null, line, new Point(32, 32), radius, radius);
            }

            var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            var payload = png.ToArray();

            using var cur = new MemoryStream();
            using (var writer = new BinaryWriter(cur, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)2);
                writer.Write((ushort)1);
                writer.Write((byte)64);
                writer.Write((byte)64);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)32);
                writer.Write((ushort)32);
                writer.Write((uint)payload.Length);
                writer.Write((uint)22);
            }

            cur.Write(payload, 0, payload.Length);

            var directory = Path.Combine(AppContext.BaseDirectory, "cursors");
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                directory = Path.Combine(Path.GetTempPath(), "MarkshotCursors");
                Directory.CreateDirectory(directory);
            }

            var file = Path.Combine(directory, $"cursor-{key}.cur");
            File.WriteAllBytes(file, cur.ToArray());

            var handle = LoadCursorFromFile(file);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var safeHandle = new CursorHandle(handle);
            Handles.Add(safeHandle);
            return CursorInteropHelper.Create(safeHandle);
        }
        catch (Exception ex)
        {
            Log.Error("创建工具光标失败: " + key, ex);
            return null;
        }
    }
}
