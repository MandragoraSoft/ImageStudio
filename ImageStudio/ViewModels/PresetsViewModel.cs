using ImageStudio.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Selektor modelu w oknie zestawów: widok modeli rodzaju (osobny od głównego okna) i filtr zakładki z MainViewModel
public sealed record PresetModelPicker(ICollectionView View, ModelFilter Filter);

// Zapytanie o nazwę i opis zestawu: prompt i konfiguracja z edycji oraz model wspomagania zestawu (null = domyślny)
public sealed record PresetSuggestRequest(string Prompt, string? SystemPrompt, string? Model);

// Otwarcie edytora promptu dla zestawu: tytuł okna, wejście, model i parametry przebudowy zestawu
public sealed record PresetPromptEditRequest(string Title, PromptEdit Initial, string? Model, IReadOnlyDictionary<string, ParamState> Parameters);

// Dane z głównego okna: modele każdego rodzaju, id aktywnego dostawcy (zapisywane przy zmianie modelu) i selektory modeli.
// Suggest, EditPrompt i autozapis są opcjonalne: bez nich przyciski i ustawienia są ukryte albo wyłączone
public sealed record PresetsContext(
    Func<string, IReadOnlyList<ModelOption>> ModelsFor,
    string? ProviderId,
    IReadOnlyDictionary<string, PresetModelPicker>? Pickers = null,
    Func<PresetSuggestRequest, Task<PresetDraft>>? Suggest = null,
    Func<PresetPromptEditRequest, PromptEdit?>? EditPrompt = null,
    bool AutoSave = false,
    Action<bool>? AutoSaveChanged = null);

// Parametr ulepszania promptu w edytorze: klucz jest stały, włączenie i wartość można zmienić
public sealed partial class PresetParamItem(string key, bool enabled, double value) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    private bool _enabled = enabled;

    [ObservableProperty]
    private double _value = value;
}

// Okno zarządzania zestawami: lista z wyszukiwaniem, edycja wszystkich pól, duplikowanie, usuwanie i wczytanie
public sealed partial class PresetsViewModel : ObservableObject
{
    // Pełna lista zestawów; Presets to jej widok po filtrach
    private readonly List<Preset> _all = [];

    // Zmiany wykonywane przez program (przebudowa listy, przeładowanie) nie pytają o niezapisane zmiany
    private bool _busy;

    private readonly PresetsContext? _context;

    public ObservableCollection<Preset> Presets { get; } = [];

    public IReadOnlyList<string> KindFilters { get; } = ["all", "generate", "edit"];

    public IReadOnlyList<string> QualityOptions { get; } = StudioDefaults.Qualities;

    // Kafelki proporcji w sekcji rozmiaru (te same co w głównym oknie)
    public IReadOnlyList<string> AspectPresets { get; } = StudioDefaults.ImageAspects;

    public ObservableCollection<PresetParamItem> EditParameters { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPresetCommand), nameof(DeletePresetCommand), nameof(DuplicatePresetCommand), nameof(SaveDetailsCommand),
                                nameof(SuggestNameCommand), nameof(SuggestDescriptionCommand), nameof(EditInEditorCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PickerView), nameof(PickerFilter), nameof(SelectedModelOption), nameof(ModelWarning),
                              nameof(EnhancerPickerView), nameof(EnhancerPickerFilter), nameof(SelectedEnhancerOption), nameof(EnhancerWarning),
                              nameof(CanEditInEditor))]
    private Preset? _selectedPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editDescription = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand), nameof(SuggestNameCommand), nameof(SuggestDescriptionCommand))]
    private string _editPrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editSystemPrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editNegativePrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(DetailsSummary))]
    private string _editSeedText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(DetailsSummary))]
    private string _editQuality = "auto";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedAspect), nameof(DetailsSummary))]
    private int _editWidth;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedAspect), nameof(DetailsSummary))]
    private int _editHeight;

    // Proporcja z predefiniowanej listy pasującej do wymiarów (null = własny rozmiar)
    public string? SelectedAspect
    {
        get => ImageAspect.LabelFor(EditWidth, EditHeight);
        set
        {
            // Wybór proporcji zmienia wysokość przy zachowanej szerokości
            if (value is null || ImageAspect.Ratio(value) is not { } ratio || EditWidth <= 0) return;
            EditHeight = ImageAspect.Snap(EditWidth / ratio);
        }
    }

    // Jednowierszowy opis rozmiaru, seeda i jakości do nagłówka zwijanej sekcji
    public string DetailsSummary => Loc.T("presets.details.summary", EditWidth, EditHeight,
        Norm(EditSeedText) is { Length: > 0 } seed ? seed : Loc.T("presets.details.seedRandom"), EditQuality);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedModelOption), nameof(ModelWarning))]
    private string? _editModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _searchText = "";

    [ObservableProperty]
    private string _kindFilter = "all";

    [ObservableProperty]
    private string _status = "";

    // Zapis bez pytania przy zmianie zestawu i zamykaniu; zapamiętywany w stanie aplikacji przez kontekst
    [ObservableProperty]
    private bool _autoSave;

    partial void OnAutoSaveChanged(bool value) => _context?.AutoSaveChanged?.Invoke(value);

    // Zestaw wybrany do wczytania; odczytywany przez WpfDialogService po zamknięciu okna
    public Preset? ChosenPreset { get; private set; }

    // Ustawiane przez okno, żeby ViewModel mógł je zamknąć
    public event Action? CloseRequested;

    private readonly IDialogService _dialogs;
    private readonly IPresetStore _presets;
    private readonly LegacyPromptImporter _importer;

    public PresetsViewModel(IDialogService dialogs, IPresetStore presets, LegacyPromptImporter importer, PresetsContext? context = null)
    {
        _dialogs = dialogs;
        _presets = presets;
        _importer = importer;
        _context = context;
        // Pole, nie właściwość: wartość startowa nie wywołuje zapisu stanu
        _autoSave = context?.AutoSave ?? false;
        Reload(null);
    }

    public bool HasSelection => SelectedPreset is not null;

    // Niezapisane zmiany: którekolwiek pole edycji różni się od zaznaczonego zestawu
    public bool IsDirty => SelectedPreset is { } p
        && (Norm(EditName) != Norm(p.Name) || Norm(EditDescription) != Norm(p.Description)
            || EditPrompt != (p.Prompt ?? "") || EditSystemPrompt != (p.SystemPrompt ?? "")
            || Norm(EditNegativePrompt) != Norm(p.NegativePrompt) || Norm(EditSeedText) != Norm(p.SeedText)
            || EditQuality != (p.Quality ?? "auto") || EditWidth != p.Width || EditHeight != p.Height
            || EditModel != p.Model || EditEnhancerModel != p.EnhancerModel
            || Norm(EditEnhancerInstruction) != Norm(p.EnhancerInstruction) || ParametersChanged(p));

    // Selektor modelu i jego filtr dla rodzaju zaznaczonego zestawu (null bez kontekstu)
    private PresetModelPicker? CurrentPicker =>
        SelectedPreset is { } p && _context?.Pickers is { } pickers && pickers.TryGetValue(p.Kind, out var picker) ? picker : null;

    public ICollectionView? PickerView => CurrentPicker?.View;

    public ModelFilter? PickerFilter => CurrentPicker?.Filter;

    // Zaznaczenie w ModelPicker: tylko wybór użytkownika zmienia EditModel; model spoza listy zostaje jako Id
    public ModelOption? SelectedModelOption
    {
        get => SelectedPreset is { } p ? ModelsOf(p.Kind).FirstOrDefault(m => m.Id == EditModel) : null;
        set
        {
            if (value is not null) EditModel = value.Id;
        }
    }

    // Ostrzeżenie, gdy zapisany model nie jest na bieżącej liście modeli tego rodzaju
    public string ModelWarning => SelectedPreset is { } p && !string.IsNullOrEmpty(EditModel)
        && ModelsOf(p.Kind) is { Count: > 0 } models && !models.Any(m => m.Id == EditModel)
        ? Loc.T("presets.msg.modelMissing", EditModel)
        : "";

    private bool HasSelectedPreset => SelectedPreset is not null;

    // --- Edytor promptu z okna zestawów ---

    public bool CanEditInEditor => HasSelectedPreset && _context?.EditPrompt is not null;

    // Wynik edytora wpisuje tylko pola edycji; zapis dopiero przez przycisk, pytanie albo autozapis
    [RelayCommand(CanExecute = nameof(CanEditInEditor))]
    private void EditInEditor()
    {
        if (_context?.EditPrompt is not { } edit || SelectedPreset is null) return;

        var parameters = EditParameters.ToDictionary(p => p.Key, p => new ParamState { Enabled = p.Enabled, Value = p.Value });
        var result = edit(new PresetPromptEditRequest(Loc.T("presets.editor.title", EditName),
                                                     new PromptEdit(EditSystemPrompt, EditPrompt, EditEnhancerInstruction, [],
                                                         EditNegativePrompt),
                                                     EditEnhancerModel, parameters));
        if (result is null) return;

        EditSystemPrompt = result.System ?? "";
        EditPrompt = result.Prompt;
        if (result.NegativePrompt is not null)
            EditNegativePrompt = result.NegativePrompt;
        EditEnhancerInstruction = result.Instruction ?? EditEnhancerInstruction;
    }

    // --- Model wspomagania (tekstowy, do ulepszania promptu zestawu) ---

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand), nameof(SuggestNameCommand), nameof(SuggestDescriptionCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedEnhancerOption), nameof(EnhancerWarning))]
    private string? _editEnhancerModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editEnhancerInstruction = "";

    private IReadOnlyList<ModelOption> EnhancerModelsOf() => _context?.ModelsFor("enhancer") ?? [];

    public ICollectionView? EnhancerPickerView => _context?.Pickers?.GetValueOrDefault("enhancer")?.View;

    public ModelFilter? EnhancerPickerFilter => _context?.Pickers?.GetValueOrDefault("enhancer")?.Filter;

    // Zaznaczenie w ModelPicker wspomagania: tylko wybór użytkownika zmienia EditEnhancerModel
    public ModelOption? SelectedEnhancerOption
    {
        get => SelectedPreset is not null ? EnhancerModelsOf().FirstOrDefault(m => m.Id == EditEnhancerModel) : null;
        set
        {
            if (value is not null) EditEnhancerModel = value.Id;
        }
    }

    // Ostrzeżenie, gdy zapisany model wspomagania nie jest na niepustej liście (model zostaje w zestawie)
    public string EnhancerWarning => SelectedPreset is not null && !string.IsNullOrEmpty(EditEnhancerModel)
        && EnhancerModelsOf() is { Count: > 0 } models && !models.Any(m => m.Id == EditEnhancerModel)
        ? Loc.T("presets.msg.modelMissing", EditEnhancerModel)
        : "";

    // --- Wspomaganie AI nazwy i opisu ---

    public bool CanSuggest => _context?.Suggest is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestNameCommand), nameof(SuggestDescriptionCommand))]
    private bool _isSuggesting;

    private bool CanSuggestNow => CanSuggest && HasSelectedPreset && !IsSuggesting && !string.IsNullOrWhiteSpace(EditPrompt);

    // Każdy przycisk wpisuje tylko swoje pole; wynik niczego nie zapisuje (zestaw staje się zmieniony)
    [RelayCommand(CanExecute = nameof(CanSuggestNow))]
    private Task SuggestNameAsync() => SuggestAsync(draft => EditName = draft.Name);

    [RelayCommand(CanExecute = nameof(CanSuggestNow))]
    private Task SuggestDescriptionAsync() => SuggestAsync(draft => EditDescription = draft.Description);

    private async Task SuggestAsync(Action<PresetDraft> apply)
    {
        if (_context?.Suggest is not { } suggest) return;

        IsSuggesting = true;
        Status = Loc.T("presetSave.msg.thinking");
        try
        {
            var system = string.IsNullOrWhiteSpace(EditSystemPrompt) ? null : EditSystemPrompt;
            apply(await suggest(new PresetSuggestRequest(EditPrompt, system, EditEnhancerModel)));
            Status = "";
        }
        catch (Exception ex)
        {
            Status = Loc.T("presetSave.msg.failed", ex.Message);
        }
        finally
        {
            IsSuggesting = false;
        }
    }

    private IReadOnlyList<ModelOption> ModelsOf(string kind) => _context?.ModelsFor(kind) ?? [];

    // Przed zmianą zaznaczenia pytamy o niezapisane zmiany; zapis zestawu, który przestaje być zaznaczony, też tu się dzieje
    partial void OnSelectedPresetChanging(Preset? value) => ResolveUnsaved();

    partial void OnSelectedPresetChanged(Preset? value) => LoadEditFields(value);

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    partial void OnKindFilterChanged(string value) => RefreshVisible();

    private static string Norm(string? text) => text?.Trim() ?? "";

    private bool ParametersChanged(Preset p) => EditParameters.Any(i =>
        !p.Parameters.TryGetValue(i.Key, out var state) || state.Enabled != i.Enabled || state.Value != i.Value);

    private void LoadEditFields(Preset? p)
    {
        EditName = p?.Name ?? "";
        EditDescription = p?.Description ?? "";
        EditPrompt = p?.Prompt ?? "";
        EditSystemPrompt = p?.SystemPrompt ?? "";
        EditNegativePrompt = p?.NegativePrompt ?? "";
        EditSeedText = p?.SeedText ?? "";
        EditQuality = p?.Quality ?? "auto";
        EditWidth = p?.Width ?? 0;
        EditHeight = p?.Height ?? 0;
        EditModel = p?.Model;
        EditEnhancerModel = p?.EnhancerModel;
        EditEnhancerInstruction = p?.EnhancerInstruction ?? "";

        EditParameters.Clear();
        if (p is null) return;
        foreach (var (key, state) in p.Parameters)
        {
            var item = new PresetParamItem(key, state.Enabled, state.Value);
            item.PropertyChanged += (_, _) => SaveDetailsCommand.NotifyCanExecuteChanged();
            EditParameters.Add(item);
        }
    }

    // Ładuje listę z magazynu i zaznacza wskazany zestaw (albo pierwszy)
    private void Reload(string? selectFilePath)
    {
        _all.Clear();
        _all.AddRange(_presets.LoadAll());
        ApplyFilter(selectFilePath);
    }

    // Przebudowa widoku po filtrach; zaznaczenie zostaje, jeśli zestaw nadal pasuje
    private void ApplyFilter(string? selectFilePath)
    {
        var wasBusy = _busy;
        _busy = true;
        try
        {
            Presets.Clear();
            foreach (var preset in _all.OrderByDescending(p => p.UpdatedUtc).Where(Matches)) Presets.Add(preset);
            SelectedPreset = Presets.FirstOrDefault(p => p.FilePath == selectFilePath) ?? Presets.FirstOrDefault();
        }
        finally
        {
            _busy = wasBusy;
        }
    }

    // Zmiana filtra: jeśli zaznaczony zestaw znika z listy, najpierw pytamy o jego niezapisane zmiany
    private void RefreshVisible()
    {
        if (SelectedPreset is { } p && !Matches(p)) ResolveUnsaved();
        ApplyFilter(SelectedPreset?.FilePath);
    }

    private bool Matches(Preset p)
    {
        if (KindFilter != "all" && p.Kind != KindFilter) return false;
        var text = SearchText.Trim();
        return text.Length == 0
               || (p.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
               || (p.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
               || (p.Prompt?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // Pytanie o niezapisane zmiany: tak = zapis (przy błędzie walidacji zmiany są odrzucane), nie = odrzucenie.
    // Zwraca false tylko wtedy, gdy użytkownik chciał zapisać, a zapis się nie udał
    private bool ResolveUnsaved()
    {
        if (_busy || SelectedPreset is not { } current || !IsDirty) return true;
        // Autozapis: bez pytania; błąd walidacji zostawia Status z powodem (jak odpowiedź „tak” przy błędzie)
        if (!AutoSave && !_dialogs.Confirm(Loc.T("presets.confirmUnsaved", current.Name), Loc.T("presets.confirmUnsaved.title"))) return true;
        if (!TrySaveDetails()) return false;
        ApplyFilter(current.FilePath);
        return true;
    }

    // Zamknięcie okna (X, Esc, Zamknij) pyta o niezapisane zmiany tak samo jak zmiana zaznaczenia.
    // Po wczytaniu zestawu (ChosenPreset) pytanie już padło, więc nie pytamy drugi raz
    public bool ConfirmClose() => ChosenPreset is not null || ResolveUnsaved();

    // Walidacja i zapis wszystkich pól zaznaczonego zestawu; błąd ustawia Status i niczego nie zapisuje
    private bool TrySaveDetails()
    {
        if (SelectedPreset is not { } preset) return false;

        var name = Norm(EditName);
        if (name.Length == 0) return Fail(Loc.T("presets.msg.emptyName"));
        if (_presets.LoadAll().Any(o => o.FilePath != preset.FilePath && o.Kind == preset.Kind
                                        && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Fail(Loc.T("presets.msg.duplicateName"));
        if (EditWidth <= 0 || EditHeight <= 0) return Fail(Loc.T("presets.msg.badSize"));

        // Zmieniony model z listy zapisuje id dostawcy; bez zmiany Provider zostaje jak był (także null)
        if (EditModel != preset.Model && EditModel is not null && _context is { } context
            && ModelsOf(preset.Kind).Any(m => m.Id == EditModel))
            preset.Provider = context.ProviderId;

        var seed = Norm(EditSeedText);
        preset.Name = name;
        preset.Description = Norm(EditDescription);
        preset.Prompt = EditPrompt;
        preset.SystemPrompt = EditSystemPrompt;
        preset.NegativePrompt = Norm(EditNegativePrompt);
        preset.SeedText = seed.Length == 0 ? null : seed;
        preset.Quality = EditQuality;
        preset.Width = EditWidth;
        preset.Height = EditHeight;
        preset.Model = EditModel;
        // Model spoza listy zostaje jak jest (okno pokazuje ostrzeżenie); pusta instrukcja = domyślna z głównego okna
        preset.EnhancerModel = EditEnhancerModel;
        var instruction = Norm(EditEnhancerInstruction);
        preset.EnhancerInstruction = instruction.Length == 0 ? null : instruction;
        foreach (var item in EditParameters)
            preset.Parameters[item.Key] = new ParamState { Enabled = item.Enabled, Value = item.Value };

        _presets.Save(preset);
        return true;
    }

    private bool Fail(string message)
    {
        Status = message;
        return false;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void LoadPreset()
    {
        if (SelectedPreset is null || !ResolveUnsaved()) return;
        ChosenPreset = SelectedPreset;
        CloseRequested?.Invoke();
    }

    // Nazwa i opis są w pliku; nazwa pliku się nie zmienia. Zapisuje wszystkie pola edycji
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void SaveDetails()
    {
        if (!TrySaveDetails()) return;
        ApplyFilter(SelectedPreset?.FilePath);
        Status = Loc.T("presets.msg.changesSaved");
    }

    // Kopia wszystkich pól zaznaczonego zestawu; nazwa dostaje numer, gdy zajęta w tym rodzaju
    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void DuplicatePreset()
    {
        if (SelectedPreset is not { } source || !ResolveUnsaved()) return;

        var copy = _presets.Create(new Preset
        {
            Name = CopyName(source),
            Description = source.Description,
            Kind = source.Kind,
            Prompt = source.Prompt,
            SystemPrompt = source.SystemPrompt,
            Model = source.Model,
            Provider = source.Provider,
            Width = source.Width,
            Height = source.Height,
            SeedText = source.SeedText,
            NegativePrompt = source.NegativePrompt,
            Quality = source.Quality,
            EnhancerModel = source.EnhancerModel,
            EnhancerInstruction = source.EnhancerInstruction,
            Parameters = source.Parameters.ToDictionary(p => p.Key, p => new ParamState { Enabled = p.Value.Enabled, Value = p.Value.Value }),
        });
        Reload(copy.FilePath);
        Status = Loc.T("presets.msg.duplicated", copy.Name);
    }

    private string CopyName(Preset source)
    {
        var taken = _presets.LoadAll().Where(p => p.Kind == source.Kind).Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = Loc.T("presets.copyName", source.Name);
        var name = baseName;
        for (var n = 2; taken.Contains(name); n++) name = $"{baseName} {n}";
        return name;
    }

    // Jednorazowy import starej historii promptów; powtórne uruchomienie pomija już zaimportowane
    [RelayCommand]
    private void ImportLegacy()
    {
        if (!ResolveUnsaved()) return;

        var result = _importer.Import();
        if (!result.FileFound)
        {
            Status = Loc.T("presets.msg.noLegacy");
            return;
        }

        Reload(SelectedPreset?.FilePath);
        Status = Loc.T("presets.msg.imported", result.Imported, result.Skipped);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;

        if (!_dialogs.Confirm(Loc.T("presets.confirmDelete", SelectedPreset.Name), Loc.T("presets.confirmDelete.title"))) return;

        _presets.Delete(SelectedPreset);
        Reload(null);
        Status = Loc.T("presets.msg.deleted");
    }
}
