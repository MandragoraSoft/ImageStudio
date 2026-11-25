using CommunityToolkit.Mvvm.Input;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // Zapis galerii po każdym przeniesionym pliku (stan wpisów trzyma GalleryStore)
    [RelayCommand]
    private void OpenArchiveManager() =>
        _dialogs.ShowArchiveManager(new ArchiveManagerViewModel(Gallery, _storage, () => _galleryStore.Save(Gallery)));
}
