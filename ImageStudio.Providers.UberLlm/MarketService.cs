using System.Net.Http;
using System.Text.Json;

namespace ImageStudio.Services;

// Publiczny rynek cen: bez klucza API, bo to inny host niż api.uberllm.dev
public sealed class MarketService : IDisposable
{
    private HttpClient _http;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public MarketService(string marketUrl) : this(marketUrl, null) { }

    public MarketService(string marketUrl, Func<HttpMessageHandler>? handlerFactory)
    {
        _handlerFactory = handlerFactory;
        _http = CreateClient(marketUrl);
    }

    // Po zmianie adresu rynku w ustawieniach
    public void Reconfigure(string marketUrl)
    {
        _http.Dispose();
        _http = CreateClient(marketUrl);
    }

    private HttpClient CreateClient(string marketUrl) => new(_handlerFactory?.Invoke() ?? new HttpClientHandler())
    {
        BaseAddress = new Uri(marketUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Ceny z rynku: media (obrazy, wideo: media_price_now_usd) i tekst (price_now: wejście/wyjście za 1M tokenów); klucz to id modelu
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
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
            {
                if (TokenPricing(m) is { } tokens) result[property.Name] = tokens;
                continue;
            }

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

    // Modele tekstowe: price_now to obiekt z cenami za milion tokenów (wejście, wyjście, odczyt z cache)
    private static ModelPricing? TokenPricing(JsonElement m)
    {
        if (!m.TryGetProperty("price_now", out var now) || now.ValueKind != JsonValueKind.Object) return null;
        if (Number(now, "input_usd_per_mtok") is not { } input) return null;

        return new ModelPricing(input, null, Number(m, "source_discount_pct"), ModelPricing.TokenUnit,
                                Number(m, "market_offers"), Number(m, "requests_24h"),
                                Number(now, "output_usd_per_mtok"), Number(now, "cache_read_usd_per_mtok"));
    }

    public void Dispose() => _http.Dispose();

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;
}
