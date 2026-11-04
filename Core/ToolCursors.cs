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
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    private const uint IMAGE_CURSOR = 2;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;

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

    /// <summary>
    /// Light curved double arrow (white with a thin dark halo, like the system
    /// cursors) so it reads on both dark and light screenshots.
    /// </summary>
    private static Cursor? BuildRotationCursor(bool clockwise)
    {
        _ = clockwise; // one symmetric cursor for both directions
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                const double cx = 16;
                const double cy = 17;
                const double radius = 9;
                const double from = 205;
                const double to = 335;
                var start = Polar(cx, cy, radius, from);
                var end = Polar(cx, cy, radius, to);
                var arc = new StreamGeometry();
                using (var context = arc.Open())
                {
                    context.BeginFigure(start, false, false);
                    context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                }

                arc.Freeze();
                var halo = new Pen(new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), 4.4)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                var line = new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2.0)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };

                var heads = new[] { ArrowHead(cx, cy, radius, from, -1), ArrowHead(cx, cy, radius, to, 1) };
                dc.DrawGeometry(null, halo, arc);
                foreach (var head in heads)
                {
                    dc.DrawGeometry(null, new Pen(halo.Brush, 2.4) { LineJoin = PenLineJoin.Round }, head);
                }

                dc.DrawGeometry(null, line, arc);
                foreach (var head in heads)
                {
                    dc.DrawGeometry(line.Brush, null, head);
                }

                // Small centre dot marks the hot spot.
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), null, new Point(16, 16), 2.2, 2.2);
                dc.DrawEllipse(line.Brush, null, new Point(16, 16), 1.2, 1.2);
            }

            return SaveCursor(visual, 32, 16, 16, "rotate-light.cur", trueSize: false);
        }
        catch (Exception ex)
        {
            Log.Error("创建旋转光标失败", ex);
            return null;
        }
    }

    /// <summary>A filled triangle pointing along the arc's tangent at <paramref name="angle"/>.</summary>
    private static Geometry ArrowHead(double cx, double cy, double radius, double angle, int direction)
    {
        var tip = Polar(cx, cy, radius, angle + direction * 14);
        var baseCenter = Polar(cx, cy, radius, angle);
        var outward = new Vector(Math.Cos(angle * Math.PI / 180), Math.Sin(angle * Math.PI / 180));
        var head = new StreamGeometry();
        using (var context = head.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(baseCenter + outward * 3.6, true, false);
            context.LineTo(baseCenter - outward * 3.6, true, false);
        }

        head.Freeze();
        return head;
    }

    /// <param name="trueSize">Brush rings must show at their real pixel size; small symbols follow the system cursor size.</param>
    private static Cursor? SaveCursor(Visual visual, int size, int hotX, int hotY, string fileName, bool trueSize = true)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
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
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)hotX);
            writer.Write((ushort)hotY);
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

        var file = Path.Combine(directory, fileName);
        File.WriteAllBytes(file, cur.ToArray());
        // At its real size: LoadCursorFromFile scales a cursor to the system cursor size, which made the
        // brush ring smaller than what the eraser / pen really covers once the ring outgrew 64 px.
        var handle = trueSize
            ? LoadImage(IntPtr.Zero, file, IMAGE_CURSOR, size, size, LR_LOADFROMFILE)
            : LoadImage(IntPtr.Zero, file, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var safeHandle = new CursorHandle(handle);
        Handles.Add(safeHandle);
        return CursorInteropHelper.Create(safeHandle);
    }

    private static Point Polar(double cx, double cy, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(cx + Math.Cos(radians) * radius, cy + Math.Sin(radians) * radius);
    }

    /// <param name="scale">Screen pixels per canvas pixel (a zoomed pin on a high-DPI screen &gt; 1), so the ring
    /// is as big as the stroke really is on screen.</param>
    public static Cursor Get(AnnotationTool tool, double thickness = 3, Color? color = null, double scale = 1)
    {
        if (tool == AnnotationTool.Select || tool == AnnotationTool.None)
        {
            return Cursors.Arrow;
        }

        if (tool == AnnotationTool.Text)
        {
            return Cursors.IBeam;
        }

        if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line or AnnotationTool.Polyline
            or AnnotationTool.Arrow or AnnotationTool.Mosaic or AnnotationTool.Watermark or AnnotationTool.Number)
        {
            return Cursors.Cross;
        }

        // The ring matches what the brush paints: eraser 2x (EraserWidth), highlighter 4x (PenAnnotation).
        var width = tool switch
        {
            AnnotationTool.Eraser or AnnotationTool.Cutout => Math.Max(2, thickness),
            AnnotationTool.Highlighter => thickness * 4,
            _ => thickness
        };
        // Down to 2 px: a thin brush shows thin (it used to stop at 6 and stay there).
        var diameter = Math.Clamp((int)Math.Round(width * Math.Max(0.1, scale)), 2, 240);
        var ink = tool is AnnotationTool.Eraser or AnnotationTool.Cutout
            ? Colors.White
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

    /// <summary>Cursor bitmap edge: 64 px, larger for big brushes (the ring is never clipped).</summary>
    private static int CursorSize(int diameter) => Math.Max(64, (int)Math.Ceiling((diameter + 12) / 32.0) * 32);

    /// <summary>
    /// Brush ring: a bright white ring with a soft dark halo (visible on any
    /// background), the tool colour as an inner ring for the pen, a light
    /// translucent fill for the eraser, and a centre dot.
    /// </summary>
    private static Cursor? Build(int diameter, Color ink, string key)
    {
        try
        {
            var eraser = key.StartsWith(nameof(AnnotationTool.Eraser), StringComparison.Ordinal) ||
                         key.StartsWith(nameof(AnnotationTool.Cutout), StringComparison.Ordinal);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var size = CursorSize(diameter);
                var center = new Point(size / 2.0, size / 2.0);
                var radius = diameter / 2.0;
                var halo = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), diameter < 6 ? 2.2 : 3.2);
                var ring = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), diameter < 6 ? 1.1 : 1.6);
                var fill = eraser ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : null;
                dc.DrawEllipse(fill, halo, center, radius, radius);
                dc.DrawEllipse(null, ring, center, radius, radius);
                if (!eraser && radius > 4)
                {
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(ink), 1.3), center, radius - 1.8, radius - 1.8);
                }

                if (diameter >= 8)
                {
                    // The centre dot; a small ring marks its centre itself (the dot would hide it).
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, center, 1.8, 1.8);
                    dc.DrawEllipse(Brushes.White, null, center, 0.9, 0.9);
                }
            }

            var canvas = CursorSize(diameter);
            return SaveCursor(visual, canvas, canvas / 2, canvas / 2, $"cursor-{key}.cur");
        }
        catch (Exception ex)
        {
            Log.Error("创建工具光标失败: " + key, ex);
            return null;
        }
    }
}
