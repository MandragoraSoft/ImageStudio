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

    [ObservableProperty]
    private string _selectedQuality = "auto";

    // Kroki przebudowy AI osobno dla zakładek; trafiają do modelu jako kontekst i znikają po wczytaniu zestawu
    private IReadOnlyList<PromptRevision>? _generateHistory;
    private IReadOnlyList<PromptRevision>? _editHistory;

    private List<Preset> PresetsOfKind(string kind) => _presetStore.LoadAll().Where(p => p.Kind == kind).ToList();

    [RelayCommand]
    private void EditGeneratePrompt() =>
        EditPromptWithPresets("Prompt generowania", "generate", new PromptEdit(GenerateSystemPrompt, GeneratePrompt, EnhancerInstruction, _generateHistory), edit =>
        {
            GenerateSystemPrompt = edit.System ?? "";
            GeneratePrompt = edit.Prompt;
            _generateHistory = edit.History;
        }, () => DescribeImagesAsync([]));

    [RelayCommand]
    private void EditEditPrompt() =>
        EditPromptWithPresets("Prompt edycji", "edit", new PromptEdit(EditSystemPrompt, EditPrompt, EnhancerInstruction, _editHistory), edit =>
        {
            EditSystemPrompt = edit.System ?? "";
            EditPrompt = edit.Prompt;
            _editHistory = edit.History;
        }, () => DescribeImagesAsync(References));

    // Edytor z zestawami. Gdy w oknie nadpisano zestaw, a zatwierdzone pola są z nim zgodne, stan
    // "zmieniono od ostatniego zapisu" wraca do wartości sprzed otwarcia (zapis jest propagowany do okna głównego)
    private void EditPromptWithPresets(string title, string kind, PromptEdit initial, Action<PromptEdit> apply,
                                       Func<Task<string>> describe)
    {
        var wasDirty = PresetDirty;
        Preset? saved = null;
        var edit = _dialogs.ShowPromptEditor(title, initial, RebuildPromptAsync, this, PresetsOfKind(kind),
                                             preset => { _presetStore.Save(preset); saved = preset; }, describe);
        if (edit is null) return;

        apply(edit);
        EnhancerInstruction = edit.Instruction ?? EnhancerInstruction;
        if (saved is null) return;

        Status = $"Zapisano prompt w zestawie: {saved.Name}";
        if (edit.Prompt == saved.Prompt && (edit.System ?? "") == saved.SystemPrompt)
            PresetDirty = wasDirty;
    }

    [RelayCommand]
    private void EditVideoPrompt()
    {
        if (_dialogs.ShowPromptEditor("Prompt wideo", new PromptEdit(null, VideoPrompt),
                                      describeImages: () => DescribeImagesAsync(VideoAttachments)) is { } edit) VideoPrompt = edit.Prompt;
    }

    // Opis obrazów wybranym modelem tekstowym (musi przyjmować obrazy). Obrazy z zakładki (referencje, załączniki wideo),
    // a gdy ich brak, z wyboru plików; pusty wynik = anulowano
    private async Task<string> DescribeImagesAsync(IReadOnlyList<ReferenceItem> tabImages)
    {
        var model = SelectedEnhancerModel ?? throw new InvalidOperationException("Wybierz model w „Ustawieniach ulepszania AI”.");
        if (_chatModels.TryGetValue(model, out var info) && info.AcceptsImage == false)
            throw new InvalidOperationException($"Model {model} nie przyjmuje obrazów. Wybierz model z ikoną obrazu.");

        var paths = tabImages.Count > 0 ? tabImages.Select(i => i.Path).ToList() : _dialogs.PickImageFiles()?.ToList();
        if (paths is not { Count: > 0 }) return "";

        var images = paths.Select(path =>
        {
            var (bytes, mime) = ImageFitter.Downscale(File.ReadAllBytes(path), _settings.MaxSide);
            return new ChatImage(bytes, mime);
        }).ToList();

        var messages = new List<ChatMessage>
        {
            new("system", "You are a precise image analyst. Describe exactly what is visible: subjects and their count, pose and " +
                          "expression, clothing, objects, setting, composition and camera angle, lighting, colours, style and any " +
                          "visible text. If there are several images, describe each one in turn (\"Image 1:\", \"Image 2:\") and note " +
                          "how they relate. Be factual, do not guess what is not visible. Reply in Polish, as plain text without markdown."),
            new("user", images.Count == 1 ? "Describe this image in detail." : $"Describe these {images.Count} images in detail.", Images: images),
        };
        var reply = await _provider.ChatAsync(model, messages);
        return reply.Content.Trim();
    }

    // Przebudowa z okna edytora: bieżące pola i prompt techniczny z okna, bez zmiany stanu aplikacji (zapis dopiero po Zastosuj)
    private async Task<PromptEdit> RebuildPromptAsync(PromptEdit current)
    {
        var model = SelectedEnhancerModel ?? throw new InvalidOperationException("Wybierz model ulepszania w „Ustawieniach ulepszania AI”.");
        var instruction = string.IsNullOrWhiteSpace(current.Instruction) ? DefaultInstruction : current.Instruction;
        var parts = await _provider.EnhancePromptAsync(model, instruction, current.System ?? "", current.Prompt, EnhancerParameterValues(),
                                                       current.History);
        var step = new PromptRevision(current.System ?? "", current.Prompt, parts.System, parts.Concept, instruction, parts.Changes);
        var history = (current.History ?? []).Append(step).TakeLast(OpenAiCompat.MaxHistory).ToList();
        return current with { System = parts.System, Prompt = parts.Concept, History = history };
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

    // --- Ulepszanie promptu ---

    // Prompt wysyłany do modelu: najpierw konfiguracja, potem koncept
    private static string Compose(string system, string concept) =>
        string.IsNullOrWhiteSpace(system) ? concept.Trim() : $"{system.Trim()}\n\n{concept.Trim()}";

    // --- Komendy generowania / zapisu ---

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync()
    {
        SaveState();
        var prompt = Compose(GenerateSystemPrompt, GeneratePrompt);
        return RunAsync(() => _provider.GenerateImageAsync(SelectedModel!.Id, prompt, Size, CurrentImageOptions()),
                        prompt, SelectedModel!.Id, "generate", Size, SelectedModel!.Pricing?.PerResult);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync()
    {
        SaveState();
        return RunAsync(async () =>
        {
            var edited = await _provider.EditImageAsync(SelectedEditModel!.Id, Compose(EditSystemPrompt, EditPrompt),
                                                  References.Select(r => r.Path).ToList());
            // Endpoint edycji nie respektuje rozmiaru, więc wynik dopasowujemy do wybranych proporcji lokalnie
            return edited with { Bytes = ImageFitter.FitTo(edited.Bytes, Width, Height) };
        }, Compose(EditSystemPrompt, EditPrompt), SelectedEditModel!.Id, "edit", Size, SelectedEditModel!.Pricing?.PerResult);
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
            Status = "Skopiowano do schowka";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się skopiować: {ex.Message}";
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
            Status = $"Zapisano: {target}";
            return;
        }

        var ext = _imageBytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = _dialogs.ChooseSaveFile($"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        if (path is null) return;

        File.WriteAllBytes(path, _imageBytes!);
        Status = $"Zapisano: {path}";
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
        Status = "Wyczyszczono wynik.";
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
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
