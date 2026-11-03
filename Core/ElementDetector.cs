using System.Windows.Automation;

namespace Markshot.Core;

/// <summary>An element under the cursor: its screen rectangle (physical pixels) and a short label.</summary>
public sealed record DetectedElement(Rect Bounds, string Label);

/// <summary>
/// UI elements under a point (buttons, text, pictures, tables, panes …) through UI Automation,
/// like PixPin's element capture. Walks down from the window to the smallest element that
/// contains the point; each level is one entry, smallest first. Runs off the UI thread
/// (UI Automation talks to the other process and may take a moment).
/// </summary>
public static class ElementDetector
{
    private const int MaxDepth = 40;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    public static List<DetectedElement> Detect(IntPtr window, int x, int y, CancellationToken cancel = default)
    {
        var chain = new List<DetectedElement>();
        if (window == IntPtr.Zero)
        {
            return chain;
        }

        var started = Stopwatch.StartNew();
        try
        {
            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.LocalizedControlTypeProperty);
            using (cache.Activate())
            {
                var current = AutomationElement.FromHandle(window);
                var point = new Point(x, y);
                for (var depth = 0; depth < MaxDepth && current != null; depth++)
                {
                    if (cancel.IsCancellationRequested || started.Elapsed > Budget)
                    {
                        break;
                    }

                    if (depth > 0 && Entry(current) is { } entry)
                    {
                        chain.Add(entry);
                    }

                    current = ChildAt(current, point);
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // The window went away or does not answer: whatever was found so far.
        }

        chain.Reverse(); // smallest first
        // Levels with the same rectangle are one step for the user.
        var distinct = new List<DetectedElement>();
        foreach (var element in chain)
        {
            if (!distinct.Any(seen => Same(seen.Bounds, element.Bounds)))
            {
                distinct.Add(element);
            }
        }

        return distinct;
    }

    /// <summary>The smallest visible child containing the point (siblings may overlap).</summary>
    private static AutomationElement? ChildAt(AutomationElement parent, Point point)
    {
        AutomationElement? best = null;
        var bestArea = double.MaxValue;
        var children = parent.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            var bounds = Bounds(child);
            if (bounds.IsEmpty || !bounds.Contains(point) || Offscreen(child))
            {
                continue;
            }

            var area = bounds.Width * bounds.Height;
            if (area < bestArea)
            {
                best = child;
                bestArea = area;
            }
        }

        return best;
    }

    private static DetectedElement? Entry(AutomationElement element)
    {
        var bounds = Bounds(element);
        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4)
        {
            return null;
        }

        string type = "", name = "";
        try
        {
            type = (element.GetCachedPropertyValue(AutomationElement.LocalizedControlTypeProperty) as string ?? "").Trim();
            name = (element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "").Trim();
        }
        catch (InvalidOperationException)
        {
        }

        name = name.ReplaceLineEndings(" ");
        if (name.Length > 24)
        {
            name = name[..24] + "…";
        }

        var label = (type, name) switch
        {
            ("", "") => "元素",
            ("", _) => name,
            (_, "") => type,
            _ => type + " · " + name
        };
        return new DetectedElement(bounds, label);
    }

    private static Rect Bounds(AutomationElement element)
    {
        try
        {
            var value = element.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
            return value is Rect rect && !rect.IsEmpty && !double.IsInfinity(rect.Width) && !double.IsInfinity(rect.Height)
                ? rect
                : Rect.Empty;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static bool Offscreen(AutomationElement element)
    {
        try
        {
            return element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty) is true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool Same(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 && Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;
}
