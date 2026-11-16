using Markshot.Annotations;

namespace Markshot.Core;

// Explicit schema: these are data tags, never CLR/user-supplied type names. Derived types are
// checked before their bases. Rendering caches/group links are deliberately not persisted.
public sealed partial class PinArchive
{
    private sealed class AnnotationWriter
    {
        private readonly BinaryWriter _w;
        private readonly ResourceWriter _resources;
        public AnnotationWriter(BinaryWriter writer, ResourceWriter resources) { _w = writer; _resources = resources; }
        public void Number(double n) { if (!double.IsFinite(n)) throw new InvalidDataException("Non-finite archive value"); _w.Write(n); }
        public void Point(Point p) { Number(p.X); Number(p.Y); }
        public void Vector(Vector p) { Number(p.X); Number(p.Y); }
        public void Rect(Rect r) { _w.Write(r.IsEmpty); if (!r.IsEmpty) { Number(r.X); Number(r.Y); Number(r.Width); Number(r.Height); } }
        public void Color(Color c) { _w.Write(c.A); _w.Write(c.R); _w.Write(c.G); _w.Write(c.B); }
        public void Text(string? s) { _w.Write(s != null); if (s != null) { var bytes = Encoding.UTF8.GetBytes(s); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Archive text too long"); _w.Write(bytes.Length); _w.Write(bytes); } }
        public void Image(BitmapSource? b) => Text(b == null ? null : _resources.Add(b));
        public void Points(IReadOnlyList<Point> points) { _w.Write(points.Count); foreach (var p in points) Point(p); }
        private void Corners(IEnumerable<int> values) { var array = values.OrderBy(i => i).ToArray(); _w.Write(array.Length); foreach (var i in array) _w.Write(i); }
        private void Pen(PenAnnotation p)
        {
            Points(p.Points); Corners(p.Corners); _w.Write(p.Highlight); Number(p.Softness);
            _w.Write(p.Pressures != null);
            if (p.Pressures != null) { _w.Write(p.Pressures.Count); foreach (var n in p.Pressures) { if (!float.IsFinite(n)) throw new InvalidDataException(); _w.Write(n); } }
        }
        private void Adjust(ImageAdjust? a)
        {
            _w.Write(a != null); if (a == null) return;
            Number(a.Brightness); Number(a.Contrast); Number(a.Saturation); Number(a.Opacity); _w.Write(a.Gray); _w.Write(a.Invert); Number(a.Hue);
            _w.Write(a.Curve != null); if (a.Curve != null) { _w.Write(a.Curve.Length); foreach (var n in a.Curve) Number(n); }
            Number(a.Temperature); Number(a.Sharpen); Number(a.Blur);
        }
        public void Items(IReadOnlyList<Annotation> items)
        {
            _w.Write(items.Count);
            foreach (var a in items)
            {
                var tag = a switch
                {
                    RectAnnotation => 1, EllipseAnnotation => 2, ArrowAnnotation => 3, LineAnnotation => 4,
                    CutoutAnnotation => 6, PolylineAnnotation => 7, PenAnnotation when a.GetType() == typeof(PenAnnotation) => 5,
                    ImageAnnotation => 8, MosaicAnnotation => 9, WatermarkAnnotation => 10, TextAnnotation => 11,
                    NumberAnnotation => 12, MosaicBrushAnnotation => 13, SpotlightAnnotation => 14,
                    _ => throw new InvalidDataException("Unsupported annotation type")
                };
                _w.Write((byte)tag);
                Color(a.Color); Number(a.Thickness); _w.Write(a.Fill); Number(a.CornerRadius); Number(a.Opacity); Number(a.Rotation); _w.Write(a.Hidden);
                _w.Write(a.EraseMasks != null);
                if (a.EraseMasks != null)
                {
                    _w.Write(a.EraseMasks.Count);
                    foreach (var m in a.EraseMasks) { Points(m.Points); Number(m.Width); Number(m.RecordedSize.Width); Number(m.RecordedSize.Height); Number(m.Strength); Number(m.Softness); }
                }
                switch (a)
                {
                    case RectAnnotation p: Rect(p.Rect); break;
                    case EllipseAnnotation p: Rect(p.Rect); break;
                    case ArrowAnnotation p: Point(p.Start); Point(p.End); _w.Write((byte)p.Style); Vector(p.Bend); break;
                    case LineAnnotation p: Point(p.Start); Point(p.End); Vector(p.Bend); break;
                    case CutoutAnnotation p: Pen(p); _w.Write(p.Baked); break;
                    case PolylineAnnotation p:
                        Pen(p); _w.Write(p.Bends.Count); foreach (var pair in p.Bends.OrderBy(pair => pair.Key)) { _w.Write(pair.Key); Vector(pair.Value); } break;
                    case PenAnnotation p: Pen(p); break;
                    case ImageAnnotation p:
                        Rect(p.Rect); Image(p.Image); Image(p.Origin); Adjust(p.Adjust); Number(p.Radius); Number(p.ShadowSize); Number(p.BorderWidth); Color(p.BorderColor); break;
                    case MosaicAnnotation p:
                        Rect(p.Rect); Number(p.BlockSize); Number(p.Blur); _w.Write(p.Gaussian); Number(p.Feather); Image(p.Pattern); Rect(p.PatternRect); break;
                    case WatermarkAnnotation p:
                        Rect(p.Rect); Text(p.Text); Number(p.FontSize); Number(p.Angle); Vector(p.Shift); break;
                    case TextAnnotation p:
                        Point(p.Position); Text(p.Text); Number(p.FontSize); _w.Write(p.Bold); _w.Write(p.Italic); Color(p.StrokeColor); Number(p.StrokeThickness); Text(p.FontName); break;
                    case NumberAnnotation p:
                        Point(p.Center); _w.Write(p.Number); Number(p.Radius); _w.Write(p.Hollow); _w.Write(p.Outlined); _w.Write(p.Tail.HasValue); if (p.Tail is { } tail) Point(tail); break;
                    case MosaicBrushAnnotation p:
                        Points(p.Points); Corners(p.Corners); Number(p.BlockSize); Number(p.Blur); _w.Write(p.Gaussian); Number(p.Feather); Image(p.Pattern); Rect(p.PatternRect); break;
                    case SpotlightAnnotation p: Rect(p.Rect); _w.Write(p.Ellipse); Number(p.Darkness); Number(p.Feather); break;
                }
            }
        }
    }

    private sealed class AnnotationReader
    {
        private readonly BinaryReader _r;
        private readonly ResourceReader _resources;
        public AnnotationReader(BinaryReader reader, ResourceReader resources) { _r = reader; _resources = resources; }
        public int Count(int maximum = 1_000_000) { var n = _r.ReadInt32(); if (n < 0 || n > maximum) throw new InvalidDataException("Archive count out of range"); return n; }
        public double Number() { var n = _r.ReadDouble(); if (!double.IsFinite(n)) throw new InvalidDataException("Non-finite archive value"); return n; }
        private double Positive() { var n = Number(); if (n < 0) throw new InvalidDataException("Negative archive size"); return n; }
        public Point Point() => new(Number(), Number());
        public Vector Vector() => new(Number(), Number());
        public Rect Rect() => _r.ReadBoolean() ? System.Windows.Rect.Empty : new Rect(Number(), Number(), Positive(), Positive());
        public Color Color() => System.Windows.Media.Color.FromArgb(_r.ReadByte(), _r.ReadByte(), _r.ReadByte(), _r.ReadByte());
        public string? Text()
        {
            if (!_r.ReadBoolean()) return null;
            var size = Count(1024 * 1024); var bytes = _r.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes);
        }
        public BitmapSource? Image() => Text() is { } id ? _resources.Get(id) : null;
        public List<Point> Points() { var count = Count(); var points = new List<Point>(count); for (var i = 0; i < count; i++) points.Add(Point()); return points; }
        private void Corners(HashSet<int> corners) { var count = Count(); for (var i = 0; i < count; i++) corners.Add(_r.ReadInt32()); }
        private void Pen(PenAnnotation p)
        {
            p.Points.AddRange(Points()); Corners(p.Corners); p.Highlight = _r.ReadBoolean(); p.Softness = Number();
            if (_r.ReadBoolean()) { var count = Count(); p.Pressures = new List<float>(count); for (var i = 0; i < count; i++) { var n = _r.ReadSingle(); if (!float.IsFinite(n)) throw new InvalidDataException(); p.Pressures.Add(n); } }
        }
        private ImageAdjust? Adjust()
        {
            if (!_r.ReadBoolean()) return null;
            var brightness = Number(); var contrast = Number(); var saturation = Number(); var opacity = Number(); var gray = _r.ReadBoolean(); var invert = _r.ReadBoolean(); var hue = Number();
            double[]? curve = null;
            if (_r.ReadBoolean()) { curve = new double[Count(4096)]; if (curve.Length < 4 || curve.Length % 2 != 0) throw new InvalidDataException("Invalid tone curve"); for (var i = 0; i < curve.Length; i++) curve[i] = Number(); }
            return new ImageAdjust(brightness, contrast, saturation, opacity, gray, invert, hue, curve, Number(), Number(), Number());
        }
        public List<Annotation> Items()
        {
            var count = Count(100_000); var items = new List<Annotation>(count);
            for (var i = 0; i < count; i++)
            {
                Annotation a = _r.ReadByte() switch
                {
                    1 => new RectAnnotation(), 2 => new EllipseAnnotation(), 3 => new ArrowAnnotation(), 4 => new LineAnnotation(),
                    5 => new PenAnnotation(), 6 => new CutoutAnnotation(), 7 => new PolylineAnnotation(), 8 => new ImageAnnotation(),
                    9 => new MosaicAnnotation(), 10 => new WatermarkAnnotation(), 11 => new TextAnnotation(), 12 => new NumberAnnotation(),
                    13 => new MosaicBrushAnnotation(), 14 => new SpotlightAnnotation(), _ => throw new InvalidDataException("Unknown annotation tag")
                };
                a.Color = Color(); a.Thickness = Positive(); a.Fill = _r.ReadBoolean(); a.CornerRadius = Positive(); a.Opacity = Number(); a.Rotation = Number(); a.Hidden = _r.ReadBoolean();
                if (_r.ReadBoolean())
                {
                    var masks = Count(100_000); a.EraseMasks = new List<EraseStroke>(masks);
                    for (var j = 0; j < masks; j++) { var points = Points(); if (points.Count == 0) throw new InvalidDataException("Empty erase mask"); a.EraseMasks.Add(new EraseStroke(points, Positive(), new Size(Positive(), Positive()), Number(), Number())); }
                }
                switch (a)
                {
                    case RectAnnotation p: p.Rect = Rect(); break;
                    case EllipseAnnotation p: p.Rect = Rect(); break;
                    case ArrowAnnotation p:
                        p.Start = Point(); p.End = Point(); p.Style = (ArrowStyle)_r.ReadByte(); if (!Enum.IsDefined(p.Style)) throw new InvalidDataException("Unknown arrow style"); p.Bend = Vector(); break;
                    case LineAnnotation p: p.Start = Point(); p.End = Point(); p.Bend = Vector(); break;
                    case CutoutAnnotation p: Pen(p); p.Baked = _r.ReadBoolean(); break;
                    case PolylineAnnotation p:
                        Pen(p); var bends = Count(); for (var j = 0; j < bends; j++) { var key = _r.ReadInt32(); if (key < 0 || key + 1 >= p.Points.Count) throw new InvalidDataException("Invalid bend"); p.Bends.Add(key, Vector()); } break;
                    case PenAnnotation p: Pen(p); break;
                    case ImageAnnotation p:
                        p.Rect = Rect(); p.Image = Image(); p.Origin = Image(); p.Adjust = Adjust(); p.Radius = Positive(); p.ShadowSize = Positive(); p.BorderWidth = Positive(); p.BorderColor = Color(); break;
                    case MosaicAnnotation p:
                        p.Rect = Rect(); p.BlockSize = Positive(); p.Blur = Positive(); p.Gaussian = _r.ReadBoolean(); p.Feather = Positive(); p.Pattern = Image(); p.PatternRect = Rect(); break;
                    case WatermarkAnnotation p:
                        p.Rect = Rect(); p.Text = Text() ?? ""; p.FontSize = Positive(); p.Angle = Number(); p.Shift = Vector(); break;
                    case TextAnnotation p:
                        p.Position = Point(); p.Text = Text() ?? ""; p.FontSize = Positive(); p.Bold = _r.ReadBoolean(); p.Italic = _r.ReadBoolean(); p.StrokeColor = Color(); p.StrokeThickness = Positive(); p.FontName = Text(); break;
                    case NumberAnnotation p:
                        p.Center = Point(); p.Number = _r.ReadInt32(); p.Radius = Positive(); p.Hollow = _r.ReadBoolean(); p.Outlined = _r.ReadBoolean(); if (_r.ReadBoolean()) p.Tail = Point(); break;
                    case MosaicBrushAnnotation p:
                        p.Points.AddRange(Points()); Corners(p.Corners); p.BlockSize = Positive(); p.Blur = Positive(); p.Gaussian = _r.ReadBoolean(); p.Feather = Positive(); p.Pattern = Image(); p.PatternRect = Rect(); break;
                    case SpotlightAnnotation p: p.Rect = Rect(); p.Ellipse = _r.ReadBoolean(); p.Darkness = Number(); p.Feather = Positive(); break;
                }
                items.Add(a);
            }
            return items;
        }
    }
}
