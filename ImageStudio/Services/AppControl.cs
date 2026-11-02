namespace ImageStudio.Services;

// Stan aplikacji widoczny dla modelu: zakładka, wybrane modele, prompty, rozmiar i bieżący status
public sealed record AppState(string Tab, string Provider, string? GenerationModel, string? EditModel, string? VideoModel,
                              string GeneratePrompt, string EditPrompt, string VideoPrompt, string Size,
                              int References, bool Busy, string Status);

public sealed record ModelSummary(string Id, string? Owner, string Price, bool AcceptsImage, bool Preferred);

public sealed record GalleryItem(string Id, string Kind, string Model, string Prompt, string Path, string Cost, DateTime CreatedUtc);

// Operacje na oknie aplikacji, które mogą wykonywać narzędzia sterujące (czat i serwer MCP). Wywoływane z wątku UI.
// kind: "generate" (generowanie), "edit" (edycja z referencjami), "video" (wideo).
public interface IAppControl
{
    AppState GetState();

    // Rodzaje ("generate", "edit", "video"), dla których aktywny dostawca ma jakiekolwiek modele; narzędzia bez modeli są ukrywane
    IReadOnlyList<string> AvailableKinds();
    IReadOnlyList<ModelSummary> ListModels(string kind, string? query, int limit);
    void SelectModel(string kind, string id);
    void SetPrompt(string kind, string prompt, string? systemPrompt);
    void SetSize(int width, int height);
    void SetVideoOptions(int? durationSeconds, string? aspectRatio);

    // tab: "generate", "edit", "video" albo "chat"
    void SwitchTab(string tab);

    void AddReference(string path);
    Task AddReferenceFromUrlAsync(string url);
    void RemoveReference(int index);
    void ClearReferences();

    // Załączniki wideo (obrazy wejściowe) są osobną listą niż referencje edycji
    void AddVideoAttachment(string path);
    void ClearVideoAttachments();

    // ratio: np. "16:9" albo "source" (proporcje pierwszej referencji)
    void SetAspect(string ratio);

    // Ponowne pobranie katalogu modeli i cen aktywnego dostawcy
    Task ReloadModelsAsync();

    // Uruchamia generowanie z bieżącymi ustawieniami (kosztuje); zwraca opis wyniku albo rzuca z powodem niepowodzenia
    Task<string> GenerateAsync(string kind);
}
