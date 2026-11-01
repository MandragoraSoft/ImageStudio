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
        return new ProviderCatalog(image, video, chat, pricing ?? new Dictionary<string, ModelPricing>(), warnings);
    }

    Task<ImageResult> GenerateImageAsync(string model, string prompt, string size, ImageOptions? options,
                                         CancellationToken ct = default);
    Task<ImageResult> EditImageAsync(string model, string prompt, IReadOnlyList<string> imagePaths,
                                     CancellationToken ct = default);
    Task<ImageResult> GenerateVideoAsync(string model, string prompt, int duration, string aspectRatio,
                                         IReadOnlyList<(byte[] Bytes, string Mime)> attachments,
                                         Action<string>? progress = null, CancellationToken ct = default);
    // Zwykła rozmowa: cała historia (system, user, assistant, tool) na wejściu, odpowiedź modelu na wyjściu.
    // Z tools model może zamiast tekstu poprosić o wywołanie narzędzi (ChatReply.ToolCalls).
    Task<ChatReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages,
                              IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default);

    Task<PromptParts> EnhancePromptAsync(string model, string instruction, string system, string concept,
                                         IReadOnlyDictionary<string, object> parameters,
                                         IReadOnlyList<PromptRevision>? history = null, CancellationToken ct = default);
}
