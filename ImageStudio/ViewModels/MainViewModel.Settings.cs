using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // --- Przeglądarka modeli ---

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void OpenModelBrowser()
    {
        var models = Models.Select(m => (m, "generowanie"))
            .Concat(EditModels.Select(m => (m, "edycja")))
            .Concat(VideoModels.Select(m => (m, "wideo")));
        var viewModel = new ModelBrowserViewModel(_provider.DisplayName, models, _prefs.For(_provider.Id), _prefs);
        _dialogs.ShowModelBrowser(viewModel);
        RefreshFilters();
    }

    // --- Ustawienia ---

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private async Task OpenSettingsAsync()
    {
        // Do okna idą tylko sekrety z SecretStore: wartość ze zmiennej środowiskowej nie może trafić do pliku
        var current = new SettingsResult(_settings.BaseUrl, _settings.MarketUrl, _settings.MaxSide,
                                         _secrets.Load() ?? "", _settings.Theme, _settings.ProviderId,
                                         _secrets.Load("openrouter") ?? "",
                                         _settings.OpenAiBaseUrl, _secrets.Load("openai") ?? "",
                                         ProviderRegistry.Available.ToDictionary(p => p.Id, p => _prefs.For(p.Id).PreferredOnly),
                                         ProviderRegistry.Available.ToDictionary(p => p.Id, p => _prefs.For(p.Id).DefaultTextModel),
                                         EnhancerModels.ToList());
        var result = _dialogs.ShowSettings(current, [new AppSettingsTab(current), new ProviderSettingsTab(current, LoadTextModelsAsync)]);
        if (result is null) return;

        foreach (var (id, preferredOnly) in result.PreferredOnly ?? new Dictionary<string, bool>())
            _prefs.For(id).PreferredOnly = preferredOnly;
        foreach (var (id, model) in result.DefaultTextModels ?? new Dictionary<string, string?>())
            _prefs.For(id).DefaultTextModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        _prefs.Save();

        _settingsStore.Save(result.BaseUrl, result.MarketUrl, result.MaxSide, result.ApiKey, result.Theme,
                         result.ProviderId, result.OpenRouterKey, result.OpenAiBaseUrl, result.OpenAiKey);
        var previous = _settings;
        _settings = _settingsStore.Load();
        if (_settings.Theme != previous.Theme) _themes.Apply(result.Theme);

        // Zmiana samego motywu lub preferencji nie wymaga nowego providera ani ponownego pobierania list modeli
        if (_settings with { Theme = "" } == previous with { Theme = "" })
        {
            PreferredOnly = _prefs.For(_provider.Id).PreferredOnly;
            Status = "Zapisano ustawienia.";
            return;
        }

        // Nowe ustawienia (także inny provider) = nowa instancja providera
        await SwitchProviderAsync(_providers.Create(result.ProviderId, _settings));
    }

    // Modele tekstowe dostawcy wybranego w oknie ustawień, pobrane z jeszcze niezapisanymi adresami i kluczami.
    // Zmienna środowiskowa z kluczem ma pierwszeństwo, tak jak przy zwykłym wczytaniu ustawień
    private async Task<IReadOnlyList<string>> LoadTextModelsAsync(SettingsResult draft)
    {
        static string Key(string envVar, string typed) =>
            Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } env ? env : typed;

        var settings = new AppSettings(draft.BaseUrl, draft.MarketUrl, draft.MaxSide, Key("UBERLLM_API_KEY", draft.ApiKey), draft.Theme,
                                       draft.ProviderId, Key("OPENROUTER_API_KEY", draft.OpenRouterKey), draft.OpenAiBaseUrl,
                                       Key("OPENAI_API_KEY", draft.OpenAiKey));
        using var provider = _providers.Create(draft.ProviderId, settings);
        return (await provider.GetChatModelsAsync()).Select(m => m.Id).ToList();
    }

    // Jedyne miejsce wymiany providera: zwalnia starego, odświeża bindowane właściwości i listy modeli.
    // Filtry i wybory z poprzedniego providera są zerowane, bo inaczej ukryłyby wszystkie nowe modele.
    private async Task SwitchProviderAsync(IAiProvider provider)
    {
        _provider.Dispose();
        _provider = provider;
        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(ProviderName));

        SearchText = "";
        SelectedProvider = AllProviders;
        SelectedModel = null;
        SelectedEditModel = null;
        SelectedVideoModel = null;
        await InitializeAsync();
    }

    // --- Zestawy ustawień ---

    // Czy zestaw różni się od ostatniego zapisu lub wczytania; steruje przyciskiem "Zapisz zestaw"
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private bool _presetDirty;

    private bool CanSavePreset => !IsBusy && PresetDirty;

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var defaultName = CurrentPromptForTab().Trim() is { Length: > 0 } text
            ? text.Length > 40 ? text[..40] + "..." : text
            : $"Zestaw {DateTime.Now:dd.MM HH:mm}";

        var draft = _dialogs.ShowSavePreset(defaultName, CurrentPromptForTab(),
                                           SelectedEnhancerModel is null ? null : SuggestPresetDraftAsync);
        if (draft is null) return;

        _presetStore.Create(CapturePreset(draft));
        PresetDirty = false;
        Status = $"Zapisano zestaw: {draft.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void OpenPresets()
    {
        var preset = _dialogs.ShowPresets();
        if (preset is not null) ApplyPreset(preset);
    }

    // AI proponuje nazwę i opis zestawu na podstawie promptu, modelem tekstowym wybranym w ulepszaniu
    private async Task<PresetDraft> SuggestPresetDraftAsync()
    {
        var prompt = CurrentPromptForTab();
        var system = ActiveTab == 1 ? EditSystemPrompt : GenerateSystemPrompt;
        var messages = new List<ChatMessage>
        {
            new("system", "You name saved presets of an image generation app. Reply with JSON only, no code fences: " +
                          "{\"name\": \"...\", \"description\": \"...\"}. name = at most 5 words, description = one short sentence. " +
                          "Use the language of the prompt."),
            new("user", string.IsNullOrWhiteSpace(system) ? prompt : $"CONFIG:\n{system}\n\nPROMPT:\n{prompt}"),
        };
        var reply = await _provider.ChatAsync(SelectedEnhancerModel!, messages);
        return PresetSaveViewModel.ParseDraft(reply.Content);
    }

    private string CurrentPromptForTab() => ActiveTab == 1 ? EditPrompt : GeneratePrompt;

    // Zestaw zapisuje wszystko, co wpływa na wynik: prompt, model, rozmiar, parametry i ulepszanie
    private Preset CapturePreset(PresetDraft draft) => new()
    {
        Name = draft.Name,
        Description = draft.Description,
        Kind = ActiveTab == 1 ? "edit" : "generate",
        Prompt = CurrentPromptForTab(),
        SystemPrompt = ActiveTab == 1 ? EditSystemPrompt : GenerateSystemPrompt,
        Model = ActiveTab == 1 ? SelectedEditModel?.Id : SelectedModel?.Id,
        Width = Width,
        Height = Height,
        SeedText = SeedText,
        NegativePrompt = NegativePrompt,
        Quality = SelectedQuality,
        EnhancerModel = SelectedEnhancerModel,
        EnhancerInstruction = EnhancerInstruction,
        Parameters = EnhancerParameters.ToDictionary(p => p.Key, p => new ParamState { Enabled = p.Enabled, Value = p.Value }),
    };

    // Wczytanie przełącza na zakładkę zestawu i ustawia wszystkie jego wartości
    private void ApplyPreset(Preset preset)
    {
        _generateHistory = _editHistory = null;
        var isEdit = preset.Kind == "edit";
        if (isEdit)
        {
            EditPrompt = preset.Prompt;
            EditSystemPrompt = preset.SystemPrompt;
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == preset.Model) ?? SelectedEditModel;
        }
        else
        {
            GeneratePrompt = preset.Prompt;
            GenerateSystemPrompt = preset.SystemPrompt;
            SelectedModel = Models.FirstOrDefault(m => m.Id == preset.Model) ?? SelectedModel;
            SeedText = preset.SeedText ?? "";
            NegativePrompt = preset.NegativePrompt ?? "";
            SelectedQuality = preset.Quality ?? "auto";
        }

        SetDimensions(preset.Width, preset.Height);
        EnhancerInstruction = string.IsNullOrWhiteSpace(preset.EnhancerInstruction) ? DefaultInstruction : preset.EnhancerInstruction;
        if (preset.EnhancerModel is not null && EnhancerModels.Contains(preset.EnhancerModel))
            SelectedEnhancerModel = preset.EnhancerModel;

        // Parametry trafiają do stanu, potem lista AI przebudowuje się dla bieżącego modelu
        foreach (var (key, value) in preset.Parameters) _state.EnhancerParams[key] = value;
        RebuildEnhancerParameters();

        ActiveTab = isEdit ? 1 : 0;
        Status = $"Wczytano zestaw: {preset.Name}";
        SaveState();
        PresetDirty = false; // wczytany zestaw jest zgodny z zapisem
    }

    // Ustawia wymiary bez przeliczania proporcji (wartości z zestawu są już spójne)
    private void SetDimensions(int width, int height)
    {
        _syncing = true;
        try
        {
            Width = width;
            Height = height;
        }
        finally
        {
            _syncing = false;
        }
        _aspect = height > 0 ? (double)width / height : 1.0;
    }
}
