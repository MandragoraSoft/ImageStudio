using System.Globalization;
using System.Windows.Data;

namespace ImageStudio.Localization;

// Zamiast TargetNullValue (które nie przyjmuje powiązania): pusta wartość daje tekst ze słownika, klucz w ConverterParameter
public sealed class NullToLoc : IValueConverter
{
    public static readonly NullToLoc Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Loc.T((string)parameter!) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
