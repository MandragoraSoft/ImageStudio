using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;

namespace ImageStudio.Services;

// Provider OpenAI (oficjalne API lub dowolny kompatybilny adres: proxy, serwer lokalny).
// Obrazy: /v1/images/generations i /v1/images/edits. Tekst: /v1/chat/completions.
// Brak wideo (Sora ma inny, asynchroniczny kontrakt) i brak cennika w odpowiedziach: koszt jest nieznany.
public sealed class OpenAiProvider : IAiProvider
{
    public const string Identifier = "openai";
    public const string DefaultBaseUrl = "https://api.openai.com";

    // Jedna nazwa właściciela we wszystkich katalogach (obrazy, tekst, dźwięk), żeby filtr "Dostawca" nie miał dwóch wpisów
    private const string Owner = "openai";

    // OpenAI nie podaje cen w API; ceny tekstu bierzemy z publicznego katalogu OpenRouter (modele openai/<id>, bez klucza)
    private const string PricingCatalogUrl = "https://openrouter.ai/api/v1/models";

    private static readonly string[] ChatParameters =
    [
        "temperature", "top_p", "max_tokens", "seed", "frequency_penalty", "presence_penalty",
    ];

    private readonly HttpClient _http;

    // Obrazy zwracane jako URL pobieramy bez klucza OpenAI
    private readonly HttpClient _files = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Ścisły JSON Schema tylko przy oficjalnym adresie OpenAI (patrz SupportsStrictSchema)
    private readonly bool _officialApi;

    public OpenAiProvider(string baseUrl, string apiKey)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        _officialApi = IsOfficial(root);
        _http = new HttpClient
        {
            BaseAddress = new Uri(root.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    // Adres oficjalnego API: tylko tu znamy obsługę ścisłego schematu (proxy i serwery zgodne z OpenAI tego nie gwarantują)
    private static bool IsOfficial(string root) =>
        Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    // Jawna lista: sama data nazwy nie potwierdza możliwości innych modeli ani serwerów.
    public static bool SupportsStrictSchema(string model) =>
        model is "gpt-4o-mini" or "gpt-4o-mini-2024-07-18" or "gpt-4o-2024-08-06";

    public string Id => Identifier;
    public const string DefaultTextModelId = "gpt-4o-mini";
    public string? DefaultTextModel => DefaultTextModelId;
    public string DisplayName => "OpenAI";

    public async Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default)
    {
        var ids = await GetModelIdsAsync(ct);
        var generation = ids.Where(IsImageModel).Select(ToInfo).ToList();
        var edit = ids.Where(IsEditModel).Select(ToInfo).ToList();
        return new ImageModels(generation, edit);
    }

    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImageModelInfo>>([]);

    public async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        var ids = await GetModelIdsAsync(ct);
        return ids.Where(IsChatModel)
                  .Select(id => new ChatModelInfo(id, ChatParameters, OwnedBy: Owner))
                  .ToList();
    }

    // Dźwięk: API nie podaje typu modelu, więc klasyfikacja po nazwie; głosy są stałe (wspólne dla modeli tts)
    private static readonly string[] SpeechVoices = ["alloy", "ash", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer"];

    public async Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        (await GetModelIdsAsync(ct)).Where(id => id.Contains("tts", StringComparison.OrdinalIgnoreCase))
                                    .Select(id => new AudioModelInfo(id, Owner)).ToList();

    public Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(SpeechVoices);

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default) =>
        OpenAiAudio.SynthesizeAsync(_http, model, text, voice, speed, format, ct);

    public async Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        (await GetModelIdsAsync(ct)).Where(id => id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                                                 || id.Contains("transcribe", StringComparison.OrdinalIgnoreCase))
                                    .Select(id => new AudioModelInfo(id, Owner)).ToList();

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default) =>
        OpenAiAudio.TranscribeAsync(_http, model, filePath, language, prompt, ct);

    // Brak ceny nie jest błędem (modele obrazów i dźwięku nie mają jej w katalogu), więc awaria katalogu OpenRouter
    // zostawia listę bez cen zamiast psuć odświeżanie modeli
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
    {
        try
        {
            var ids = await GetModelIdsAsync(ct);
            using var response = await _files.GetAsync(PricingCatalogUrl, ct);
            return response.IsSuccessStatusCode
                ? ParseCatalogPricing(await response.Content.ReadAsStringAsync(ct), ids)
                : new Dictionary<string, ModelPricing>();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new Dictionary<string, ModelPricing>();
        }
    }

    // Ceny za 1M tokenów z katalogu OpenRouter (pricing.prompt/completion/input_cache_read w USD za token) dla podanych id OpenAI.
    // Wersje z datą (gpt-4o-mini-2024-07-18) dostają cenę modelu bez daty, warianty z ":" (batch, free) są pomijane
    public static IReadOnlyDictionary<string, ModelPricing> ParseCatalogPricing(string catalogJson, IEnumerable<string> ids)
    {
        using var doc = JsonDocument.Parse(catalogJson);
        var catalog = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = model.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (id is null || !id.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) || id.Contains(':')) continue;
            if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object) continue;

            static decimal? PerMillion(JsonElement p, string name) =>
                p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var usd) && usd >= 0
                    ? usd * 1_000_000m
                    : null;

            if (PerMillion(pricing, "prompt") is not { } input) continue;
            catalog[id["openai/".Length..]] = new ModelPricing(input, null, null, ModelPricing.TokenUnit, null, null,
                                                               PerMillion(pricing, "completion"), PerMillion(pricing, "input_cache_read"));
        }

        var result = new Dictionary<string, ModelPricing>();
        foreach (var id in ids)
        {
            var dated = System.Text.RegularExpressions.Regex.Replace(id, @"-\d{4}-\d{2}-\d{2}$", "");
            if (catalog.TryGetValue(id, out var price) || catalog.TryGetValue(dated, out price)) result[id] = price;
        }
        return result;
    }

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default)
    {
        // Brak potwierdzonego natywnego parametru: ostrzegamy i nie dopisujemy tekstu do promptu
        if (!string.IsNullOrWhiteSpace(options?.NegativePrompt)) options.NegativeNotice?.Invoke(NegativePromptOutcome.Unsupported);
        var text = prompt;

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = text,
            ["n"] = 1,
            ["size"] = size,
        };
        // Parametr jakości tylko gdy użytkownik go wybrał; dall-e zwraca URL, więc prosimy o base64
        if (!string.IsNullOrWhiteSpace(options?.Quality)) body["quality"] = options!.Quality!;
        if (model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)) body["response_format"] = "b64_json";

        return PostImageAsync("v1/images/generations", body, ct);
    }

    // Edycja i wiele obrazów: gpt-image przyjmuje pole "image[]" (wiele plików), dall-e-2 jedno pole "image"
    public async Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                                  CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
        };

        var field = model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase) ? "image" : "image[]";
        foreach (var path in imagePaths)
        {
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(path));
            content.Add(file, field, Path.GetFileName(path));
        }

        using var response = await _http.PostAsync("v1/images/edits", content, ct);
        return await ReadImageAsync(response, ct);
    }

    // OpenAI (ten adapter) nie generuje wideo
    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Provider OpenAI nie obsługuje tu generowania wideo. Wybierz UberLLM.");

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        OpenAiCompat.ChatAsync(_http, "v1/chat/completions", model, messages, tools, ct, options);
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        OpenAiCompat.ChatStreamAsync(_http, "v1/chat/completions", model, messages, tools, onDelta, ct, options);


    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default, string negative = "") =>
        OpenAiCompat.EnhanceAsync(_http, "v1/chat/completions", model, instruction, system, concept, parameters, history,
                                  _officialApi && SupportsStrictSchema(model) ? PromptResponseMode.JsonSchema : PromptResponseMode.JsonObject,
                                  ct, negative);

    public void Dispose()
    {
        _http.Dispose();
        _files.Dispose();
    }

    private async Task<ImageResult> PostImageAsync(string path, Dictionary<string, object> body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, ct);
        return await ReadImageAsync(response, ct);
    }

    private async Task<ImageResult> ReadImageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        var item = doc.RootElement.GetProperty("data")[0];

        if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return new ImageResult(Convert.FromBase64String(b64.GetString()!), null);

        if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            return new ImageResult(await _files.GetByteArrayAsync(url.GetString(), ct), null);

        throw new InvalidOperationException("Odpowiedź OpenAI nie zawiera obrazu.");
    }

    private async Task<List<string>> GetModelIdsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("v1/models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Klasyfikacja po nazwie: API /v1/models nie podaje typu modelu
    private static bool IsImageModel(string id) =>
        id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) || id.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditModel(string id) =>
        id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) || id.Equals("dall-e-2", StringComparison.OrdinalIgnoreCase);

    private static bool IsChatModel(string id)
    {
        var lower = id.ToLowerInvariant();
        if (IsImageModel(id)) return false;
        if (lower.Contains("audio") || lower.Contains("realtime") || lower.Contains("tts") ||
            lower.Contains("transcribe") || lower.Contains("embedding") || lower.Contains("moderation") ||
            lower.Contains("search"))
            return false;
        return lower.StartsWith("gpt-") || lower.StartsWith("chatgpt") || lower.StartsWith("o1") ||
               lower.StartsWith("o3") || lower.StartsWith("o4");
    }

    private static ImageModelInfo ToInfo(string id) =>
        new(id, Owner, false, id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)
                                 ? ["text", "image"]
                                 : ["text"]);

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
