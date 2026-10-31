using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.ViewModels;

public sealed partial class PresetSaveViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description = "";

    public PresetSaveViewModel(string defaultName)
    {
        _name = defaultName;
    }
}
