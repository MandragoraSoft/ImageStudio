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
    private readonly OutgoingRequestRegistry? _requests;
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
                                         _storage.Active.Id, null, Limits.MaxTools, Limits.ListLimit, Limits.MaxListLimit,
                                         _state.GalleryArchiveFolder, _state.PromptChatHistory, _settings.HideToTrayOnClose);
        Voice.RefreshDevices();
        var result = _dialogs.ShowSettings(current, [new AppSettingsTab(current, ClearSelectedUserDataAsync),
                                                     new AudioSettingsTab(current, Voice.Microphones.ToList(), Voice.Speakers.ToList()),
                                                     new StorageSettingsTab(current, _storage),
                                                     new McpSettingsTab(current),
                                                     new ProviderSettingsTab(current, LoadTextModelsAsync, SignInToChatGptAsync)]);
        if (result is null || _resetting) return;

        Voice.ApplyDevices(result.Microphone, result.Speaker);
        Limits.Set(result.McpMaxTools, result.McpListLimit, result.McpMaxListLimit);

        _storage.Apply(result.StorageId, result.StorageConfig);
        _state.StorageId = _storage.Active.Id;
        _state.StorageConfig = result.StorageConfig is { } config ? _storage.WithoutSecrets(config) : _state.StorageConfig;
        _state.GalleryArchiveFolder = string.IsNullOrWhiteSpace(result.GalleryArchiveFolder)
            ? UserState.DefaultGalleryArchiveFolder
            : result.GalleryArchiveFolder.Trim();
        _state.PromptChatHistory = result.PromptChatHistory;
        SyncVoiceFolder();
        SaveState();

        foreach (var (id, preferredOnly) in result.PreferredOnly ?? new Dictionary<string, bool>())
            _prefs.For(id).PreferredOnly = preferredOnly;
        foreach (var (id, model) in result.DefaultTextModels ?? new Dictionary<string, string?>())
            _prefs.For(id).DefaultTextModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        _prefs.Save();

        _settingsStore.Save(result.BaseUrl, result.MarketUrl, result.MaxSide, result.ApiKey, result.Theme,
                         result.ProviderId, result.OpenRouterKey, result.OpenAiBaseUrl, result.OpenAiKey, result.UseStreaming, result.Language,
                         result.MistralKey, result.KiloKey, result.OpenCodeGoKey, result.ChatGptSession, result.HideToTrayOnClose);
        var previous = _settings;
        _settings = _settingsStore.Load();
        HideToTrayOnClose = _settings.HideToTrayOnClose;
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

    private async Task ClearSelectedUserDataAsync(UserDataCleanupSelection selection)
    {
        if (_resetting || !selection.AnySelected) return;

        var selected = new List<string>();
        if (selection.SettingsAndPrompts) selected.Add(Loc.T("settings.dataCleanup.category.settings"));
        if (selection.ChatHistoryAndMemory) selected.Add(Loc.T("settings.dataCleanup.category.history"));
        if (selection.Presets) selected.Add(Loc.T("settings.dataCleanup.category.presets"));
        if (selection.GalleryIndexes) selected.Add(Loc.T("settings.dataCleanup.category.galleries"));
        if (selection.McpConfiguration) selected.Add(Loc.T("settings.dataCleanup.category.mcp"));
        if (selection.WindowLayouts) selected.Add(Loc.T("settings.dataCleanup.category.windows"));
        if (selection.ProviderLoginsAndKeys) selected.Add(Loc.T("settings.dataCleanup.category.credentials"));

        var details = Loc.T("settings.dataCleanup.confirm.details");
        if (selection.Presets)
            details += Environment.NewLine + Loc.T("settings.dataCleanup.confirm.presets");
        if (selection.McpConfiguration)
            details += Environment.NewLine + Loc.T("settings.dataCleanup.confirm.mcp");
        if (selection.ProviderLoginsAndKeys)
            details += Environment.NewLine + Loc.T("settings.dataCleanup.confirm.credentials");

        if (!_dialogs.Confirm(Loc.T("settings.dataCleanup.confirm", string.Join(Environment.NewLine, selected), details),
                              Loc.T("settings.dataCleanup.confirm.title")))
            return;

        _resetting = true;
        IsBusy = true;
        var deletionStarted = false;
        try
        {
            UserDataMutationGate.BlockWrites(_dataCleanup.RootPath, selection);
            if (_ready) SaveStateCore();
            _requests?.StopAndCancelAll();
            Chat.StopCommand.Execute(null);
            Voice.StopCurrentOperation();
            await Tools.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            _provider.Dispose();
            if (_requests is not null && !await _requests.WaitForIdleAsync(TimeSpan.FromSeconds(5)))
                throw new TimeoutException(Loc.T("settings.dataCleanup.error.active"));

            deletionStarted = true;
            _dataCleanup.Clean(selection);
        }
        catch (Exception ex)
        {
            var message = deletionStarted
                ? Loc.T("settings.dataCleanup.error", ex.Message)
                : Loc.T("settings.dataCleanup.error.stopFailed", ex.Message);
            _dialogs.ShowError(message,
                               Loc.T("settings.dataCleanup.error.title"));
        }
        finally
        {
            _applicationLifetime.Exit();
        }
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
    private async Task<ChatGptSession> SignInToChatGptAsync(ChatGptSession? previous, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient(_requests?.CreateHandler() ?? new System.Net.Http.HttpClientHandler())
            { Timeout = TimeSpan.FromSeconds(30) };
        return await new ChatGptAuth(http).SignInAsync(previous, "ImageStudio", _dialogs.OpenFile, Loc.T("provider.chatgpt.done"), ct);
    }

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
                                           SelectedEnhancerModel is null ? null
                                           : () => SuggestPresetDraftAsync(CurrentPromptForTab(), ActiveTab == 1 ? EditSystemPrompt : GenerateSystemPrompt, null));
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
        foreach (var (kind, source, filter) in new (string, ObservableCollection<ModelOption>, ModelFilter)[]
                 {
                     ("generate", Models, GenerateFilter), ("edit", EditModels, EditFilter), ("enhancer", ChatModelOptions, EnhancerFilter),
                 })
        {
            var view = new ListCollectionView(source);
            view.Filter = filter.Matches;
            void Refresh() => view.Refresh();
            filter.Changed += Refresh;
            detach.Add(() => filter.Changed -= Refresh);
            pickers[kind] = new PresetModelPicker(view, filter);
        }

        try
        {
            var context = new PresetsContext(
                kind => kind switch { "enhancer" => ChatModelOptions, "generate" or "edit" => ModelsFor(kind), _ => [] },
                _provider.Id, pickers,
                Suggest: request => SuggestPresetDraftAsync(request.Prompt, request.SystemPrompt, request.Model),
                EditPrompt: EditPresetPrompt,
                AutoSave: _state.PresetsAutoSave,
                AutoSaveChanged: value =>
                {
                    _state.PresetsAutoSave = value;
                    SaveState();
                });
            var preset = _dialogs.ShowPresets(context);
            if (preset is not null) ApplyPreset(preset);
        }
        finally
        {
            foreach (var release in detach) release();
        }
    }

    // AI proponuje nazwę i opis zestawu na podstawie promptu, modelem tekstowym wybranym w ulepszaniu
    // Model: z żądania, jeśli jest na liście ulepszania, inaczej wybrany w głównym oknie
    private async Task<PresetDraft> SuggestPresetDraftAsync(string prompt, string? system, string? model)
    {
        var messages = new List<ChatMessage>
        {
            new("system", "You name saved presets of an image generation app. Reply with JSON only, no code fences: " +
                          "{\"name\": \"...\", \"description\": \"...\"}. name = at most 5 words, description = one short sentence. " +
                          "Use the language of the prompt."),
            new("user", string.IsNullOrWhiteSpace(system) ? prompt : $"CONFIG:\n{system}\n\nPROMPT:\n{prompt}"),
        };
        var reply = await _provider.ChatAsync(EnhancerModelOrThrow(model), messages);
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
        NegativePrompt = ActiveTab == 1 ? EditNegativePrompt : NegativePrompt,
        Quality = SelectedQuality,
        EnhancerModel = SelectedEnhancerModel,
        EnhancerInstruction = EnhancerInstruction,
        Parameters = EnhancerParameters.ToDictionary(p => p.Key, p => new ParamState { Enabled = p.Enabled, Value = p.Value }),
    };

    // Wczytanie przełącza na zakładkę zestawu i ustawia wszystkie jego wartości
    private void ApplyPreset(Preset preset)
    {
        var isEdit = preset.Kind == "edit";
        if (isEdit)
        {
            _editHistory = null;
            EditPrompt = preset.Prompt;
            EditSystemPrompt = preset.SystemPrompt;
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == preset.Model) ?? SelectedEditModel;
            EditNegativePrompt = preset.NegativePrompt ?? "";
        }
        else
        {
            _generateHistory = null;
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
