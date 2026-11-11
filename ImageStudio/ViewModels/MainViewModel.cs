using ImageStudio.Localization;
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

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public const string AllProviders = ModelOption.AllOwners;
    public const string DefaultInstruction = StudioDefaults.PromptInstruction;

    private const string DefaultGeneration = "venice/z-image-turbo";
    private const string DefaultEdit = "venice/grok-imagine-edit";

    private static readonly (string Label, double Ratio)[] AspectRatios =
    [
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ];

    private static readonly ILogger Log = AppLog.For<MainViewModel>();

    private IAiProvider _provider;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly IProviderFactory _providers;
    private readonly IThemeService _themes;
    private readonly IDialogService _dialogs;
    private readonly ImageDownloadService _downloads;
    private readonly ISettingsStore _settingsStore;
    private readonly IUserStateStore _stateStore;
    private readonly IGalleryStore _galleryStore;
    private readonly IPresetStore _presetStore;
    private readonly ISecretStore _secrets;
    private readonly McpToolHost _toolHost;
    private readonly McpOptions _mcp;
    private readonly UserState _state;
    private readonly ModelPreferences _prefs;
    private readonly Dictionary<string, ChatModelInfo> _chatModels = [];
    private AppSettings _settings;

    // Stan zapisywany dopiero po wczytaniu modeli, żeby puste wartości startowe nie nadpisały zapamiętanych
    private bool _ready;


    // Surowe bajty wyniku: zapis zachowuje oryginalny format (PNG/JPEG), bez ponownego kodowania
    private byte[]? _imageBytes;

    // Proporcja W/H używana przy blokadzie; _syncing chroni przed rekurencją przy wzajemnym przeliczaniu W i H
    private double _aspect = 1.0;
    private bool _syncing;

    // --- Wynik i stan ---
    [ObservableProperty]
    private string _status = "Gotowy";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(EditCommand), nameof(AddReferencesCommand),
                                nameof(CopyImageCommand), nameof(SaveImageAsCommand), nameof(OpenSettingsCommand),
                                nameof(AddReferenceFromUrlCommand), nameof(OpenPresetsCommand), nameof(SavePresetCommand), nameof(OpenModelBrowserCommand),
                                nameof(GenerateVideoCommand), nameof(AddVideoAttachmentsCommand), nameof(PasteReferenceCommand),
                                nameof(PasteVideoAttachmentCommand), nameof(RepeatGalleryEntryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyImageCommand), nameof(SaveImageAsCommand), nameof(ClearResultCommand))]
    private BitmapSource? _image;

    // Podgląd wyniku: dopasowany do okna (domyślnie) albo w skali ręcznej; nie zmienia pliku
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomed), nameof(ZoomLabel))]
    private bool _fitToWindow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private double _zoom = 1.0;

    public bool IsZoomed => !FitToWindow;

    public string ZoomLabel => FitToWindow ? "Dopasowane do okna" : $"Skala: {Zoom:P0}";

    // Ręczna zmiana skali wyłącza dopasowanie
    partial void OnZoomChanged(double value)
    {
        if (FitToWindow) FitToWindow = false;
    }

    // --- Filtry modeli: osobne dla każdego selektora, więc zakładki nie wpływają na siebie ---
    public ModelFilter GenerateFilter { get; }
    public ModelFilter EditFilter { get; }
    public ModelFilter VideoFilter { get; }
    public ModelFilter ChatFilter { get; }
    public ModelFilter EnhancerFilter { get; }
    public ModelFilter SpeechFilter { get; }
    public ModelFilter TranscriptionFilter { get; }

    private IEnumerable<ModelFilter> AllFilters =>
        [GenerateFilter, EditFilter, VideoFilter, ChatFilter, EnhancerFilter, SpeechFilter, TranscriptionFilter];

    // Filtr z widokami, które odświeża; "preferowane" zapisuje się jako ostatni wybór dostawcy
    private ModelFilter BindFilter(bool imageFilter, params ICollectionView[] views)
    {
        var filter = new ModelFilter(imageFilter, () => _prefs.For(_provider.Id), OpenModelBrowserCommand);
        foreach (var view in views) view.Filter = filter.Matches;
        filter.Changed += () => { foreach (var view in views) view.Refresh(); };
        filter.PreferredOnlyToggled += value =>
        {
            _prefs.For(_provider.Id).PreferredOnly = value;
            _prefs.Save();
        };
        return filter;
    }

    // Tryb "tylko preferowane" dostawcy jest wartością startową każdego filtru
    private void ApplyPreferredOnly()
    {
        var value = _prefs.For(_provider.Id).PreferredOnly;
        foreach (var filter in AllFilters) filter.PreferredOnly = value;
    }

    private readonly MediaStorageHost _storage;

    // Trwające operacje sieciowe (spinner w stopce, przyciemnione okno odpowiedzi)
    public IActivityTracker Activity { get; }

    // Wszystkie zależności przychodzą z kontenera DI (patrz Composition/ServiceCollectionExtensions)
    public MainViewModel(IAiProvider provider, IProviderFactory providers, IDialogService dialogs,
                         ImageDownloadService downloads, AppSettings settings, ISettingsStore settingsStore,
                         IUserStateStore stateStore, IGalleryStore galleryStore, IPresetStore presetStore,
                         ISecretStore secrets, ModelPreferences prefs, McpToolHost toolHost, McpOptions mcp, IThemeService themes,
                         IActivityTracker activity, IChatSessionStore chatSessions, IAudioPlayer audioPlayer, IAudioRecorder audioRecorder,
                         IVoiceGalleryStore voiceGallery, IMemoryStore memory, MediaStorageHost storage, ISkillStore skills)
    {
        _storage = storage;
        _provider = provider;
        _providers = providers;
        _dialogs = dialogs;
        _downloads = downloads;
        _settings = settings;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _galleryStore = galleryStore;
        _presetStore = presetStore;
        _secrets = secrets;
        _prefs = prefs;
        _toolHost = toolHost;
        _mcp = mcp;
        _themes = themes;
        Activity = activity;

        // Ostatnio wpisane prompty i wybory wracają po uruchomieniu
        _state = _stateStore.Load();
        _storage.Load(_state);
        GeneratePrompt = _state.GeneratePrompt ?? "";
        GenerateSystemPrompt = _state.GenerateSystemPrompt ?? "";
        EditPrompt = _state.EditPrompt ?? "";
        EditSystemPrompt = _state.EditSystemPrompt ?? "";
        EnhancerInstruction = string.IsNullOrWhiteSpace(_state.EnhancerInstruction) ? DefaultInstruction : _state.EnhancerInstruction;
        SeedText = _state.SeedText ?? "";
        NegativePrompt = _state.NegativePrompt ?? "";
        SelectedQuality = _state.Quality ?? "auto";
        Studio = new StudioApi(() => _provider, _storage, this, _presetStore, _downloads, Activity, new WindowDefaults(this),
                               () => _prefs.For(_provider.Id).Preferred);
        Limits = McpLimits.From(_state);
        var appTools = new CompositeToolSet(new AppControlTools(this, _dialogs.ConfirmToolCall, Limits),
                                            new StudioTools(Studio, _dialogs.ConfirmToolCall, Limits));
        var server = new AppServerViewModel(new McpAppServer(appTools, RunOnUi, Limits), _dialogs, _state.McpServerEnabled,
                                            _state.McpServerPort ?? AppServerViewModel.DefaultPort, secrets: _secrets);
        var builtin = BuiltinToolRegistry.Create(memory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImageStudio", "Workspace"),
                                                 _dialogs.ConfirmToolCall, skills: skills);
        Tools = new ToolsViewModel(_toolHost, _state.UseTools, _state.RequireToolApproval ?? true, _state.McpEnabled,
                                   _mcp.ConfigPath, appTools, _state.AppControlEnabled, server, builtin,
                                   _state.BuiltinToolsEnabled ?? true, memory, _state.DisabledTools, Limits, skills, _state.ToolProfiles);
        Tools.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ToolsViewModel.DisabledTools) or nameof(ToolsViewModel.Profiles)) SaveState();
        };
        Tools.Permissions.Load(_state.AlwaysAllowedTools);
        Tools.Permissions.Changed += SaveState;
        // O zgodę na narzędzia czat pyta w swoim oknie (zamiast okna dialogowego)
        Chat = new ChatViewModel(() => _provider, EnhancerModels, ReportChat, _state.ChatSystemPrompt ?? "",
                                 Tools, null,
                                 new ChatImageSupport(_dialogs,
                                                      id => _chatModels.TryGetValue(id, out var info) ? info.AcceptsImage : null,
                                                      () => _settings.MaxSide),
                                 id => _chatModels.GetValueOrDefault(id),
                                 _state.ChatReasoningEnabled ?? true, _state.ChatReasoningEffort ?? ReasoningSupport.DefaultEffort,
                                 activity, ChatModelOptions, () => _settings.UseStreaming, chatSessions)
        {
            Permissions = Tools.Permissions,
            InlineApproval = true,
            CollapseLong = _state.CollapseLongResults,
        };
        Voice = new VoiceViewModel(() => _provider, _dialogs, audioPlayer, audioRecorder, ReportChat,
                                   () => _ = InitializeAsync(),
                                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ImageStudio"), _state,
                                   () => SelectedEnhancerModel, null, voiceGallery, Studio);
        SyncVoiceFolder();
        Voice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VoiceViewModel.SelectedSpeechModel) or nameof(VoiceViewModel.SelectedVoice)
                or nameof(VoiceViewModel.Speed) or nameof(VoiceViewModel.Format) or nameof(VoiceViewModel.SelectedTranscriptionModel)
                or nameof(VoiceViewModel.Language) or nameof(VoiceViewModel.SelectedMicrophone) or nameof(VoiceViewModel.SelectedSpeaker)
                or nameof(VoiceViewModel.SelectedSpeechLanguage)) SaveState();
        };
        ChatModelOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedEnhancerModelOption));
        // Wybór poziomu rozumowania jest zapamiętywany od razu (bez oznaczania zestawu jako zmienionego)
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ReasoningEnabled) or nameof(ChatViewModel.SelectedReasoningEffort) or nameof(ChatViewModel.CollapseLong)) SaveState();
        };

        ModelsView = CollectionViewSource.GetDefaultView(Models);
        EditModelsView = CollectionViewSource.GetDefaultView(EditModels);
        VideoModelsView = CollectionViewSource.GetDefaultView(VideoModels);
        // Selektor ulepszania ma własny widok tych samych modeli (widok domyślny należy do czatu)
        ChatModelsView = new CollectionViewSource { Source = ChatModelOptions }.View;
        GenerateFilter = BindFilter(true, ModelsView);
        EditFilter = BindFilter(true, EditModelsView);
        VideoFilter = BindFilter(true, VideoModelsView);
        ChatFilter = BindFilter(true, Chat.ModelOptionsView);
        EnhancerFilter = BindFilter(true, ChatModelsView);
        SpeechFilter = BindFilter(false, Voice.SpeechModelsView);
        TranscriptionFilter = BindFilter(false, Voice.TranscriptionModelsView);
        Loc.LanguageChanged += RefreshLanguageTexts;


        References.CollectionChanged += (_, _) => EditCommand.NotifyCanExecuteChanged();
        VideoAttachments.CollectionChanged += (_, _) =>
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(VideoHint));
        };

        foreach (var entry in _galleryStore.Load()) Gallery.Add(entry);
    }

    // Listy z /v1/models wraz z cenami; widoki stosują filtry
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<ModelOption> EditModels { get; } = [];
    public ICollectionView ModelsView { get; }
    public ICollectionView EditModelsView { get; }
    public ObservableCollection<string> Providers { get; } = [];

    // Modele czatu z opisem (wspólne dla czatu i ulepszania promptów); filtrowane jak modele obrazów
    public ObservableCollection<ModelOption> ChatModelOptions { get; } = [];
    public ICollectionView ChatModelsView { get; }

    // Wybór w selektorze ulepszania promptów; null z selektora (filtr ukrył pozycję) nie zmienia wyboru
    public ModelOption? SelectedEnhancerModelOption
    {
        get => ChatModelOptions.FirstOrDefault(o => o.Id == SelectedEnhancerModel);
        set
        {
            if (value is not null) SelectedEnhancerModel = value.Id;
            else OnPropertyChanged();
        }
    }

    // Operacje bez okna (narzędzia MCP): jawne parametry, wynik w galerii, bez zakładek i podglądu
    public StudioApi Studio { get; }

    // Limity narzędzi MCP z Ustawień (zakładka MCP)
    public McpLimits Limits { get; }

    // Aktywny dostawca AI; odświeżane w SwitchProviderAsync
    public string ProviderId => _provider.Id;

    // Preferowany model tekstowy użytkownika dla tego dostawcy, a gdy go nie ma, domyślny z kontraktu dostawcy
    private string? EffectiveDefaultTextModel => _prefs.For(_provider.Id).DefaultTextModel ?? _provider.DefaultTextModel;
    public string ProviderName => _provider.DisplayName;

    // Modele czatu do ulepszania promptu i ich widoczne parametry
    public ObservableCollection<string> EnhancerModels { get; } = [];
    public ObservableCollection<ModelParameter> EnhancerParameters { get; } = [];

    public IReadOnlyList<string> QualityOptions { get; } = ["auto", "low", "medium", "high"];
    public IReadOnlyList<string> AspectPresets { get; } = ["1:1", "4:3", "3:2", "16:9", "9:16"];

    // Obrazy referencyjne; pierwszy jest podstawowy
    public ObservableCollection<ReferenceItem> References { get; } = [];
    private string? _primaryPath;

    // Zmiany wyboru modeli zapisują stan od razu
    partial void OnSelectedModelChanged(ModelOption? value) => OnPresetInputChanged();
    partial void OnSelectedEditModelChanged(ModelOption? value) => OnPresetInputChanged();
    partial void OnEnhancerInstructionChanged(string value) => OnPresetInputChanged();
    partial void OnSeedTextChanged(string value) => OnPresetInputChanged();
    partial void OnNegativePromptChanged(string value) => OnPresetInputChanged();
    partial void OnSelectedQualityChanged(string value) => OnPresetInputChanged();
    partial void OnGeneratePromptChanged(string value) => OnPresetInputChanged();
    partial void OnEditPromptChanged(string value) => OnPresetInputChanged();
    partial void OnGenerateSystemPromptChanged(string value) => OnPresetInputChanged();
    partial void OnEditSystemPromptChanged(string value) => OnPresetInputChanged();

    partial void OnSelectedEnhancerModelChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedEnhancerModelOption));
        RebuildEnhancerParameters();
        OnPresetInputChanged();
    }

    // Każda zmiana ustawień zapisuje stan i włącza przycisk "Zapisz zestaw"
    private void OnPresetInputChanged()
    {
        SaveState();
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_ready) PresetDirty = true;
    }

    // Model pokazywany w metryczce: zależy od aktywnej zakładki
    public ModelOption? ActiveModel => ActiveTab == 0 ? SelectedModel : ActiveTab == 1 ? SelectedEditModel : SelectedVideoModel;

    public string Size => $"{Width}x{Height}";

    // Nazwa proporcji, jeśli W:H pasuje do jednego z presetów (null = dowolna); używana do podświetlania
    public string? AspectLabel
    {
        get
        {
            if (Height == 0) return null;
            var ratio = Width / (double)Height;
            var match = AspectRatios.FirstOrDefault(p => Math.Abs(ratio - p.Ratio) < 0.02);
            return match.Label;
        }
        set
        {
            if (value is not null) SetAspect(value);
        }
    }

    private bool CanGenerate => !IsBusy && !string.IsNullOrWhiteSpace(GeneratePrompt) && SelectedModel is not null;
    private bool CanEdit => !IsBusy && !string.IsNullOrWhiteSpace(EditPrompt)
                            && References.Count > 0 && SelectedEditModel is not null;
    private bool CanPickOrCopy => !IsBusy;
    private bool HasImage => Image is not null;
    private bool HasSource => SourceImage is not null;

    private void RefreshFilters()
    {
        foreach (var filter in AllFilters) filter.Refresh();
    }

    // Start, zmiana dostawcy i ręczne odświeżenie (ReloadModelsCommand): katalog dostawcy ładuje IAiProvider.ReloadAsync,
    // a wybory użytkownika (modele, filtry) zostają, o ile model nadal istnieje
    public async Task InitializeAsync()
    {
        // Zajętość na czas ładowania: pasek postępu i zablokowane akcje (także po zmianie dostawcy w Ustawieniach)
        IsBusy = true;
        Status = Loc.T("models.msg.loading");
        ApplyPreferredOnly();
        var keepVideo = SelectedVideoModel?.Id;
        try
        {
            ApplyCatalog(await _provider.ReloadAsync(), keepVideo);
        }
        catch (Exception ex)
        {
            Status = Loc.T("models.msg.failed", ex.Message);
            Log.LogError("{Message}", Status);
        }

        IsBusy = false;
        _ready = true;
        SaveState();

        // Łączenie z serwerami MCP może trwać (npx), więc nie blokuje startu; błędy trafiają do statusu serwera
        _ = Tools.InitializeAsync();
    }

    // Ręczne odświeżenie list modeli i cen z aktywnego dostawcy (przycisk w selektorze modeli)
    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private Task ReloadModelsAsync() => InitializeAsync();

    private void ApplyCatalog(ProviderCatalog catalog, string? keepVideo)
    {
        Studio.Catalog = catalog;
        var pricing = catalog.Pricing;
        if (catalog.Image is { } models)
        {
            Replace(Models, models.Generation, pricing);
            Replace(EditModels, models.Edit, pricing);
            SelectedModel = Models.FirstOrDefault(m => m.Id == _state.GenerationModel)
                            ?? Models.FirstOrDefault(m => m.Id == DefaultGeneration)
                            ?? Models.FirstOrDefault();
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == _state.EditModel)
                                ?? EditModels.FirstOrDefault(m => m.Id == DefaultEdit)
                                ?? EditModels.FirstOrDefault();
        }

        if (catalog.Video is { } video)
        {
            Replace(VideoModels, video, pricing);
            SelectedVideoModel = VideoModels.FirstOrDefault(m => m.Id == keepVideo)
                                 ?? VideoModels.FirstOrDefault(m => m.Id == _state.VideoModel)
                                 ?? VideoModels.FirstOrDefault(m => m.Pricing is not null)
                                 ?? VideoModels.FirstOrDefault();
        }

        // Brak modeli tekstowych nie blokuje generowania, tylko wyłącza ulepszanie promptu
        if (catalog.Chat is { } chat) ApplyChatModels(chat, pricing);
        Voice.ApplyCatalog(catalog);

        RefreshOwners();
        UpdateTabVisibility();
        Status = Loc.T("models.msg.loaded", Models.Count, EditModels.Count, VideoModels.Count);
        if (catalog.Warnings.Count > 0)
        {
            var warnings = string.Join("; ", catalog.Warnings);
            Status += $" · Uwagi: {warnings}";
            Log.LogWarning("Przeładowanie katalogu: {Warnings}", warnings);
        }
    }

    // Zapis stanu i zamknięcie połączeń MCP przy wyjściu (kończy procesy serwerów)
    public void Shutdown()
    {
        SaveState();
        Task.Run(() => Tools.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    private void ApplyChatModels(IReadOnlyList<ChatModelInfo> chat, IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        _chatModels.Clear();
        foreach (var model in chat) _chatModels[model.Id] = model;
        Chat.RefreshReasoningSupport();
        ReplaceStrings(EnhancerModels, chat.Select(m => m.Id));
        ReplaceChatOptions(chat, pricing);

        SelectedEnhancerModel = chat.Any(m => m.Id == _state.EnhancerModel) ? _state.EnhancerModel
                                : chat.Any(m => m.Id == EffectiveDefaultTextModel) ? EffectiveDefaultTextModel
                                : chat.FirstOrDefault()?.Id;
        Chat.SelectedModel = _state.ChatModel is { } chatModel && EnhancerModels.Contains(chatModel)
            ? chatModel
            : SelectedEnhancerModel;
    }

    // Parametry widoczne tylko dla modelu, który je deklaruje; wartości z zapamiętanego stanu
    private void RebuildEnhancerParameters()
    {
        EnhancerParameters.Clear();
        if (SelectedEnhancerModel is null || !_chatModels.TryGetValue(SelectedEnhancerModel, out var info)) return;

        foreach (var spec in ModelParameter.Catalog.Where(s => info.SupportedParameters.Contains(s.Key)))
        {
            var saved = _state.EnhancerParams.GetValueOrDefault(spec.Key);
            var parameter = new ModelParameter(spec, saved?.Enabled ?? false, saved?.Value ?? spec.Default)
            {
                Changed = OnPresetInputChanged,
            };
            EnhancerParameters.Add(parameter);
        }
    }

    // Parametry włączone przez użytkownika; reszta nie trafia do żądania
    private Dictionary<string, object> EnhancerParameterValues() =>
        EnhancerParameters.Where(p => p.Enabled).ToDictionary(p => p.Key, p => p.ToApiValue());

    // Zapis wyborów, instrukcji, ostatnich promptów i parametrów; przy zmianach i przy zamykaniu
    public void SaveState()
    {
        if (!_ready) return;

        _state.GeneratePrompt = GeneratePrompt;
        _state.GenerateSystemPrompt = GenerateSystemPrompt;
        _state.EditPrompt = EditPrompt;
        _state.EditSystemPrompt = EditSystemPrompt;
        _state.GenerationModel = SelectedModel?.Id ?? _state.GenerationModel;
        _state.EditModel = SelectedEditModel?.Id ?? _state.EditModel;
        Limits.Capture(_state);
        _state.VideoModel = SelectedVideoModel?.Id ?? _state.VideoModel;
        _state.EnhancerModel = SelectedEnhancerModel ?? _state.EnhancerModel;
        _state.ChatModel = Chat.SelectedModel ?? _state.ChatModel;
        _state.ChatSystemPrompt = Chat.SystemPrompt;
        _state.ChatReasoningEnabled = Chat.ReasoningEnabled;
        _state.ChatReasoningEffort = Chat.SelectedReasoningEffort;
        Voice.Capture(_state);
        _state.UseTools = Tools.UseTools;
        _state.RequireToolApproval = Tools.RequireApproval;
        _state.AlwaysAllowedTools = Tools.Permissions.Always.ToList();
        _state.CollapseLongResults = Chat.CollapseLong;
        _state.McpEnabled = Tools.EnabledServers.ToList();
        _state.AppControlEnabled = Tools.AppControlEnabled;
        _state.BuiltinToolsEnabled = Tools.BuiltinEnabled;
        _state.DisabledTools = Tools.DisabledTools.ToList();
        _state.ToolProfiles = Tools.Profiles.ToDictionary(p => p.Key, p => p.Value.ToList());
        _state.McpServerEnabled = Tools.Server?.Enabled ?? false;
        _state.McpServerPort = Tools.Server?.Port;
        _state.EnhancerInstruction = EnhancerInstruction;
        _state.SeedText = SeedText;
        _state.NegativePrompt = NegativePrompt;
        _state.Quality = SelectedQuality;
        foreach (var p in EnhancerParameters)
            _state.EnhancerParams[p.Key] = new ParamState { Enabled = p.Enabled, Value = p.Value };
        _stateStore.Save(_state);
    }

    // Status i log błędów z czatu (czat nie zna paska stanu ani loggera)
    private void ReportChat(string message, bool isError)
    {
        Status = message;
        if (isError) Log.LogError("{Message}", message);
    }
}
