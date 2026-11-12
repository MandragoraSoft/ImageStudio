using System.IO;
using System.Windows.Controls;
using ImageStudio.ViewModels;
using Microsoft.Win32;

namespace ImageStudio.Views;

public partial class AppSettingsTabView : UserControl
{
    public AppSettingsTabView()
    {
        InitializeComponent();
    }

    private void BrowseArchive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not AppSettingsTab settings) return;

        var initialDirectory = Directory.Exists(settings.GalleryArchiveFolder)
            ? settings.GalleryArchiveFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        if (dialog.ShowDialog() == true) settings.GalleryArchiveFolder = dialog.FolderName;
    }
}
