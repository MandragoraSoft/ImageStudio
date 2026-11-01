using System.Text.Json;

namespace ImageStudio.Services;

// Mistral (api.mistral.ai): czat i modele z obrazem w formacie OpenAI oraz rozpoznawanie mowy (Voxtral, /v1/audio/transcriptions).
// Katalog /v1/models wymaga klucza i podaje możliwości (capabilities: completion_chat, vision, function_calling),
// ale nie ceny: ceny zostają puste. Syntezy mowy nie ma tu, bo jej API (JSON z audio_data w base64) różni się od OpenAI
public sealed class MistralProvider : OpenAiChatProvider
{
    public const string Identifier = "mistral";
    public const string DefaultTextModelId = "mistral-small-latest";

    private const string ModelsPath = "v1/models";
    private const string Owner = "mistral";

    public MistralProvider(string apiKey) : base("https://api.mistral.ai/", apiKey, "v1/chat/completions")
    {
    }

    public override string Id => Identifier;
    public override string DisplayName => "Mistral";
    public override string? DefaultTextModel => DefaultTextModelId;

    public override async Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: false, ct));
        return ParseChatModels(doc.RootElement);
    }

    // Modele z capabilities.completion_chat (embeddingi, moderacja i OCR odpadają); zarchiwizowane pomijamy.
    // Parametr seed to u Mistrala random_seed, więc go nie deklarujemy
    public static IReadOnlyList<ChatModelInfo> ParseChatModels(JsonElement root) =>
        ModelEntries(root)
            .Where(m => !Flag(m, "archived") && (!m.TryGetProperty("capabilities", out var caps) || Flag(caps, "completion_chat")))
            .Select(m =>
            {
                var caps = m.TryGetProperty("capabilities", out var c) ? c : default;
                var vision = caps.ValueKind == JsonValueKind.Object && Flag(caps, "vision");
                var supported = new List<string> { "temperature", "top_p", "max_tokens" };
                if (caps.ValueKind == JsonValueKind.Object && Flag(caps, "function_calling")) supported.Add("tools");
                var description = m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                return new ChatModelInfo(m.GetProperty("id").GetString()!, supported, vision, Owner,
                                         vision ? ["text", "image"] : ["text"], description);
            })
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // Katalog nie oznacza modeli dźwięku osobno, więc rozpoznajemy je po nazwie (rodzina Voxtral, bez TTS i trybu realtime)
    public override async Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ModelsJsonAsync(ModelsPath, refresh: false, ct));
        return ModelEntries(doc.RootElement)
            .Select(m => m.GetProperty("id").GetString()!)
            .Where(id => id.Contains("voxtral", StringComparison.OrdinalIgnoreCase)
                         && !id.Contains("tts", StringComparison.OrdinalIgnoreCase)
                         && !id.Contains("realtime", StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new AudioModelInfo(id, Owner))
            .ToList();
    }

    public override Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                           CancellationToken ct = default) =>
        OpenAiAudio.TranscribeAsync(Http, model, filePath, language, prompt, ct, openAiFields: false);
}
