using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Model z listy wraz z atrybutami (do filtrów) i ceną z rynku (Pricing może być null)
public sealed record ModelOption(
    string Id,
    string? OwnedBy,
    bool Nsfw,
    IReadOnlyList<string> InputModalities,
    ModelPricing? Pricing,
    string? Description = null,
    IReadOnlyList<string>? OutputModalities = null,
    bool Reasoning = false)
{
    // Ikony możliwości w szablonie (App.xaml, ModelIconsTemplate)
    public bool AcceptsFile => InputModalities.Contains("file");
    public bool AcceptsAudio => InputModalities.Contains("audio");
    public bool AcceptsVideo => InputModalities.Contains("video");
    public bool ReturnsText => OutputModalities?.Contains("text") == true;

    // Atrybut zgodny z filtrem "Przyjmuje obraz" i ikoną w szablonie
    public bool AcceptsImage => InputModalities.Contains("image");

    // Koszty w dymku nad pozycją modelu (podświetlenie myszą): cena bieżąca, katalogowa, rabat i dane rynku
    public string CostText
    {
        get
        {
            if (Pricing is not { } p) return "Brak ceny w katalogu (dostawca podaje ceny tylko dla części modeli).";

            var unit = p.Unit == ModelPricing.TokenUnit ? "za 1M tokenów" : $"za {p.Unit ?? "szt."}";
            var lines = new List<string> { $"Cena teraz: ${p.PriceNow:0.####} {unit}" };
            if (p.ListUsd is { } list) lines.Add($"Cena katalogowa: ${list:0.####}");
            if (p.DiscountPct is > 0 and var discount) lines.Add($"Rabat: {discount:0.#}%");
            if (p.MarketOffers is { } offers) lines.Add($"Oferty na rynku: {offers:0}");
            if (p.Requests24h is { } requests) lines.Add($"Zapytania (24 h): {requests:0}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    // Cena wyrównywana do prawej w dropdownie (szablon w App.xaml)
    public string PriceText => Pricing is null
        ? "—"
        : $"${Pricing.PriceNow:0.####}/{Pricing.Unit ?? "szt."}";
}
