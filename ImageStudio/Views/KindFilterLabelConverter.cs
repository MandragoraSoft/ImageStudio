using System.Globalization;
using System.Windows.Data;
using ImageStudio.Localization;

namespace ImageStudio.Views;

// Wartość filtra rodzaju zestawów ("all", "generate", "edit") na tekst z tłumaczeń (ComboBox z tekstami, nie obiektami)
public sealed class KindFilterLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "generate" => Loc.T("presets.kind.generate"),
        "edit" => Loc.T("presets.kind.edit"),
        _ => Loc.T("presets.kind.all"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
