using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public SettingsViewModel(SettingsResult current)
    {
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
