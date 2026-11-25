using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PPlusTracklistEditor.App.Services;

/// <summary>How the app picks light or dark.</summary>
public enum ThemeChoice { FollowWindows, Light, Dark }

/// <summary>
/// Applies the Lagoon colors. WPF's Fluent theme supplies the shapes and the light/dark text colors; this copies the
/// Lagoon light or dark set (accent shades, window color, and the app's own playback / attention colors) over it, and
/// switches when the choice changes or, for "follow Windows", when Windows switches.
/// Views that draw themselves (the waveform, the timeline) listen to <see cref="Changed"/> to repaint.
/// </summary>
public static class ThemeService
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ThemeChoice _choice = ThemeChoice.FollowWindows;

    public static ThemeChoice Choice => _choice;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    /// <summary>Call once at startup, before any window is created.</summary>
    public static void Initialize(ThemeChoice choice = ThemeChoice.FollowWindows)
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            // Windows says something changed (maybe light/dark). Let WPF update its own theme first, then re-apply ours.
            if (_choice == ThemeChoice.FollowWindows)
                Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => Apply(_choice));
        };
        Apply(choice);
    }

    public static void Apply(ThemeChoice choice)
    {
        if (Application.Current is not { } app) return;
        _choice = choice;
        bool dark = choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => WindowsPrefersDark() };

        var mode = choice switch { ThemeChoice.Dark => ThemeMode.Dark, ThemeChoice.Light => ThemeMode.Light, _ => ThemeMode.System };
        if (app.ThemeMode != mode) app.ThemeMode = mode; // swaps the Fluent theme; our colors go on top afterwards

        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{typeof(ThemeService).Assembly.GetName().Name};component/Themes/Lagoon.{(dark ? "Dark" : "Light")}.xaml"),
        };
        foreach (object key in colors.Keys) app.Resources[key] = colors[key];
        OverrideAccentBrushes(app, colors, dark);

        bool changed = IsDark != dark;
        IsDark = dark;
        if (changed || choice != ThemeChoice.FollowWindows || Changed is not null) Changed?.Invoke();
    }

    /// <summary>
    /// The Fluent theme builds its accent brushes (selected rows, sliders, primary buttons...) from the accent color Windows
    /// supplies, fixed when the theme loads, and its controls hold those very brush objects. So replacing color resources
    /// doesn't reach them; instead the listed brushes (see <see cref="AccentBrushTable"/>) are recolored in place with the
    /// Lagoon shade they used to take from Windows.
    /// </summary>
    private static void OverrideAccentBrushes(Application app, ResourceDictionary lagoon, bool dark)
    {
        foreach (var (key, colorKey) in dark ? AccentBrushTable.Dark : AccentBrushTable.Light)
        {
            if (lagoon[colorKey] is not Color color) continue;
            foreach (var dictionary in Flatten(app.Resources.MergedDictionaries))
                if (dictionary.Contains(key) && dictionary[key] is SolidColorBrush { IsFrozen: false } brush)
                {
                    brush.Color = color;
                    break;
                }
        }
    }

    private static IEnumerable<ResourceDictionary> Flatten(IEnumerable<ResourceDictionary> dictionaries) =>
        dictionaries.SelectMany(d => Flatten(d.MergedDictionaries).Prepend(d));
    private static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ---- Window caption (the strip with the title and the minimize/close buttons, drawn by Windows) ----

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private const int DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;

    /// <summary>
    /// Paints a window's caption in the title bar green with white text, so the green band at the top of the main window
    /// reaches the top edge. Follows light/dark changes. Windows 11 only; older Windows keeps its normal caption.
    /// </summary>
    public static void UseGreenCaption(Window window)
    {
        void Paint()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            Set(hwnd, DwmCaptionColor, window.TryFindResource("LagoonTitleBarColor") as Color?);
            Set(hwnd, DwmBorderColor, window.TryFindResource("LagoonTitleBarColor") as Color?);
            Set(hwnd, DwmTextColor, window.TryFindResource("LagoonOnTitleBarColor") as Color?);
        }
        void OnChanged() => window.Dispatcher.BeginInvoke(Paint);
        window.SourceInitialized += (_, _) => Paint();
        Changed += OnChanged;
        window.Closed += (_, _) => Changed -= OnChanged;
    }

    private static void Set(IntPtr hwnd, int attribute, Color? color)
    {
        if (color is not { } c) return;
        int colorRef = c.R | (c.G << 8) | (c.B << 16); // Windows wants 0x00BBGGRR
        _ = DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int)); // fails harmlessly before Windows 11
    }

    /// <summary>A brush from the current theme, or <paramref name="fallback"/> if it isn't there (e.g. in a designer).</summary>
    public static Brush Brush(FrameworkElement element, string key, Brush fallback) =>
        element.TryFindResource(key) as Brush ?? fallback;
}
