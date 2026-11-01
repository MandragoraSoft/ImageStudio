using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Konfiguracja jednego providera: każdy ma własny ViewModel i własny widok (szablon wybierany po typie)
public abstract partial class ProviderSettingsViewModel : ObservableObject
{
    public abstract string ProviderId { get; }

    // Zmienna środowiskowa z kluczem dostawcy (ma pierwszeństwo nad zapisanym sekretem)
    public abstract string EnvVar { get; }

    // Ostrzeżenie w oknie ustawień; null, gdy zmienna nie jest ustawiona
    public string? EnvNotice => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }
        ? $"Zmienna środowiskowa {EnvVar} jest ustawiona i ma pierwszeństwo nad kluczem wpisanym tutaj."
        : null;

    // Lista modeli tego dostawcy: false = wszystkie, true = tylko preferowane
    [ObservableProperty]
    private bool _preferredOnly;

    // Preferowany model tekstowy tego dostawcy wybierany w standardowym selektorze modeli (ModelPicker);
    // lista zawiera modele znane aplikacji (aktywny dostawca) oraz model domyślny dostawcy
    public ObservableCollection<string> TextModels { get; } = [];

    [ObservableProperty]
    private string? _defaultTextModel;

    public string? ProviderDefaultTextModel { get; init; }

    // Do zapisu: null, gdy wybrano model domyślny dostawcy (wtedy zmiana domyślnego w dostawcy działa od razu)
    public string? PreferredTextModelOrNull => DefaultTextModel == ProviderDefaultTextModel ? null : DefaultTextModel;

    public void SetTextModels(IEnumerable<string> known, string? preferred)
    {
        TextModels.Clear();
        foreach (var model in known.Append(ProviderDefaultTextModel).Append(preferred).OfType<string>().Distinct())
            TextModels.Add(model);
        DefaultTextModel = preferred ?? ProviderDefaultTextModel;
    }
}

public sealed partial class UberLlmSettingsViewModel : ProviderSettingsViewModel
{
    public UberLlmSettingsViewModel(string baseUrl, string marketUrl, string apiKey)
    {
        _baseUrl = baseUrl;
        _marketUrl = marketUrl;
        _apiKey = apiKey;
    }

    public override string ProviderId => UberLlmProvider.Identifier;
    public override string EnvVar => "UBERLLM_API_KEY";

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _marketUrl;

    [ObservableProperty]
    private string _apiKey;
}

public sealed partial class OpenRouterSettingsViewModel : ProviderSettingsViewModel
{
    public OpenRouterSettingsViewModel(string apiKey)
    {
        _apiKey = apiKey;
    }

    public override string ProviderId => OpenRouterProvider.Identifier;
    public override string EnvVar => "OPENROUTER_API_KEY";

    [ObservableProperty]
    private string _apiKey;
}

public sealed partial class OpenAiSettingsViewModel : ProviderSettingsViewModel
{
    public OpenAiSettingsViewModel(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
    }

    public override string ProviderId => OpenAiProvider.Identifier;
    public override string EnvVar => "OPENAI_API_KEY";

    // Adres bazowy: oficjalny OpenAI albo dowolny kompatybilny endpoint (proxy, serwer lokalny)
    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _apiKey;
}
