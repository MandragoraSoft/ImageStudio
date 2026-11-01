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
}
