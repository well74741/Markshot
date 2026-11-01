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
    private static readonly Dictionary<bool, Cursor> RotationCache = new();
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

    public static Cursor GetRotationCursor(bool clockwise)
    {
        if (RotationCache.TryGetValue(clockwise, out var cached))
        {
            return cached;
        }

        var cursor = BuildRotationCursor(clockwise) ?? Cursors.Hand;
        RotationCache[clockwise] = cursor;
        return cursor;
    }

    private static Cursor? BuildRotationCursorFromAsset()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Markshot;component/Assets/Icons/rotation.png"));
            if (resource == null)
            {
                return null;
            }

            using var imageStream = resource.Stream;
            var decoder = BitmapDecoder.Create(
                imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var transformed = new TransformedBitmap(frame, new ScaleTransform(32.0 / frame.PixelWidth, 32.0 / frame.PixelHeight));
            transformed.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));
            using var png = new MemoryStream();
            encoder.Save(png);
            var payload = png.ToArray();
            using var cur = new MemoryStream();
            using (var writer = new BinaryWriter(cur, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)2);
                writer.Write((ushort)1);
                writer.Write((byte)32);
                writer.Write((byte)32);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)16);
                writer.Write((ushort)16);
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

            var file = Path.Combine(directory, "rotate-icon.cur");
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
            Log.Error("创建旋转图标光标失败", ex);
            return null;
        }
    }

    private static Cursor? BuildRotationCursor(bool clockwise)
    {
        var assetCursor = BuildRotationCursorFromAsset();
        if (assetCursor != null)
        {
            return assetCursor;
        }

        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                const double cx = 16;
                const double cy = 16;
                const double radius = 9;
                var start = Polar(cx, cy, radius, clockwise ? 210 : -30);
                var end = Polar(cx, cy, radius, clockwise ? -30 : 210);
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(start, false, false);
                    context.ArcTo(end, new Size(radius, radius), 0, true,
                        clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        true, false);
                }

                geometry.Freeze();
                var outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 0, 0, 0)), 3.2);
                var line = new Pen(new SolidColorBrush(Color.FromRgb(245, 245, 245)), 1.5);
                dc.DrawGeometry(null, outline, geometry);
                dc.DrawGeometry(null, line, geometry);

                var angle = clockwise ? -30 : 210;
                var tangent = clockwise ? angle + 90 : angle - 90;
                var tip = Polar(cx, cy, radius, angle);
                var back = Polar(cx, cy, radius, angle - (clockwise ? 18 : -18));
                var perpendicular = new Vector(
                    Math.Cos((tangent + 90) * Math.PI / 180),
                    Math.Sin((tangent + 90) * Math.PI / 180));
                var head = new StreamGeometry();
                using (var context = head.Open())
                {
                    context.BeginFigure(tip, true, true);
                    context.LineTo(back + perpendicular * 2.8, true, false);
                    context.LineTo(back - perpendicular * 2.8, true, false);
                }

                head.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(245, 245, 245)), outline, head);
            }

            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
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
                writer.Write((byte)32);
                writer.Write((byte)32);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)16);
                writer.Write((ushort)16);
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

            var file = Path.Combine(directory, clockwise ? "rotate-cw.cur" : "rotate-ccw.cur");
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
            Log.Error("创建旋转光标失败", ex);
            return null;
        }
    }

    private static Point Polar(double cx, double cy, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(cx + Math.Cos(radians) * radius, cy + Math.Sin(radians) * radius);
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
