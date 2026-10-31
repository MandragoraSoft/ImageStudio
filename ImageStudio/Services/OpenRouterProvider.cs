using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImageStudio.Services;

// Drugi provider: OpenRouter (openrouter.ai). Obrazy przez chat/completions z modalities ["image","text"],
// odpowiedź z obrazami w message.images. Brak wideo i brak cennika obrazów w API: koszt z usage.cost.
public sealed class OpenRouterProvider : IAiProvider
{
    public const string Identifier = "openrouter";

    private const string ChatPath = "/api/v1/chat/completions";
    private const string ModelsPath = "/api/v1/models";

    private readonly HttpClient _http;

    // Obrazy zwrócone jako URL: pobierane bez klucza OpenRouter (nie wysyłamy go na obce hosty)
    private readonly HttpClient _files = new() { Timeout = TimeSpan.FromMinutes(2) };

    private sealed record RawModel(string Id, IReadOnlyList<string> Input, IReadOnlyList<string> Output,
                                   IReadOnlyList<string> Supported);

    public OpenRouterProvider(string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("X-Title", "ImageStudio");
    }

    public string Id => Identifier;
    public string DisplayName => "OpenRouter";

    public async Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default)
    {
        var models = await GetModelsAsync(ct);
        var generation = models.Where(m => m.Output.Contains("image")).Select(ToInfo).ToList();
        var edit = models.Where(m => m.Output.Contains("image") && m.Input.Contains("image")).Select(ToInfo).ToList();
        return new ImageModels(generation, edit);
    }

    // OpenRouter nie ma endpointu wideo
    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImageModelInfo>>([]);

    public async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        var models = await GetModelsAsync(ct);
        return models.Where(m => m.Output.Contains("text"))
                     .Select(m => new ChatModelInfo(m.Id, m.Supported))
                     .ToList();
    }

    // Brak cennika obrazów w katalogu modeli; ceny pokazują się jako „—”
    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModelPricing>>(new Dictionary<string, ModelPricing>());

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default)
    {
        var text = $"{prompt}\n\nOutput size: {size}.";
        if (!string.IsNullOrWhiteSpace(options?.NegativePrompt))
            text += $" Avoid: {options.NegativePrompt}.";

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["modalities"] = new[] { "image", "text" },
            ["messages"] = new[] { new { role = "user", content = text } },
        };
        if (options?.Seed is int seed) body["seed"] = seed;

        return SendImageRequestAsync(body, ct);
    }

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var path in imagePaths)
            content.Add(new { type = "image_url", image_url = new { url = DataUri(path) } });

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["modalities"] = new[] { "image", "text" },
            ["messages"] = new[] { new { role = "user", content } },
        };
        return SendImageRequestAsync(body, ct);
    }

    // OpenRouter nie generuje wideo: czytelny błąd zamiast cichego pominięcia
    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException("OpenRouter nie udostępnia generowania wideo. Wybierz provider UberLLM.");

    public async Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                      IReadOnlyDictionary<string, object> parameters,
                                                      CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "system", content = $"{instruction}\n\n{ImageGenerationService.SplitFormat}" },
                new { role = "user", content = $"SYSTEM (config):\n{system}\n\nCONCEPT:\n{concept}" },
            },
        };
        foreach (var (key, value) in parameters) body[key] = value;

        using var response = await _http.PostAsJsonAsync(ChatPath, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? "";
        var parts = ImageGenerationService.ParseSplit(content, system, concept);
        if (string.IsNullOrWhiteSpace(parts.Concept))
            throw new InvalidOperationException("Model nie zwrócił promptu.");
        return parts;
    }

    public void Dispose()
    {
        _http.Dispose();
        _files.Dispose();
    }

    private async Task<ImageResult> SendImageRequestAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(ChatPath, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var message = root.GetProperty("choices")[0].GetProperty("message");

        decimal? cost = root.TryGetProperty("usage", out var usage)
                        && usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal()
            : null;

        if (message.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0)
        {
            var url = images[0].GetProperty("image_url").GetProperty("url").GetString() ?? "";
            var bytes = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(url[(url.IndexOf(',') + 1)..])
                : await _files.GetByteArrayAsync(url, ct);
            return new ImageResult(bytes, cost);
        }

        var reply = message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String
            ? contentProp.GetString()
            : null;
        throw new InvalidOperationException($"Model nie zwrócił obrazu. {reply}".Trim());
    }

    private async Task<List<RawModel>> GetModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ModelsPath, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => new RawModel(
                m.GetProperty("id").GetString()!,
                Strings(m, "architecture", "input_modalities"),
                Strings(m, "architecture", "output_modalities"),
                Strings(m, "supported_parameters")))
            .ToList();
    }

    private static ImageModelInfo ToInfo(RawModel m) =>
        new(m.Id, m.Id.Split('/')[0], false, m.Input);

    // Tablica tekstów pod ścieżką właściwości; brak ścieżki = pusta lista
    private static IReadOnlyList<string> Strings(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (!current.TryGetProperty(name, out var next)) return [];
            current = next;
        }

        return current.ValueKind == JsonValueKind.Array
            ? current.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
            : [];
    }

    private static string DataUri(string path) =>
        $"data:{MimeOf(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
