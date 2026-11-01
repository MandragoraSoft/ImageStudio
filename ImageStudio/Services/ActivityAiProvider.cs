using ImageStudio.Localization;
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
        Track(Loc.T("activity.imageModels"), () => inner.GetImageModelsAsync(ct));

    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.videoModels"), () => inner.GetVideoModelsAsync(ct));

    public Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.chatModels"), () => inner.GetChatModelsAsync(ct));

    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.pricing"), () => inner.GetPricingAsync(ct));

    public Task<ProviderCatalog> ReloadAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.reload"), () => inner.ReloadAsync(ct));

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default) =>
        Track(Loc.T("activity.generateImage"), () => inner.GenerateImageAsync(model, prompt, size, options, ct));

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default) =>
        Track(Loc.T("activity.editImage"), () => inner.EditImageAsync(model, prompt, imagePaths, ct));

    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        Track(Loc.T("activity.generateVideo"), () => inner.GenerateVideoAsync(model, prompt, duration, aspectRatio, attachments, progress, ct));

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        Track(Loc.T("activity.chat"), () => inner.ChatAsync(model, messages, tools, ct, options));

    // Strumień widać w stopce, ale okno z napływającym tekstem nie jest przyciemniane
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        Track(Loc.T("activity.chatStream"), () => inner.ChatStreamAsync(model, messages, tools, onDelta, ct, options), dimResponse: false);

    public Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.speechModels"), () => inner.GetSpeechModelsAsync(ct));

    public Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        Track(Loc.T("activity.transcriptionModels"), () => inner.GetTranscriptionModelsAsync(ct));

    public Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) =>
        Track(Loc.T("activity.voices"), () => inner.GetSpeechVoicesAsync(model, ct), dimResponse: false);

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default) =>
        Track(Loc.T("activity.synthesize"), () => inner.SynthesizeAsync(model, text, voice, speed, format, ct));

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default) =>
        Track(Loc.T("activity.transcribe"), () => inner.TranscribeAsync(model, filePath, language, prompt, ct));

    public Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default) =>
        Track(Loc.T("activity.enhance"), () => inner.EnhancePromptAsync(model, instruction, system, concept, parameters, history, ct));

    public void Dispose() => inner.Dispose();
}
