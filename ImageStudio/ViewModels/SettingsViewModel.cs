using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

public sealed record ThemeOption(string Code, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeService.System, "Systemowy"),
        new(ThemeService.Light, "Jasny"),
        new(ThemeService.Dark, "Ciemny"),
    ];

    public IReadOnlyList<ProviderRegistry.ProviderInfo> Providers { get; } = ProviderRegistry.Available;

    // Ustawienia ogólne (nie zależą od providera)
    [ObservableProperty]
    private int _maxSide;

    [ObservableProperty]
    private string _selectedTheme;

    // Wybór providera przełącza komponent konfiguracji pod dropdownem
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProvider))]
    private string _selectedProvider;

    public UberLlmSettingsViewModel UberLlm { get; }
    public OpenRouterSettingsViewModel OpenRouter { get; }
    public OpenAiSettingsViewModel OpenAi { get; }

    public ProviderSettingsViewModel ActiveProvider => SelectedProvider switch
    {
        OpenRouterProvider.Identifier => OpenRouter,
        OpenAiProvider.Identifier => OpenAi,
        _ => UberLlm,
    };

    // Pobiera modele tekstowe dostawcy z ustawieniami wpisanymi w oknie (jeszcze niezapisanymi); null = brak odświeżania
    private readonly Func<SettingsResult, Task<IReadOnlyList<string>>>? _loadTextModels;

    [ObservableProperty]
    private string _textModelsStatus = "";

    private bool CanReloadTextModels => _loadTextModels is not null;

    // Odświeża listę modeli tekstowych aktywnego dostawcy, np. po wpisaniu klucza
    [RelayCommand(CanExecute = nameof(CanReloadTextModels))]
    private async Task ReloadTextModelsAsync()
    {
        var provider = ActiveProvider;
        TextModelsStatus = "Pobieranie modeli...";
        try
        {
            var models = await _loadTextModels!(ToResult());
            provider.SetTextModels(models, provider.PreferredTextModelOrNull);
            TextModelsStatus = $"Pobrano modeli: {models.Count}";
        }
        catch (Exception ex)
        {
            TextModelsStatus = $"Nie udało się pobrać modeli: {ex.Message}";
        }
    }

    // Bieżący stan okna jako wynik (wartości przycięte); używany przy zapisie i przy odświeżaniu modeli
    public SettingsResult ToResult() => new(
        UberLlm.BaseUrl.Trim(), UberLlm.MarketUrl.Trim(), MaxSide, UberLlm.ApiKey.Trim(), SelectedTheme, SelectedProvider,
        OpenRouter.ApiKey.Trim(), OpenAi.BaseUrl.Trim(), OpenAi.ApiKey.Trim(),
        new Dictionary<string, bool>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredOnly,
            [OpenRouter.ProviderId] = OpenRouter.PreferredOnly,
            [OpenAi.ProviderId] = OpenAi.PreferredOnly,
        },
        new Dictionary<string, string?>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredTextModelOrNull,
            [OpenRouter.ProviderId] = OpenRouter.PreferredTextModelOrNull,
            [OpenAi.ProviderId] = OpenAi.PreferredTextModelOrNull,
        });

    public SettingsViewModel(SettingsResult current, Func<SettingsResult, Task<IReadOnlyList<string>>>? loadTextModels = null)
    {
        _loadTextModels = loadTextModels;
        _maxSide = current.MaxSide;
        _selectedTheme = current.Theme;
        _selectedProvider = current.ProviderId;
        UberLlm = new UberLlmSettingsViewModel(current.BaseUrl, current.MarketUrl, current.ApiKey)
            { ProviderDefaultTextModel = UberLlmProvider.DefaultTextModelId };
        OpenRouter = new OpenRouterSettingsViewModel(current.OpenRouterKey)
            { ProviderDefaultTextModel = OpenRouterProvider.DefaultTextModelId };
        OpenAi = new OpenAiSettingsViewModel(current.OpenAiBaseUrl, current.OpenAiKey)
            { ProviderDefaultTextModel = OpenAiProvider.DefaultTextModelId };
        foreach (var provider in new ProviderSettingsViewModel[] { UberLlm, OpenRouter, OpenAi })
        {
            provider.PreferredOnly = current.PreferredOnly?.GetValueOrDefault(provider.ProviderId) ?? false;
            provider.SetTextModels(provider.ProviderId == current.ProviderId ? current.ActiveTextModels ?? [] : [],
                                   current.DefaultTextModels?.GetValueOrDefault(provider.ProviderId));
        }
    }
}
