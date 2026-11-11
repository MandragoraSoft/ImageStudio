using System.Globalization;

namespace ImageStudio.Services;

// Proporcje obrazu wyjścia: parsowanie etykiet "W:H", etykieta dla wymiarów i zaokrąglanie do wielokrotności 8
public static class ImageAspect
{
    // Tolerancja dopasowania wymiarów do predefiniowanej proporcji
    private const double Tolerance = 0.02;

    // W/H z etykiety; null, gdy tekst nie ma postaci "W:H" albo wysokość nie jest dodatnia
    public static double? Ratio(string? label)
    {
        var parts = label?.Split(':');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || h <= 0)
            return null;

        return w / h;
    }

    // Etykieta predefiniowanej proporcji pasującej do wymiarów; null = dowolna (wysokość 0 też)
    public static string? LabelFor(int width, int height)
    {
        if (height == 0) return null;
        var ratio = width / (double)height;
        return StudioDefaults.ImageAspects.FirstOrDefault(label => Math.Abs(ratio - Ratio(label)!.Value) < Tolerance);
    }

    // Wielokrotność 8, minimum 8
    public static int Snap(double value) => Math.Max(8, (int)Math.Round(value / 8.0) * 8);
}
