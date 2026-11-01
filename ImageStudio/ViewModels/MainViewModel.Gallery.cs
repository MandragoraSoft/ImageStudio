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

    public string GalleryTotalLabel
    {
        get
        {
            var total = Gallery.Where(e => e.CostUsd is not null).Sum(e => e.CostUsd!.Value);
            return $"Galeria: {Gallery.Count} · łącznie ~${total:0.0000}";
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
        Status = "Wyczyszczono galerię (pliki obrazów zostały w folderze zapisów).";
    }

    // --- Menu kontekstowe miniatury (prawy przycisk) ---

    // Usuwa wpis z galerii; plik zostaje w folderze zapisów (jak przy "Wyczyść galerię")
    [RelayCommand]
    private void RemoveGalleryEntry(GalleryEntry? entry)
    {
        if (entry is null) return;

        RemoveFromGallery(entry);
        Status = "Usunięto z galerii (plik został w folderze zapisów).";
    }

    // Usuwa wpis razem z plikiem na dysku, po potwierdzeniu
    [RelayCommand]
    private void DeleteGalleryEntryFile(GalleryEntry? entry)
    {
        if (entry is null) return;
        if (!_dialogs.Confirm($"Usunąć z galerii i z dysku?{Environment.NewLine}{Environment.NewLine}{entry.FilePath}", "Usuwanie z dysku")) return;

        // Wyświetlane wideo trzyma plik otwarty: najpierw zwalniamy podgląd
        if (ShowVideo && VideoSource?.LocalPath == entry.FilePath) ClearResult();

        try
        {
            if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Nie udało się usunąć pliku: {ex.Message}";
            return;
        }

        RemoveFromGallery(entry);
        Status = "Usunięto z galerii i z dysku.";
    }

    [RelayCommand]
    private void CopyGalleryPrompt(GalleryEntry? entry)
    {
        if (entry is null) return;

        _dialogs.CopyText(entry.Prompt);
        Status = "Skopiowano prompt.";
    }

    private void RemoveFromGallery(GalleryEntry entry)
    {
        if (SelectedGalleryEntry == entry) SelectedGalleryEntry = null;
        Gallery.Remove(entry);
        _galleryStore.Save(Gallery);
        OnPropertyChanged(nameof(GalleryTotalLabel));
    }
}
