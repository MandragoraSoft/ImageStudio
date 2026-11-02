using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Kontrakty operacji aplikacji dostępnych bez okna: wspólne dla okna (ViewModele), narzędzi MCP i przyszłego CLI.
// Żądania są niezmienne i mają jawne pola; Publish = false oznacza, że wynik nie trafia do biblioteki (gospodarz zrobi to sam,
// np. okno dodaje miniaturę i pokazuje podgląd).

// Stałe wspólne dla okna i narzędzi
public static class StudioDefaults
{
    // Polecenie przebudowy promptu, gdy użytkownik nie podał własnego
    public const string PromptInstruction =
        "You improve image generation prompts. Make the concept vivid and detailed, " +
        "keep the same language, subject and intent.";

    // Limit pliku dla rozpoznawania mowy (limit API)
    public const long MaxAudioBytes = 40L * 1024 * 1024;

    public const int MinPixels = 64;
    public const int MaxPixels = 4096;
    public const int MaxVideoSeconds = 60;
    public const int MaxEditSide = 1536;  // dłuższy bok wyniku edycji bez podanych wymiarów
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;

    public static readonly IReadOnlyList<string> VideoAspects = ["16:9", "9:16", "1:1", "4:3"];
    public static readonly IReadOnlyList<string> AudioFormats = ["mp3", "wav"];

    // Kolejność jak na liście języków w zakładce Głos; wartość trafia do polecenia tłumaczenia
    public static readonly IReadOnlyDictionary<string, string> SpeechLanguages = new Dictionary<string, string>
    {
        ["pl"] = "Polish", ["en"] = "English", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["it"] = "Italian",
        ["pt"] = "Portuguese", ["ru"] = "Russian", ["uk"] = "Ukrainian", ["cs"] = "Czech", ["ja"] = "Japanese",
        ["zh"] = "Mandarin Chinese", ["ko"] = "Korean",
    };
}

// Model pominięty (null) = domyślny wybrany przez gospodarza, a gdy go nie ma, pierwszy z katalogu dostawcy
public sealed record ImageRequest(string Prompt, string? SystemPrompt = null, string? Model = null, int Width = 1024, int Height = 1024,
                                  ImageOptions? Options = null, bool Publish = true);

// References: ścieżki plików albo adresy http(s); pierwszy jest podstawowy. Width i Height razem dopasowują wynik do proporcji
public sealed record EditRequest(string Prompt, string? SystemPrompt, IReadOnlyList<string> References, string? Model = null,
                                 int? Width = null, int? Height = null, bool Publish = true);

public sealed record VideoGenerationRequest(string Prompt, string? Model = null, int Duration = 5, string AspectRatio = "16:9",
                                  IReadOnlyList<string>? Attachments = null, Action<string>? Progress = null, bool Publish = true);

// Language: kod z StudioDefaults.SpeechLanguages (tekst jest najpierw tłumaczony); Stage dostaje "translating" i "synthesizing"
public sealed record SpeechRequest(string Text, string? Model = null, string? Voice = null, double? Speed = null, string Format = "mp3",
                                   string? Language = null, Action<string>? Stage = null, bool Publish = true);

public sealed record TranscriptionRequest(string Path, string? Model = null, string? Language = null, string? Hint = null, bool Publish = true);

public sealed record PromptRebuildRequest(string Prompt, string? SystemPrompt = null, string? Instruction = null, string? Model = null,
                                          IReadOnlyDictionary<string, object>? Parameters = null,
                                          IReadOnlyList<PromptRevision>? History = null);

// Opis obrazów modelem tekstowym, który je przyjmuje; obrazy większe niż MaxSide są zmniejszane
public sealed record DescribeRequest(IReadOnlyList<string> Images, string Model, int MaxSide);

public sealed record SavePresetRequest(string Name, string Kind, string Prompt, string? SystemPrompt = null, string? Model = null,
                                       int Width = 1024, int Height = 1024, ImageOptions? Options = null);

// Domyślne ustawienia aplikacji: dostawca i wybrane modele. Źródłem prawdy są pliki użytkownika (settings.json: dostawca,
// state.json: modele); okno tylko je wiąże (zmiana w oknie zapisuje plik, narzędzia czytają i zapisują ten sam plik/stan).
public interface IStudioDefaults
{
    string ProviderId { get; }

    // kind: generate, edit, video, speech, transcription, chat; null = brak wyboru
    string? Model(string kind);

    void SetModel(string kind, string id);
}

public sealed record DefaultsInfo(string Provider, string ProviderName, IReadOnlyDictionary<string, string?> Models);

// Entry: opis wyniku (plik w magazynie, model, koszt); Bytes: bajty obrazu (tylko dla obrazów, null dla wideo i dźwięku)
public sealed record StudioResult(GalleryEntry Entry, byte[]? Bytes = null);

public sealed record AudioModelSummary(string Id, string? Owner, string Price);

// Biblioteka wyników: galeria obrazów i wideo oraz galeria głosu (nowsze wpisy pierwsze). W aplikacji to kolekcje okna
// (zmiany od razu widać w galerii), w trybie bez okna (CLI) mogą to być same pliki. Wywoływana z jednego wątku naraz.
public interface IStudioLibrary
{
    IReadOnlyList<GalleryEntry> Media { get; }
    IReadOnlyList<GalleryEntry> Voice { get; }
    void AddMedia(GalleryEntry entry);
    void AddVoice(GalleryEntry entry);

    // Usuwa wpis o danym id z galerii (plik zostaje); false = nie ma takiego wpisu
    bool Remove(string id);

    void Clear(bool voice);
}

// Operacje aplikacji bez okna: jawne żądania zamiast stanu zakładek. Nie dotyka widoków, podglądu ani odtwarzacza.
// Błędy wejścia to ArgumentException, brak modeli lub możliwości u dostawcy InvalidOperationException.
public interface IStudioApi
{
    // generate, edit, video, speech, transcription oraz chat (modele tekstowe), dla których dostawca ma modele
    IReadOnlyList<string> AvailableKinds();

    Task<StudioResult> GenerateImageAsync(ImageRequest request, CancellationToken ct = default);
    Task<StudioResult> EditImageAsync(EditRequest request, CancellationToken ct = default);
    Task<StudioResult> GenerateVideoAsync(VideoGenerationRequest request, CancellationToken ct = default);
    Task<StudioResult> SpeakAsync(SpeechRequest request, CancellationToken ct = default);
    Task<StudioResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default);

    // Aktywny dostawca i modele wybrane domyślnie (te same, których użyje wywołanie bez parametru model)
    DefaultsInfo GetDefaults();

    // Zapisuje domyślny model rodzaju (generate, edit, video, speech, transcription, chat); model musi być w katalogu dostawcy
    void SetDefaultModel(string kind, string model);

    // Modele obrazów i wideo (kind: generate, edit, video) z ceną; preferowane użytkownika na początku
    Task<IReadOnlyList<ModelSummary>> ModelsAsync(string kind, string? query, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<AudioModelSummary>> AudioModelsAsync(string kind, string? query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> VoicesAsync(string model, CancellationToken ct = default);

    // Przebudowa promptu i opis obrazów: nic nie zmieniają w aplikacji
    Task<PromptParts> RebuildPromptAsync(PromptRebuildRequest request, CancellationToken ct = default);
    Task<string> DescribeImagesAsync(DescribeRequest request, CancellationToken ct = default);

    IReadOnlyList<Preset> Presets(string? kind);
    Preset FindPreset(string name);
    Preset SavePreset(SavePresetRequest request);

    // kind: generate, edit, video, speech, transcript albo "media" / "voice"; null = wszystko, najnowsze pierwsze
    IReadOnlyList<GalleryEntry> List(string? kind, int limit);
    void RemoveEntry(string id);
    Task DeleteEntryAsync(string id, CancellationToken ct = default);
    void Clear(string scope);
    Task<string> ExportAsync(string id, string target, CancellationToken ct = default);
}
