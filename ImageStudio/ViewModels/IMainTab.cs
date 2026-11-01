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

public sealed record GenerateTab(MainViewModel Content) : IMainTab
{
    public string Title => "Generowanie";
    public int Order => 0;
    object IMainTab.Content => Content;
}

public sealed record EditTab(MainViewModel Content) : IMainTab
{
    public string Title => "Edycja";
    public int Order => 10;
    object IMainTab.Content => Content;
}

public sealed record VideoTab(MainViewModel Content) : IMainTab
{
    public string Title => "Wideo";
    public int Order => 20;
    object IMainTab.Content => Content;
}

public sealed record ChatTab(ChatViewModel Content) : IMainTab
{
    public string Title => "Czat";
    public int Order => 30;
    object IMainTab.Content => Content;
}
