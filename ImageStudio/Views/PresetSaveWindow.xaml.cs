using System.Windows;

namespace ImageStudio.Views;

public partial class PresetSaveWindow : Window
{
    public PresetSaveWindow()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
