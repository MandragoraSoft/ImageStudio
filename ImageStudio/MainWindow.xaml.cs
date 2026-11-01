using System.Windows;
using System.Windows.Controls;
using ImageStudio.Localization;
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

        // Zakładki bez modeli są ukrywane po załadowaniu katalogu dostawcy
        void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.TabVisibility)) ApplyTabVisibility(viewModel.TabVisibility);
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        ApplyTabVisibility(viewModel.TabVisibility);
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;

        // Tytuły zakładek są zwykłym tekstem (IMainTab.Title), więc po zmianie języka odświeżamy je ręcznie
        Loc.LanguageChanged += RefreshTabTitles;
        Closed += (_, _) => Loc.LanguageChanged -= RefreshTabTitles;
    }

    // Zdarzenie jest statyczne i może przyjść z dowolnego wątku, a nagłówki należą do wątku okna
    private void RefreshTabTitles()
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(() =>
        {
            foreach (TabItem item in MainTabs.Items)
                if (item.Tag is IMainTab tab) item.Header = tab.Title;
        });
    }

    // Indeks zakładki = indeks w TabVisibility; zakładki dodatkowe (za wbudowanymi) są zawsze widoczne
    private void ApplyTabVisibility(IReadOnlyList<bool> visible)
    {
        for (var i = 0; i < MainTabs.Items.Count; i++)
            if (MainTabs.Items[i] is TabItem item) item.Visibility = i < visible.Count && !visible[i] ? Visibility.Collapsed : Visibility.Visible;
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
            MainTabs.Items.Add(new TabItem { Header = tab.Title, Tag = tab, Content = new ContentPresenter { Content = tab } });
        }
    }
}
