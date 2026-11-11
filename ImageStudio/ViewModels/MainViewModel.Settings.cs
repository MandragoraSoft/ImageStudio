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
using ImageStudio.Storage;
using Microsoft.Extensions.Logging;
using ImageStudio.Localization;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // --- Przeglądarka modeli ---

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void OpenModelBrowser()
    {
        var models = Models.Select(m => (m, "generowanie"))
            .Concat(EditModels.Select(m => (m, "edycja")))
            .Concat(VideoModels.Select(m => (m, "wideo")))
            .Concat(ChatModelOptions.Select(m => (m, "czat")))
            .Concat(Voice.SpeechModels.Select(m => (m, "mowa")))
            .Concat(Voice.TranscriptionModels.Select(m => (m, "transkrypcja")));
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
                                         EnhancerModels.ToList(), _settings.UseStreaming,
                                         Voice.SelectedMicrophone?.Name, Voice.SelectedSpeaker?.Name, _settings.Language,
                                         _secrets.Load("mistral") ?? "", _secrets.Load("kilo") ?? "", _secrets.Load("opencodego") ?? "",
                                         _secrets.Load(ProviderRegistry.ChatGptSecret) ?? "",
                                         _storage.Active.Id, null, Limits.MaxTools, Limits.ListLimit, Limits.MaxListLimit);
        Voice.RefreshDevices();
        var result = _dialogs.ShowSettings(current, [new AppSettingsTab(current),
                                                     new AudioSettingsTab(current, Voice.Microphones.ToList(), Voice.Speakers.ToList()),
                                                     new StorageSettingsTab(current, _storage),
                                                     new McpSettingsTab(current),
                                                     new ProviderSettingsTab(current, LoadTextModelsAsync, SignInToChatGptAsync)]);
        if (result is null) return;

        Voice.ApplyDevices(result.Microphone, result.Speaker);
        Limits.Set(result.McpMaxTools, result.McpListLimit, result.McpMaxListLimit);

        _storage.Apply(result.StorageId, result.StorageConfig);
        _state.StorageId = _storage.Active.Id;
        _state.StorageConfig = result.StorageConfig is { } config ? _storage.WithoutSecrets(config) : _state.StorageConfig;
        SyncVoiceFolder();
        SaveState();

        foreach (var (id, preferredOnly) in result.PreferredOnly ?? new Dictionary<string, bool>())
            _prefs.For(id).PreferredOnly = preferredOnly;
        foreach (var (id, model) in result.DefaultTextModels ?? new Dictionary<string, string?>())
            _prefs.For(id).DefaultTextModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        _prefs.Save();

        _settingsStore.Save(result.BaseUrl, result.MarketUrl, result.MaxSide, result.ApiKey, result.Theme,
                         result.ProviderId, result.OpenRouterKey, result.OpenAiBaseUrl, result.OpenAiKey, result.UseStreaming, result.Language,
                         result.MistralKey, result.KiloKey, result.OpenCodeGoKey, result.ChatGptSession);
        var previous = _settings;
        _settings = _settingsStore.Load();
        if (_settings.Theme != previous.Theme) _themes.Apply(result.Theme);
        Loc.Set(_settings.Language);

        // Zmiana samego motywu lub preferencji nie wymaga nowego providera ani ponownego pobierania list modeli
        if (_settings with { Theme = "", UseStreaming = true, Language = "" } == previous with { Theme = "", UseStreaming = true, Language = "" })
        {
            ApplyPreferredOnly();
            Status = Loc.T("settings.msg.saved");
            return;
        }

        // Nowe ustawienia (także inny provider) = nowa instancja providera
        await SwitchProviderAsync(_providers.Create(result.ProviderId, _settings));
    }

    // Nagrania i strażnik usuwania w zakładce Głos trzymają się folderu dźwięku aktywnej wtyczki (chmura = bez zmiany)
    private void SyncVoiceFolder()
    {
        if (_storage.Active.LocalFolder(MediaKind.Audio) is { } folder) Voice.OutputDir = folder;
    }

    // Modele tekstowe dostawcy wybranego w oknie ustawień, pobrane z jeszcze niezapisanymi adresami i kluczami.
    // Zmienna środowiskowa z kluczem ma pierwszeństwo, tak jak przy zwykłym wczytaniu ustawień
    private async Task<IReadOnlyList<string>> LoadTextModelsAsync(SettingsResult draft)
    {
        static string Key(string envVar, string typed) =>
            Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } env ? env : typed;

        var settings = new AppSettings(draft.BaseUrl, draft.MarketUrl, draft.MaxSide, Key("UBERLLM_API_KEY", draft.ApiKey), draft.Theme,
                                       draft.ProviderId, Key("OPENROUTER_API_KEY", draft.OpenRouterKey), draft.OpenAiBaseUrl,
                                       Key("OPENAI_API_KEY", draft.OpenAiKey),
                                       MistralKey: Key("MISTRAL_API_KEY", draft.MistralKey), KiloKey: Key("KILO_API_KEY", draft.KiloKey),
                                       OpenCodeGoKey: Key("OPENCODE_API_KEY", draft.OpenCodeGoKey),
                                       ChatGptSession: draft.ChatGptSession);
        using var provider = _providers.Create(draft.ProviderId, settings);
        return (await provider.GetChatModelsAsync()).Select(m => m.Id).ToList();
    }

    // Logowanie "Sign in with ChatGPT" z okna ustawień: strona logowania w przeglądarce systemowej, powrót na 127.0.0.1
    private Task<ChatGptSession> SignInToChatGptAsync(ChatGptSession? previous, CancellationToken ct) =>
        new ChatGptAuth().SignInAsync(previous, "ImageStudio", _dialogs.OpenFile, Loc.T("provider.chatgpt.done"), ct);

    // Jedyne miejsce wymiany providera: zwalnia starego, odświeża bindowane właściwości i listy modeli.
    // Filtry i wybory z poprzedniego providera są zerowane, bo inaczej ukryłyby wszystkie nowe modele.
    private async Task SwitchProviderAsync(IAiProvider provider)
    {
        _provider.Dispose();
        _provider = provider;
        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(ProviderName));

        foreach (var filter in AllFilters) filter.Reset();
        SelectedModel = null;
        SelectedEditModel = null;
        SelectedVideoModel = null;
        Voice.Reset();
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
        Status = Loc.T("presets.msg.saved", draft.Name);
    }

    // Okno zestawów dostaje własne widoki modeli nad tymi samymi listami (współdzielony widok zsynchronizowałby zaznaczenie
    // z selektorem głównego okna) z filtrem zakładki. Subskrypcje filtra zdejmujemy po zamknięciu okna. Widok tworzony wprost
    // (nie tymczasowym CollectionViewSource), bo ten bywa zbierany przez GC
    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void OpenPresets()
    {
        var detach = new List<Action>();
        var pickers = new Dictionary<string, PresetModelPicker>();
        foreach (var (kind, filter) in new[] { ("generate", GenerateFilter), ("edit", EditFilter) })
        {
            var view = new ListCollectionView(ModelsFor(kind));
            view.Filter = filter.Matches;
            void Refresh() => view.Refresh();
            filter.Changed += Refresh;
            detach.Add(() => filter.Changed -= Refresh);
            pickers[kind] = new PresetModelPicker(view, filter);
        }

        try
        {
            var context = new PresetsContext(kind => kind is "generate" or "edit" ? ModelsFor(kind) : [], _provider.Id, pickers);
            var preset = _dialogs.ShowPresets(context);
            if (preset is not null) ApplyPreset(preset);
        }
        finally
        {
            foreach (var release in detach) release();
        }
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
        Provider = _provider.Id,
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
        Status = Loc.T("presets.msg.loaded", preset.Name);
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
