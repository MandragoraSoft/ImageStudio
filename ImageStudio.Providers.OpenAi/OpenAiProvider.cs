using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImageStudio.Services;

// Provider OpenAI (oficjalne API lub dowolny kompatybilny adres: proxy, serwer lokalny).
// Obrazy: /v1/images/generations i /v1/images/edits. Tekst: /v1/chat/completions.
// Brak wideo (Sora ma inny, asynchroniczny kontrakt) i brak cennika w odpowiedziach: koszt jest nieznany.
public sealed class OpenAiProvider : IAiProvider
{
    public const string Identifier = "openai";
    public const string DefaultBaseUrl = "https://api.openai.com";

    private static readonly string[] ChatParameters =
    [
        "temperature", "top_p", "max_tokens", "seed", "frequency_penalty", "presence_penalty",
    ];

    private readonly HttpClient _http;

    // Obrazy zwracane jako URL pobieramy bez klucza OpenAI
    private readonly HttpClient _files = new() { Timeout = TimeSpan.FromMinutes(2) };

    public OpenAiProvider(string baseUrl, string apiKey)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        _http = new HttpClient
        {
            BaseAddress = new Uri(root.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

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
                  .Select(id => new ChatModelInfo(id, ChatParameters))
                  .ToList();
    }

    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModelPricing>>(new Dictionary<string, ModelPricing>());

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default)
    {
        var text = prompt;
        if (!string.IsNullOrWhiteSpace(options?.NegativePrompt))
            text += $"\n\nAvoid: {options.NegativePrompt}.";

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
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default) =>
        OpenAiCompat.ChatAsync(_http, "v1/chat/completions", model, messages, tools, ct);

    public async Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                      IReadOnlyDictionary<string, object> parameters,
                                                      CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "system", content = $"{instruction}\n\n{OpenAiCompat.SplitFormat}" },
                new { role = "user", content = $"SYSTEM (config):\n{system}\n\nCONCEPT:\n{concept}" },
            },
        };
        foreach (var (key, value) in parameters) body[key] = value;

        using var response = await _http.PostAsJsonAsync("v1/chat/completions", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? "";
        var parts = OpenAiCompat.ParseSplit(content, system, concept);
        if (string.IsNullOrWhiteSpace(parts.Concept))
            throw new InvalidOperationException("Model nie zwrócił promptu.");
        return parts;
    }

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
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");

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
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

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
        new(id, "OpenAI", false, id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)
                                 ? ["text", "image"]
                                 : ["text"]);

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
