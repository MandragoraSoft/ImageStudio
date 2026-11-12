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
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // --- Generowanie ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _generatePrompt = "";

    // Część konfiguracyjna promptu (styl, technika); dołączana przed konceptem
    [ObservableProperty]
    private string _generateSystemPrompt = "";

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

    // Osobny negatywny prompt edycji (nie miesza się z generowaniem); idzie tylko natywnym parametrem modelu, który go potwierdza
    [ObservableProperty]
    private string _editNegativePrompt = "";

    [ObservableProperty]
    private string _selectedQuality = "auto";

    // Kroki przebudowy AI osobno dla zakładek; trafiają do modelu jako kontekst i znikają po wczytaniu zestawu
    private IReadOnlyList<PromptRevision>? _generateHistory;
    private IReadOnlyList<PromptRevision>? _editHistory;

    private List<Preset> PresetsOfKind(string kind) => _presetStore.LoadAll().Where(p => p.Kind == kind).ToList();

    [RelayCommand]
    private void EditGeneratePrompt() =>
        EditPromptWithPresets("Prompt generowania", "generate",
            new PromptEdit(GenerateSystemPrompt, GeneratePrompt, EnhancerInstruction, _generateHistory, NegativePrompt), edit =>
        {
            GenerateSystemPrompt = edit.System ?? "";
            GeneratePrompt = edit.Prompt;
            if (edit.NegativePrompt is not null) NegativePrompt = edit.NegativePrompt;
            _generateHistory = edit.History;
        }, () => DescribeImagesAsync([]));

    [RelayCommand]
    private void EditEditPrompt() =>
        EditPromptWithPresets("Prompt edycji", "edit",
            new PromptEdit(EditSystemPrompt, EditPrompt, EnhancerInstruction, _editHistory, EditNegativePrompt), edit =>
        {
            EditSystemPrompt = edit.System ?? "";
            EditPrompt = edit.Prompt;
            if (edit.NegativePrompt is not null) EditNegativePrompt = edit.NegativePrompt;
            _editHistory = edit.History;
        }, () => DescribeImagesAsync(References));

    // Edytor z zestawami. Gdy w oknie nadpisano zestaw, a zatwierdzone pola są z nim zgodne, stan
    // "zmieniono od ostatniego zapisu" wraca do wartości sprzed otwarcia (zapis jest propagowany do okna głównego)
    private void EditPromptWithPresets(string title, string kind, PromptEdit initial, Action<PromptEdit> apply,
                                       Func<Task<string>> describe)
    {
        var wasDirty = PresetDirty;
        Preset? saved = null;
        var edit = _dialogs.ShowPromptEditor(title, initial, current => RebuildPromptAsync(current), this, PresetsOfKind(kind),
                                             preset => { _presetStore.Save(preset); saved = preset; }, describe);
        if (edit is null) return;

        apply(edit);
        EnhancerInstruction = edit.Instruction ?? EnhancerInstruction;
        if (saved is null) return;

        Status = Loc.T("gen.msg.promptSaved", saved.Name);
        if (edit.Prompt == saved.Prompt && (edit.System ?? "") == saved.SystemPrompt
            && (edit.NegativePrompt is null || edit.NegativePrompt == (saved.NegativePrompt ?? "")))
            PresetDirty = wasDirty;
    }

    // Edytor z okna zestawów: bez paska zestawów i panelu parametrów; model i parametry przebudowy z zestawu.
    // Nie zmienia promptów, historii ani wyboru modelu w głównym oknie (wynik wraca tylko do pól zestawu)
    private PromptEdit? EditPresetPrompt(PresetPromptEditRequest request) =>
        _dialogs.ShowPromptEditor(request.Title, request.Initial,
                                  current => RebuildPromptAsync(current, request.Model, ApiParameters(request.Parameters)),
                                  describeImages: () => DescribeImagesAsync([], request.Model));

    // Wartości włączonych parametrów w kształcie żądania API (całkowite jako int), jak w ModelParameter.ToApiValue
    private static Dictionary<string, object> ApiParameters(IReadOnlyDictionary<string, ParamState> parameters)
    {
        var values = new Dictionary<string, object>();
        foreach (var spec in ModelParameter.Catalog)
        {
            if (!parameters.TryGetValue(spec.Key, out var state) || !state.Enabled) continue;
            values[spec.Key] = spec.IsInteger ? (object)(int)Math.Round(state.Value) : state.Value;
        }

        return values;
    }

    [RelayCommand]
    private void EditVideoPrompt()
    {
        if (_dialogs.ShowPromptEditor("Prompt wideo", new PromptEdit(VideoSystemPrompt, VideoPrompt, NegativePrompt: VideoNegativePrompt),
                                      describeImages: () => DescribeImagesAsync(VideoAttachments)) is not { } edit) return;

        VideoSystemPrompt = edit.System ?? "";
        VideoPrompt = edit.Prompt;
        if (edit.NegativePrompt is not null) VideoNegativePrompt = edit.NegativePrompt;
    }

    // Opis obrazów wybranym modelem tekstowym (musi przyjmować obrazy). Obrazy z zakładki (referencje, załączniki wideo),
    // a gdy ich brak, z wyboru plików; pusty wynik = anulowano
    // Model opisu/przebudowy: z żądania (np. zestawu), jeśli jest na liście ulepszania; inaczej wybrany w głównym oknie
    private string EnhancerModelOrThrow(string? requested) =>
        requested is not null && EnhancerModels.Contains(requested) ? requested
        : SelectedEnhancerModel ?? throw new InvalidOperationException(Loc.T("gen.err.noEnhancer"));

    private async Task<string> DescribeImagesAsync(IReadOnlyList<ReferenceItem> tabImages, string? requestedModel = null)
    {
        var model = EnhancerModelOrThrow(requestedModel);
        if (_chatModels.TryGetValue(model, out var info) && info.AcceptsImage == false)
            throw new InvalidOperationException(Loc.T("gen.err.noImages", model));

        var paths = tabImages.Count > 0 ? tabImages.Select(i => i.Path).ToList() : _dialogs.PickImageFiles()?.ToList();
        if (paths is not { Count: > 0 }) return "";

        return await Studio.DescribeImagesAsync(new DescribeRequest(paths, model, _settings.MaxSide));
    }

    // Przebudowa z okna edytora: bieżące pola i prompt techniczny z okna, bez zmiany stanu aplikacji (zapis dopiero po Zastosuj).
    // Model i parametry domyślnie z głównego okna; zestawy podają własne
    private async Task<PromptEdit> RebuildPromptAsync(PromptEdit current, string? requestedModel = null,
                                                      IReadOnlyDictionary<string, object>? parameters = null)
    {
        var instruction = string.IsNullOrWhiteSpace(current.Instruction) ? DefaultInstruction : current.Instruction;
        var parts = await Studio.RebuildPromptAsync(new PromptRebuildRequest(current.Prompt, current.System, instruction,
                                                                              EnhancerModelOrThrow(requestedModel),
                                                                              parameters ?? EnhancerParameterValues(), current.History,
                                                                              current.NegativePrompt));
        var step = new PromptRevision(current.System ?? "", current.Prompt, parts.System, parts.Concept, instruction, parts.Changes,
                                      current.NegativePrompt, parts.Negative);
        var history = (current.History ?? []).Append(step).TakeLast(OpenAiCompat.MaxHistory).ToList();
        return current with { System = current.System is null ? null : parts.System, Prompt = parts.Concept,
                              NegativePrompt = current.NegativePrompt is null ? null : parts.Negative, History = history };
    }

    // --- Edycja / generowanie z referencjami ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private string _editPrompt = "";

    [ObservableProperty]
    private string _editSystemPrompt = "";

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
    private string? _selectedEnhancerModel;

    [ObservableProperty]
    private string _enhancerInstruction = DefaultInstruction;

    // Indeks zakładki w panelu wejściowym: 0 = generowanie, 1 = edycja
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveModel), nameof(IsChatTab), nameof(IsVoiceTab), nameof(IsOutputTab))]
    private int _activeTab;

    // Zakładka czatu (indeks 3) zastępuje podgląd wyniku rozmową i chowa metryczkę modelu obrazowego
    public bool IsChatTab => ActiveTab == 3;
    public bool IsVoiceTab => ActiveTab == 4;

    // Zakładki z obrazem lub wideo w prawym panelu (wynik, galeria, metryczka modelu); czat i głos mają własny panel
    public bool IsOutputTab => ActiveTab < 3;

    public ToolsViewModel Tools { get; }
    public ChatViewModel Chat { get; }
    public VoiceViewModel Voice { get; }

    // --- Komendy generowania / zapisu ---

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync()
    {
        SaveState();
        return RunAsync(() => Studio.GenerateImageAsync(new ImageRequest(GeneratePrompt, GenerateSystemPrompt, SelectedModel!.Id,
                                                                         Width, Height, CurrentImageOptions(), Publish: false)));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync()
    {
        SaveState();
        return RunAsync(() => Studio.EditImageAsync(new EditRequest(EditPrompt, EditSystemPrompt, References.Select(r => r.Path).ToList(),
                                                                    SelectedEditModel!.Id, Width, Height, Publish: false,
                                                                    NegativePrompt: EditNegativePrompt)));
    }

    // Ctrl+Enter w głównym oknie: akcja aktywnej zakładki. Wideo celowo pomijamy (każde wywołanie to płatne zadanie)
    [RelayCommand]
    private void RunActiveTab()
    {
        var command = ActiveTab switch { 0 => GenerateCommand, 1 => EditCommand, _ => null };
        if (command?.CanExecute(null) == true) command.Execute(null);
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
            Status = Loc.T("gen.msg.copied");
        }
        catch (Exception ex)
        {
            Status = Loc.T("gen.msg.copyFailed", ex.Message);
            Log.LogError("{Message}", Status);
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
            Status = Loc.T("gen.msg.saved", target);
            return;
        }

        var ext = _imageBytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = _dialogs.ChooseSaveFile($"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        if (path is null) return;

        File.WriteAllBytes(path, _imageBytes!);
        Status = Loc.T("gen.msg.saved", path);
    }

    // Czyści ekran wyniku (obraz lub wideo); plik w galerii i zapis w folderze zostają
    private bool CanClearResult => Image is not null || ShowVideo;

    [RelayCommand(CanExecute = nameof(CanClearResult))]
    private void ClearResult()
    {
        Image = null;
        _imageBytes = null;
        ShowVideo = false;
        VideoSource = null;
        SelectedGalleryEntry = null;
        Status = Loc.T("gen.msg.cleared");
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

    // Ostrzeżenia (np. negative prompt niewysłany) zostają w statusie obok informacji o zapisie
    private static string WithWarnings(string status, IReadOnlyList<string>? warnings) =>
        warnings is { Count: > 0 } ? $"{status} {string.Join(" ", warnings)}" : status;

    // Każdy wynik trafia do galerii razem z promptem i kosztem (StudioApi liczy estymatę z cennika, gdy API nie zwróci kosztu)
    private async Task RunAsync(Func<Task<StudioResult>> produce)
    {
        IsBusy = true;
        Status = Loc.T("gen.msg.processing");
        try
        {
            var result = await produce();
            var entry = result.Entry;
            _imageBytes = result.Bytes;
            Image = LoadImage(result.Bytes!);
            FitToWindow = true; // nowy wynik od razu mieści się w oknie
            ShowVideo = false;

            entry.Thumbnail = GalleryStore.Thumbnail(entry.FilePath);
            AddToGallery(entry);
            Status = WithWarnings(Loc.T("gen.msg.saved", entry.FilePath), result.Warnings);
        }
        catch (Exception ex)
        {
            Status = Loc.T("msg.error", ex.Message);
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
