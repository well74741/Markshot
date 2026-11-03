using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Pin window: the toolbar window that follows the pin.
public partial class PinWindow
{
    /// <summary>Screen rectangle of the image in physical pixels.</summary>
    private Native.RECT ImageScreenRect()
    {
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var window);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var inset = (int)Math.Round(Pad * dpi);
        return new Native.RECT
        {
            Left = window.Left + inset,
            Top = window.Top + inset,
            Right = window.Right - inset,
            Bottom = window.Bottom - inset
        };
    }

    /// <summary>
    /// Puts the toolbar window under the image (above it when there is no room below), or
    /// where the user dragged it relative to the image.
    /// </summary>
    private void PlaceToolbar()
    {
        if (!_editMode || _toolbarWindow is not { IsVisible: true } host || host.Handle == IntPtr.Zero)
        {
            return;
        }

        var image = ImageScreenRect();
        var bar = host.PixelBounds;
        int x, y;
        if (_toolbarOffset is { } offset)
        {
            x = image.Left + (int)offset.X;
            y = image.Top + (int)offset.Y;
        }
        else
        {
            var gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
            x = image.Left;
            y = image.Bottom + gap;
            var screenBottom = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (y + bar.Height > screenBottom)
            {
                y = image.Top - gap - bar.Height;
            }
        }

        _placingToolbar = true;
        host.MoveTo(x, y);
        _placingToolbar = false;
    }

    private void ShowToolbarWindow()
    {
        if (IsEditor)
        {
            return; // the toolbar is part of the editor window
        }

        if (_toolbarWindow == null)
        {
            _toolbarWindow = new ToolbarHostWindow(Toolbar) { Owner = this };
            // Should it ever get the keys itself, they still belong to the pin.
            _toolbarWindow.PreviewKeyDown += (_, e) => OnPreviewKeyDown(this, e);
            Toolbar.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(ReclaimKeyboard), DispatcherPriority.Input);
            _toolbarWindow.LocationChanged += (_, _) =>
            {
                // Dragged by its handle: remember where, relative to the image.
                if (_placingToolbar || !_editMode || !_toolbarWindow.IsVisible)
                {
                    return;
                }

                var image = ImageScreenRect();
                var bar = _toolbarWindow.PixelBounds;
                _toolbarOffset = new Point(bar.Left - image.Left, bar.Top - image.Top);
            };
        }

        _toolbarWindow.Show();
        PlaceToolbar();
    }
}
