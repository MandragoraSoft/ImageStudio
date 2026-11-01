namespace ImageStudio.Services;

// Dostawca usług AI: modele obrazów, wideo i tekstu, generowanie i ulepszanie promptów.
// Aplikacja rozmawia tylko z tym interfejsem; konkretny dostawca (np. UberLLM) to jego implementacja.
public interface IAiProvider : IDisposable
{
    // Stały identyfikator (zapisywany w ustawieniach) i nazwa do wyświetlenia
    string Id { get; }
    string DisplayName { get; }

    // Domyślny model tekstowy dostawcy (ulepszanie promptów, czat, nazwy zestawów); null = pierwszy z listy.
    // Użytkownik może go nadpisać w Ustawieniach (preferowany model domyślny tego dostawcy)
    string? DefaultTextModel => null;

    Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default);

    // Ceny za jednostkę (obraz, sekunda) z rynku; klucz to id modelu
    Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default);

    // Przeładowanie katalogu dostawcy (modele obrazów, wideo, tekstu i ceny) jednym wywołaniem; służy zarówno
    // do startu i zmiany dostawcy, jak i do ręcznego odświeżenia z GUI. Dostawca z własną pamięcią podręczną
    // nadpisuje tę metodę i najpierw ją czyści. Częściowy błąd nie przerywa przeładowania: trafia do Warnings
    async Task<ProviderCatalog> ReloadAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();

        async Task<T?> Part<T>(string name, Func<Task<T>> load, T? fallback = default)
        {
            try
            {
                return await load();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"{name}: {ex.Message}");
                return fallback;
            }
        }

        var image = await Part("modele obrazów", () => GetImageModelsAsync(ct));
        var pricing = await Part<IReadOnlyDictionary<string, ModelPricing>>("ceny", () => GetPricingAsync(ct),
                                                                              new Dictionary<string, ModelPricing>());
        var video = await Part("modele wideo", () => GetVideoModelsAsync(ct));
        var chat = await Part("modele tekstowe", () => GetChatModelsAsync(ct));
        var speech = await Part("modele mowy", () => GetSpeechModelsAsync(ct));
        var transcription = await Part("modele transkrypcji", () => GetTranscriptionModelsAsync(ct));
        return new ProviderCatalog(image, video, chat, pricing ?? new Dictionary<string, ModelPricing>(), warnings, speech, transcription);
    }

    // Dźwięk (zakładka Głos). Dostawca bez dźwięku nie implementuje niczego: listy są puste, a wywołanie rzuca wyjątek.
    // Mowa z tekstu: modele i ich głosy (puste = model nie ma listy głosów, zostaje domyślny)
    Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioModelInfo>>([]);
    Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                      CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje syntezy mowy.");

    // Tekst z nagrania (plik na dysku: mp3, wav, ogg, flac, m4a, webm); language i prompt są opcjonalnymi podpowiedziami
    Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioModelInfo>>([]);
    Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                           CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje rozpoznawania mowy.");

    Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                         CancellationToken ct = default);
    Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                     CancellationToken ct = default);
    Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                         IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                         Action<string>? progress = null, CancellationToken ct = default);
    // Zwykła rozmowa: cała historia (system, user, assistant, tool) na wejściu, odpowiedź modelu na wyjściu.
    // Z tools model może zamiast tekstu poprosić o wywołanie narzędzi (ChatReply.ToolCalls).
    // options (opcjonalne) włącza/wyłącza i ustawia poziom rozumowania modelu, który to deklaruje
    Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                              IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                              ChatOptions? options = null);

    // Odpowiedź strumieniowa: onDelta dostaje kolejne kawałki tekstu i rozumowania w miarę napływu (na wątku, z którego
    // wywołano metodę), a wynik to ta sama odpowiedź co z ChatAsync (pełny tekst, narzędzia, zużycie). Dostawca bez
    // strumienia dostaje domyślną implementację: jedno wywołanie ChatAsync i jeden kawałek z całą odpowiedzią
    async Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                          IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                          CancellationToken ct = default, ChatOptions? options = null)
    {
        var reply = await ChatAsync(model, messages, tools, ct, options);
        if (reply.Content.Length > 0 || reply.Reasoning is { Length: > 0 }) onDelta(new ChatDelta(reply.Content, reply.Reasoning));
        return reply;
    }

    Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                         IReadOnlyDictionary<string, object> parameters,
                                         IReadOnlyList<PromptRevision>? history = null, CancellationToken ct = default);
}
