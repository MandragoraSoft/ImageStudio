namespace ImageStudio.ViewModels;

// Kontrakt zakładki okna ustawień. Zakładki są wstrzykiwane do SettingsViewModel (okno nie zna ich typów): każda podaje
// tytuł i kolejność, a przy zapisie nakłada swoje wartości na wynik. Widok zakładki okno dobiera po typie ViewModelu
// (DataTemplate w SettingsWindow.xaml); zakładka bez widoku kończy się wyjątkiem przy otwarciu okna.
public interface ISettingsTab
{
    // Napis na nagłówku zakładki
    string Title { get; }

    // Kolejność zakładek (rosnąco)
    int Order { get; }

    // Zwraca wynik z wartościami tej zakładki; wejściowy wynik niesie wartości pozostałych zakładek
    SettingsResult Apply(SettingsResult result);
}
