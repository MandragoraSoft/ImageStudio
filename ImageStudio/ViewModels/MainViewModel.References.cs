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
            Status = Loc.T("ref.msg.count", References.Count);
        }
        catch (Exception ex)
        {
            Status = Loc.T("ref.msg.loadFailed", ex.Message);
            Log.LogError("{Message}", Status);
        }
    }

    private bool CanAddUrl => !IsBusy && !string.IsNullOrWhiteSpace(ReferenceUrl);

    [RelayCommand(CanExecute = nameof(CanAddUrl))]
    private async Task AddReferenceFromUrlAsync()
    {
        var url = ReferenceUrl.Trim();
        IsBusy = true;
        Status = Loc.T("ref.msg.downloading");
        try
        {
            string path;
            using (Activity.Begin(Loc.T("activity.downloadImage")))
                path = await _downloads.DownloadAsync(url);
            References.Add(new ReferenceItem(path, ReferenceItem.Shorten(url), url));
            if (SourceImage is null) LoadPrimary();
            ReferenceUrl = "";
            Status = Loc.T("ref.msg.addedUrl");
        }
        catch (Exception ex)
        {
            Status = Loc.T("ref.msg.downloadFailed", ex.Message);
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
        Status = Loc.T("ref.msg.addedClipboard", References.Count);
    }

    // Zapisuje obraz ze schowka jako PNG w katalogu referencji; null, gdy schowka nie ma obrazu
    private string? PasteClipboardImage()
    {
        try
        {
            var image = _dialogs.GetClipboardImage();
            if (image is null)
            {
                Status = Loc.T("msg.noClipboardImage");
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
            Status = Loc.T("msg.pasteFailed", ex.Message);
            Log.LogError("{Message}", Status);
            return null;
        }
    }

    [RelayCommand]
    private void RemoveReference(ReferenceItem? item)
    {
        var index = ReferenceIndex(item);
        if (index < 0) return;
        References.RemoveAt(index);

        if (References.Count == 0)
        {
            SourceImage = null;
        }
        else if (index == 0)
        {
            // Usunięto podstawowy: nowym podstawowym staje się następny na liście
            LoadPrimary();
        }
        Status = Loc.T("ref.msg.count", References.Count);
    }

    private int ReferenceIndex(ReferenceItem? item)
    {
        for (var i = 0; i < References.Count; i++)
            if (ReferenceEquals(References[i], item)) return i;
        return -1;
    }

    private bool CanMoveReferenceLeft(ReferenceItem? item) => ReferenceIndex(item) > 0;
    private bool CanMoveReferenceRight(ReferenceItem? item)
    {
        var index = ReferenceIndex(item);
        return index >= 0 && index < References.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanMoveReferenceLeft))]
    private void MoveReferenceLeft(ReferenceItem? item)
    {
        var index = ReferenceIndex(item);
        if (index > 0) MoveReference([index, index - 1]);
    }

    [RelayCommand(CanExecute = nameof(CanMoveReferenceRight))]
    private void MoveReferenceRight(ReferenceItem? item)
    {
        var index = ReferenceIndex(item);
        if (index >= 0 && index < References.Count - 1) MoveReference([index, index + 1]);
    }

    [RelayCommand]
    private void MoveReference(int[]? indices)
    {
        if (indices is not { Length: 2 }) return;
        var from = indices[0];
        var to = indices[1];
        if (from < 0 || from >= References.Count || to < 0 || to >= References.Count || from == to) return;

        // Wczytanie przed zmianą kolejności pozwala zachować spójny stan, jeśli plik zniknął.
        BitmapImage? primary = null;
        if (from == 0 || to == 0)
        {
            var newPrimary = from == 0 ? References[1] : References[from];
            try { primary = LoadImage(File.ReadAllBytes(newPrimary.Path)); }
            catch (Exception ex)
            {
                Status = Loc.T("ref.msg.loadFailed", ex.Message);
                Log.LogError("{Message}", Status);
                return;
            }
        }

        References.Move(from, to);
        if (primary is not null)
        {
            SourceImage = primary;
            ApplyAspectFrom(primary.PixelWidth, primary.PixelHeight);
        }
    }

    [RelayCommand]
    private void ClearReferences()
    {
        References.Clear();
        SourceImage = null;
        Status = Loc.T("ref.msg.cleared");
    }

    // Podgląd i proporcje z pierwszego obrazu (podstawowego)
    private void LoadPrimary()
    {
        var source = LoadImage(File.ReadAllBytes(References[0].Path));
        SourceImage = source;
        ApplyAspectFrom(source.PixelWidth, source.PixelHeight);
    }
}
