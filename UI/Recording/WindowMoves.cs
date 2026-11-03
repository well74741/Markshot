// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;

namespace Markshot.UI.Recording;

/// <summary>Where a recorder window should go, in physical pixels.</summary>
internal readonly record struct WindowMove(IntPtr Handle, int X, int Y, int Width, int Height, bool Resize);

/// <summary>
/// Moves the recorder's windows together in one step (DeferWindowPos): moved one after another,
/// the screen could show the frame already moved and the toolbar not yet — the toolbar flickered
/// while the area was dragged. The stacking order is left alone.
/// </summary>
internal static class WindowMoves
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void Apply(IReadOnlyList<WindowMove> moves)
    {
        if (moves.Count == 0)
        {
            return;
        }

        var batch = BeginDeferWindowPos(moves.Count);
        foreach (var m in moves)
        {
            if (batch == IntPtr.Zero)
            {
                break;
            }

            batch = DeferWindowPos(batch, m.Handle, IntPtr.Zero, m.X, m.Y, m.Width, m.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | (m.Resize ? 0 : SWP_NOSIZE));
        }

        if (batch != IntPtr.Zero)
        {
            EndDeferWindowPos(batch);
            return;
        }

        // Could not batch (e.g. a window just closed): one by one.
        foreach (var m in moves)
        {
            SetWindowPos(m.Handle, IntPtr.Zero, m.X, m.Y, m.Width, m.Height, SWP_NOZORDER | SWP_NOACTIVATE | (m.Resize ? 0 : SWP_NOSIZE));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int count);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EndDeferWindowPos(IntPtr info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
