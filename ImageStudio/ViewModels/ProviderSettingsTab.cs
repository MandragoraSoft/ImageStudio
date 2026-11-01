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
    public ChatGptSettingsViewModel ChatGpt { get; }
    public KeyOnlySettingsViewModel Mistral { get; }
    public KeyOnlySettingsViewModel Kilo { get; }
    public KeyOnlySettingsViewModel OpenCodeGo { get; }

    public ProviderSettingsViewModel ActiveProvider => SelectedProvider switch
    {
        OpenRouterProvider.Identifier => OpenRouter,
        OpenAiProvider.Identifier => OpenAi,
        ChatGptProvider.Identifier => ChatGpt,
        MistralProvider.Identifier => Mistral,
        KiloProvider.Identifier => Kilo,
        OpenCodeGoProvider.Identifier => OpenCodeGo,
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
        TextModelsStatus = Loc.T("models.msg.loadingShort");
        try
        {
            var models = await _loadTextModels!(Apply(_current));
            provider.SetTextModels(models, provider.PreferredTextModelOrNull);
            TextModelsStatus = Loc.T("models.msg.count", models.Count);
        }
        catch (Exception ex)
        {
            TextModelsStatus = Loc.T("models.msg.failed", ex.Message);
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
        ChatGptSession = ChatGpt.SessionJson,
        MistralKey = Mistral.ApiKey.Trim(),
        KiloKey = Kilo.ApiKey.Trim(),
        OpenCodeGoKey = OpenCodeGo.ApiKey.Trim(),
        PreferredOnly = new Dictionary<string, bool>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredOnly,
            [OpenRouter.ProviderId] = OpenRouter.PreferredOnly,
            [OpenAi.ProviderId] = OpenAi.PreferredOnly,
            [ChatGpt.ProviderId] = ChatGpt.PreferredOnly,
            [Mistral.ProviderId] = Mistral.PreferredOnly,
            [Kilo.ProviderId] = Kilo.PreferredOnly,
            [OpenCodeGo.ProviderId] = OpenCodeGo.PreferredOnly,
        },
        DefaultTextModels = new Dictionary<string, string?>
        {
            [UberLlm.ProviderId] = UberLlm.PreferredTextModelOrNull,
            [OpenRouter.ProviderId] = OpenRouter.PreferredTextModelOrNull,
            [OpenAi.ProviderId] = OpenAi.PreferredTextModelOrNull,
            [ChatGpt.ProviderId] = ChatGpt.PreferredTextModelOrNull,
            [Mistral.ProviderId] = Mistral.PreferredTextModelOrNull,
            [Kilo.ProviderId] = Kilo.PreferredTextModelOrNull,
            [OpenCodeGo.ProviderId] = OpenCodeGo.PreferredTextModelOrNull,
        },
    };

    public ProviderSettingsTab(SettingsResult current, Func<SettingsResult, Task<IReadOnlyList<string>>>? loadTextModels = null,
                               Func<ChatGptSession?, CancellationToken, Task<ChatGptSession>>? signInToChatGpt = null)
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
        ChatGpt = new ChatGptSettingsViewModel(current.ChatGptSession, signInToChatGpt);
        Mistral = new KeyOnlySettingsViewModel(MistralProvider.Identifier, "MISTRAL_API_KEY", current.MistralKey, "provider.mistral.note")
            { ProviderDefaultTextModel = MistralProvider.DefaultTextModelId };
        Kilo = new KeyOnlySettingsViewModel(KiloProvider.Identifier, "KILO_API_KEY", current.KiloKey, "provider.kilo.note")
            { ProviderDefaultTextModel = KiloProvider.DefaultTextModelId };
        OpenCodeGo = new KeyOnlySettingsViewModel(OpenCodeGoProvider.Identifier, "OPENCODE_API_KEY", current.OpenCodeGoKey, "provider.opencodego.note")
            { ProviderDefaultTextModel = OpenCodeGoProvider.DefaultTextModelId };
        foreach (var provider in new ProviderSettingsViewModel[] { UberLlm, OpenRouter, OpenAi, ChatGpt, Mistral, Kilo, OpenCodeGo })
        {
            provider.PreferredOnly = current.PreferredOnly?.GetValueOrDefault(provider.ProviderId) ?? false;
            provider.SetTextModels(provider.ProviderId == current.ProviderId ? current.ActiveTextModels ?? [] : [],
                                   current.DefaultTextModels?.GetValueOrDefault(provider.ProviderId));
        }
    }
}
