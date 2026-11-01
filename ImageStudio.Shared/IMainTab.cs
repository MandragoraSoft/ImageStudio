namespace ImageStudio.ViewModels;

// Kontrakt zakładki lewego panelu okna głównego. Szkielet (MainWindow) zna tylko ten interfejs: zakładki są wstrzykiwane
// do MainViewModel, każda podaje tytuł, kolejność i Content (ViewModel, na którym pracuje jej widok). Widok dobiera okno
// po typie zakładki (DataTemplate w MainWindow.xaml); zakładka bez widoku kończy się wyjątkiem przy tworzeniu okna.
// Wbudowane zakładki zajmują indeksy 0-3 (ActiveTab), dodatkowe wstrzyknięte dochodzą za nimi (większy Order).
public interface IMainTab
{
    string Title { get; }
    int Order { get; }

    // DataContext widoku zakładki
    object Content { get; }
}
