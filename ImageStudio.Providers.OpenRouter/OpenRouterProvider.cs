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
    private const string ModelsPath = "/api/v1/models?output_modalities=all";

    private readonly HttpClient _http;

    // Obrazy zwrócone jako URL: pobierane bez klucza OpenRouter (nie wysyłamy go na obce hosty)
    private readonly HttpClient _files = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Modele zwracające wyłącznie obraz (FLUX, Recraft...): wymagają modalities ["image"], bez "text"
    private readonly HashSet<string> _imageOnly = [];

    private string[] Modalities(string model) => _imageOnly.Contains(model) ? ["image"] : ["image", "text"];

    private sealed record RawModel(string Id, IReadOnlyList<string> Input, IReadOnlyList<string> Output,
                                   IReadOnlyList<string> Supported, string? Description = null, decimal? ImageTokenUsd = null,
                                   decimal? PromptUsd = null, decimal? CompletionUsd = null);

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
    public const string DefaultTextModelId = "openai/gpt-4o-mini";
    public string? DefaultTextModel => DefaultTextModelId;
    public string DisplayName => "OpenRouter";

    public async Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default)
    {
        var models = await GetModelsAsync(ct);
        _imageOnly.Clear();
        foreach (var m in models.Where(m => m.Output.Contains("image") && !m.Output.Contains("text")))
            _imageOnly.Add(m.Id);
        var generation = models.Where(m => m.Output.Contains("image")).Select(ToInfo).ToList();
        var edit = models.Where(m => m.Output.Contains("image") && m.Input.Contains("image")).Select(ToInfo).ToList();
        return new ImageModels(generation, edit);
    }

    // Wideo: osobny katalog /api/v1/videos/models; wejście obrazowe jest możliwe, gdy model przyjmuje klatki
    public async Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        (await GetVideoCatalogAsync(ct))
            .Select(v => new ImageModelInfo(v.Id, v.Id.Split('/')[0], false,
                                            v.Frames.Count > 0 ? ["text", "image"] : ["text"], v.Description, ["video"]))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        var models = await GetModelsAsync(ct);
        return models.Where(m => m.Output.Contains("text"))
                     .Select(m => new ChatModelInfo(m.Id, m.Supported, m.Input.Contains("image"), m.Id.Split('/')[0], m.Input, m.Description))
                     .ToList();
    }

    // Katalog podaje cenę tokenów obrazu wyjściowego (pricing.image_output); brak ceny = „—”
    // Wideo: cena za sekundę, gdy katalog podaje ją wprost (cents_per_..._second); rzeczywisty koszt przychodzi z zadania
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
    {
        var prices = (await GetModelsAsync(ct))
            .Where(m => m.ImageTokenUsd is > 0)
            .ToDictionary(m => m.Id, m => new ModelPricing(m.ImageTokenUsd!.Value * 1_000_000m, null, null,
                                                           ModelPricing.TokenUnit, null, null));
        // Modele tekstowe: cena promptu i odpowiedzi za 1M tokenów (modele z ceną obrazu zostają przy cenie obrazu)
        foreach (var m in (await GetModelsAsync(ct)).Where(m => m.ImageTokenUsd is not > 0 && m.PromptUsd is > 0 && m.Output.Contains("text")))
            prices[m.Id] = new ModelPricing(m.PromptUsd!.Value * 1_000_000m, null, null, ModelPricing.TokenUnit, null, null,
                                            m.CompletionUsd * 1_000_000m);
        try
        {
            foreach (var v in await GetVideoCatalogAsync(ct))
                if (v.SecondUsd is > 0)
                    prices[v.Id] = new ModelPricing(v.SecondUsd.Value, null, null, "s", null, null);
        }
        catch (Exception)
        {
            // Brak katalogu wideo nie odbiera cen obrazów
        }

        return prices;
    }

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default)
    {
        var text = $"{prompt}\n\nOutput size: {size}.";
        if (!string.IsNullOrWhiteSpace(options?.NegativePrompt))
            text += $" Avoid: {options.NegativePrompt}.";

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["modalities"] = Modalities(model),
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
            ["modalities"] = Modalities(model),
            ["messages"] = new[] { new { role = "user", content } },
        };
        return SendImageRequestAsync(body, ct);
    }

    // Wideo: POST /videos tworzy zadanie, potem odpytywanie polling_url i pobranie MP4 z /videos/{id}/content.
    // Obrazy: pierwszy (i drugi) jako klatka początkowa (końcowa), jeśli model je przyjmuje; reszta jako referencje.
    // Nieobsługiwane wartości (czas, proporcje) kończą się HTTP 400 z listą dozwolonych.
    // Parametry żądania wideo odrzucone przez dany model w tej sesji (id modelu -> nazwy parametrów)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _rejectedVideoParameters = new();

    public async Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                      IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                      Action<string>? progress = null, CancellationToken ct = default)
    {
        var frames = (await GetVideoCatalogAsync(ct)).FirstOrDefault(v => v.Id == model)?.Frames ?? [];
        var images = attachments
            .Select(a => new { type = "image_url", image_url = new { url = $"data:{a.Mime};base64,{Convert.ToBase64String(a.Bytes)}" } })
            .ToList();

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["duration"] = duration,
            ["aspect_ratio"] = aspectRatio,
        };

        var firstFrame = frames.Contains("first_frame") && images.Count > 0;
        var lastFrame = firstFrame && frames.Contains("last_frame") && images.Count > 1;
        var used = firstFrame ? (lastFrame ? 2 : 1) : 0;
        if (used > 0)
            body["frame_images"] = images.Take(used)
                .Select((img, i) => new { img.type, img.image_url, frame_type = i == 0 ? "first_frame" : "last_frame" })
                .ToList();
        if (images.Count > used) body["input_references"] = images.Skip(used).ToList();

        foreach (var skipped in _rejectedVideoParameters.GetValueOrDefault(model) ?? []) body.Remove(skipped);

        string createText;
        while (true)
        {
            using var create = await _http.PostAsJsonAsync("/api/v1/videos", body, ct);
            createText = await create.Content.ReadAsStringAsync(ct);
            if (create.IsSuccessStatusCode) break;

            // Model może nie przyjmować części parametrów (np. aspect_ratio): ponawiamy bez odrzuconego i zapamiętujemy
            if (ApiErrors.RejectedParameter(createText) is { } rejected && rejected is not ("model" or "prompt") && body.Remove(rejected))
            {
                _rejectedVideoParameters.AddOrUpdate(model, _ => [rejected], (_, known) => [.. known, rejected]);
                progress?.Invoke($"model nie przyjmuje parametru {rejected}: pomijam");
                continue;
            }

            throw ApiErrors.Failure(create.StatusCode, createText);
        }

        using var job = JsonDocument.Parse(createText);
        var id = job.RootElement.GetProperty("id").GetString()!;
        var pollUrl = job.RootElement.TryGetProperty("polling_url", out var pu) && pu.GetString() is { Length: > 0 } url
            ? url
            : $"/api/v1/videos/{id}";
        var deadline = DateTime.UtcNow.AddMinutes(20);

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Generowanie wideo trwa dłużej niż 20 minut.");

            using var poll = await _http.GetAsync(pollUrl, ct);
            var pollText = await poll.Content.ReadAsStringAsync(ct);
            if (!poll.IsSuccessStatusCode)
                throw ApiErrors.Failure(poll.StatusCode, pollText);

            using var state = JsonDocument.Parse(pollText);
            var status = state.RootElement.GetProperty("status").GetString() ?? "";
            progress?.Invoke(status);

            if (status == "completed")
            {
                decimal? cost = state.RootElement.TryGetProperty("usage", out var usage)
                                && usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetDecimal()
                    : null;
                return new ImageResult(await _http.GetByteArrayAsync($"/api/v1/videos/{id}/content?index=0", ct), cost);
            }

            if (status is "failed" or "cancelled" or "expired")
            {
                var error = state.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
                throw new InvalidOperationException($"Generowanie wideo zakończone statusem {status}. {error}".Trim());
            }
        }
    }

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        OpenAiCompat.ChatAsync(_http, ChatPath, model, messages, tools, ct, options);
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        OpenAiCompat.ChatStreamAsync(_http, ChatPath, model, messages, tools, onDelta, ct, options);


    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default) =>
        OpenAiCompat.EnhanceAsync(_http, ChatPath, model, instruction, system, concept, parameters, history, jsonMode: true, ct);

    public void Dispose()
    {
        _http.Dispose();
        _files.Dispose();
    }

    private async Task<ImageResult> SendImageRequestAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(ChatPath, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        // Katalog bywa niedokładny co do modalności wyjścia: błąd endpointu ("No endpoints found ... modalities")
        // leczymy jednorazowym ponowieniem z drugim zestawem i zapamiętujemy go dla modelu
        if (!response.IsSuccessStatusCode && IsEndpointMismatch(text) && body["model"] is string model)
        {
            var imageOnly = !_imageOnly.Contains(model);
            body["modalities"] = imageOnly ? new[] { "image" } : new[] { "image", "text" };
            response.Dispose();
            response = await _http.PostAsJsonAsync(ChatPath, body, ct);
            text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                _ = imageOnly ? _imageOnly.Add(model) : _imageOnly.Remove(model);
        }

        using var final = response;
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, text);

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

    private static bool IsEndpointMismatch(string error) =>
        error.Contains("No endpoints found", StringComparison.OrdinalIgnoreCase)
        || error.Contains("modalit", StringComparison.OrdinalIgnoreCase);

    // Jedno pobranie katalogu na providera (modele obrazów, ceny i modele czatu korzystają z tej samej listy)
    private Task<List<RawModel>>? _modelsTask;

    private Task<List<RawModel>> GetModelsAsync(CancellationToken ct)
    {
        if (_modelsTask is { IsFaulted: false, IsCanceled: false }) return _modelsTask;
        return _modelsTask = FetchModelsAsync(ct);
    }

    private sealed record VideoModel(string Id, string? Description, IReadOnlyList<string> Frames, decimal? SecondUsd);

    private Task<List<VideoModel>>? _videoTask;

    private Task<List<VideoModel>> GetVideoCatalogAsync(CancellationToken ct)
    {
        if (_videoTask is { IsFaulted: false, IsCanceled: false }) return _videoTask;
        return _videoTask = FetchVideoCatalogAsync(ct);
    }

    private async Task<List<VideoModel>> FetchVideoCatalogAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/api/v1/videos/models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => new VideoModel(
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                Strings(m, "supported_frame_images"),
                SecondUsd(m)))
            .ToList();
    }

    // Najniższa jawna cena za sekundę wyjścia (klucze cents_per_*second*, w centach); inne SKU mają niejasne jednostki
    private static decimal? SecondUsd(JsonElement model)
    {
        if (!model.TryGetProperty("pricing_skus", out var skus) || skus.ValueKind != JsonValueKind.Object) return null;

        var cents = skus.EnumerateObject()
            .Where(s => s.Name.StartsWith("cents_per_") && s.Name.Contains("second") && s.Value.ValueKind == JsonValueKind.String)
            .Select(s => decimal.TryParse(s.Value.GetString(), System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null)
            .OfType<decimal>()
            .Where(v => v > 0)
            .ToList();
        return cents.Count > 0 ? cents.Min() / 100m : null;
    }

    // Cena tokena (USD) z pricing.<name>; katalog podaje ją jako tekst
    private static decimal? PricingUsd(JsonElement model, string name) =>
        model.TryGetProperty("pricing", out var pricing)
        && pricing.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var usd)
            ? usd
            : null;

    private static decimal? ImageTokenUsd(JsonElement model) =>
        model.TryGetProperty("pricing", out var pricing)
        && pricing.TryGetProperty("image_output", out var value) && value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var usd)
            ? usd
            : null;

    private async Task<List<RawModel>> FetchModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ModelsPath, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => new RawModel(
                m.GetProperty("id").GetString()!,
                Strings(m, "architecture", "input_modalities"),
                Strings(m, "architecture", "output_modalities"),
                Strings(m, "supported_parameters"),
                m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                ImageTokenUsd(m), PricingUsd(m, "prompt"), PricingUsd(m, "completion")))
            .ToList();
    }

    private static ImageModelInfo ToInfo(RawModel m) =>
        new(m.Id, m.Id.Split('/')[0], false, m.Input, m.Description, m.Output);

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
