using ImageStudio.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.ViewModels;

// Opis parametru modelu czatu (Label i Hint to klucze tłumaczeń): klucz zgodny z API, zakres, wartość domyślna
public sealed record ParamSpec(string Key, string Label, double Min, double Max, double Default, bool IsInteger, string Hint);

// Parametr widoczny w UI. Wysyłany do API tylko gdy Enabled (domyślne wartości serwera zostają nietknięte).
public sealed partial class ModelParameter : ObservableObject
{
    // Katalog parametrów tekstowych; widoczne są tylko te, które model deklaruje w supported_parameters
    public static IReadOnlyList<ParamSpec> Catalog { get; } =
    [
        new("temperature", "param.temperature", 0, 2, 1.0, false, "param.temperature.tip"),
        new("top_p", "param.topp", 0, 1, 1.0, false, "param.topp.tip"),
        new("top_k", "param.topk", 0, 100, 40, true, "param.topk.tip"),
        new("max_tokens", "param.maxtokens", 16, 4096, 400, true, "param.maxtokens.tip"),
        new("presence_penalty", "param.presencepenalty", -2, 2, 0, false, "param.presencepenalty.tip"),
        new("frequency_penalty", "param.frequencypenalty", -2, 2, 0, false, "param.frequencypenalty.tip"),
        new("seed", "param.seed", 0, 99999, 0, true, "param.seed.tip"),
    ];

    public ParamSpec Spec { get; }

    public string Key => Spec.Key;
    public string Label => Loc.T(Spec.Label);
    public string Hint => Loc.T(Spec.Hint);
    public double Min => Spec.Min;
    public double Max => Spec.Max;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private double _value;

    [ObservableProperty]
    private bool _enabled;

    // Wywoływane przy każdej zmianie, żeby ViewModel zapisał stan
    public Action? Changed { get; set; }

    public ModelParameter(ParamSpec spec, bool enabled, double value)
    {
        Spec = spec;
        _enabled = enabled;
        _value = value;
    }

    public string Display => Spec.IsInteger ? Math.Round(Value).ToString("0") : Value.ToString("0.##");

    public object ToApiValue() => Spec.IsInteger ? (object)(int)Math.Round(Value) : Value;

    partial void OnValueChanged(double value) => Changed?.Invoke();

    partial void OnEnabledChanged(bool value) => Changed?.Invoke();
}
