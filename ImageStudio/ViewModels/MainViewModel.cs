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

    private const string DefaultEnhancer = "venice/uncensored";
    private const string DefaultGeneration = "venice/z-image-turbo";
    private const string DefaultEdit = "venice/grok-imagine-edit";

    private static readonly (string Label, double Ratio)[] AspectRatios =
    [
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ];

    private static readonly ILogger Log = AppLog.For<MainViewModel>();

    private IAiProvider _provider;
    private readonly IDialogService _dialogs;
    private readonly ImageDownloadService _downloads;
    private readonly UserState _state;
    private readonly ModelPreferences _prefs = ModelPreferences.Load();
    private readonly Dictionary<string, ChatModelInfo> _chatModels = [];
    private AppSettings _settings;

    // Stan zapisywany dopiero po wczytaniu modeli, żeby puste wartości startowe nie nadpisały zapamiętanych
    private bool _ready;

    // Tekst sprzed ulepszenia; "Cofnij" przywraca go, dopóki nie zostanie użyty ponownie
    private PromptParts? _undoGenerate;
    private PromptParts? _undoEdit;

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
                                nameof(AddReferenceFromUrlCommand), nameof(EnhanceGeneratePromptCommand),
                                nameof(EnhanceEditPromptCommand), nameof(OpenPresetsCommand), nameof(SavePresetCommand), nameof(OpenModelBrowserCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyImageCommand), nameof(SaveImageAsCommand))]
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
        Chat = new ChatViewModel(() => _provider, EnhancerModels, ReportChat, _state.ChatSystemPrompt ?? "");

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
    private bool CanEnhanceGenerate => !IsBusy && !string.IsNullOrWhiteSpace(GeneratePrompt) && SelectedEnhancerModel is not null;
    private bool CanEnhanceEdit => !IsBusy && !string.IsNullOrWhiteSpace(EditPrompt) && SelectedEnhancerModel is not null;
    private bool CanUndoGenerate => !IsBusy && _undoGenerate is not null;
    private bool CanUndoEdit => !IsBusy && _undoEdit is not null;
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

    public async Task InitializeAsync()
    {
        Status = "Pobieranie listy modeli i cen...";
        PreferredOnly = _prefs.For(_provider.Id).PreferredOnly;
        try
        {
            var models = await _provider.GetImageModelsAsync();
            var pricing = await LoadPricingAsync();

            Replace(Models, models.Generation, pricing);
            Replace(EditModels, models.Edit, pricing);
            FillProviders(models.Generation.Concat(models.Edit));
            SelectedModel = Models.FirstOrDefault(m => m.Id == _state.GenerationModel)
                            ?? Models.FirstOrDefault(m => m.Id == DefaultGeneration)
                            ?? Models.FirstOrDefault();
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == _state.EditModel)
                                ?? EditModels.FirstOrDefault(m => m.Id == DefaultEdit)
                                ?? EditModels.FirstOrDefault();

            Replace(VideoModels, await _provider.GetVideoModelsAsync(), pricing);
            SelectedVideoModel ??= VideoModels.FirstOrDefault(m => m.Pricing is not null) ?? VideoModels.FirstOrDefault();

            Status = $"Modele: {Models.Count} generowania, {EditModels.Count} edycji, {VideoModels.Count} wideo";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się pobrać modeli: {ex.Message}";
            Log.LogError("{Message}", Status);
        }

        await LoadEnhancersAsync();
        _ready = true;
        SaveState();
    }

    // Brak modeli czatu nie blokuje generowania, tylko wyłącza ulepszanie promptu
    private async Task LoadEnhancersAsync()
    {
        try
        {
            var chat = await _provider.GetChatModelsAsync();
            _chatModels.Clear();
            foreach (var model in chat) _chatModels[model.Id] = model;
            ReplaceStrings(EnhancerModels, chat.Select(m => m.Id));

            SelectedEnhancerModel = chat.Any(m => m.Id == _state.EnhancerModel) ? _state.EnhancerModel
                                    : chat.Any(m => m.Id == DefaultEnhancer) ? DefaultEnhancer
                                    : chat.FirstOrDefault()?.Id;
            Chat.SelectedModel = _state.ChatModel is { } chatModel && EnhancerModels.Contains(chatModel)
                ? chatModel
                : SelectedEnhancerModel;
        }
        catch (Exception ex)
        {
            Status = $"Bez modeli do ulepszania promptu: {ex.Message}";
            Log.LogError("{Message}", Status);
        }
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
        _state.EnhancerInstruction = EnhancerInstruction;
        _state.SeedText = SeedText;
        _state.NegativePrompt = NegativePrompt;
        _state.Quality = SelectedQuality;
        foreach (var p in EnhancerParameters)
            _state.EnhancerParams[p.Key] = new ParamState { Enabled = p.Enabled, Value = p.Value };
        _state.Save();
    }

    // Brak cen nie blokuje listy modeli, tylko zostawia etykiety bez ceny
    private async Task<IReadOnlyDictionary<string, ModelPricing>> LoadPricingAsync()
    {
        try
        {
            return await _provider.GetPricingAsync();
        }
        catch (Exception ex)
        {
            Status = $"Bez cen z rynku: {ex.Message}";
            Log.LogError("{Message}", Status);
            return new Dictionary<string, ModelPricing>();
        }
    }

    // Status i log błędów z czatu (czat nie zna paska stanu ani loggera)
    private void ReportChat(string message, bool isError)
    {
        Status = message;
        if (isError) Log.LogError("{Message}", message);
    }
}
