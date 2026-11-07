using Microsoft.Win32;

namespace Markshot.Core;

/// <summary>Colour theme of the interface: dark (default), light, or following Windows.</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>
/// Interface colours that switch at run time. The XAML refers to them as
/// <c>{DynamicResource T_AARRGGBB}</c> — the key is the dark-theme colour — and the light-theme
/// colour is derived from it: white overlays become black overlays, light text becomes dark text,
/// dark backgrounds become light ones (the order of the greys is kept, so hierarchy stays the same).
/// Colours with a hue (accent, red, green…) are not themed.
/// </summary>
public static partial class Theme
{
    private static readonly HashSet<string> Keys = new();
    private static bool _listening;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>The colours in use are the light ones (light, or System on a light Windows).</summary>
    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    /// <summary>The named brushes of App.xaml and their dark colours.</summary>
    private static readonly (object Key, uint Dark)[] Named =
    {
        ("PanelBackground", 0xF21C1C1C),
        ("PanelBorder", 0x33FFFFFF),
        ("TextPrimary", 0xFFF5F5F5),
        ("TextSecondary", 0xFF9A9A9A),
        ("Hover", 0x26FFFFFF),
        ("InputBackground", 0xFF262626),
        (SystemColors.MenuBrushKey, 0xFF1E1E1E),
        (SystemColors.MenuTextBrushKey, 0xFFF0F0F0),
        (SystemColors.ControlBrushKey, 0xFF1E1E1E)
    };

    public static void Apply(string? mode) =>
        Apply(Enum.TryParse<ThemeMode>(mode, true, out var parsed) ? parsed : ThemeMode.Dark);

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        IsLight = mode == ThemeMode.Light || (mode == ThemeMode.System && WindowsUsesLightTheme());
        if (mode == ThemeMode.System && !_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
                }
            };
        }

        var resources = Application.Current?.Resources;
        if (resources == null)
        {
            return;
        }

        foreach (var (key, dark) in Named)
        {
            resources[key] = Freeze(new SolidColorBrush(Color(dark)));
        }

        foreach (var key in GeneratedKeys)
        {
            Keys.Add(key);
        }

        foreach (var key in Keys)
        {
            resources[key] = Freeze(new SolidColorBrush(ColorOfKey(key)));
        }

        Changed?.Invoke();
    }

    /// <summary>Resource key of a dark-theme colour (registers it when new).</summary>
    public static string Key(uint dark)
    {
        var key = "T_" + dark.ToString("X8", CultureInfo.InvariantCulture);
        if (Keys.Add(key) && Application.Current?.Resources is { } resources)
        {
            resources[key] = Freeze(new SolidColorBrush(ColorOfKey(key)));
        }

        return key;
    }

    /// <summary>Binds a brush property to a themed colour (follows theme switches).</summary>
    public static void Bind(FrameworkElement element, DependencyProperty property, uint dark) =>
        element.SetResourceReference(property, Key(dark));

    /// <summary>The colour for the current theme, given its dark-theme value (0xAARRGGBB).</summary>
    public static Color Color(uint dark)
    {
        var c = System.Windows.Media.Color.FromArgb((byte)(dark >> 24), (byte)(dark >> 16), (byte)(dark >> 8), (byte)dark);
        return IsLight ? ToLight(c) : c;
    }

    public static SolidColorBrush Brush(uint dark) => Freeze(new SolidColorBrush(Color(dark)));

    private static Color ColorOfKey(string key) =>
        Color(uint.Parse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>Light-theme counterpart of a dark-theme interface colour.</summary>
    public static Color ToLight(Color c)
    {
        if (c.R != c.G || c.G != c.B)
        {
            return c; // has a hue: accent, status colours…
        }

        var g = c.R;
        if (g == 0xFF && c.A < 0xFF)
        {
            // White overlay (lines, hover plates) → black overlay, a little softer.
            return System.Windows.Media.Color.FromArgb((byte)Math.Round(c.A * 0.7), 0, 0, 0);
        }

        byte light;
        if (g >= 0x5A)
        {
            // Text and icons: light grey → dark grey (white → near black).
            light = (byte)Math.Round(0xA8 - (g - 0x5A) * (0xA8 - 0x1A) / (double)(0xFF - 0x5A));
        }
        else if (g >= 0x40)
        {
            light = (byte)(0x113 - g); // tracks, dim surfaces
        }
        else
        {
            light = (byte)Math.Clamp(Math.Round(0xFF - Math.Max(0, g - 0x10) * 0.62), 0xD8, 0xFF); // backgrounds
        }

        return System.Windows.Media.Color.FromArgb(c.A, light, light, light);
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Title bar of a normal window in the theme's colours (Windows 10 2004+ / 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = IsLight ? 0 : 1;
        Native.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    /// <summary>Keeps a window's title bar in step with theme switches.</summary>
    public static void TrackTitleBar(Window window)
    {
        void Update()
        {
            ApplyTitleBar(window);
            if (window.Icon != null)
            {
                window.Icon = Logo(); // the white logo is not readable on a light title bar: a dark one there
            }
        }

        window.SourceInitialized += (_, _) => Update();
        Changed += Update;
        window.Closed += (_, _) => Changed -= Update;
    }

    private static ImageSource? _logo;
    private static ImageSource? _logoDark;

    /// <summary>The logo for a title bar of the current theme (white on dark, dark on light).</summary>
    public static ImageSource Logo()
    {
        var name = IsLight ? "markshot_dark.ico" : "markshot.ico";
        ref var cached = ref IsLight ? ref _logoDark : ref _logo;
        return cached ??= BitmapFrame.Create(new Uri("pack://application:,,,/Markshot;component/Assets/" + name));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// Interface size: 小 (as designed), 中 (one step larger) or 大 (two steps). One shared transform is used as
/// the LayoutTransform of every panel / window content, so switching resizes them all live.
/// </summary>
public static class UiScale
{
    public const double Medium = 1.15;
    public const double Large = 1.3;

    /// <summary>The settings window always has this size (it does not grow or shrink with the setting).</summary>
    public const double SettingsFactor = Medium;

    /// <summary>The settings window's own transform (fixed at <see cref="SettingsFactor"/>).</summary>
    public static ScaleTransform SettingsTransform { get; } = Freeze(new ScaleTransform(SettingsFactor, SettingsFactor));

    private static ScaleTransform Freeze(ScaleTransform t)
    {
        t.Freeze();
        return t;
    }

    /// <summary>The factor of a stored size ("Small" / "Medium" / "Large").</summary>
    public static double FactorOf(string? size) =>
        string.Equals(size, "Large", StringComparison.OrdinalIgnoreCase) ? Large
        : string.Equals(size, "Medium", StringComparison.OrdinalIgnoreCase) ? Medium
        : 1.0;

    /// <summary>The shared transform (not frozen: changing it updates every user).</summary>
    public static ScaleTransform Transform { get; } = new(1, 1);

    public static double Factor => Transform.ScaleX;

    public static event Action? Changed;

    public static void Apply(string? size)
    {
        var factor = FactorOf(size);
        if (Math.Abs(Transform.ScaleX - factor) < 0.001)
        {
            return;
        }

        Transform.ScaleX = factor;
        Transform.ScaleY = factor;
        Changed?.Invoke();
    }

    /// <summary>Scales an element's layout (its size, text and spacing) with the interface size.</summary>
    public static void Attach(FrameworkElement element) => element.LayoutTransform = Transform;

    /// <summary>
    /// A fixed-size window: its content is scaled and the window itself grows / shrinks with it
    /// (from its designed size).
    /// </summary>
    public static void AttachWindow(Window window)
    {
        if (window is UI.SettingsWindow)
        {
            // The settings keep one size whatever the interface size (switching it there must not jump the window).
            if (window.Content is FrameworkElement fixedContent)
            {
                fixedContent.LayoutTransform = SettingsTransform;
            }

            if (!double.IsNaN(window.Width)) window.Width *= SettingsFactor;
            if (!double.IsNaN(window.Height) && window.SizeToContent is SizeToContent.Manual or SizeToContent.Width) window.Height *= SettingsFactor;
            window.MinWidth *= SettingsFactor;
            window.MinHeight *= SettingsFactor;
            return;
        }

        var width = window.Width;
        var height = window.Height;
        var minWidth = window.MinWidth;
        var minHeight = window.MinHeight;
        var applied = 1.0;

        void Resize()
        {
            var k = Factor;
            if (!double.IsNaN(width))
            {
                window.Width = window.ActualWidth > 0 ? window.Width / applied * k : width * k;
            }

            if (!double.IsNaN(height) && window.SizeToContent is SizeToContent.Manual or SizeToContent.Width)
            {
                window.Height = window.ActualHeight > 0 ? window.Height / applied * k : height * k;
            }

            window.MinWidth = minWidth * k;
            window.MinHeight = minHeight * k;
            applied = k;
        }

        if (window.Content is FrameworkElement content)
        {
            Attach(content);
        }

        Resize();
        Changed += Resize;
        window.Closed += (_, _) => Changed -= Resize;
    }

    /// <summary>Menus and tooltips (they live in their own popups) follow the size too.</summary>
    public static void HookPopups()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => ((FrameworkElement)sender).LayoutTransform = Transform));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) => ((FrameworkElement)sender).LayoutTransform = Transform));
    }
}
