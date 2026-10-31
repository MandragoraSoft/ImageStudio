using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Model z listy wraz z atrybutami (do filtrów) i ceną z rynku (Pricing może być null)
public sealed record ModelOption(
    string Id,
    string? OwnedBy,
    bool Nsfw,
    IReadOnlyList<string> InputModalities,
    ModelPricing? Pricing)
{
    // Cena wyrównywana do prawej w dropdownie (szablon w App.xaml)
    public string PriceText => Pricing is null
        ? "—"
        : $"${Pricing.PriceNow:0.####}/{Pricing.Unit ?? "szt."}";
}
