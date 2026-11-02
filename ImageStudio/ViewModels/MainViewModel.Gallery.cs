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
    // --- Galeria (bufor wyników) ---

    public ObservableCollection<GalleryEntry> Gallery { get; } = [];

    [ObservableProperty]
    private GalleryEntry? _selectedGalleryEntry;

    // Etykiety liczone w kodzie nie mają powiązania ze słownikiem: po zmianie języka odświeżamy je ręcznie
    private void RefreshLanguageTexts()
    {
        OnPropertyChanged(nameof(GalleryTotalLabel));
        OnPropertyChanged(nameof(VideoEstimateLabel));
        OnPropertyChanged(nameof(VideoHint));
        Voice.RefreshLanguageTexts();
    }

    public string GalleryTotalLabel
    {
        get
        {
            var total = Gallery.Where(e => e.CostUsd is not null).Sum(e => e.CostUsd!.Value);
            return Loc.T("gallery.total", Gallery.Count, total);
        }
    }

    // Kliknięcie miniatury pokazuje ten obraz lub wideo w podglądzie wyniku; opis (prompt, rodzaj, koszt) jest w dymku miniatury
    partial void OnSelectedGalleryEntryChanged(GalleryEntry? value)
    {
        if (value is null || !File.Exists(value.FilePath)) return;

        if (value.Kind == "video")
        {
            ShowVideo = true;
            VideoSource = new Uri(value.FilePath);
            return;
        }

        ShowVideo = false;
        _imageBytes = File.ReadAllBytes(value.FilePath);
        Image = LoadImage(_imageBytes);
        FitToWindow = true;
    }

    private void AddToGallery(GalleryEntry entry)
    {
        Gallery.Insert(0, entry);
        while (Gallery.Count > GalleryStore.MaxEntries) Gallery.RemoveAt(Gallery.Count - 1);
        _galleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
    }

    // Czyści bufor; pliki obrazów zostają w folderze zapisów
    [RelayCommand]
    private void ClearGallery()
    {
        Gallery.Clear();
        SelectedGalleryEntry = null;
        _galleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
        Status = Loc.T("gallery.msg.cleared");
    }

    // --- Menu kontekstowe miniatury (prawy przycisk) ---

    // Powtarza operację z wpisu (ten sam prompt, referencje i parametry; kosztuje jak pierwotna) i pokazuje nowy wynik jak zwykły.
    // Wpis starszy niż zapis wejść (edycja bez zapisanych referencji) kończy się komunikatem w stopce
    [RelayCommand(CanExecute = nameof(CanRepeatGalleryEntry))]
    private async Task RepeatGalleryEntryAsync(GalleryEntry? entry)
    {
        if (entry is null) return;

        if (entry.Kind != "video")
        {
            await RunAsync(() => Studio.RepeatAsync(new RepeatRequest(entry.Id, Publish: false)));
            return;
        }

        IsBusy = true;
        Status = Loc.T("video.msg.sending");
        try
        {
            var repeated = (await Studio.RepeatAsync(new RepeatRequest(entry.Id, Progress: s => Status = Loc.T("video.msg.progress", s), Publish: false))).Entry;
            ShowVideo = true;
            VideoSource = new Uri(repeated.FilePath);
            repeated.Thumbnail = GalleryStore.Thumbnail(repeated.FilePath);
            AddToGallery(repeated);
            Status = Loc.T("video.msg.saved", repeated.FilePath);
        }
        catch (Exception ex)
        {
            Status = Loc.T("video.msg.error", ex.Message);
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRepeatGalleryEntry => !IsBusy;

    // Usuwa wpis z galerii; plik zostaje w folderze zapisów (jak przy "Wyczyść galerię")
    [RelayCommand]
    private void RemoveGalleryEntry(GalleryEntry? entry)
    {
        if (entry is null) return;

        RemoveFromGallery(entry);
        Status = Loc.T("gallery.msg.removed");
    }

    // Usuwa wpis razem z plikiem na dysku, po potwierdzeniu
    [RelayCommand]
    private void DeleteGalleryEntryFile(GalleryEntry? entry)
    {
        if (entry is null) return;
        if (!_dialogs.Confirm(Loc.T("gallery.confirmDelete", entry.FilePath), Loc.T("gallery.confirmDelete.title"))) return;

        // Wyświetlane wideo trzyma plik otwarty: najpierw zwalniamy podgląd
        if (ShowVideo && VideoSource?.LocalPath == entry.FilePath) ClearResult();

        try
        {
            if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("gallery.msg.deleteFailed", ex.Message);
            return;
        }

        RemoveFromGallery(entry);
        Status = Loc.T("gallery.msg.deleted");
    }

    [RelayCommand]
    private void CopyGalleryPrompt(GalleryEntry? entry)
    {
        if (entry is null) return;

        _dialogs.CopyText(entry.Prompt);
        Status = Loc.T("gallery.msg.promptCopied");
    }

    private void RemoveFromGallery(GalleryEntry entry)
    {
        if (SelectedGalleryEntry == entry) SelectedGalleryEntry = null;
        Gallery.Remove(entry);
        _galleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
    }
}
