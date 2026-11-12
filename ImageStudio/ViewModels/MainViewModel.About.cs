using CommunityToolkit.Mvvm.Input;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    [RelayCommand]
    private void OpenAbout() => _dialogs.ShowAbout();
}
