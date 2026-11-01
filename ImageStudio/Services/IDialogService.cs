using System.Windows.Media.Imaging;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;

namespace ImageStudio.Services;

// Ustawienia edytowane w oknie Settings (wejście i wynik); null jako wynik = anulowano
public sealed record SettingsResult(string BaseUrl, string MarketUrl, int MaxSide, string ApiKey, string Theme,
                                   string ProviderId = "uberllm", string OpenRouterKey = "",
                                   string OpenAiBaseUrl = "https://api.openai.com", string OpenAiKey = "",
                                   IReadOnlyDictionary<string, bool>? PreferredOnly = null);

// Nazwa i opis nowego zestawu; null = anulowano
public sealed record PresetDraft(string Name, string Description);

// Wynik edytora promptu; System = null, gdy dany rodzaj nie ma promptu systemowego
public sealed record PromptEdit(string? System, string Prompt, string? Instruction = null);

// Interakcje z systemem i oknami, których ViewModel nie wykonuje sam
public interface IDialogService
{
    string[]? PickImageFiles();
    string? ChooseSaveFile(string suggestedFileName);
    void CopyImage(BitmapSource image);
    BitmapSource? GetClipboardImage();
    SettingsResult? ShowSettings(SettingsResult current);
    PresetDraft? ShowSavePreset(string defaultName);

    // Edytor promptu w oknie modalnym; null = anulowano. initial.System == null ukrywa sekcję konfiguracji,
    // initial.Instruction == null albo rebuild == null ukrywa sekcję promptu technicznego i przebudowy AI;
    // enhancerContext (DataContext panelu modelu i parametrów przetwarzania tekstu) null = bez panelu
    PromptEdit? ShowPromptEditor(string title, PromptEdit initial, Func<PromptEdit, Task<PromptEdit>>? rebuild = null,
                                 object? enhancerContext = null);

    void CopyText(string text);

    // Zgoda na wywołanie narzędzia, o które poprosił model; false = odmowa
    bool ConfirmToolCall(ToolCall call);

    // Modalna przeglądarka modeli z opisami i dodawaniem do preferowanych
    void ShowModelBrowser(ModelBrowserViewModel viewModel);

    // Zarządzanie zestawami; zwraca zestaw do wczytania albo null
    Preset? ShowPresets();
}
