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

    public PresetsViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
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
        foreach (var preset in PresetStore.LoadAll()) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.FilePath == selectFilePath) ?? Presets.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void LoadPreset()
    {
        ChosenPreset = SelectedPreset;
        CloseRequested?.Invoke();
    }

    // Nazwa i opis są w pliku; nazwa pliku się nie zmienia
    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void SaveDetails()
    {
        if (SelectedPreset is null) return;

        var name = EditName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "Nazwa zestawu nie może być pusta.";
            return;
        }

        SelectedPreset.Name = name;
        SelectedPreset.Description = EditDescription.Trim();
        PresetStore.Save(SelectedPreset);
        Reload(SelectedPreset.FilePath);
        Status = "Zapisano nazwę i opis.";
    }

    // Jednorazowy import starej historii promptów; powtórne uruchomienie pomija już zaimportowane
    [RelayCommand]
    private void ImportLegacy()
    {
        var result = LegacyPromptImporter.Import();
        if (!result.FileFound)
        {
            Status = "Nie znaleziono starej historii (prompts.json).";
            return;
        }

        Reload(SelectedPreset?.FilePath);
        Status = $"Zaimportowano: {result.Imported}, pominięto (już istnieją): {result.Skipped}.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;

        if (!_dialogs.Confirm($"Usunąć zestaw „{SelectedPreset.Name}”?", "Usuwanie zestawu")) return;

        PresetStore.Delete(SelectedPreset);
        Reload(null);
        Status = "Usunięto zestaw.";
    }
}
