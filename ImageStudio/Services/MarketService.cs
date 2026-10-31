using System.Net.Http;
using System.Text.Json;

namespace ImageStudio.Services;

public sealed record ModelPricing(
    decimal PriceNow,
    decimal? ListUsd,
    decimal? DiscountPct,
    string? Unit,
    decimal? MarketOffers,
    decimal? Requests24h);

// Publiczny rynek cen: bez klucza API, bo to inny host niż api.uberllm.dev
public sealed class MarketService : IDisposable
{
    private HttpClient _http;

    public MarketService(string marketUrl)
    {
        _http = CreateClient(marketUrl);
    }

    // Po zmianie adresu rynku w ustawieniach
    public void Reconfigure(string marketUrl)
    {
        _http.Dispose();
        _http = CreateClient(marketUrl);
    }

    private static HttpClient CreateClient(string marketUrl) => new()
    {
        BaseAddress = new Uri(marketUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Tylko modele z cenami za media (obrazy, wideo); klucz to id modelu
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetMediaPricingAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/api/v1/public/market/live", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        var result = new Dictionary<string, ModelPricing>();

        foreach (var property in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var m = property.Value;
            if (!m.TryGetProperty("media_price_now_usd", out var price) || price.ValueKind != JsonValueKind.Number)
                continue;

            result[property.Name] = new ModelPricing(
                PriceNow: price.GetDecimal(),
                ListUsd: Number(m, "media_list_usd"),
                DiscountPct: Number(m, "source_discount_pct"),
                Unit: m.TryGetProperty("media_unit", out var unit) && unit.ValueKind == JsonValueKind.String
                    ? unit.GetString()
                    : null,
                MarketOffers: Number(m, "market_offers"),
                Requests24h: Number(m, "requests_24h"));
        }

        return result;
    }

    public void Dispose() => _http.Dispose();

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;
}
