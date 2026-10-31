using System.Windows;

namespace ImageStudio.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    // Zapis zamyka okno z DialogResult = true; wartości czyta WpfDialogService z ViewModelu
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
