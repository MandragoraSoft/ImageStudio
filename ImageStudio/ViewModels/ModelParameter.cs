using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.ViewModels;

// Opis parametru modelu czatu: klucz zgodny z API, zakres, wartość domyślna
public sealed record ParamSpec(string Key, string Label, double Min, double Max, double Default, bool IsInteger, string Hint);

// Parametr widoczny w UI. Wysyłany do API tylko gdy Enabled (domyślne wartości serwera zostają nietknięte).
public sealed partial class ModelParameter : ObservableObject
{
    // Katalog parametrów tekstowych; widoczne są tylko te, które model deklaruje w supported_parameters
    public static IReadOnlyList<ParamSpec> Catalog { get; } =
    [
        new("temperature", "Temperatura", 0, 2, 1.0, false, "Losowość odpowiedzi: niżej = bardziej przewidywalnie"),
        new("top_p", "Top-p", 0, 1, 1.0, false, "Nucleus sampling: ogranicza do najbardziej prawdopodobnych tokenów"),
        new("top_k", "Top-k", 0, 100, 40, true, "Liczba najbardziej prawdopodobnych tokenów do wyboru"),
        new("max_tokens", "Maks. tokenów", 16, 4096, 400, true, "Limit długości odpowiedzi"),
        new("presence_penalty", "Kara za obecność", -2, 2, 0, false, "Dodatnie wartości zachęcają do nowych tematów"),
        new("frequency_penalty", "Kara za częstość", -2, 2, 0, false, "Dodatnie wartości ograniczają powtórzenia"),
        new("seed", "Seed", 0, 99999, 0, true, "Stały seed daje powtarzalne wyniki"),
    ];

    public ParamSpec Spec { get; }

    public string Key => Spec.Key;
    public string Label => Spec.Label;
    public string Hint => Spec.Hint;
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
