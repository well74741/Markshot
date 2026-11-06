namespace Markshot.Annotations;

/// <summary>
/// The geometry of an element while it is being drawn, shared by the capture
/// overlay and pin windows. Follows the SketchPlayer annotation notes §3.5/3.6/4.3:
/// stretch, Space-move, E-rotate and Ctrl-round are kept as independent
/// quantities and recombined on every move, so modifiers never fight each other.
///
/// * Shapes (rect / ellipse / mosaic box / line / arrow) rotate about the
///   <b>start point</b> — the only point that does not move while stretching —
///   so the shape no longer drifts when you keep dragging after rotating.
/// * Stretching after any modifier is relative to where the pointer was when
///   the modifier ended, so nothing jumps.
/// * Freehand strokes (pen, highlighter, eraser, brush mosaic): Shift draws a
///   straight segment from the last point, Space moves the whole stroke.
/// </summary>
public sealed class CreationGesture
{
    private readonly Point _start;
    private Point _baseEnd;
    private Vector _offset;
    private double _rotation;
    private double _round = -1;

    private Point _stretchAnchorPointer;
    private Point _stretchAnchorEnd;

    private bool _moving;
    private Point _moveAnchor;
    private List<Point>? _moveSnapshot;

    private bool _rotating;
    private double _rotationStart;
    private double _rotationStartAngle;

    private bool _rounding;
    private double _roundStartDistance;
    private double _roundStart;

    private Point? _straightAnchor;
    private int _straightBase;

    public CreationGesture(Annotation target, Point start)
    {
        Target = target;
        _start = start;
        _baseEnd = start;
        _stretchAnchorPointer = start;
        _stretchAnchorEnd = start;
        LastPointer = start;
    }

    public Annotation Target { get; }

    public Point LastPointer { get; private set; }

    public bool IsMoving => _moving;

    public bool IsRotating => _rotating;

    private bool IsFreehand => Target is PenAnnotation or MosaicBrushAnnotation;

    private List<Point>? FreehandPoints => Target switch
    {
        PenAnnotation pen => pen.Points,
        MosaicBrushAnnotation brush => brush.Points,
        _ => null
    };

    private HashSet<int>? FreehandCorners => Target switch
    {
        PenAnnotation pen => pen.Corners,
        MosaicBrushAnnotation brush => brush.Corners,
        _ => null
    };

    /// <summary>Feeds a pointer position (canvas pixels) with the current modifier state.</summary>
    public void Move(Point pointer, bool shift, bool ctrl)
    {
        LastPointer = pointer;
        if (IsFreehand)
        {
            MoveFreehand(pointer, shift);
            return;
        }

        if (_moving)
        {
            _offset += pointer - _moveAnchor;
            _moveAnchor = pointer;
        }
        else if (_rotating)
        {
            var pivot = _start + _offset;
            _rotation = _rotationStart + Angle(pointer - pivot) - _rotationStartAngle;
        }
        else if (ctrl && Target is RectAnnotation)
        {
            UpdateRound(pointer);
        }
        else
        {
            if (_rounding)
            {
                EndRound(pointer);
            }

            var delta = pointer - _stretchAnchorPointer;
            var local = _stretchAnchorEnd + RotateVector(delta, -_rotation);
            _baseEnd = shift ? Constrain(_start, local) : local;
        }

        Apply();
    }

    /// <summary>Space pressed: move the element with the pointer.</summary>
    public void BeginMove()
    {
        if (_moving)
        {
            return;
        }

        _moving = true;
        _moveAnchor = LastPointer;
        if (FreehandPoints is { } points)
        {
            _moveSnapshot = new List<Point>(points);
        }
    }

    public void EndMove()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        _moveSnapshot = null;
        ResetStretchAnchor();
        _straightAnchor = null;
        if (FreehandPoints is { } points)
        {
            _straightBase = points.Count;
        }
    }

    /// <summary>E pressed: rotate about the start point.</summary>
    public void BeginRotate()
    {
        if (_rotating || IsFreehand || Target is NumberAnnotation)
        {
            return;
        }

        _rotating = true;
        _rotationStart = _rotation;
        _rotationStartAngle = Angle(LastPointer - (_start + _offset));
    }

    public void EndRotate()
    {
        if (!_rotating)
        {
            return;
        }

        // Releasing E recomputes nothing; stretching continues from here.
        _rotating = false;
        ResetStretchAnchor();
    }

    private void ResetStretchAnchor()
    {
        _stretchAnchorPointer = LastPointer;
        _stretchAnchorEnd = _baseEnd;
    }

    private void UpdateRound(Point pointer)
    {
        var center = ShapeCenter();
        var distance = (pointer - center).Length;
        if (!_rounding)
        {
            _rounding = true;
            // A minimum travel so a shaky hand near the centre does not snap to a pill.
            _roundStartDistance = Math.Max(24, distance);
            _roundStart = Math.Max(0, _round);
        }

        var t = distance <= _roundStartDistance
            ? _roundStart + (1 - _roundStart) * (1 - distance / _roundStartDistance)
            : _roundStart * Math.Max(0, 1 - (distance - _roundStartDistance) / _roundStartDistance);
        _round = Math.Clamp(t, 0, 1);
    }

    private void EndRound(Point pointer)
    {
        _rounding = false;
        _stretchAnchorPointer = pointer;
        _stretchAnchorEnd = _baseEnd;
    }

    private Point ShapeCenter()
    {
        var mid = new Point((_start.X + _baseEnd.X) / 2, (_start.Y + _baseEnd.Y) / 2);
        return RotateAround(mid, _start, _rotation) + _offset;
    }

    /// <summary>The tablet pen's pressure now (null: a mouse); a pen stroke keeps one value per point.</summary>
    public float? Pressure { get; set; }

    private void KeepPressure()
    {
        if (Pressure is not { } pressure || Target is not PenAnnotation pen)
        {
            return;
        }

        pen.Pressures ??= new List<float>();
        if (pen.Pressures.Count > pen.Points.Count)
        {
            pen.Pressures.RemoveRange(pen.Points.Count, pen.Pressures.Count - pen.Points.Count);
        }

        while (pen.Pressures.Count < pen.Points.Count)
        {
            pen.Pressures.Add(pressure);
        }
    }

    private void MoveFreehand(Point pointer, bool shift)
    {
        MoveFreehandPoints(pointer, shift);
        KeepPressure();
    }

    private void MoveFreehandPoints(Point pointer, bool shift)
    {
        var points = FreehandPoints!;
        if (_moving && _moveSnapshot != null)
        {
            var delta = pointer - _moveAnchor;
            points.Clear();
            points.AddRange(_moveSnapshot.Select(p => p + delta));
            return;
        }

        var corners = FreehandCorners!;
        if (shift)
        {
            // Replace everything after the anchor with a straight segment.
            if (_straightAnchor == null)
            {
                _straightAnchor = points.Count > 0 ? points[^1] : pointer;
                _straightBase = Math.Max(0, points.Count - 1);
            }

            if (points.Count > _straightBase)
            {
                points.RemoveRange(_straightBase, points.Count - _straightBase);
            }

            corners.RemoveWhere(i => i >= _straightBase);
            points.Add(_straightAnchor.Value);
            points.Add(pointer);
            corners.Add(points.Count - 2);
            corners.Add(points.Count - 1);
            return;
        }

        if (_straightAnchor != null)
        {
            // Shift released: the straight segment is final, free drawing continues.
            _straightAnchor = null;
            _straightBase = points.Count;
        }

        if (points.Count == 0 || (points[^1] - pointer).Length > 1.5)
        {
            points.Add(pointer);
        }
    }

    /// <summary>Writes the current geometry into the target element.</summary>
    public void Apply()
    {
        switch (Target)
        {
            case LineAnnotation line:
                line.Start = _start + _offset;
                line.End = RotateAround(_baseEnd, _start, _rotation) + _offset;
                break;

            case ArrowAnnotation arrow:
                arrow.Start = _start + _offset;
                arrow.End = RotateAround(_baseEnd, _start, _rotation) + _offset;
                break;

            case RectAnnotation rect:
                rect.Rect = PlaceRect(out var rectRotation);
                rect.Rotation = rectRotation;
                if (_round >= 0)
                {
                    rect.CornerRadius = _round * Math.Min(rect.Rect.Width, rect.Rect.Height) / 2;
                }

                break;

            case EllipseAnnotation ellipse:
                ellipse.Rect = PlaceRect(out var ellipseRotation);
                ellipse.Rotation = ellipseRotation;
                break;

            case MosaicAnnotation mosaic:
                mosaic.Rect = PlaceRect(out var mosaicRotation);
                mosaic.Rotation = mosaicRotation;
                break;

            case NumberAnnotation number:
                // The badge stays where it was pressed; dragging on draws an arrow out of it to the pointer.
                number.Center = _start + _offset;
                number.Tail = (LastPointer - number.Center).Length > number.Radius + 6 ? LastPointer : null;
                break;
        }
    }

    /// <summary>
    /// Elements store their rotation about their own centre. A box rotated about
    /// the start point equals the same box rotated about its centre and shifted
    /// by R·(C − S) + S − C, so the start point stays exactly under the cursor
    /// where the drag began.
    /// </summary>
    private Rect PlaceRect(out double rotation)
    {
        var box = new Rect(_start, _baseEnd);
        rotation = _rotation;
        var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var placed = RotateAround(center, _start, _rotation) + _offset;
        return new Rect(placed.X - box.Width / 2, placed.Y - box.Height / 2, box.Width, box.Height);
    }

    /// <summary>Shift: square / circle for boxes, 45° steps for lines (length kept).</summary>
    private Point Constrain(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Target is RectAnnotation or EllipseAnnotation or MosaicAnnotation)
        {
            var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(start.X + Math.Sign(dx == 0 ? 1 : dx) * size, start.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6)
        {
            return end;
        }

        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
    }

    private static double Angle(Vector v) => Math.Atan2(v.Y, v.X) * 180 / Math.PI;

    private static Vector RotateVector(Vector v, double degrees)
    {
        if (Math.Abs(degrees) < 0.001)
        {
            return v;
        }

        var r = degrees * Math.PI / 180;
        return new Vector(v.X * Math.Cos(r) - v.Y * Math.Sin(r), v.X * Math.Sin(r) + v.Y * Math.Cos(r));
    }

    private static Point RotateAround(Point point, Point pivot, double degrees) =>
        pivot + RotateVector(point - pivot, degrees);
}
