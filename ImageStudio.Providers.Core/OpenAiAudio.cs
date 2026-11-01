using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImageStudio.Services;

// Dźwięk w formacie OpenAI, wspólny dla OpenAI i UberLLM: POST /v1/audio/speech (odpowiedź to plik) i
// POST /v1/audio/transcriptions (formularz z plikiem, odpowiedź JSON). UberLLM podaje koszt w nagłówku
// X-UberLLM-Cost-USD (mowa) albo w usage.cost_usd (transkrypcja); OpenAI nie podaje kosztu: wtedy null
public static class OpenAiAudio
{
    public const string SpeechPath = "v1/audio/speech";
    public const string TranscriptionPath = "v1/audio/transcriptions";

    public static async Task<AudioResult> SynthesizeAsync(HttpClient http, string model, string text, string? voice,
                                                          double? speed, string format, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["model"] = model, ["input"] = text, ["response_format"] = format };
        if (!string.IsNullOrWhiteSpace(voice)) body["voice"] = voice;
        if (speed is { } s and not 1.0) body["speed"] = s;

        while (true)
        {
            using var response = await http.PostAsJsonAsync(SpeechPath, body, ct);
            if (response.IsSuccessStatusCode)
                return new AudioResult(await response.Content.ReadAsByteArrayAsync(ct), ExtensionOf(response, (string)body["response_format"]), CostHeader(response));

            // Modele przyjmują różne formaty (np. Orpheus tylko wav); błąd podaje listę, więc ponawiamy z pierwszym dozwolonym
            var error = await response.Content.ReadAsStringAsync(ct);
            if (SupportedFormat(error) is { } supported && !string.Equals(supported, (string)body["response_format"], StringComparison.OrdinalIgnoreCase))
            {
                body["response_format"] = supported;
                continue;
            }

            throw ApiErrors.Failure(response.StatusCode, error);
        }
    }

    // "... Supported formats: wav, mp3." -> pierwszy format z listy (null, gdy błąd o formatach nie mówi)
    public static string? SupportedFormat(string errorBody)
    {
        var match = System.Text.RegularExpressions.Regex.Match(errorBody, @"Supported formats?:\s*([A-Za-z0-9_]+)");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static async Task<TranscriptResult> TranscribeAsync(HttpClient http, string model, string filePath,
                                                               string? language, string? prompt, CancellationToken ct,
                                                               bool openAiFields = true)
    {
        // openAiFields = false: tylko model, plik i język (Mistral odrzuca pola spoza swojej specyfikacji)
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        if (openAiFields) content.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language)) content.Add(new StringContent(language.Trim()), "language");
        if (openAiFields && !string.IsNullOrWhiteSpace(prompt)) content.Add(new StringContent(prompt.Trim()), "prompt");

        await using var stream = File.OpenRead(filePath);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");  // format API rozpoznaje po zawartości
        content.Add(file, "file", Path.GetFileName(filePath));

        using var response = await http.PostAsync(TranscriptionPath, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ApiErrors.Failure(response.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        var transcript = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var cost = doc.RootElement.TryGetProperty("usage", out var usage)
                   && usage.TryGetProperty("cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal()
            : CostHeader(response);
        return new TranscriptResult(transcript, cost);
    }

    // Lista głosów z JSON Schema żądania (properties.voice.enum); pusta, gdy model nie ma stałej listy
    public static IReadOnlyList<string> VoicesFromSchema(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty("properties", out var props) && props.TryGetProperty("voice", out var voice)
        && voice.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList()
            : [];

    private static decimal? CostHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-UberLLM-Cost-USD", out var values)
        && decimal.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost)
            ? cost
            : null;

    // Rozszerzenie pliku wg typu odpowiedzi; gdy serwer go nie zdradza, wg żądanego formatu
    private static string ExtensionOf(HttpResponseMessage response, string requested) =>
        response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/ogg" or "audio/opus" => "ogg",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/aac" => "aac",
            _ => requested,
        };
}
