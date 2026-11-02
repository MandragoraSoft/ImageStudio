using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Localization;
using ImageStudio.Services;
using ImageStudio.Storage;

namespace ImageStudio.ViewModels;

public sealed partial class StorageField(StorageSetting setting) : ObservableObject
{
    public string Key { get; } = setting.Key;
    public string Label { get; } = Loc.T(setting.LabelKey);
    public bool IsFolder { get; } = setting.IsFolder;

    [ObservableProperty]
    private string _value = setting.Value;
}

// Wtyczka magazynu z polami do edycji (pozycja listy wyboru)
public sealed class StorageOption(IMediaStoragePlugin plugin)
{
    public string Id { get; } = plugin.Id;
    public string Name { get; } = plugin.DisplayName;
    public IReadOnlyList<StorageField> Fields { get; } = plugin.Settings.Select(s => new StorageField(s)).ToList();
}

// Zakładka "Zapis": wtyczka magazynu wyników (lista jak przy dostawcy AI) i jej ustawienia, np. foldery zapisu plików
public sealed partial class StorageSettingsTab : ObservableObject, ISettingsTab
{
    public string Title => Loc.T("settings.tab.storage");
    public int Order => 7;

    public IReadOnlyList<StorageOption> Options { get; }

    [ObservableProperty]
    private StorageOption? _selected;

    public StorageSettingsTab(SettingsResult current, MediaStorageHost host)
    {
        Options = host.Plugins.Select(p => new StorageOption(p)).ToList();
        _selected = Options.FirstOrDefault(o => o.Id == (current.StorageId ?? host.Active.Id)) ?? Options[0];
    }

    public SettingsResult Apply(SettingsResult result) => result with
    {
        StorageId = Selected?.Id ?? result.StorageId,
        StorageConfig = Options.ToDictionary(o => o.Id, o => o.Fields.ToDictionary(f => f.Key, f => f.Value)),
    };
}
