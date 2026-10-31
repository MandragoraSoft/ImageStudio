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

public sealed class ImageGenerationService : IDisposable
{
    private const string GenerationPath = "/v1/images/generations";
    private const string EditPath = "/v1/images/edits";

    private readonly HttpClient _http;

    public ImageGenerationService(string baseUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    public async Task<byte[]> GenerateAsync(string model, string prompt, string size, CancellationToken ct = default)
    {
        var request = new ImageRequest(model, prompt, 1, size, "b64_json");
        using var response = await _http.PostAsJsonAsync(GenerationPath, request, ct);
        return await ReadImageAsync(response, ct);
    }

    // Edycja obrazu z dysku (multipart/form-data, pole "image")
    public async Task<byte[]> EditAsync(string model, string prompt, string imagePath, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
        };

        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var image = new ByteArrayContent(imageBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeOf(imagePath));
        content.Add(image, "image", Path.GetFileName(imagePath));

        using var response = await _http.PostAsync(EditPath, content, ct);
        return await ReadImageAsync(response, ct);
    }

    public void Dispose() => _http.Dispose();

    private async Task<byte[]> ReadImageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("data")[0];

        if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return Convert.FromBase64String(b64.GetString()!);

        // ponytail: upstream może zwrócić URL zamiast base64; pobieramy wtedy plik (nagłówek Authorization jest domyślny)
        if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            return await _http.GetByteArrayAsync(url.GetString(), ct);

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
        [property: JsonPropertyName("response_format")] string ResponseFormat);
}
