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

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public const string AllProviders = "Wszyscy";
    public const string DefaultInstruction =
        "Rewrite the user's image generation prompt so it is more vivid and detailed. " +
        "Keep the same language, subject and intent. Reply with the improved prompt only, without quotes or commentary.";

    private const string DefaultEnhancer = "venice/uncensored";
    private const string DefaultGeneration = "venice/z-image-turbo";
    private const string DefaultEdit = "venice/grok-imagine-edit";

    private static readonly (string Label, double Ratio)[] AspectRatios =
    [
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ];

    private readonly ImageGenerationService _service;
    private readonly MarketService _market;
    private readonly IDialogService _dialogs;
    private readonly ImageDownloadService _downloads;
    private readonly UserState _state;
    private readonly Dictionary<string, ChatModelInfo> _chatModels = [];
    private AppSettings _settings;

    // Stan zapisywany dopiero po wczytaniu modeli, żeby puste wartości startowe nie nadpisały zapamiętanych
    private bool _ready;

    // Tekst sprzed ulepszenia; "Cofnij" przywraca go, dopóki nie zostanie użyty ponownie
    private string? _undoGenerate;
    private string? _undoEdit;

    // Surowe bajty wyniku: zapis zachowuje oryginalny format (PNG/JPEG), bez ponownego kodowania
    private byte[]? _imageBytes;

    // Proporcja W/H używana przy blokadzie; _syncing chroni przed rekurencją przy wzajemnym przeliczaniu W i H
    private double _aspect = 1.0;
    private bool _syncing;

    // --- Generowanie ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(EnhanceGeneratePromptCommand))]
    private string _generatePrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private ModelOption? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size), nameof(AspectLabel))]
    private int _width = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size), nameof(AspectLabel))]
    private int _height = 1024;

    [ObservableProperty]
    private bool _keepAspect = true;

    // Parametry obrazu (wysyłane tylko gdy ustawione)
    [ObservableProperty]
    private string _seedText = "";

    [ObservableProperty]
    private string _negativePrompt = "";

    [ObservableProperty]
    private string _selectedQuality = "auto";

    // --- Edycja / generowanie z referencjami ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(EnhanceEditPromptCommand))]
    private string _editPrompt = "";

    // Adres obrazu referencyjnego do pobrania
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReferenceFromUrlCommand))]
    private string _referenceUrl = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private ModelOption? _selectedEditModel;

    // Podgląd pierwszego (podstawowego) obrazu; proporcje wyjścia biorą się z niego
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySourceAspectCommand))]
    private BitmapSource? _sourceImage;

    // Model tekstowy do ulepszania promptów (wspólny dla obu zakładek) i jego instrukcja
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnhanceGeneratePromptCommand), nameof(EnhanceEditPromptCommand))]
    private string? _selectedEnhancerModel;

    [ObservableProperty]
    private string _enhancerInstruction = DefaultInstruction;

    // Indeks zakładki w panelu wejściowym: 0 = generowanie, 1 = edycja
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private int _activeTab;

    // --- Wynik i stan ---
    [ObservableProperty]
    private string _status = "Gotowy";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(EditCommand), nameof(AddReferencesCommand),
                                nameof(CopyImageCommand), nameof(SaveImageAsCommand), nameof(OpenSettingsCommand),
                                nameof(AddReferenceFromUrlCommand), nameof(EnhanceGeneratePromptCommand),
                                nameof(EnhanceEditPromptCommand), nameof(OpenPresetsCommand), nameof(SavePresetCommand))]
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

    public MainViewModel(ImageGenerationService service, MarketService market, IDialogService dialogs,
                         ImageDownloadService downloads, AppSettings settings)
    {
        _service = service;
        _market = market;
        _dialogs = dialogs;
        _downloads = downloads;
        _settings = settings;

        // Ostatnio wpisane prompty i wybory wracają po uruchomieniu
        _state = UserState.Load();
        GeneratePrompt = _state.GeneratePrompt ?? "";
        EditPrompt = _state.EditPrompt ?? "";
        EnhancerInstruction = string.IsNullOrWhiteSpace(_state.EnhancerInstruction) ? DefaultInstruction : _state.EnhancerInstruction;
        SeedText = _state.SeedText ?? "";
        NegativePrompt = _state.NegativePrompt ?? "";
        SelectedQuality = _state.Quality ?? "auto";

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
        return string.IsNullOrWhiteSpace(SearchText)
               || m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        Status = "Pobieranie listy modeli i cen...";
        try
        {
            var models = await _service.GetImageModelsAsync();
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

            Replace(VideoModels, await _service.GetVideoModelsAsync(), pricing);
            SelectedVideoModel ??= VideoModels.FirstOrDefault(m => m.Pricing is not null) ?? VideoModels.FirstOrDefault();

            Status = $"Modele: {Models.Count} generowania, {EditModels.Count} edycji, {VideoModels.Count} wideo";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się pobrać modeli: {ex.Message}";
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
            var chat = await _service.GetChatModelsAsync();
            _chatModels.Clear();
            foreach (var model in chat) _chatModels[model.Id] = model;
            ReplaceStrings(EnhancerModels, chat.Select(m => m.Id));

            SelectedEnhancerModel = chat.Any(m => m.Id == _state.EnhancerModel) ? _state.EnhancerModel
                                    : chat.Any(m => m.Id == DefaultEnhancer) ? DefaultEnhancer
                                    : chat.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            Status = $"Bez modeli do ulepszania promptu: {ex.Message}";
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
        _state.EditPrompt = EditPrompt;
        _state.GenerationModel = SelectedModel?.Id ?? _state.GenerationModel;
        _state.EditModel = SelectedEditModel?.Id ?? _state.EditModel;
        _state.EnhancerModel = SelectedEnhancerModel ?? _state.EnhancerModel;
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
            return await _market.GetMediaPricingAsync();
        }
        catch (Exception ex)
        {
            Status = $"Bez cen z rynku: {ex.Message}";
            return new Dictionary<string, ModelPricing>();
        }
    }

    // --- Ustawienia ---

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private async Task OpenSettingsAsync()
    {
        var current = new SettingsResult(_settings.BaseUrl, _settings.MarketUrl, _settings.MaxSide,
                                         _settings.ApiKey, _settings.Theme);
        var result = _dialogs.ShowSettings(current);
        if (result is null) return;

        AppSettings.Save(result.BaseUrl, result.MarketUrl, result.MaxSide, result.ApiKey, result.Theme);
        ThemeService.Apply(result.Theme);
        _service.Reconfigure(result.BaseUrl, result.ApiKey);
        _market.Reconfigure(result.MarketUrl);
        _settings = new AppSettings(result.BaseUrl, result.MarketUrl, result.MaxSide, result.ApiKey, result.Theme);
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

        var draft = _dialogs.ShowSavePreset(defaultName);
        if (draft is null) return;

        PresetStore.Create(CapturePreset(draft));
        PresetDirty = false;
        Status = $"Zapisano zestaw: {draft.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void OpenPresets()
    {
        var preset = _dialogs.ShowPresets();
        if (preset is not null) ApplyPreset(preset);
    }

    private string CurrentPromptForTab() => ActiveTab == 1 ? EditPrompt : GeneratePrompt;

    // Zestaw zapisuje wszystko, co wpływa na wynik: prompt, model, rozmiar, parametry i ulepszanie
    private Preset CapturePreset(PresetDraft draft) => new()
    {
        Name = draft.Name,
        Description = draft.Description,
        Kind = ActiveTab == 1 ? "edit" : "generate",
        Prompt = CurrentPromptForTab(),
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
        var isEdit = preset.Kind == "edit";
        if (isEdit)
        {
            EditPrompt = preset.Prompt;
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == preset.Model) ?? SelectedEditModel;
        }
        else
        {
            GeneratePrompt = preset.Prompt;
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

    // --- Proporcje wyjścia ---

    partial void OnWidthChanged(int value)
    {
        MarkDirty();
        OnDimensionChanged(widthChanged: true);
    }

    partial void OnHeightChanged(int value)
    {
        MarkDirty();
        OnDimensionChanged(widthChanged: false);
    }

    private void OnDimensionChanged(bool widthChanged)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (KeepAspect)
            {
                if (widthChanged) Height = Snap(Width / _aspect);
                else Width = Snap(Height * _aspect);
            }
            else if (Height > 0)
            {
                _aspect = (double)Width / Height;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private void SetAspect(string? ratio)
    {
        var parts = ratio?.Split(':');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || h <= 0)
            return;

        _aspect = w / h;
        _syncing = true;
        try
        {
            Height = Snap(Width / _aspect);
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private void ApplySourceAspect()
    {
        if (SourceImage is null) return;
        ApplyAspectFrom(SourceImage.PixelWidth, SourceImage.PixelHeight);
    }

    // Wymiary wyjścia biorą proporcje z obrazu podstawowego, z limitem długiego boku
    private void ApplyAspectFrom(int pixelWidth, int pixelHeight)
    {
        var scale = Math.Min(1.0, _settings.MaxSide / (double)Math.Max(pixelWidth, pixelHeight));
        _syncing = true;
        try
        {
            Width = Snap(pixelWidth * scale);
            Height = Snap(pixelHeight * scale);
        }
        finally
        {
            _syncing = false;
        }
        _aspect = (double)pixelWidth / pixelHeight;
    }

    // Wielokrotność 8, minimum 8
    private static int Snap(double value) => Math.Max(8, (int)Math.Round(value / 8.0) * 8);

    // --- Referencje ---

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void AddReferences()
    {
        var paths = _dialogs.PickImageFiles();
        if (paths is null || paths.Length == 0) return;

        try
        {
            foreach (var path in paths)
                References.Add(new ReferenceItem(ImageOrientation.EnsureUpright(path), Path.GetFileName(path), path));
            if (SourceImage is null) LoadPrimary();
            Status = $"Referencje: {References.Count}";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się wczytać obrazu: {ex.Message}";
        }
    }

    private bool CanAddUrl => !IsBusy && !string.IsNullOrWhiteSpace(ReferenceUrl);

    [RelayCommand(CanExecute = nameof(CanAddUrl))]
    private async Task AddReferenceFromUrlAsync()
    {
        var url = ReferenceUrl.Trim();
        IsBusy = true;
        Status = "Pobieranie obrazu z linku...";
        try
        {
            var path = await _downloads.DownloadAsync(url);
            References.Add(new ReferenceItem(path, ReferenceItem.Shorten(url), url));
            if (SourceImage is null) LoadPrimary();
            ReferenceUrl = "";
            Status = "Dodano obraz z linku";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się pobrać obrazu: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveReference(ReferenceItem? item)
    {
        if (item is null || !References.Remove(item)) return;

        if (References.Count == 0)
        {
            _primaryPath = null;
            SourceImage = null;
        }
        else if (item.Path == _primaryPath)
        {
            // Usunięto podstawowy: nowym podstawowym staje się następny na liście
            LoadPrimary();
        }
        Status = $"Referencje: {References.Count}";
    }

    [RelayCommand]
    private void ClearReferences()
    {
        References.Clear();
        _primaryPath = null;
        SourceImage = null;
        Status = "Wyczyszczono referencje";
    }

    // Podgląd i proporcje z pierwszego obrazu (podstawowego)
    private void LoadPrimary()
    {
        _primaryPath = References[0].Path;
        var source = LoadImage(File.ReadAllBytes(_primaryPath));
        SourceImage = source;
        ApplyAspectFrom(source.PixelWidth, source.PixelHeight);
    }

    // --- Ulepszanie promptu ---

    [RelayCommand(CanExecute = nameof(CanEnhanceGenerate))]
    private Task EnhanceGeneratePromptAsync() => EnhancePromptAsync(GeneratePrompt, v => _undoGenerate = v, v => GeneratePrompt = v);

    [RelayCommand(CanExecute = nameof(CanEnhanceEdit))]
    private Task EnhanceEditPromptAsync() => EnhancePromptAsync(EditPrompt, v => _undoEdit = v, v => EditPrompt = v);

    [RelayCommand(CanExecute = nameof(CanUndoGenerate))]
    private void UndoEnhanceGeneratePrompt()
    {
        GeneratePrompt = _undoGenerate!;
        _undoGenerate = null;
        UndoEnhanceGeneratePromptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndoEdit))]
    private void UndoEnhanceEditPrompt()
    {
        EditPrompt = _undoEdit!;
        _undoEdit = null;
        UndoEnhanceEditPromptCommand.NotifyCanExecuteChanged();
    }

    // Ulepszony tekst zastępuje pole; oryginał zostaje do cofnięcia
    private async Task EnhancePromptAsync(string prompt, Action<string> keepOriginal, Action<string> apply)
    {
        IsBusy = true;
        Status = "Ulepszanie promptu...";
        try
        {
            var instruction = string.IsNullOrWhiteSpace(EnhancerInstruction) ? DefaultInstruction : EnhancerInstruction;
            var improved = await _service.EnhancePromptAsync(SelectedEnhancerModel!, instruction, prompt, EnhancerParameterValues());
            keepOriginal(prompt);
            apply(improved);
            Status = $"Prompt ulepszony ({SelectedEnhancerModel})";
        }
        catch (Exception ex)
        {
            Status = $"Błąd ulepszania promptu: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            UndoEnhanceGeneratePromptCommand.NotifyCanExecuteChanged();
            UndoEnhanceEditPromptCommand.NotifyCanExecuteChanged();
        }
    }

    // --- Komendy generowania / zapisu ---

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync()
    {
        SaveState();
        return RunAsync(() => _service.GenerateAsync(SelectedModel!.Id, GeneratePrompt, Size, CurrentImageOptions()),
                        GeneratePrompt, SelectedModel!.Id, "generate", Size, SelectedModel!.Pricing?.PriceNow);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync()
    {
        SaveState();
        return RunAsync(async () =>
        {
            var edited = await _service.EditAsync(SelectedEditModel!.Id, EditPrompt, References.Select(r => r.Path).ToList());
            // Endpoint edycji nie respektuje rozmiaru, więc wynik dopasowujemy do wybranych proporcji lokalnie
            return edited with { Bytes = ImageFitter.FitTo(edited.Bytes, Width, Height) };
        }, EditPrompt, SelectedEditModel!.Id, "edit", Size, SelectedEditModel!.Pricing?.PriceNow);
    }

    // Seed i negative prompt tylko gdy podane; jakość "auto" = nie wysyłamy
    private ImageOptions CurrentImageOptions()
    {
        int? seed = int.TryParse(SeedText, out var parsed) ? parsed : null;
        var negative = string.IsNullOrWhiteSpace(NegativePrompt) ? null : NegativePrompt.Trim();
        var quality = SelectedQuality == "auto" ? null : SelectedQuality;
        return new ImageOptions(seed, negative, quality);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void CopyImage()
    {
        try
        {
            _dialogs.CopyImage(Image!);
            Status = "Skopiowano do schowka";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się skopiować: {ex.Message}";
        }
    }

    private bool CanSave => Image is not null || ShowVideo;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveImageAs()
    {
        if (ShowVideo && VideoSource is not null)
        {
            var target = _dialogs.ChooseSaveFile($"{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            if (target is null) return;

            File.Copy(VideoSource.LocalPath, target, overwrite: true);
            Status = $"Zapisano: {target}";
            return;
        }

        var ext = _imageBytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = _dialogs.ChooseSaveFile($"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        if (path is null) return;

        File.WriteAllBytes(path, _imageBytes!);
        Status = $"Zapisano: {path}";
    }

    // 100%: rzeczywisty rozmiar obrazu z przewijaniem
    [RelayCommand]
    private void ResetZoom()
    {
        Zoom = 1.0;
        FitToWindow = false;
    }

    [RelayCommand]
    private void FitImage() => FitToWindow = true;

    // Każdy wynik trafia do galerii razem z promptem i kosztem; estymata z cennika, gdy API nie zwróci kosztu
    private async Task RunAsync(Func<Task<ImageResult>> produce, string prompt, string model, string kind,
                                string size, decimal? estimate)
    {
        IsBusy = true;
        Status = "Przetwarzanie...";
        try
        {
            var result = await produce();
            var path = SaveImage(result.Bytes);
            _imageBytes = result.Bytes;
            Image = LoadImage(result.Bytes);
            FitToWindow = true; // nowy wynik od razu mieści się w oknie
            ShowVideo = false;

            AddToGallery(new GalleryEntry
            {
                Prompt = prompt,
                Model = model,
                Kind = kind,
                Size = size,
                FilePath = path,
                CostUsd = result.CostUsd ?? estimate,
                CostEstimated = result.CostUsd is null && estimate is not null,
                Thumbnail = GalleryStore.Thumbnail(path),
            });
            Status = $"Zapisano: {path}";
        }
        catch (Exception ex)
        {
            Status = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Wideo ---

    public ObservableCollection<ModelOption> VideoModels { get; } = [];
    public IReadOnlyList<string> VideoAspects { get; } = ["16:9", "9:16", "1:1", "4:3"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateVideoCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel), nameof(VideoEstimateLabel), nameof(VideoHint))]
    private ModelOption? _selectedVideoModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateVideoCommand))]
    private string _videoPrompt = "";

    // Sekundy wideo (parametr duration)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoEstimateLabel))]
    private int _videoDuration = 5;

    [ObservableProperty]
    private string _videoAspect = "16:9";

    // Załączniki do promptu wideo (obrazy wejściowe); wiele = pole input_images
    public ObservableCollection<ReferenceItem> VideoAttachments { get; } = [];

    public string VideoHint
    {
        get
        {
            if (VideoAttachments.Count == 0) return "";
            return SelectedVideoModel is { AcceptsImage: false }
                ? "Wybrany model nie przyjmuje obrazów. Wybierz model image → video lub reference → video."
                : $"Załączniki: {VideoAttachments.Count}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void AddVideoAttachments()
    {
        var paths = _dialogs.PickImageFiles();
        if (paths is null || paths.Length == 0) return;

        try
        {
            foreach (var path in paths)
                VideoAttachments.Add(new ReferenceItem(ImageOrientation.EnsureUpright(path), Path.GetFileName(path), path));
            Status = $"Załączniki wideo: {VideoAttachments.Count}";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się wczytać załącznika: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveVideoAttachment(ReferenceItem? item)
    {
        if (item is not null) VideoAttachments.Remove(item);
    }

    [RelayCommand]
    private void ClearVideoAttachments() => VideoAttachments.Clear();

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    // Wynik: wideo zamiast obrazu w podglądzie
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage))]
    [NotifyCanExecuteChangedFor(nameof(SaveImageAsCommand))]
    private bool _showVideo;

    [ObservableProperty]
    private Uri? _videoSource;

    public bool ShowImage => !ShowVideo;

    public string VideoEstimateLabel => SelectedVideoModel?.Pricing is { } p
        ? $"Szacunek: ~${p.PriceNow * VideoDuration:0.00} ({VideoDuration} s × ${p.PriceNow:0.####}/s)"
        : "Brak ceny dla tego modelu";

    // Załączniki wymagają modelu, który przyjmuje obrazy
    private bool CanGenerateVideo => !IsBusy && !string.IsNullOrWhiteSpace(VideoPrompt)
                                     && SelectedVideoModel is not null
                                     && (VideoAttachments.Count == 0 || SelectedVideoModel.AcceptsImage);

    // Każde wywołanie tworzy płatne zadanie; postęp idzie do paska stanu
    [RelayCommand(CanExecute = nameof(CanGenerateVideo))]
    private async Task GenerateVideoAsync()
    {
        var model = SelectedVideoModel!;
        var estimate = model.Pricing?.PriceNow * VideoDuration;
        IsBusy = true;
        Status = "Wysyłanie zadania wideo...";
        try
        {
            var attachments = VideoAttachments
                .Select(a => (Bytes: File.ReadAllBytes(a.Path), Mime: MimeOf(a.Path)))
                .ToList();

            var result = await _service.GenerateVideoAsync(model.Id, VideoPrompt, VideoDuration, VideoAspect,
                                                           attachments, s => Status = $"Wideo: {s}...");

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ImageStudio");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            await File.WriteAllBytesAsync(path, result.Bytes);

            ShowVideo = true;
            VideoSource = new Uri(path);
            AddToGallery(new GalleryEntry
            {
                Prompt = VideoPrompt,
                Model = model.Id,
                Kind = "video",
                Size = $"{VideoAspect} · {VideoDuration} s" + (attachments.Count > 0 ? $" · {attachments.Count} zał." : ""),
                FilePath = path,
                CostUsd = result.CostUsd ?? estimate,
                CostEstimated = result.CostUsd is null && estimate is not null,
            });
            Status = $"Zapisano wideo: {path}";
        }
        catch (Exception ex)
        {
            Status = $"Błąd wideo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Galeria (bufor wyników) ---

    public ObservableCollection<GalleryEntry> Gallery { get; } = [];

    [ObservableProperty]
    private GalleryEntry? _selectedGalleryEntry;

    public string GalleryTotalLabel
    {
        get
        {
            var total = Gallery.Where(e => e.CostUsd is not null).Sum(e => e.CostUsd!.Value);
            return $"Galeria: {Gallery.Count} · łącznie ~${total:0.0000}";
        }
    }

    // Kliknięcie miniatury pokazuje ten obraz w podglądzie wyniku
    partial void OnSelectedGalleryEntryChanged(GalleryEntry? value)
    {
        if (value is null || !File.Exists(value.FilePath)) return;

        if (value.Kind == "video")
        {
            ShowVideo = true;
            VideoSource = new Uri(value.FilePath);
            Status = $"{value.KindLabel} · {value.CostLabel} · {value.Prompt}";
            return;
        }

        ShowVideo = false;
        _imageBytes = File.ReadAllBytes(value.FilePath);
        Image = LoadImage(_imageBytes);
        FitToWindow = true;
        Status = $"{value.KindLabel} · {value.CostLabel} · {value.Prompt}";
    }

    private void AddToGallery(GalleryEntry entry)
    {
        Gallery.Insert(0, entry);
        while (Gallery.Count > GalleryStore.MaxEntries) Gallery.RemoveAt(Gallery.Count - 1);
        GalleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
    }

    // Czyści bufor; pliki obrazów zostają w folderze zapisów
    [RelayCommand]
    private void ClearGallery()
    {
        Gallery.Clear();
        SelectedGalleryEntry = null;
        GalleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
        Status = "Wyczyszczono galerię (pliki obrazów zostały w folderze zapisów).";
    }

    private static void Replace(ObservableCollection<ModelOption> target, IEnumerable<ImageModelInfo> infos,
                                IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        target.Clear();
        foreach (var info in infos.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            target.Add(new ModelOption(info.Id, info.OwnedBy, info.Nsfw, info.InputModalities,
                                       pricing.GetValueOrDefault(info.Id)));
    }

    private void FillProviders(IEnumerable<ImageModelInfo> infos)
    {
        Providers.Clear();
        Providers.Add(AllProviders);
        foreach (var owner in infos.Select(i => i.OwnedBy).OfType<string>().Distinct().OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
            Providers.Add(owner);
    }

    private static void ReplaceStrings(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // Automatyczny zapis do Obrazy\ImageStudio (kopia bezpieczeństwa; "Zapisz jako" zapisuje w wybranym miejscu)
    private static string SaveImage(byte[] bytes)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ImageStudio");
        Directory.CreateDirectory(dir);

        var ext = bytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static BitmapImage LoadImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // zwalnia strumień po załadowaniu
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
