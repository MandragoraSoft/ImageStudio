using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ImageStudio.Services;

// Motyw: zmienia kolory istniejących pędzli w App.xaml, więc całe UI przechodzi naraz
// Motyw za interfejsem: ViewModel nie woła statycznej klasy WPF (testy podstawiają atrapę)
public interface IThemeService
{
    void Apply(string theme);
}

public sealed class WpfThemeService : IThemeService
{
    public void Apply(string theme) => ThemeService.Apply(theme);
}

public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["WindowBgBrush"] = "#F4F6FA",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceAltBrush"] = "#F9FAFC",
        ["BorderSoftBrush"] = "#D5DAE3",
        ["TextPrimaryBrush"] = "#1F2430",
        ["TextMutedBrush"] = "#6B7280",
        ["AccentBrush"] = "#3B6FE0",
        ["AccentHoverBrush"] = "#2F5FCC",
        ["AccentSoftBrush"] = "#DCE6FB",
        ["AccentTextBrush"] = "#FFFFFF",
        ["DisabledBrush"] = "#C9CED9",
        ["SuccessBrush"] = "#2E8B57",
        ["WarningBrush"] = "#C2410C",
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["WindowBgBrush"] = "#1B1D22",
        ["SurfaceBrush"] = "#2A2C33",
        ["SurfaceAltBrush"] = "#24262C",
        ["BorderSoftBrush"] = "#3C404A",
        ["TextPrimaryBrush"] = "#E6E7EB",
        ["TextMutedBrush"] = "#9AA0AC",
        ["AccentBrush"] = "#5B8CFF",
        ["AccentHoverBrush"] = "#4A7AEE",
        ["AccentSoftBrush"] = "#33446B",
        ["AccentTextBrush"] = "#FFFFFF",
        ["DisabledBrush"] = "#3A3D46",
        ["SuccessBrush"] = "#4CC38A",
        ["WarningBrush"] = "#F59E42",
    };

    // Pasek tytułu okna rysuje system, więc nie zmienia go podmiana pędzli: osobno przełączamy tryb ciemny (DWM)
    private const int DwmUseImmersiveDarkMode = 20;
    private static bool _titleBarsHooked;
    private static bool _dark;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Pasek tytułu zgodny z bieżącym motywem; bez uchwytu (okno jeszcze nie pokazane) nic nie robi
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var value = _dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    public static void Apply(string theme)
    {
        var dark = theme switch
        {
            Dark => true,
            Light => false,
            _ => !IsWindowsLightTheme(),
        };

        _dark = dark;
        if (!_titleBarsHooked)
        {
            // Okna otwierane później dostają pasek tytułu w aktualnym motywie po załadowaniu
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                                              new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
            _titleBarsHooked = true;
        }
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);

        // Pędzle z XAML są zamrożone, więc podmieniamy wpis; odwołania DynamicResource odświeżają się same
        foreach (var (key, hex) in dark ? DarkPalette : LightPalette)
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }

    // Ustawienie systemowe Windows: AppsUseLightTheme = 1 oznacza jasny motyw aplikacji
    private static bool IsWindowsLightTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                          "AppsUseLightTheme", 1) is int value && value == 1;
}
