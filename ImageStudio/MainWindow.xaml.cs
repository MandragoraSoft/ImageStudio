using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageStudio.Localization;
using ImageStudio.ViewModels;

namespace ImageStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    // Zakładki w kolejności logicznej: pozycja to indeks ActiveTab/TabVisibility, niezależny od kolejności wyświetlania,
    // którą użytkownik zmienia przeciąganiem nagłówków (MainTabs.Items)
    private readonly List<TabItem> _tabItems = [];
    private bool _syncing;
    private TabItem? _dragItem;
    private Point _dragStart;

    // ViewModel i zakładki (IMainTab, w tym wstrzyknięte) przychodzą z kontenera DI
    public MainWindow(MainViewModel viewModel, IEnumerable<IMainTab> tabs)
    {
        _viewModel = viewModel;
        InitializeComponent();
        BuildTabs(tabs.OrderBy(t => t.Order).ToList());
        ApplySavedOrder(viewModel.SavedTabOrder);
        DataContext = viewModel;
        SelectActiveTab();
        ApplyTabVisibility(viewModel.TabVisibility);
        EnableTabReordering();

        // Zakładki bez modeli są ukrywane po załadowaniu katalogu dostawcy; ActiveTab zmienia się też spoza okna
        void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.TabVisibility)) ApplyTabVisibility(viewModel.TabVisibility);
            else if (e.PropertyName == nameof(MainViewModel.ActiveTab)) SelectActiveTab();
        }

        viewModel.PropertyChanged += OnViewModelChanged;

        // Tytuły zakładek są zwykłym tekstem (IMainTab.Title), więc po zmianie języka odświeżamy je ręcznie
        Loc.LanguageChanged += RefreshTabTitles;
        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= RefreshTabTitles;
            viewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    // Zdarzenie jest statyczne i może przyjść z dowolnego wątku, a nagłówki należą do wątku okna
    private void RefreshTabTitles()
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in _tabItems)
            {
                if (item.Tag is IMainTab tab) item.Header = tab.Title;
                item.ToolTip = Loc.T("tab.reorder.tip");
            }
        });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // Wybór zakładki z interfejsu: ActiveTab to indeks logiczny. SelectionChanged bąbelkuje też z kontrolek w treści zakładek
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != MainTabs || _syncing || MainTabs.SelectedItem is not TabItem item) return;

        var index = _tabItems.IndexOf(item);
        if (index >= 0 && _viewModel.ActiveTab != index) _viewModel.ActiveTab = index;
    }

    private void SelectActiveTab()
    {
        if (_tabItems.Count == 0) return;

        _syncing = true;
        try
        {
            MainTabs.SelectedItem = _tabItems[Math.Clamp(_viewModel.ActiveTab, 0, _tabItems.Count - 1)];
        }
        finally
        {
            _syncing = false;
        }
    }

    // Indeks zakładki = indeks w TabVisibility; zakładki dodatkowe (za wbudowanymi) są zawsze widoczne
    private void ApplyTabVisibility(IReadOnlyList<bool> visible)
    {
        for (var i = 0; i < _tabItems.Count; i++)
            _tabItems[i].Visibility = i < visible.Count && !visible[i] ? Visibility.Collapsed : Visibility.Visible;
    }

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
            var item = new TabItem
            {
                Header = tab.Title, Tag = tab, ToolTip = Loc.T("tab.reorder.tip"),
                Content = new ContentPresenter { Content = tab },
            };
            _tabItems.Add(item);
            MainTabs.Items.Add(item);
        }
    }

    // Klucz zakładki w zapisie kolejności: nazwa typu (stabilna, niezależna od języka tytułu)
    private static string KeyOf(TabItem item) => item.Tag?.GetType().Name ?? "";

    // Zapisana kolejność; zakładki, których w niej nie ma (nowe), wchodzą na koniec w kolejności wbudowanej
    private void ApplySavedOrder(IReadOnlyList<string>? saved)
    {
        if (saved is not { Count: > 0 }) return;

        var ordered = saved.Select(name => _tabItems.FirstOrDefault(t => KeyOf(t) == name)).OfType<TabItem>().Distinct()
            .Concat(_tabItems).Distinct().ToList();
        MainTabs.Items.Clear();
        foreach (var item in ordered) MainTabs.Items.Add(item);
    }

    // Przeciąganie nagłówków: ruch myszy z wciśniętym lewym przyciskiem zaczyna przeciąganie, upuszczenie na innym nagłówku
    // przesuwa zakładkę na jego miejsce
    private void EnableTabReordering()
    {
        MainTabs.AllowDrop = true;
        MainTabs.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragItem = TabItemAt(e.OriginalSource);
            _dragStart = e.GetPosition(null);
        };
        MainTabs.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;

            var position = e.GetPosition(null);
            if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop(item, new DataObject(typeof(TabItem), item), DragDropEffects.Move);
        };
        MainTabs.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(TabItem)) && TabItemAt(e.OriginalSource) is not null
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        };
        MainTabs.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(TabItem)) is TabItem dragged && TabItemAt(e.OriginalSource) is { } target) MoveTab(dragged, target);
            e.Handled = true;
        };
        MainTabs.PreviewMouseLeftButtonUp += (_, _) => _dragItem = null;
    }

    // Nagłówek zakładki pod kursorem (null, gdy kursor jest nad treścią zakładki albo pustym miejscem paska)
    private static TabItem? TabItemAt(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null and not TabItem)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as TabItem;
    }

    // Przestawia zakładkę na miejsce docelowej, zachowując wybraną; kolejność trafia do zapisu użytkownika
    internal void MoveTab(TabItem dragged, TabItem target)
    {
        if (dragged == target || !MainTabs.Items.Contains(dragged) || !MainTabs.Items.Contains(target)) return;

        var selected = MainTabs.SelectedItem;
        _syncing = true;
        try
        {
            var index = MainTabs.Items.IndexOf(target);
            MainTabs.Items.Remove(dragged);
            MainTabs.Items.Insert(index, dragged);
            MainTabs.SelectedItem = selected;
        }
        finally
        {
            _syncing = false;
        }

        _viewModel.SaveTabOrder(MainTabs.Items.OfType<TabItem>().Select(KeyOf).ToList());
    }
}
