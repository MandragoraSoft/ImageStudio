using System.Windows.Data;
using System.Windows.Markup;

namespace ImageStudio.Localization;

// {loc:T klucz}: tekst z słownika, który zmienia się po przełączeniu języka (powiązanie z LocalizationSource)
public sealed class TExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
