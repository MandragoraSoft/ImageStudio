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
            Log.LogError("{Message}", Status);
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
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Wklejenie obrazu ze schowka (np. zrzutu ekranu) jako źródła edycji
    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void PasteReference()
    {
        var path = PasteClipboardImage();
        if (path is null) return;

        References.Add(new ReferenceItem(path, $"Schowek {DateTime.Now:HH:mm:ss}", "schowek"));
        if (SourceImage is null) LoadPrimary();
        Status = $"Dodano obraz ze schowka. Referencje: {References.Count}";
    }

    // Zapisuje obraz ze schowka jako PNG w katalogu referencji; null, gdy schowka nie ma obrazu
    private string? PasteClipboardImage()
    {
        try
        {
            var image = _dialogs.GetClipboardImage();
            if (image is null)
            {
                Status = "Schowek nie zawiera obrazu.";
                return null;
            }

            var dir = Path.Combine(AppPaths.UserDataDir, "refs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path))
                encoder.Save(file);
            return path;
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się wkleić obrazu: {ex.Message}";
            Log.LogError("{Message}", Status);
            return null;
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
}
