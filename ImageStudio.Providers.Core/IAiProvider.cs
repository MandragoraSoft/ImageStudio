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
                                         IReadOnlyDictionary<string, object> parameters, CancellationToken ct = default);
}
