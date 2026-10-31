using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Services;

// Atrybuty modelu z /v1/models, używane do filtrowania w UI
public sealed record ImageModelInfo(string Id, string? OwnedBy, bool Nsfw, IReadOnlyList<string> InputModalities);

public sealed record ImageModels(IReadOnlyList<ImageModelInfo> Generation, IReadOnlyList<ImageModelInfo> Edit);

// Model czatu z listą parametrów, które deklaruje (supported_parameters)
public sealed record ChatModelInfo(string Id, IReadOnlyList<string> SupportedParameters);

// Wynik generowania/edycji: bajty obrazu i koszt z API (null, gdy API go nie zwróciło)
public sealed record ImageResult(byte[] Bytes, decimal? CostUsd);

// Opcjonalne parametry generowania; null = nie wysyłamy (API odrzuca nieznane klucze)
public sealed record ImageOptions(int? Seed, string? NegativePrompt, string? Quality);

// Prompt rozdzielony: System = konfiguracja (styl, technika), Concept = treść wskazana przez użytkownika
public sealed record PromptParts(string System, string Concept);

public sealed class ImageGenerationService : IDisposable
{
    private const string GenerationPath = "/v1/images/generations";
    private const string EditPath = "/v1/images/edits";
    private const string VideoPath = "/v1/video/generations";

    private HttpClient _http;

    public ImageGenerationService(string baseUrl, string apiKey)
    {
        _http = CreateClient(baseUrl, apiKey);
    }

    // Po zmianie ustawień w oknie Settings: nowy adres i klucz bez restartu
    public void Reconfigure(string baseUrl, string apiKey)
    {
        _http.Dispose();
        _http = CreateClient(baseUrl, apiKey);
    }

    private static HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    // Modele dostępne (available) i podzielone na generowanie oraz edycję według endpointów
    public async Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/v1/models?type=image", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var generation = new List<ImageModelInfo>();
        var edit = new List<ImageModelInfo>();

        foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
                continue;

            var id = model.GetProperty("id").GetString()!;
            var endpoints = model.TryGetProperty("endpoints", out var ep)
                ? ep.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];

            var info = new ImageModelInfo(
                Id: id,
                OwnedBy: model.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                    ? owner.GetString()
                    : null,
                Nsfw: model.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True,
                InputModalities: model.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind == JsonValueKind.Array
                    ? inputs.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : []);

            if (endpoints.Contains(GenerationPath)) generation.Add(info);
            if (endpoints.Contains(EditPath)) edit.Add(info);
        }

        return new ImageModels(generation, edit);
    }

    // Dostępne modele czatu (do ulepszania promptu) wraz z ich parametrami
    public async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/v1/models?type=chat", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(m => m.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
            .Where(m => m.TryGetProperty("endpoints", out var ep)
                        && ep.EnumerateArray().Any(e => e.GetString() == "/v1/chat/completions"))
            .Select(m => new ChatModelInfo(
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? sp.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : []))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Rozdział promptu: część konfiguracyjną (styl, technika, format) od konceptu (co ma być na obrazie).
    // Model zwraca JSON; przy błędzie parsowania koncept to surowa odpowiedź, a system zostaje bez zmian.
    public async Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                      IReadOnlyDictionary<string, object> parameters, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "system", content = $"{instruction}\n\n{SplitFormat}" },
                new { role = "user", content = $"SYSTEM (config):\n{system}\n\nCONCEPT:\n{concept}" },
            },
        };
        foreach (var (key, value) in parameters) body[key] = value;

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? "";
        var parts = ParseSplit(content, system, concept);
        if (string.IsNullOrWhiteSpace(parts.Concept))
            throw new InvalidOperationException("Model nie zwrócił promptu.");
        return parts;
    }

    internal const string SplitFormat =
        "Split the input into two parts and reply with JSON only, no code fences: " +
        "{\"system\": \"...\", \"concept\": \"...\"}. " +
        "\"system\" = technical configuration (style, lighting, camera, quality, format, constraints), keep existing system text. " +
        "\"concept\" = what the user wants depicted, improved and vivid. Keep the input language.";

    internal static PromptParts ParseSplit(string content, string fallbackSystem, string fallbackConcept)
    {
        var json = content.Trim();
        if (json.StartsWith("```"))
            json = json.Trim('`').Trim();
        if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            json = json[4..].Trim();

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            var system = root.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!.Trim()
                : fallbackSystem.Trim();
            var concept = root.TryGetProperty("concept", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!.Trim()
                : "";
            return new PromptParts(system, concept);
        }
        catch (JsonException)
        {
            return new PromptParts(fallbackSystem.Trim(), content.Trim().Trim('"'));
        }
    }


    public async Task<ImageResult> GenerateAsync(string model, string prompt, string size, ImageOptions? options = null,
                                            CancellationToken ct = default)
    {
        var request = new ImageRequest(model, prompt, 1, size, "b64_json",
                                       options?.Seed, options?.NegativePrompt, options?.Quality);
        using var response = await _http.PostAsJsonAsync(GenerationPath, request, ct);
        return await ReadImageAsync(response, ct);
    }

    // Edycja / generowanie z referencjami (multipart/form-data). Wiele obrazów: pole "image[]" (sprawdzone na API)
    public async Task<ImageResult> EditAsync(string model, string prompt, IReadOnlyList<string> imagePaths, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
        };

        foreach (var path in imagePaths)
        {
            var image = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
            image.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeOf(path));
            content.Add(image, "image[]", Path.GetFileName(path));
        }

        using var response = await _http.PostAsync(EditPath, content, ct);
        return await ReadImageAsync(response, ct);
    }

    // Modele wideo (endpoint /v1/video/generations); te same atrybuty co modele obrazów
    public async Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/v1/models?type=video", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(m => m.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
            .Where(m => m.TryGetProperty("endpoints", out var ep)
                        && ep.EnumerateArray().Any(e => e.GetString() == VideoPath))
            .Select(m => new ImageModelInfo(
                Id: m.GetProperty("id").GetString()!,
                OwnedBy: m.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null,
                Nsfw: m.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True,
                InputModalities: m.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind == JsonValueKind.Array
                    ? inputs.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : []))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Wideo: tworzy zadanie, odpytuje o status co 5 s i pobiera mp4 po zakończeniu.
    // Załączniki (obrazy wejściowe) idą jako data: URI: pojedynczy w polu "image", wiele w "input_images".
    public async Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                      IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                      Action<string>? progress = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["duration"] = duration,
            ["aspect_ratio"] = aspectRatio,
        };

        var uris = attachments.Select(a => $"data:{a.Mime};base64,{Convert.ToBase64String(a.Bytes)}").ToList();
        if (uris.Count == 1) body["image"] = uris[0];
        else if (uris.Count > 1) body["input_images"] = uris;

        using var create = await _http.PostAsJsonAsync(VideoPath, body, ct);
        var createText = await create.Content.ReadAsStringAsync(ct);
        if (!create.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)create.StatusCode}: {createText}");

        using var job = JsonDocument.Parse(createText);
        var pollUrl = job.RootElement.GetProperty("poll_url").GetString()!;
        var deadline = DateTime.UtcNow.AddMinutes(20);

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Generowanie wideo trwa dłużej niż 20 minut.");

            using var poll = await _http.GetAsync(pollUrl, ct);
            var pollText = await poll.Content.ReadAsStringAsync(ct);
            if (!poll.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)poll.StatusCode}: {pollText}");

            using var state = JsonDocument.Parse(pollText);
            var status = state.RootElement.GetProperty("status").GetString() ?? "";
            progress?.Invoke(status);

            if (status == "succeeded")
            {
                decimal? cost = state.RootElement.TryGetProperty("usage", out var usage)
                                && usage.TryGetProperty("cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetDecimal()
                    : null;
                var url = state.RootElement.GetProperty("results")[0].GetProperty("url").GetString()!;
                return new ImageResult(await _http.GetByteArrayAsync(url, ct), cost);
            }

            if (status is "failed" or "cancelled")
                throw new InvalidOperationException($"Generowanie wideo zakończone statusem: {status}.");
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<ImageResult> ReadImageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        // Koszt z API (usage.cost_usd) jest w odpowiedzi generowania i edycji
        decimal? cost = doc.RootElement.TryGetProperty("usage", out var usage)
                        && usage.TryGetProperty("cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal()
            : null;
        var item = doc.RootElement.GetProperty("data")[0];

        if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return new ImageResult(Convert.FromBase64String(b64.GetString()!), cost);

        // ponytail: upstream może zwrócić URL zamiast base64; pobieramy wtedy plik (nagłówek Authorization jest domyślny)
        if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            return new ImageResult(await _http.GetByteArrayAsync(url.GetString(), ct), cost);

        throw new InvalidOperationException("Odpowiedź nie zawiera obrazu.");
    }

    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png"
    };

    private sealed record ImageRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("n")] int N,
        [property: JsonPropertyName("size")] string Size,
        [property: JsonPropertyName("response_format")] string ResponseFormat,
        [property: JsonPropertyName("seed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seed,
        [property: JsonPropertyName("negative_prompt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NegativePrompt,
        [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quality);
}
