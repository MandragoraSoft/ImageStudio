using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Services;


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
            throw ApiErrors.Failure(response.StatusCode, body);

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
                    : [],
                Description: Describe(model));

            if (endpoints.Contains(GenerationPath)) generation.Add(info);
            if (endpoints.Contains(EditPath)) edit.Add(info);
        }

        return new ImageModels(generation, edit);
    }

    // Ceny z katalogu modeli (pole pricing w /v1/models?type=image|video|chat): uzupełniają rynek, który nie wycenia modeli
    // bez aktywnych ofert (np. qwen/venice-qwen-image-2-1-turbo ma tylko media_list_usd). Awaria jednego katalogu nie psuje reszty
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetCatalogPricingAsync(CancellationToken ct = default)
    {
        var prices = new Dictionary<string, ModelPricing>();
        foreach (var type in new[] { "image", "video", "chat" })
        {
            try
            {
                using var response = await _http.GetAsync($"/v1/models?type={type}", ct);
                if (!response.IsSuccessStatusCode) continue;
                foreach (var (id, price) in ParseCatalogPricing(await response.Content.ReadAsStringAsync(ct))) prices.TryAdd(id, price);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // brak ceny z katalogu to tylko brak ceny
            }
        }

        return prices;
    }

    // pricing: media mają usd (cena teraz, bywa pominięta) i list_usd (cena producenta) za unit (image, second...);
    // modele tekstowe input_usd / output_usd / cache_read_usd za "1M tokens"
    public static IReadOnlyDictionary<string, ModelPricing> ParseCatalogPricing(string body)
    {
        static decimal? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

        var prices = new Dictionary<string, ModelPricing>();
        using var doc = JsonDocument.Parse(body);
        foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object) continue;

            var id = model.GetProperty("id").GetString()!;
            var unit = pricing.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (unit == "1M tokens")
            {
                if (Number(pricing, "input_usd") is { } input)
                    prices[id] = new ModelPricing(input, Number(pricing, "list_input_usd"), null, ModelPricing.TokenUnit, null, null,
                                                  Number(pricing, "output_usd"), Number(pricing, "cache_read_usd"));
                continue;
            }

            var list = Number(pricing, "list_usd");
            if ((Number(pricing, "usd") ?? list) is not { } now) continue;

            decimal? discount = list is > 0 && now < list ? Math.Round((1 - now / list.Value) * 100, 1) : null;
            prices[id] = new ModelPricing(now, list, discount, unit, null, null);
        }

        return prices;
    }

    // Opis modelu z /v1/models, jeśli API go zwraca; brak = UI pokaże opis z atrybutów
    private static string? Describe(JsonElement model) =>
        model.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    // Dostępne modele czatu (do ulepszania promptu) wraz z ich parametrami
    public async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/v1/models?type=chat", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(m => m.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
            .Where(m => m.TryGetProperty("endpoints", out var ep)
                        && ep.EnumerateArray().Any(e => e.GetString() == "/v1/chat/completions"))
            .Select(m => new ChatModelInfo(
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? sp.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : [],
                m.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                    ? modalities.EnumerateArray().Any(x => x.GetString() == "image")
                    : null,
                m.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null,
                m.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind == JsonValueKind.Array
                    ? inputs.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : null,
                Describe(m)))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        OpenAiCompat.ChatAsync(_http, "/v1/chat/completions", model, messages, tools, ct, options);
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        OpenAiCompat.ChatStreamAsync(_http, "/v1/chat/completions", model, messages, tools, onDelta, ct, options);



    // Rozdział promptu: część konfiguracyjną (styl, technika, format) od konceptu (co ma być na obrazie).
    // Model zwraca JSON; przy błędzie parsowania koncept to surowa odpowiedź, a system zostaje bez zmian.
    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default) =>
        OpenAiCompat.EnhanceAsync(_http, "/v1/chat/completions", model, instruction, system, concept, parameters, history, jsonMode: false, ct);



    public async Task<ImageResult> GenerateAsync(string model, string prompt, string size, ImageOptions? options = null,
                                            CancellationToken ct = default)
    {
        var request = new ImageRequest(model, prompt, 1, size, "b64_json",
                                       options?.Seed, options?.NegativePrompt, options?.Quality);
        using var response = await _http.PostAsJsonAsync(GenerationPath, request, ct);
        return await ReadImageAsync(response, ct);
    }

    public Task<ImageResult> EditAsync(string model, string prompt, IReadOnlyList<string> imagePaths, CancellationToken ct = default) =>
        EditAsync(model, prompt, imagePaths, null, ct);

    // Pola formularza odrzucone wcześniej przez dany model edycji w tej sesji (id modelu -> nazwy)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _rejectedEditParameters = new();

    // Edycja / generowanie z referencjami (multipart/form-data). Wiele obrazów: pole "image[]" (sprawdzone na API).
    // Z options.Aspect model dostaje aspect_ratio (albo size), jeśli jego schemat z GET /v1/models/<id> je oferuje: najbliższa dozwolona
    // wartość. Gdy API mimo to odrzuci pole, ponawiamy bez niego i zapamiętujemy do końca sesji.
    public async Task<ImageResult> EditAsync(string model, string prompt, IReadOnlyList<string> imagePaths, ImageEditOptions? options,
                                             CancellationToken ct = default)
    {
        var shape = new Dictionary<string, string>();
        if (options?.Aspect is { } aspect && PickShape(await GetModelSchemaAsync(model, ct), aspect) is { } picked)
            shape[picked.Field] = picked.Value;
        foreach (var skipped in _rejectedEditParameters.GetValueOrDefault(model) ?? []) shape.Remove(skipped);

        while (true)
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent(model), "model" },
                { new StringContent(prompt), "prompt" },
            };
            foreach (var (field, value) in shape) content.Add(new StringContent(value), field);

            foreach (var path in imagePaths)
            {
                var image = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
                image.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeOf(path));
                content.Add(image, "image[]", Path.GetFileName(path));
            }

            using var response = await _http.PostAsync(EditPath, content, ct);
            if (response.IsSuccessStatusCode) return await ReadImageAsync(response, ct);

            var text = await response.Content.ReadAsStringAsync(ct);
            if (ApiErrors.RejectedParameter(text) is { } rejected && shape.Remove(rejected))
            {
                _rejectedEditParameters.AddOrUpdate(model, _ => [rejected], (_, known) => [.. known, rejected]);
                continue;
            }

            throw ApiErrors.Failure(response.StatusCode, text);
        }
    }

    // Pole proporcji z schematu modelu (aspect_ratio z listy "W:H", a gdy go nie ma, size "WxH") o proporcji najbliższej żądanej;
    // null, gdy model nie ma żadnego z nich. "auto" jest pomijane (nie niesie proporcji).
    public static (string Field, string Value)? PickShape(JsonElement? schema, double aspect)
    {
        if (schema is not { } parameters || aspect <= 0 || !parameters.TryGetProperty("properties", out var props)) return null;

        (string Field, char Separator)[] candidates = [("aspect_ratio", ':'), ("size", 'x')];
        foreach (var (field, separator) in candidates)
        {
            if (!props.TryGetProperty(field, out var spec) || !spec.TryGetProperty("enum", out var values)) continue;

            var best = values.EnumerateArray().Select(v => v.GetString())
                .Select(v => (Value: v, Ratio: Ratio(v, separator)))
                .Where(x => x.Value is not null && x.Ratio is not null)
                .OrderBy(x => Math.Abs(Math.Log(x.Ratio!.Value / aspect)))
                .FirstOrDefault();
            if (best.Value is not null) return (field, best.Value);
        }

        return null;
    }

    private static double? Ratio(string? text, char separator)
    {
        var parts = text?.Split(separator);
        return parts is { Length: 2 }
               && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
               && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
               && w > 0 && h > 0
            ? w / h
            : null;
    }

    // Modele wideo (endpoint /v1/video/generations); te same atrybuty co modele obrazów
    public async Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/v1/models?type=video", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

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
                    : [],
                Description: Describe(m)))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Wideo: tworzy zadanie, odpytuje o status co 5 s i pobiera mp4 po zakończeniu.
    // Załączniki (obrazy wejściowe) idą jako data: URI: pojedynczy w polu "image", wiele w "input_images".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement> _videoSchemas = new();

    // Pole parameters z opisu modelu (JSON Schema żądania); null, gdy nie da się go pobrać (wtedy zadziała ponawianie bez odrzuconych pól)
    private async Task<JsonElement?> GetModelSchemaAsync(string model, CancellationToken ct)
    {
        if (_videoSchemas.TryGetValue(model, out var cached)) return cached;

        try
        {
            using var response = await _http.GetAsync($"/v1/models/{model}", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return null;

            return _videoSchemas[model] = parameters.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    // Parametry żądania wideo odrzucone przez dany model w tej sesji (id modelu -> nazwy parametrów)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _rejectedVideoParameters = new();

    public async Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                      IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                      Action<string>? progress = null, CancellationToken ct = default)
    {
        // Schemat żądania tego modelu (dozwolone pola, czasy i proporcje) z GET /v1/models/<id>; bez niego idą nazwy z dokumentacji
        var notes = new List<string>();
        var body = VideoRequest.Build(await GetModelSchemaAsync(model, ct), model, prompt, duration, aspectRatio, attachments, notes);
        foreach (var note in notes) progress?.Invoke(note);

        // Parametry odrzucone wcześniej przez ten model (np. aspect_ratio dla modeli image-to-video) pomijamy od razu
        foreach (var skipped in _rejectedVideoParameters.GetValueOrDefault(model) ?? []) body.Remove(skipped);

        string createText;
        while (true)
        {
            // Idempotency-Key chroni przed zdublowanym (płatnym) zadaniem; nowy klucz na każdą zmianę ciała żądania
            using var request = new HttpRequestMessage(HttpMethod.Post, VideoPath) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var create = await _http.SendAsync(request, ct);
            createText = await create.Content.ReadAsStringAsync(ct);
            if (create.IsSuccessStatusCode) break;

            // Katalog nie mówi, jakie parametry przyjmuje model; gdy API odrzuca jeden, ponawiamy bez niego i zapamiętujemy
            if (ApiErrors.RejectedParameter(createText) is { } rejected && rejected is not ("model" or "prompt") && body.Remove(rejected))
            {
                _rejectedVideoParameters.AddOrUpdate(model, _ => [rejected], (_, known) => [.. known, rejected]);
                progress?.Invoke($"model nie przyjmuje parametru {rejected}: pomijam");
                continue;
            }

            throw ApiErrors.Failure(create.StatusCode, createText);
        }

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
                throw ApiErrors.Failure(poll.StatusCode, pollText);

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

    // Dźwięk: modele mowy (type=speech) i transkrypcji (type=transcription); cena jest w opisie modelu (pricing.usd za unit)
    public Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        GetAudioModelsAsync("speech", OpenAiAudio.SpeechPath, ct);

    public Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        GetAudioModelsAsync("transcription", OpenAiAudio.TranscriptionPath, ct);

    private async Task<IReadOnlyList<AudioModelInfo>> GetAudioModelsAsync(string type, string endpoint, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/v1/models?type={type}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(m => m.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
            .Where(m => m.TryGetProperty("endpoints", out var ep) && ep.EnumerateArray().Any(e => "/" + endpoint == e.GetString()))
            .Select(m => new AudioModelInfo(
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null,
                AudioPricing(m),
                m.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : Describe(m)))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelPricing? AudioPricing(JsonElement model)
    {
        if (!model.TryGetProperty("pricing", out var p) || !p.TryGetProperty("usd", out var usd) || usd.ValueKind != JsonValueKind.Number)
            return null;

        var list = p.TryGetProperty("list_usd", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDecimal() : (decimal?)null;
        var price = usd.GetDecimal();
        return new ModelPricing(price, list, list is > 0 ? Math.Round((list.Value - price) / list.Value * 100, 1) : null,
                                p.TryGetProperty("unit", out var u) ? u.GetString() : null, null, null);
    }

    // Głosy modelu mowy ze schematu żądania (GET /v1/models/<id>, parameters.properties.voice.enum); pobierane raz na model
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> _voices = new();

    public async Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default)
    {
        if (_voices.TryGetValue(model, out var cached)) return cached;

        using var response = await _http.GetAsync($"/v1/models/{model}", ct);
        if (!response.IsSuccessStatusCode) return [];

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return _voices[model] = doc.RootElement.TryGetProperty("parameters", out var parameters)
            ? OpenAiAudio.VoicesFromSchema(parameters)
            : [];
    }

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default) =>
        OpenAiAudio.SynthesizeAsync(_http, model, text, voice, speed, format, ct);

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default) =>
        OpenAiAudio.TranscribeAsync(_http, model, filePath, language, prompt, ct);

    public void Dispose() => _http.Dispose();

    private async Task<ImageResult> ReadImageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, body);

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
