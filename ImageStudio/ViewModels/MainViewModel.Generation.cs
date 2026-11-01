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
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(EnhanceGeneratePromptCommand))]
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

    // --- Edycja / generowanie z referencjami ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(EnhanceEditPromptCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(EnhanceGeneratePromptCommand), nameof(EnhanceEditPromptCommand))]
    private string? _selectedEnhancerModel;

    [ObservableProperty]
    private string _enhancerInstruction = DefaultInstruction;

    // Indeks zakładki w panelu wejściowym: 0 = generowanie, 1 = edycja
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveModel), nameof(IsChatTab), nameof(IsNotChatTab))]
    private int _activeTab;

    // Zakładka czatu (indeks 3) zastępuje podgląd wyniku rozmową i chowa metryczkę modelu obrazowego
    public bool IsChatTab => ActiveTab == 3;
    public bool IsNotChatTab => !IsChatTab;

    public ToolsViewModel Tools { get; }
    public ChatViewModel Chat { get; }

    // --- Ulepszanie promptu ---

    [RelayCommand(CanExecute = nameof(CanEnhanceGenerate))]
    private Task EnhanceGeneratePromptAsync() => EnhancePromptAsync(GenerateSystemPrompt, GeneratePrompt,
        v => _undoGenerate = v, v => ApplyParts(v, s => GenerateSystemPrompt = s, c => GeneratePrompt = c));

    [RelayCommand(CanExecute = nameof(CanEnhanceEdit))]
    private Task EnhanceEditPromptAsync() => EnhancePromptAsync(EditSystemPrompt, EditPrompt,
        v => _undoEdit = v, v => ApplyParts(v, s => EditSystemPrompt = s, c => EditPrompt = c));

    [RelayCommand(CanExecute = nameof(CanUndoGenerate))]
    private void UndoEnhanceGeneratePrompt()
    {
        ApplyParts(_undoGenerate!, s => GenerateSystemPrompt = s, c => GeneratePrompt = c);
        _undoGenerate = null;
        UndoEnhanceGeneratePromptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndoEdit))]
    private void UndoEnhanceEditPrompt()
    {
        ApplyParts(_undoEdit!, s => EditSystemPrompt = s, c => EditPrompt = c);
        _undoEdit = null;
        UndoEnhanceEditPromptCommand.NotifyCanExecuteChanged();
    }

    // Ulepszony tekst zastępuje pole; oryginał zostaje do cofnięcia
    // AI rozdziela tekst na konfigurację (system) i koncept, a koncept ulepsza; oryginał zostaje do cofnięcia
    private async Task EnhancePromptAsync(string system, string concept, Action<PromptParts> keepOriginal, Action<PromptParts> apply)
    {
        IsBusy = true;
        Status = "Rozdzielanie i ulepszanie promptu...";
        try
        {
            var instruction = string.IsNullOrWhiteSpace(EnhancerInstruction) ? DefaultInstruction : EnhancerInstruction;
            var parts = await _provider.EnhancePromptAsync(SelectedEnhancerModel!, instruction, system, concept, EnhancerParameterValues());
            keepOriginal(new PromptParts(system, concept));
            apply(parts);
            Status = $"Prompt rozdzielony i ulepszony ({SelectedEnhancerModel}): konfiguracja + koncept";
        }
        catch (Exception ex)
        {
            Status = $"Błąd ulepszania promptu: {ex.Message}";
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
            UndoEnhanceGeneratePromptCommand.NotifyCanExecuteChanged();
            UndoEnhanceEditPromptCommand.NotifyCanExecuteChanged();
        }
    }

    // Prompt wysyłany do modelu: najpierw konfiguracja, potem koncept
    private static string Compose(string system, string concept) =>
        string.IsNullOrWhiteSpace(system) ? concept.Trim() : $"{system.Trim()}\n\n{concept.Trim()}";

    private static void ApplyParts(PromptParts parts, Action<string> setSystem, Action<string> setConcept)
    {
        setSystem(parts.System);
        setConcept(parts.Concept);
    }

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
