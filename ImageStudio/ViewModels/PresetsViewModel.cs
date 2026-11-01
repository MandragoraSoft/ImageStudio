using ImageStudio.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

// Okno zarządzania zestawami: lista, podgląd, zmiana nazwy/opisu, usuwanie i wczytanie
public sealed partial class PresetsViewModel : ObservableObject
{
    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPresetCommand), nameof(DeletePresetCommand), nameof(SaveDetailsCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Preset? _selectedPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDetailsCommand))]
    private string _editDescription = "";

    [ObservableProperty]
    private string _status = "";

    // Zestaw wybrany do wczytania; odczytywany przez WpfDialogService po zamknięciu okna
    public Preset? ChosenPreset { get; private set; }

    // Ustawiane przez okno, żeby ViewModel mógł je zamknąć
    public event Action? CloseRequested;

    private readonly IDialogService _dialogs;
    private readonly IPresetStore _presets;
    private readonly LegacyPromptImporter _importer;

    public PresetsViewModel(IDialogService dialogs, IPresetStore presets, LegacyPromptImporter importer)
    {
        _dialogs = dialogs;
        _presets = presets;
        _importer = importer;
        Reload(null);
    }

    public bool HasSelection => SelectedPreset is not null;

    partial void OnSelectedPresetChanged(Preset? value)
    {
        EditName = value?.Name ?? "";
        EditDescription = value?.Description ?? "";
    }

    private bool HasSelectedPreset => SelectedPreset is not null;

    private void Reload(string? selectFilePath)
    {
        Presets.Clear();
        foreach (var preset in _presets.LoadAll()) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.FilePath == selectFilePath) ?? Presets.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void LoadPreset()
    {
        ChosenPreset = SelectedPreset;
        CloseRequested?.Invoke();
    }

    // Zapis aktywny tylko po zmianie nazwy lub opisu zaznaczonego zestawu
    private bool DetailsChanged => SelectedPreset is { } p
                                   && (EditName.Trim() != p.Name || EditDescription.Trim() != (p.Description ?? ""));

    // Nazwa i opis są w pliku; nazwa pliku się nie zmienia
    [RelayCommand(CanExecute = nameof(DetailsChanged))]
    private void SaveDetails()
    {
        if (SelectedPreset is null) return;

        var name = EditName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = Loc.T("presets.msg.emptyName");
            return;
        }

        SelectedPreset.Name = name;
        SelectedPreset.Description = EditDescription.Trim();
        _presets.Save(SelectedPreset);
        Reload(SelectedPreset.FilePath);
        Status = Loc.T("presets.msg.metaSaved");
    }

    // Jednorazowy import starej historii promptów; powtórne uruchomienie pomija już zaimportowane
    [RelayCommand]
    private void ImportLegacy()
    {
        var result = _importer.Import();
        if (!result.FileFound)
        {
            Status = Loc.T("presets.msg.noLegacy");
            return;
        }

        Reload(SelectedPreset?.FilePath);
        Status = Loc.T("presets.msg.imported", result.Imported, result.Skipped);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;

        if (!_dialogs.Confirm(Loc.T("presets.confirmDelete", SelectedPreset.Name), Loc.T("presets.confirmDelete.title"))) return;

        _presets.Delete(SelectedPreset);
        Reload(null);
        Status = Loc.T("presets.msg.deleted");
    }
}
