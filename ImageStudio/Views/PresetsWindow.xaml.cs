using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class PresetsWindow : Window
{
    public PresetsWindow()
    {
        InitializeComponent();
    }

    // Dwuklik na wierszu wczytuje zestaw, tak samo jak przycisk "Wczytaj"
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PresetsViewModel vm && vm.LoadPresetCommand.CanExecute(null))
            vm.LoadPresetCommand.Execute(null);
    }
}
