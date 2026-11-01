namespace ImageStudio.Services;

// Dekorator dostawcy: każde wywołanie API zgłasza się w IActivityTracker (spinner w stopce), więc żaden ViewModel
// nie musi pamiętać o sygnalizowaniu. Wewnętrzne wywołania dostawcy (np. ReloadAsync składane z getterów) liczą się raz
public sealed class ActivityAiProvider(IAiProvider inner, IActivityTracker activity) : IAiProvider
{
    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public string? DefaultTextModel => inner.DefaultTextModel;

    private async Task<T> Track<T>(string label, Func<Task<T>> call, bool dimResponse = true)
    {
        using var _ = activity.Begin(label, dimResponse);
        return await call();
    }

    public Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default) =>
        Track("Pobieranie modeli obrazów", () => inner.GetImageModelsAsync(ct));

    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        Track("Pobieranie modeli wideo", () => inner.GetVideoModelsAsync(ct));

    public Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default) =>
        Track("Pobieranie modeli tekstowych", () => inner.GetChatModelsAsync(ct));

    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        Track("Pobieranie cen", () => inner.GetPricingAsync(ct));

    public Task<ProviderCatalog> ReloadAsync(CancellationToken ct = default) =>
        Track("Odświeżanie katalogu modeli", () => inner.ReloadAsync(ct));

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default) =>
        Track("Generowanie obrazu", () => inner.GenerateImageAsync(model, prompt, size, options, ct));

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default) =>
        Track("Edycja obrazu", () => inner.EditImageAsync(model, prompt, imagePaths, ct));

    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        Track("Generowanie wideo", () => inner.GenerateVideoAsync(model, prompt, duration, aspectRatio, attachments, progress, ct));

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        Track("Odpowiedź modelu", () => inner.ChatAsync(model, messages, tools, ct, options));

    // Strumień widać w stopce, ale okno z napływającym tekstem nie jest przyciemniane
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        Track("Odpowiedź modelu (strumień)", () => inner.ChatStreamAsync(model, messages, tools, onDelta, ct, options), dimResponse: false);

    public Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        Track("Pobieranie modeli mowy", () => inner.GetSpeechModelsAsync(ct));

    public Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        Track("Pobieranie modeli transkrypcji", () => inner.GetTranscriptionModelsAsync(ct));

    public Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) =>
        Track("Pobieranie głosów", () => inner.GetSpeechVoicesAsync(model, ct), dimResponse: false);

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default) =>
        Track("Synteza mowy", () => inner.SynthesizeAsync(model, text, voice, speed, format, ct));

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default) =>
        Track("Rozpoznawanie mowy", () => inner.TranscribeAsync(model, filePath, language, prompt, ct));

    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default) =>
        Track("Przebudowa promptu", () => inner.EnhancePromptAsync(model, instruction, system, concept, parameters, history, ct));

    public void Dispose() => inner.Dispose();
}
