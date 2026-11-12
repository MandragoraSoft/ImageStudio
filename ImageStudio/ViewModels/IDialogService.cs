using System.Windows.Media.Imaging;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Ustawienia edytowane w oknie Settings (wejście i wynik); null jako wynik = anulowano.
// DefaultTextModels: preferowany model tekstowy per dostawca (null = domyślny dostawcy);
// ActiveTextModels (tylko wejście): modele tekstowe aktywnego dostawcy do selektora
public sealed record SettingsResult(string BaseUrl, string MarketUrl, int MaxSide, string ApiKey, string Theme,
                                   string ProviderId = "uberllm", string OpenRouterKey = "",
                                   string OpenAiBaseUrl = "https://api.openai.com", string OpenAiKey = "",
                                   IReadOnlyDictionary<string, bool>? PreferredOnly = null,
                                   IReadOnlyDictionary<string, string?>? DefaultTextModels = null,
                                   IReadOnlyList<string>? ActiveTextModels = null,
                                   bool UseStreaming = true,
                                   string? Microphone = null, string? Speaker = null,
                                   string Language = "system",
                                   string MistralKey = "", string KiloKey = "", string OpenCodeGoKey = "",
                                   string ChatGptSession = "",
                                   string? StorageId = null,
                                   IReadOnlyDictionary<string, Dictionary<string, string>>? StorageConfig = null,
                                   int McpMaxTools = McpLimits.DefaultMaxTools, int McpListLimit = McpLimits.DefaultListLimit,
                                   int McpMaxListLimit = McpLimits.DefaultMaxListLimit,
                                   string? GalleryArchiveFolder = null);

// Nazwa i opis nowego zestawu; null = anulowano
public sealed record PresetDraft(string Name, string Description);

// Wynik edytora promptu; System i NegativePrompt = null, gdy dane pole jest ukryte;
// History = kroki przebudowy AI (kontekst kolejnych)
public sealed record PromptEdit(string? System, string Prompt, string? Instruction = null,
                                IReadOnlyList<PromptRevision>? History = null,
                                string? NegativePrompt = null);

// Interakcje z systemem i oknami, których ViewModel nie wykonuje sam
public interface IDialogService
{
    string[]? PickImageFiles();
    string? ChooseSaveFile(string suggestedFileName);

    // Plik dźwiękowy do rozpoznania (null = anulowano) i zapis dowolnego pliku (filter w formacie okna systemowego: "Dźwięk|*.mp3")
    string? PickAudioFile();

    // Otwiera plik w programie domyślnym systemu (np. odtwarzacz dla mp3); błąd wyjątkiem
    void OpenFile(string path);
    string? ChooseSaveFileAs(string suggestedFileName, string title, string filter);
    void CopyImage(BitmapSource image);
    BitmapSource? GetClipboardImage();
// Zakładki okna są wstrzykiwane (ISettingsTab); wynik składa się z wartości wszystkich zakładek
    SettingsResult? ShowSettings(SettingsResult current, IReadOnlyList<ISettingsTab> tabs);
    void ShowAbout();
    // prompt: podgląd w oknie; suggest (opcjonalny) proponuje nazwę i opis przez AI
    PresetDraft? ShowSavePreset(string defaultName, string prompt = "", Func<Task<PresetDraft>>? suggest = null);

    // Edytor promptu w oknie modalnym; null = anulowano. initial.System == null ukrywa sekcję systemową,
    // initial.NegativePrompt == null ukrywa pole negatywne;
    // initial.Instruction == null albo rebuild == null ukrywa sekcję promptu technicznego i przebudowy AI;
    // enhancerContext (DataContext panelu modelu i parametrów przetwarzania tekstu) null = bez panelu;
    // describeImages (opcjonalny) włącza przycisk "Opisz obraz (AI)": zwraca opis obrazów do dopisania do konceptu
    // (pusty tekst = anulowano); presets (lista do wyboru) wraz z savePreset (zapis nadpisanego zestawu) włączają wczytywanie i nadpisywanie zestawów
    PromptEdit? ShowPromptEditor(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                                 object? enhancerContext = null,
                                 IReadOnlyList<Preset>? presets = null, Action<Preset>? savePreset = null,
                                 Func<Task<string>>? describeImages = null);

    void CopyText(string text);

    // Pytanie tak/nie; domyślnie odpowiedź "nie"
    bool Confirm(string text, string title);

    // Zgoda na wywołanie narzędzia, o które poprosił model; false = odmowa
    bool ConfirmToolCall(ToolCall call);

    // Modalna przeglądarka modeli z opisami i dodawaniem do preferowanych
    void ShowModelBrowser(ModelBrowserViewModel viewModel);

    // Zarządzanie zestawami; zwraca zestaw do wczytania albo null. Kontekst daje modele i selektory z głównego okna
    Preset? ShowPresets(PresetsContext? context = null);
}
