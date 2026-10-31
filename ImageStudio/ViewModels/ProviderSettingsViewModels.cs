using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Konfiguracja jednego providera: każdy ma własny ViewModel i własny widok (szablon wybierany po typie)
public abstract class ProviderSettingsViewModel : ObservableObject
{
    public abstract string ProviderId { get; }
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

    // Adres bazowy: oficjalny OpenAI albo dowolny kompatybilny endpoint (proxy, serwer lokalny)
    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _apiKey;
}
