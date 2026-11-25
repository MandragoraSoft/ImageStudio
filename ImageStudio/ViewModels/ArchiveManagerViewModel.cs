using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Localization;
using ImageStudio.Services;
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

    private void Refresh()
    {
        Items.Clear();
        var inCloud = Source == ArchiveSource.Cloud;
        foreach (var entry in _gallery.Where(e => Kinds.Contains(e.Kind) && e.IsInCloud == inCloud))
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
                    var index = _gallery.IndexOf(item.Entry);
                    if (index >= 0) _gallery[index] = item.Entry;
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
