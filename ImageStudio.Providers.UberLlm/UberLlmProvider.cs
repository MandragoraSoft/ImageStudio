namespace ImageStudio.Services;

// Pierwszy provider: UberLLM (api.uberllm.dev) dla usług AI oraz publiczny rynek cen (uberllm.dev)
public sealed class UberLlmProvider : IAiProvider
{
    public const string Identifier = "uberllm";

    private readonly ImageGenerationService _api;
    private readonly MarketService _market;

    public UberLlmProvider(string baseUrl, string apiKey, string marketUrl)
    {
        _api = new ImageGenerationService(baseUrl, apiKey);
        _market = new MarketService(marketUrl);
    }

    public string Id => Identifier;
    public const string DefaultTextModelId = "venice/uncensored";
    public string? DefaultTextModel => DefaultTextModelId;
    public string DisplayName => "UberLLM";

    public Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default) => _api.GetImageModelsAsync(ct);

    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) => _api.GetVideoModelsAsync(ct);

    public Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default) => _api.GetChatModelsAsync(ct);

    // Ceny z rynku (na żywo, z ofertami) mają pierwszeństwo; modele, których rynek nie wycenia, dostają cenę z katalogu modeli
    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default)
    {
        var prices = new Dictionary<string, ModelPricing>(await _market.GetPricingAsync(ct));
        foreach (var (id, price) in await _api.GetCatalogPricingAsync(ct)) prices.TryAdd(id, price);
        return prices;
    }

    public async Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                      CancellationToken ct = default) =>
        await _api.GenerateAsync(model, prompt, size, options, ct);

    public async Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                                  CancellationToken ct = default) =>
        await _api.EditAsync(model, prompt, imagePaths, ct);

    public async Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths, ImageEditOptions? options,
                                                  CancellationToken ct = default) =>
        await _api.EditAsync(model, prompt, imagePaths, options, ct);

    public async Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                      IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                      Action<string>? progress = null, CancellationToken ct = default) =>
        await _api.GenerateVideoAsync(model, prompt, duration, aspectRatio, attachments, progress, ct);

    public async Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                      IReadOnlyList<(byte[] Bytes, string Mime)> attachments, VideoOptions? options,
                                                      Action<string>? progress = null, CancellationToken ct = default) =>
        await _api.GenerateVideoAsync(model, prompt, duration, aspectRatio, attachments, progress, ct, options);

    public Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        _api.ChatAsync(model, messages, tools, ct, options);
    public Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        _api.ChatStreamAsync(model, messages, tools, onDelta, ct, options);


    public async Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                      IReadOnlyDictionary<string, object> parameters,
                                                      IReadOnlyList<PromptRevision>? history = null,
                                                      CancellationToken ct = default) =>
        await _api.EnhancePromptAsync(model, instruction, system, concept, parameters, history, ct);

    public Task<IReadOnlyList<AudioModelInfo>> GetSpeechModelsAsync(CancellationToken ct = default) => _api.GetSpeechModelsAsync(ct);

    public Task<IReadOnlyList<string>> GetSpeechVoicesAsync(string model, CancellationToken ct = default) => _api.GetSpeechVoicesAsync(model, ct);

    public Task<AudioResult> SynthesizeAsync(string model, string text, string? voice, double? speed, string format,
                                             CancellationToken ct = default) =>
        _api.SynthesizeAsync(model, text, voice, speed, format, ct);

    public Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) => _api.GetTranscriptionModelsAsync(ct);

    public Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                  CancellationToken ct = default) =>
        _api.TranscribeAsync(model, filePath, language, prompt, ct);

    public void Dispose()
    {
        _api.Dispose();
        _market.Dispose();
    }
}
