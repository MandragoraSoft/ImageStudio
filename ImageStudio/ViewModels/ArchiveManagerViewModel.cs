using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

// Źródło listy w panelu archiwum: pliki na dysku lokalnym albo już w chmurze
public enum ArchiveSource { Local, Cloud }

// Wpis galerii z zaznaczeniem do przeniesienia
public sealed partial class ArchiveItem(GalleryEntry entry) : ObservableObject
{
    public GalleryEntry Entry { get; } = entry;

    [ObservableProperty]
    private bool _isSelected;
}

// Panel archiwum: wyniki generowania, edycji i wideo, przenoszone między magazynem lokalnym a aktywną chmurą
public sealed partial class ArchiveManagerViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.For<ArchiveManagerViewModel>();
    private static readonly string[] Kinds = ["generate", "edit", "video"];

    private readonly ObservableCollection<GalleryEntry> _gallery;
    private readonly MediaStorageHost _host;
    private readonly Action _save;

    public ArchiveManagerViewModel(ObservableCollection<GalleryEntry> gallery, MediaStorageHost host, Action save)
    {
        _gallery = gallery;
        _host = host;
        _save = save;
        Refresh();
    }

    public ObservableCollection<ArchiveItem> Items { get; } = [];

    [ObservableProperty]
    private ArchiveSource _source = ArchiveSource.Local;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isMoving;

    [ObservableProperty]
    private string _status = "";

    public bool IsLocalSource
    {
        get => Source == ArchiveSource.Local;
        set { if (value) Source = ArchiveSource.Local; }
    }

    public bool IsCloudSource
    {
        get => Source == ArchiveSource.Cloud;
        set { if (value) Source = ArchiveSource.Cloud; }
    }

    public string ActiveStorageName => _host.Active.DisplayName;

    public string MoveToCloudLabel => Loc.T("archive.moveToCloud", ActiveStorageName);

    // Aktywna wtyczka to dysk lokalny: nie ma dokąd przenosić
    private bool HasCloud => _host.Active.Id != "local";

    partial void OnSourceChanged(ArchiveSource value)
    {
        OnPropertyChanged(nameof(IsLocalSource));
        OnPropertyChanged(nameof(IsCloudSource));
        Refresh();
    }

    partial void OnIsMovingChanged(bool value) => MoveToCloudCommand.NotifyCanExecuteChanged();

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mov", ".mkv"];

    private void Refresh()
    {
        Items.Clear();
        var inCloud = Source == ArchiveSource.Cloud;
        var entries = _gallery.Where(e => Kinds.Contains(e.Kind) && e.IsInCloud == inCloud).ToList();
        if (!inCloud)
        {
            // Dysk lokalny odpowiada od razu, więc czekanie na wynik nie blokuje UI; wpisy bez pliku nie są pokazywane
            entries = entries.Where(e => _host.Owner(e.StorageId).ExistsAsync(e.FilePath).GetAwaiter().GetResult()).ToList();
            entries.AddRange(LocalOrphans());
        }
        foreach (var entry in entries)
        {
            var item = new ArchiveItem(entry);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ArchiveItem.IsSelected)) UpdateSelection(); };
            Items.Add(item);
        }
        UpdateSelection();
        Status = Items.Count == 0 ? Loc.T("archive.empty")
               : !HasCloud ? Loc.T("archive.status.noCloud")
               : "";
    }

    // Pliki w folderach obrazów i wideo dysku lokalnego, których nie ma w galerii (bez podfolderów); ścieżka porównywana bez wielkości liter
    private List<GalleryEntry> LocalOrphans()
    {
        var known = _gallery.Select(e => FullPath(e.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<GalleryEntry>();
        var local = _host.Owner("local");
        AddOrphans(found, known, local.LocalFolder(MediaKind.Image), ImageExtensions, "generate");
        AddOrphans(found, known, local.LocalFolder(MediaKind.Video), VideoExtensions, "video");
        return found.OrderByDescending(e => e.CreatedUtc).ToList();
    }

    private static void AddOrphans(List<GalleryEntry> found, HashSet<string> known, string? folder, string[] extensions, string kind)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (!known.Add(FullPath(path))) continue;
                found.Add(new GalleryEntry
                {
                    Kind = kind,
                    StorageId = "local",
                    FilePath = path,
                    CreatedUtc = File.GetLastWriteTimeUtc(path),
                    Thumbnail = GalleryStore.Thumbnail(path),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Archiwum: nie odczytano folderu {Folder}", folder);
        }
    }

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }

    private void UpdateSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        MoveToCloudCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
    }

    private bool CanMoveToCloud() => Source == ArchiveSource.Local && SelectedCount > 0 && HasCloud && !IsMoving;

    // Przenoszenie po kolei: błąd jednego pliku nie przerywa reszty; po każdym udanym wpisie galeria i zapis są aktualne
    [RelayCommand(CanExecute = nameof(CanMoveToCloud))]
    private async Task MoveToCloudAsync()
    {
        var target = _host.Active;
        var selected = Items.Where(i => i.IsSelected).ToList();
        IsMoving = true;
        var moved = 0;
        var failed = 0;
        try
        {
            foreach (var item in selected)
            {
                try
                {
                    await _host.MoveAsync(item.Entry, target);
                    // Plik spoza galerii (sierota z folderu) dostaje wpis tak jak nowy wynik: na początku, z przycięciem do limitu
                    var index = _gallery.IndexOf(item.Entry);
                    if (index >= 0) _gallery[index] = item.Entry;
                    else
                    {
                        _gallery.Insert(0, item.Entry);
                        while (_gallery.Count > GalleryStore.MaxEntries) _gallery.RemoveAt(_gallery.Count - 1);
                    }
                    _save();
                    Items.Remove(item);
                    moved++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.LogWarning(ex, "Archiwum: nie przeniesiono pliku {Path} do {Storage}", item.Entry.FilePath, target.Id);
                }
            }
        }
        finally
        {
            IsMoving = false;
            UpdateSelection();
            Status = Loc.T("archive.status.moved", moved, failed);
        }
    }
}
