using System.Globalization;
using System.Text.Json;

namespace ImageStudio.Services;

// Kilo Gateway (kilo.ai): jedno API w formacie OpenAI do setek modeli różnych dostawców. Katalog /models jest publiczny
// i niesie modalności, obsługiwane parametry oraz ceny (USD za token jako tekst, "-1" = zmienna cena routera). Tylko tekst
// (modele zwracające obraz albo dźwięk też mają wyjście tekstowe, więc trafiają na listę czatu)
public sealed class KiloProvider : OpenAiChatProvider
{
    public const string Identifier = "kilo";

    // Rotuje darmowe modele i nie wymaga kredytów (opis w katalogu)
    public const string DefaultTextModelId = "kilo-auto/free";

    private const string ModelsPath = "models";

    public KiloProvider(string apiKey) : base("https://ai-gateway.kilo.ai/api/v1/", apiKey, "chat/completions")
    {
    }

    public override string Id => Identifier;
    public override string DisplayName => "Kilo";
    public override string? DefaultTextModel => DefaultTextModelId;

    public override async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: false, ct));
        return ParseChatModels(doc.RootElement);
    }

    public static IReadOnlyList<ChatModelInfo> ParseChatModels(JsonElement root) =>
        ModelEntries(root).Where(m => Modalities(m, "output_modalities").Contains("text"))
            .Select(ToInfo).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public override async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: true, ct));
        return ParsePricing(doc.RootElement);
    }

    // pricing.prompt / completion / input_cache_read to USD za token: przeliczamy na 1M tokenów; modele bez ceny ("-1") pomijamy
    public static IReadOnlyDictionary<string, ModelPricing> ParsePricing(JsonElement root)
    {
        static decimal? PerMillion(JsonElement p, string name) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var usd) && usd >= 0
                ? usd * 1_000_000m
                : null;

        var prices = new Dictionary<string, ModelPricing>();
        foreach (var model in ModelEntries(root))
        {
            if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object
                || PerMillion(pricing, "prompt") is not { } input)
                continue;

            prices[model.GetProperty("id").GetString()!] =
                new ModelPricing(input, null, null, ModelPricing.TokenUnit, null, null,
                                 PerMillion(pricing, "completion"), PerMillion(pricing, "input_cache_read"));
        }

        return prices;
    }

    private static List<string> Modalities(JsonElement model, string field) =>
        model.TryGetProperty("architecture", out var arch) && arch.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(m => m.GetString()).OfType<string>().ToList()
            : [];

    private static ChatModelInfo ToInfo(JsonElement model)
    {
        var id = model.GetProperty("id").GetString()!;

        // Katalog ma osobno "file" i "pdf"; aplikacja zna tylko "file"
        var input = Modalities(model, "input_modalities").Select(m => m == "pdf" ? "file" : m).Distinct().ToList();
        if (input.Count == 0) input.Add("text");

        var supported = model.TryGetProperty("supported_parameters", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(p => p.GetString()).OfType<string>().ToList()
            : [];
        var description = model.TryGetProperty("description", out var d) ? d.GetString() : null;
        return new ChatModelInfo(id, supported, input.Contains("image"), id.Split('/')[0], input, description, OpenAiCompat.ReadContextLength(model));
    }
}
