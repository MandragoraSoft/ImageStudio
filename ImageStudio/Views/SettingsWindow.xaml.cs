using System.Windows;
using System.ComponentModel;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private bool CleanupInProgress => DataContext is SettingsViewModel vm &&
        vm.Tabs.OfType<AppSettingsTab>().Any(tab => tab.ClearSelectedDataCommand.IsRunning);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (CleanupInProgress) e.Cancel = true;
    }

    // Wymuszenie kontraktu: każda wstrzyknięta zakładka musi mieć widok (DataTemplate po typie ViewModelu).
    // Wywoływane przed ShowDialog, żeby brak widoku był zwykłym wyjątkiem, a nie awarią pętli komunikatów
    public void EnsureTabViews()
    {
        if (DataContext is not SettingsViewModel viewModel) return;

        foreach (var tab in viewModel.Tabs.Where(t => TryFindResource(new DataTemplateKey(t.GetType())) is null))
            throw new InvalidOperationException(
                $"Zakładka {tab.GetType().Name} nie ma widoku: dodaj DataTemplate dla tego typu w SettingsWindow.xaml.");
    }

    // Zapis zamyka okno z DialogResult = true; wartości czyta WpfDialogService z ViewModelu
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CleanupInProgress) return;
        DialogResult = true;
    }
}
