using ImageStudio.Services;
using ImageStudio.Localization;

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
            if (Pricing is not { } p) return Loc.T("model.cost.none");

            var unit = p.Unit == ModelPricing.TokenUnit ? Loc.T("model.cost.perTokens") : Loc.T("model.cost.perUnit", p.Unit ?? Loc.T("model.cost.unitDefault"));
            var lines = p.OutputPrice is { } output
                ? new List<string> { Loc.T("model.cost.in", p.PriceNow, unit), Loc.T("model.cost.out", output, unit) }
                : new List<string> { Loc.T("model.cost.now", p.PriceNow, unit) };
            if (p.CachePrice is { } cache) lines.Add(Loc.T("model.cost.cache", cache, unit));
            if (p.ListUsd is { } list) lines.Add(Loc.T("model.cost.list", list));
            if (p.DiscountPct is > 0 and var discount) lines.Add(Loc.T("model.cost.discount", discount));
            if (p.MarketOffers is { } offers) lines.Add(Loc.T("model.cost.offers", offers));
            if (p.Requests24h is { } requests) lines.Add(Loc.T("model.cost.requests", requests));
            return string.Join(Environment.NewLine, lines);
        }
    }

    // Cena wyrównywana do prawej w dropdownie (szablon w App.xaml)
    public string PriceText => Pricing switch
    {
        null => "—",
        { OutputPrice: { } output } p => $"${p.PriceNow:0.###}/${output:0.###} za 1M",  // tekst: wejście/wyjście
        var p => $"${p.PriceNow:0.####}/{p.Unit ?? "szt."}",
    };
}
