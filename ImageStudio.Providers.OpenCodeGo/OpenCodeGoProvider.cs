using System.Text.Json;

namespace ImageStudio.Services;

// OpenCode Go (opencode.ai): abonament na modele programistyczne, czat przez /openai/v1/chat/completions.
// Katalog /v1/models jest publiczny i niesie modalności, możliwości i ceny za 1M tokenów (cost.text). Tylko tekst.
// API prosi o stały identyfikator rozmowy w nagłówku x-opencode-session (routing i cache promptów): jeden na dostawcę
public sealed class OpenCodeGoProvider : OpenAiChatProvider
{
    public const string Identifier = "opencodego";
    public const string DefaultTextModelId = "deepseek-v4.1-flash";

    private const string ModelsPath = "v1/models";

    public OpenCodeGoProvider(string apiKey) : this(apiKey, null) { }
    public OpenCodeGoProvider(string apiKey, System.Net.Http.HttpMessageHandler? handler) : base("https://opencode.ai/inference/go/", apiKey, "openai/v1/chat/completions", handler)
    {
        Http.DefaultRequestHeaders.Add("x-opencode-session", Guid.NewGuid().ToString("N"));
    }

    public override string Id => Identifier;
    public override string DisplayName => "OpenCode Go";
    public override string? DefaultTextModel => DefaultTextModelId;

    public override async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: false, ct));
        return ParseChatModels(doc.RootElement);
    }

    public static IReadOnlyList<ChatModelInfo> ParseChatModels(JsonElement root) =>
        ChatEntries(root).Select(ToInfo).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public override async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: true, ct));
        return ParsePricing(doc.RootElement);
    }

    private static IEnumerable<JsonElement> ChatEntries(JsonElement root) =>
        ModelEntries(root).Where(m => !m.TryGetProperty("type", out var type) || type.GetString() == "chat");

    // cost.text.input.price / output.price / input.cache.read: USD za 1M tokenów (mnożniki godzinowe pomijamy)
    public static IReadOnlyDictionary<string, ModelPricing> ParsePricing(JsonElement root)
    {
        var prices = new Dictionary<string, ModelPricing>();
        foreach (var model in ChatEntries(root))
        {
            if (!model.TryGetProperty("cost", out var cost) || !cost.TryGetProperty("text", out var text)
                || !text.TryGetProperty("input", out var input) || Number(input, "price") is not { } inputPrice)
                continue;

            var cache = input.TryGetProperty("cache", out var c) ? Number(c, "read") : null;
            var output = text.TryGetProperty("output", out var o) ? Number(o, "price") : null;
            prices[model.GetProperty("id").GetString()!] =
                new ModelPricing(inputPrice, null, null, ModelPricing.TokenUnit, null, null, output, cache);
        }

        return prices;
    }

    private static decimal? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static ChatModelInfo ToInfo(JsonElement model)
    {
        var id = model.GetProperty("id").GetString()!;

        // Modalności jako typy MIME ("image/png", "application/pdf") -> nazwy używane przez aplikację
        var input = model.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object
                    && modalities.TryGetProperty("input", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => KindOf(m.GetString() ?? "")).OfType<string>().Distinct().ToList()
            : ["text"];

        var supported = new List<string> { "temperature", "top_p", "max_tokens" };
        bool? supportsTools = null;
        if (model.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
        {
            if (caps.TryGetProperty("tools", out var tools))
                supportsTools = OpenAiCompat.ReadBoolean(tools, "supported");
            if (supportsTools == true)
                supported.Add("tools");

            // Interfejs ustawia poziomy low/medium/high: sterowanie tylko, gdy model zna "medium" (inaczej API odrzuciłoby wartość)
            if (caps.TryGetProperty("reasoning", out var reasoning) && reasoning.TryGetProperty("effort", out var efforts)
                && efforts.ValueKind == JsonValueKind.Array && efforts.EnumerateArray().Any(e => e.GetString() == "medium"))
                supported.Add("reasoning_effort");
        }

        var canonical = model.TryGetProperty("canonical_id", out var cid) ? cid.GetString() : null;
        var owner = canonical is { Length: > 0 } && canonical.Contains('/') ? canonical.Split('/')[0] : "opencode";
        var description = model.TryGetProperty("description", out var d) ? d.GetString() : null;
        var hasInput = model.TryGetProperty("modalities", out var mode) && mode.ValueKind == JsonValueKind.Object
                       && mode.TryGetProperty("input", out var inputs) && inputs.ValueKind == JsonValueKind.Array;
        return new ChatModelInfo(id, supported, hasInput ? input.Contains("image") : null, owner, input, description, OpenAiCompat.ReadContextLength(model))
        {
            SupportsTools = supportsTools,
        };
    }

    private static string? KindOf(string mime) => mime switch
    {
        "text" => "text",
        "application/pdf" => "file",
        _ when mime.StartsWith("image/", StringComparison.Ordinal) => "image",
        _ when mime.StartsWith("audio/", StringComparison.Ordinal) => "audio",
        _ when mime.StartsWith("video/", StringComparison.Ordinal) => "video",
        _ => null,
    };
}
