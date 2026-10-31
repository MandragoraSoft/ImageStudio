using System.Windows.Media.Imaging;
using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Ustawienia edytowane w oknie Settings (wejście i wynik); null jako wynik = anulowano
public sealed record SettingsResult(string BaseUrl, string MarketUrl, int MaxSide, string ApiKey, string Theme,
                                   string ProviderId = "uberllm", string OpenRouterKey = "",
                                   string OpenAiBaseUrl = "https://api.openai.com", string OpenAiKey = "");

// Nazwa i opis nowego zestawu; null = anulowano
public sealed record PresetDraft(string Name, string Description);

// Interakcje z systemem i oknami, których ViewModel nie wykonuje sam
public interface IDialogService
{
    string[]? PickImageFiles();
    string? ChooseSaveFile(string suggestedFileName);
    void CopyImage(BitmapSource image);
    BitmapSource? GetClipboardImage();
    SettingsResult? ShowSettings(SettingsResult current);
    PresetDraft? ShowSavePreset(string defaultName);

    // Zarządzanie zestawami; zwraca zestaw do wczytania albo null
    Preset? ShowPresets();
}
