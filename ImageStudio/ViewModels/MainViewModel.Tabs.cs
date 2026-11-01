namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // Widoczność wbudowanych zakładek wg indeksów ActiveTab: Generuj, Edycja, Wideo, Czat, Głos (dodatkowe zawsze widoczne).
    // Przed pierwszym załadowaniem katalogu wszystkie są widoczne; potem znika zakładka, dla której dostawca nie ma żadnego modelu
    public IReadOnlyList<bool> TabVisibility { get; private set; } = [true, true, true, true, true];

    // Po załadowaniu katalogu: zakładka bez modeli (np. Wideo u dostawcy bez modeli wideo) jest ukrywana, a gdy była aktywna,
    // wybór przechodzi na pierwszą widoczną. Gdy katalog nie dał żadnych modeli (brak klucza, błąd), zostają wszystkie,
    // żeby użytkownik miał dokąd wrócić i poprawić ustawienia
    private void UpdateTabVisibility()
    {
        bool[] visible =
        [
            Models.Count > 0,
            EditModels.Count > 0,
            VideoModels.Count > 0,
            ChatModelOptions.Count > 0,
            Voice.SpeechModels.Count > 0 || Voice.TranscriptionModels.Count > 0,
        ];
        if (!visible.Any(v => v)) visible = [true, true, true, true, true];

        TabVisibility = visible;
        if (ActiveTab >= 0 && ActiveTab < visible.Length && !visible[ActiveTab]) ActiveTab = Array.IndexOf(visible, true);
        OnPropertyChanged(nameof(TabVisibility));
    }
}
