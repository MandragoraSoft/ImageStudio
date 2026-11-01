using System.Windows;
using System.Windows.Controls;
using ImageStudio.ViewModels;

namespace ImageStudio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel viewModel) BuildTabs(viewModel);
        };
    }

    // Widoki zakładek powstają raz (stan paneli, np. rozwinięte akordeony, przeżywa przełączanie zakładek).
    // Zakładka bez DataTemplate to błąd programisty: wyjątek już przy tworzeniu okna
    private void BuildTabs(MainViewModel viewModel)
    {
        MainTabs.Items.Clear();
        foreach (var tab in viewModel.Tabs)
        {
            if (TryFindResource(new DataTemplateKey(tab.GetType())) is null)
                throw new InvalidOperationException(
                    $"Zakładka {tab.GetType().Name} nie ma widoku: dodaj DataTemplate dla tego typu w MainWindow.xaml.");

            // ContentPresenter z zakładką jako treścią sam wybiera DataTemplate po typie i trzyma jedną instancję widoku
            MainTabs.Items.Add(new TabItem { Header = tab.Title, Content = new ContentPresenter { Content = tab } });
        }

        MainTabs.SelectedIndex = Math.Clamp(viewModel.ActiveTab, 0, MainTabs.Items.Count - 1);
    }
}
