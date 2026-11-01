using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;
using ImageStudio.Localization;

namespace ImageStudio.ViewModels;

// Zakładka "Dostawca AI": wybór dostawcy i jego konfiguracja (adresy, klucz, preferowane modele, domyślny model tekstowy)
public sealed partial class ProviderSettingsTab : ObservableObject, ISettingsTab
{
    public string Title => Loc.T("settings.tab.provider");
    public int Order => 10;

    public IReadOnlyList<ProviderRegistry.ProviderInfo> Providers { get; } = ProviderRegistry.Available;

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
    private readonly SettingsResult _current;
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
            var models = await _loadTextModels!(Apply(_current));
            provider.SetTextModels(models, provider.PreferredTextModelOrNull);
            TextModelsStatus = $"Pobrano modeli: {models.Count}";
        }
        catch (Exception ex)
        {
            TextModelsStatus = $"Nie udało się pobrać modeli: {ex.Message}";
        }
    }

    // Nakłada wartości zakładki na wynik (przycięte); używane przy zapisie okna i przy odświeżaniu modeli
    public SettingsResult Apply(SettingsResult result) => result with
    {
        BaseUrl = UberLlm.BaseUrl.Trim(),
        MarketUrl = UberLlm.MarketUrl.Trim(),
        ApiKey = UberLlm.ApiKey.Trim(),
        ProviderId = SelectedProvider,
        OpenRouterKey = OpenRouter.ApiKey.Trim(),
        OpenAiBaseUrl = OpenAi.BaseUrl.Trim(),
        OpenAiKey = OpenAi.ApiKey.Trim(),
        PreferredOnly = new Dictionary<string, bool>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredOnly,
            [OpenRouter.ProviderId] = OpenRouter.PreferredOnly,
            [OpenAi.ProviderId] = OpenAi.PreferredOnly,
        },
        DefaultTextModels = new Dictionary<string, string?>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredTextModelOrNull,
            [OpenRouter.ProviderId] = OpenRouter.PreferredTextModelOrNull,
            [OpenAi.ProviderId] = OpenAi.PreferredTextModelOrNull,
        },
    };

    public ProviderSettingsTab(SettingsResult current, Func<SettingsResult, Task<IReadOnlyList<string>>>? loadTextModels = null)
    {
        _current = current;
        _loadTextModels = loadTextModels;
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
