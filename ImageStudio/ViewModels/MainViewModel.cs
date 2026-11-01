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

public sealed partial class MainViewModel : ObservableObject
{
    public const string AllProviders = "Wszyscy";
    public const string DefaultInstruction =
        "You improve image generation prompts. Make the concept vivid and detailed, " +
        "keep the same language, subject and intent.";

    private const string DefaultGeneration = "venice/z-image-turbo";
    private const string DefaultEdit = "venice/grok-imagine-edit";

    private static readonly (string Label, double Ratio)[] AspectRatios =
    [
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ];

    private static readonly ILogger Log = AppLog.For<MainViewModel>();

    private IAiProvider _provider;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly IDialogService _dialogs;
    private readonly ImageDownloadService _downloads;
    private readonly UserState _state;
    private readonly ModelPreferences _prefs = ModelPreferences.Load();
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
                                nameof(PasteVideoAttachmentCommand))]
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

    // --- Filtry modeli (wspólne dla obu zakładek) ---
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedProvider = AllProviders;

    [ObservableProperty]
    private bool _inputImageOnly;

    [ObservableProperty]
    private bool _hideNsfw;

    // Lista modeli: wszystkie albo tylko preferowane aktywnego dostawcy (zapis w models.json)
    [ObservableProperty]
    private bool _preferredOnly;

    public MainViewModel(IAiProvider provider, IDialogService dialogs,
                         ImageDownloadService downloads, AppSettings settings)
    {
        _provider = provider;
        _dialogs = dialogs;
        _downloads = downloads;
        _settings = settings;

        // Ostatnio wpisane prompty i wybory wracają po uruchomieniu
        _state = UserState.Load();
        GeneratePrompt = _state.GeneratePrompt ?? "";
        GenerateSystemPrompt = _state.GenerateSystemPrompt ?? "";
        EditPrompt = _state.EditPrompt ?? "";
        EditSystemPrompt = _state.EditSystemPrompt ?? "";
        EnhancerInstruction = string.IsNullOrWhiteSpace(_state.EnhancerInstruction) ? DefaultInstruction : _state.EnhancerInstruction;
        SeedText = _state.SeedText ?? "";
        NegativePrompt = _state.NegativePrompt ?? "";
        SelectedQuality = _state.Quality ?? "auto";
        var appTools = new AppControlTools(this, _dialogs.ConfirmToolCall);
        var server = new AppServerViewModel(new McpAppServer(appTools, RunOnUi), _dialogs, _state.McpServerEnabled,
                                            _state.McpServerPort ?? AppServerViewModel.DefaultPort);
        Tools = new ToolsViewModel(new McpToolHost(), _state.UseTools, _state.RequireToolApproval ?? true, _state.McpEnabled,
                                   null, appTools, _state.AppControlEnabled, server);
        Chat = new ChatViewModel(() => _provider, EnhancerModels, ReportChat, _state.ChatSystemPrompt ?? "",
                                 Tools, _dialogs.ConfirmToolCall,
                                 new ChatImageSupport(_dialogs,
                                                      id => _chatModels.TryGetValue(id, out var info) ? info.AcceptsImage : null,
                                                      () => _settings.MaxSide));

        ModelsView = CollectionViewSource.GetDefaultView(Models);
        EditModelsView = CollectionViewSource.GetDefaultView(EditModels);
        ModelsView.Filter = FilterModel;
        EditModelsView.Filter = FilterModel;
        References.CollectionChanged += (_, _) => EditCommand.NotifyCanExecuteChanged();
        VideoAttachments.CollectionChanged += (_, _) =>
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(VideoHint));
        };

        foreach (var entry in GalleryStore.Load()) Gallery.Add(entry);
    }

    // Listy z /v1/models wraz z cenami; widoki stosują filtry
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<ModelOption> EditModels { get; } = [];
    public ICollectionView ModelsView { get; }
    public ICollectionView EditModelsView { get; }
    public ObservableCollection<string> Providers { get; } = [];

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

    partial void OnSearchTextChanged(string value) => RefreshFilters();
    partial void OnSelectedProviderChanged(string value) => RefreshFilters();
    partial void OnInputImageOnlyChanged(bool value) => RefreshFilters();
    partial void OnHideNsfwChanged(bool value) => RefreshFilters();

    partial void OnPreferredOnlyChanged(bool value)
    {
        _prefs.For(_provider.Id).PreferredOnly = value;
        _prefs.Save();
        RefreshFilters();
    }

    private void RefreshFilters()
    {
        ModelsView.Refresh();
        EditModelsView.Refresh();
    }

    private bool FilterModel(object item)
    {
        if (item is not ModelOption m) return false;
        if (HideNsfw && m.Nsfw) return false;
        if (InputImageOnly && !m.AcceptsImage) return false;
        if (SelectedProvider != AllProviders && m.OwnedBy != SelectedProvider) return false;
        // Pusta lista preferowanych nie ukrywa wszystkiego: filtr działa dopiero po dodaniu pierwszego modelu
        if (PreferredOnly && _prefs.For(_provider.Id) is { Preferred.Count: > 0 } prefs && !prefs.Preferred.Contains(m.Id))
            return false;
        return string.IsNullOrWhiteSpace(SearchText)
               || m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    // Start, zmiana dostawcy i ręczne odświeżenie (ReloadModelsCommand): katalog dostawcy ładuje IAiProvider.ReloadAsync,
    // a wybory użytkownika (modele, filtry) zostają, o ile model nadal istnieje
    public async Task InitializeAsync()
    {
        // Zajętość na czas ładowania: pasek postępu i zablokowane akcje (także po zmianie dostawcy w Ustawieniach)
        IsBusy = true;
        Status = "Pobieranie listy modeli i cen...";
        PreferredOnly = _prefs.For(_provider.Id).PreferredOnly;
        var keepVideo = SelectedVideoModel?.Id;
        try
        {
            ApplyCatalog(await _provider.ReloadAsync(), keepVideo);
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się pobrać modeli: {ex.Message}";
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
        var pricing = catalog.Pricing;
        if (catalog.Image is { } models)
        {
            Replace(Models, models.Generation, pricing);
            Replace(EditModels, models.Edit, pricing);
            FillProviders(models.Generation.Concat(models.Edit));
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
                                 ?? VideoModels.FirstOrDefault(m => m.Pricing is not null)
                                 ?? VideoModels.FirstOrDefault();
        }

        // Brak modeli tekstowych nie blokuje generowania, tylko wyłącza ulepszanie promptu
        if (catalog.Chat is { } chat) ApplyChatModels(chat);

        Status = $"Modele: {Models.Count} generowania, {EditModels.Count} edycji, {VideoModels.Count} wideo";
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

    private void ApplyChatModels(IReadOnlyList<ChatModelInfo> chat)
    {
        _chatModels.Clear();
        foreach (var model in chat) _chatModels[model.Id] = model;
        ReplaceStrings(EnhancerModels, chat.Select(m => m.Id));

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
        _state.EnhancerModel = SelectedEnhancerModel ?? _state.EnhancerModel;
        _state.ChatModel = Chat.SelectedModel ?? _state.ChatModel;
        _state.ChatSystemPrompt = Chat.SystemPrompt;
        _state.UseTools = Tools.UseTools;
        _state.RequireToolApproval = Tools.RequireApproval;
        _state.McpEnabled = Tools.EnabledServers.ToList();
        _state.AppControlEnabled = Tools.AppControlEnabled;
        _state.McpServerEnabled = Tools.Server?.Enabled ?? false;
        _state.McpServerPort = Tools.Server?.Port;
        _state.EnhancerInstruction = EnhancerInstruction;
        _state.SeedText = SeedText;
        _state.NegativePrompt = NegativePrompt;
        _state.Quality = SelectedQuality;
        foreach (var p in EnhancerParameters)
            _state.EnhancerParams[p.Key] = new ParamState { Enabled = p.Enabled, Value = p.Value };
        _state.Save();
    }

    // Status i log błędów z czatu (czat nie zna paska stanu ani loggera)
    private void ReportChat(string message, bool isError)
    {
        Status = message;
        if (isError) Log.LogError("{Message}", message);
    }
}
