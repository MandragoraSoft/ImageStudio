using System.Windows;
using System.Windows.Controls;
using ImageStudio.ViewModels;
using Microsoft.Win32;

namespace ImageStudio.Views;

public partial class StorageSettingsTabView : UserControl
{
    public StorageSettingsTabView()
    {
        InitializeComponent();
    }

    // Przycisk jest tylko przy polach folderów (wtyczki bez folderów, np. chmurowe, go nie dostają)
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not StorageField field) return;

        var dialog = new OpenFolderDialog { InitialDirectory = field.Value };
        if (dialog.ShowDialog() == true) field.Value = dialog.FolderName;
    }
}
