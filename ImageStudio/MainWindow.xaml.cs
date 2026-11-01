using System.Windows;
using System.Windows.Controls;
using ImageStudio.ViewModels;

namespace ImageStudio;

public partial class MainWindow : Window
{
    // ViewModel i zakładki (IMainTab, w tym wstrzyknięte) przychodzą z kontenera DI
    public MainWindow(MainViewModel viewModel, IEnumerable<IMainTab> tabs)
    {
        InitializeComponent();
        BuildTabs(tabs.OrderBy(t => t.Order).ToList());
        DataContext = viewModel;
        MainTabs.SelectedIndex = Math.Clamp(viewModel.ActiveTab, 0, MainTabs.Items.Count - 1);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // Widoki zakładek powstają raz (stan paneli, np. rozwinięte akordeony, przeżywa przełączanie zakładek).
    // Zakładka bez DataTemplate to błąd programisty: wyjątek już przy tworzeniu okna
    private void BuildTabs(IReadOnlyList<IMainTab> tabs)
    {
        foreach (var tab in tabs)
        {
            if (TryFindResource(new DataTemplateKey(tab.GetType())) is null)
                throw new InvalidOperationException(
                    $"Zakładka {tab.GetType().Name} nie ma widoku: dodaj DataTemplate dla tego typu w MainWindow.xaml.");

            // ContentPresenter z zakładką jako treścią sam wybiera DataTemplate po typie i trzyma jedną instancję widoku
            MainTabs.Items.Add(new TabItem { Header = tab.Title, Content = new ContentPresenter { Content = tab } });
        }
    }
}
