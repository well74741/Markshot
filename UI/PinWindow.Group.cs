namespace Markshot.UI;

// Pin window: several pins picked together (Shift+click adds, Ctrl+click takes one out);
// they move, nudge and close together.
public partial class PinWindow
{
    private static readonly List<PinWindow> Group = new();
    private static PinWindow? _lastPressed;
    private readonly Dictionary<PinWindow, (int Left, int Top)> _groupDragStart = new();
    private bool _plainPressOnGroup;

    /// <summary>The pins picked together (empty when only single pins are used).</summary>
    public static IReadOnlyList<PinWindow> SelectedPins => Group;

    public bool InGroup => Group.Contains(this);

    /// <summary>
    /// A press on the pin (not annotating): Shift adds it to the picked pins (with the pin used just
    /// before, so click A then Shift+click B picks both), Ctrl takes it out, a plain press on a pin
    /// outside the group ends the group. True = the press only changed the group (no drag).
    /// </summary>
    private bool GroupPress(ModifierKeys modifiers)
    {
        var previous = _lastPressed;
        _lastPressed = this;
        _plainPressOnGroup = false;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (Group.Remove(this))
            {
                if (Group.Count == 1)
                {
                    Group.Clear(); // one pin left is no group
                }

                RefreshGroupMarks();
            }

            return true;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            if (Group.Count == 0 && previous != null && previous != this && previous.IsVisible && !previous._parked)
            {
                Group.Add(previous);
            }

            if (!Group.Contains(this))
            {
                Group.Add(this);
            }

            RefreshGroupMarks();
            return false; // Shift+drag moves the group (straight, as Shift does for one pin)
        }

        if (Group.Contains(this))
        {
            _plainPressOnGroup = true; // a drag moves them all; a click without moving picks just this one
        }
        else
        {
            ClearPinGroup();
        }

        return false;
    }

    /// <summary>The drag of this pin starts: the other picked pins remember where they were.</summary>
    private void BeginGroupDrag()
    {
        _groupDragStart.Clear();
        if (!Group.Contains(this))
        {
            return;
        }

        foreach (var pin in Group.Where(pin => pin != this && !pin._locked))
        {
            _groupDragStart[pin] = (pin._left, pin._top);
        }
    }

    private void MoveGroup(int dx, int dy)
    {
        foreach (var (pin, start) in _groupDragStart)
        {
            if (pin.IsLoaded)
            {
                pin._left = start.Left + dx;
                pin._top = start.Top + dy;
                pin.ApplyBounds();
            }
        }
    }

    /// <summary>The drag ended: a plain click (no move) on a picked pin leaves just that pin.</summary>
    private void EndGroupDrag(bool moved)
    {
        _groupDragStart.Clear();
        if (_plainPressOnGroup && !moved)
        {
            ClearPinGroup();
        }

        _plainPressOnGroup = false;
    }

    /// <summary>This pin and, when it is picked with others, the others too.</summary>
    private IEnumerable<PinWindow> WithGroup() => Group.Contains(this) ? Group.ToList() : new List<PinWindow> { this };

    private void CloseWithGroup()
    {
        foreach (var pin in WithGroup())
        {
            pin.Close();
        }
    }

    private void NudgeWithGroup(int dx, int dy)
    {
        foreach (var pin in WithGroup().Where(pin => !pin._locked))
        {
            pin._left += dx;
            pin._top += dy;
            pin.ApplyBounds();
        }
    }

    public static void ClearPinGroup()
    {
        if (Group.Count == 0)
        {
            return;
        }

        Group.Clear();
        RefreshGroupMarks();
    }

    private void LeaveGroup()
    {
        if (_lastPressed == this)
        {
            _lastPressed = null;
        }

        if (Group.Remove(this))
        {
            if (Group.Count == 1)
            {
                Group.Clear();
            }

            RefreshGroupMarks();
        }
    }

    /// <summary>Picked pins show a dashed accent outline.</summary>
    private static void RefreshGroupMarks()
    {
        foreach (var window in Application.Current.Windows.OfType<PinWindow>())
        {
            window.GroupMark.Visibility = Visibility.Collapsed; // picked pins show no dashed edge (they move together all the same)
            window.GroupMark.RadiusX = window.GroupMark.RadiusY = window.CornerRadius * window._zoom;
        }
    }
}
