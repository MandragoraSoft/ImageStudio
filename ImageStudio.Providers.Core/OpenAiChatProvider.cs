using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ImageStudio.Services;

// Podstawa dostawców tylko tekstowych w formacie OpenAI (/chat/completions): czat, narzędzia, ulepszanie promptów.
// Obrazy i wideo są nieobsługiwane (pusty katalog, NotSupportedException); pochodna podaje katalog modeli i ceny
public abstract class OpenAiChatProvider : IAiProvider
{
    protected HttpClient Http { get; }
    protected string ChatPath { get; }

    // baseAddress kończy się "/", ścieżki względne (chatPath, katalog modeli) dokleja pochodna
    protected OpenAiChatProvider(string baseAddress, string apiKey, string chatPath) : this(baseAddress, apiKey, chatPath, null) { }

    protected OpenAiChatProvider(string baseAddress, string apiKey, string chatPath, HttpMessageHandler? handler)
    {
        ChatPath = chatPath;
        Http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageStudio/1.0");
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string? DefaultTextModel => null;

    public abstract Task<IReadOnlyList<ChatModelInfo>> GetChatModelsAsync(CancellationToken ct = default);

    // Cena nie jest wymagana: dostawca bez cennika w API zostawia pustą listę
    public virtual Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModelPricing>>(new Dictionary<string, ModelPricing>());

    // Rozpoznawanie mowy: domyślnie brak, dostawca z endpointem transkrypcji nadpisuje obie metody
    public virtual Task<IReadOnlyList<AudioModelInfo>> GetTranscriptionModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioModelInfo>>([]);

    public virtual Task<TranscriptResult> TranscribeAsync(string model, string filePath, string? language, string? prompt,
                                                          CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje rozpoznawania mowy.");

    public Task<ImageModels> GetImageModelsAsync(CancellationToken ct = default) => Task.FromResult(new ImageModels([], []));

    public Task<IReadOnlyList<ImageModelInfo>> GetVideoModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImageModelInfo>>([]);

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                                CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje generowania obrazów.");

    public Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                            CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje edycji obrazów.");

    public Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                                IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                                Action<string>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} nie obsługuje generowania wideo.");

    public virtual Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                                     IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default,
                                     ChatOptions? options = null) =>
        OpenAiCompat.ChatAsync(Http, ChatPath, model, messages, tools, ct, options);

    public virtual Task<ChatReply> ChatStreamAsync(string model, IReadOnlyList<ChatMessage> messages,
                                           IReadOnlyList<ToolDefinition>? tools, Action<ChatDelta> onDelta,
                                           CancellationToken ct = default, ChatOptions? options = null) =>
        OpenAiCompat.ChatStreamAsync(Http, ChatPath, model, messages, tools, onDelta, ct, options);

    public virtual Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                                IReadOnlyDictionary<string, object> parameters,
                                                IReadOnlyList<PromptRevision>? history = null,
                                                CancellationToken ct = default, string negative = "") =>
        OpenAiCompat.EnhanceAsync(Http, ChatPath, model, instruction, system, concept, parameters, history, PromptResponseMode.JsonObject, ct, negative);

    public virtual void Dispose() => Http.Dispose();

    // Katalog /models: tablica "data" albo goła tablica (Mistral bywa różnie)
    protected static IEnumerable<JsonElement> ModelEntries(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.GetProperty("data").EnumerateArray();

    // Katalog /models w pamięci podręcznej: ReloadAsync woła GetPricingAsync przed GetChatModelsAsync, więc ceny (refresh)
    // pobierają go na nowo, a modele z tego samego żądania. Katalogi bywają duże (setki modeli), stąd jedno żądanie
    private string? _modelsJson;

    protected async Task<string> ModelsJsonAsync(string path, bool refresh, CancellationToken ct)
    {
        if (refresh || _modelsJson is null) _modelsJson = await GetStringAsync(path, ct);
        return _modelsJson;
    }

    // GET z czytelnym błędem; zwraca ciało odpowiedzi
    protected async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiErrors.Failure(response.StatusCode, body);
        return body;
    }
}
