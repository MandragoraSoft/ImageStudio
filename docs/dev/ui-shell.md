# Szkielet okna, motywy, selektor modeli i filtry

_Kiedy czytać: dotykasz menu, stopki, kolorów, `ModelPicker`, filtrów modeli, cen w listach albo zapamiętywania rozmiarów okien._

## Szkielet okna głównego

Klasyczne menu u góry (te same polecenia co przyciski) i stopka (`StatusBar`): status, aktywny dostawca oraz spinner z opisem trwającej operacji. Stopkę zasila `IActivityTracker` (`MainViewModel.Activity`). Każde wywołanie API zgłasza się samo, bo `ProviderFactory` opakowuje każdego dostawcę w `ActivityAiProvider` (dekorator); wywołania spoza dostawcy (pobranie obrazu z linku, narzędzia MCP w czacie) zgłaszają się przez `Activity.Begin("...")` w `using`. W czasie operacji okno odpowiedzi (wynik / rozmowa) ma `Opacity` 0.45. Nowe wywołania sieciowe idące poza `IAiProvider` muszą same wołać `Begin`.

Pozycja Pomoc otwiera modalne okno O aplikacji przez `OpenAboutCommand` i `IDialogService`. Teksty okna są powiązane z lokalizacją, a numer wersji pochodzi z `AssemblyInformationalVersion` aplikacji (zachowuje także prerelease i metadane kompilacji). Domyślna wersja projektów jest w `Directory.Build.props` jako `Version`; wydanie nadpisuje ją przez `-p:Version=...`.

## Kolory kontrolek

Paleta (`ThemeService`, klucze w `App.xaml`) ma osobne `ControlBrush`/`ControlHoverBrush`/`ControlBorderBrush` dla przycisków drugorzędnych, przełączników, pól tekstowych, list rozwijanych i nagłówka selektora modelu: wypełnienie odróżnia je od panelu, a obrys ma kontrast co najmniej 3:1 w obu motywach (pilnuje tego `ThemeContrastTests`). `SurfaceAltBrush` jest za bliski `SurfaceBrush` (1,08:1), żeby służyć za tło kontrolki. Nowy kolor dodawaj do obu palet.

## Motywy i style

(własny szablon ComboBox, wspólne `DataTemplate` modeli) są w `App.xaml`.

## Selektor modeli

Jeden komponent `Views/ModelPicker` (akordeon + lista + opcjonalny panel filtrów `ModelFilterBar`) używany w panelach Generowanie, Edycja, Wideo, Czat i Ulepszanie oraz w oknie Ustawień (domyślny model tekstowy dostawcy); przyjmuje `ModelOption` albo zwykłe nazwy. Filtry (`HasFilters`) to osobny `ModelFilter` na selektor (`FiltersContext`, domyślnie DataContext rodzica).

## Filtry modeli w każdym selektorze

Każdy selektor ma **własny** `ModelFilter` (szukaj, dostawca, "Przyjmuje obraz", NSFW, preferowane) w `MainViewModel`: `GenerateFilter`, `EditFilter`, `VideoFilter`, `ChatFilter`, `EnhancerFilter`, `SpeechFilter`, `TranscriptionFilter`; zakładki nie wpływają na siebie. Selektor dostaje go przez `ModelPicker.FiltersContext`, a `MainViewModel.BindFilter` podpina go jako `Filter` widoku. Selektor ulepszania ma własny widok modeli czatu (`ChatModelsView`), bo widok domyślny należy do czatu. Filtry dźwięku to `ModelFilter(imageFilter: false)`, bez "Przyjmuje obraz" (ukryłby wszystkie modele dźwięku; `ModelPicker.ShowImageFilter="False"`). "Tylko preferowane" startuje z ustawienia dostawcy, a przełączenie zapisuje się jako domyślne. `ModelPicker` zapisuje do `SelectedItem` tylko wybór użytkownika: gdy filtr ukryje wybrany model, lista traci zaznaczenie, ale wybór i nagłówek zostają. Przełącznik filtrów ma styl `FilterToggleButton`, a "Odśwież" `SecondaryButton`. Filtr NSFW to `ModelFilter.Nsfw` (`TriState`: `Only` / `Any` / `Exclude`, domyślnie `Any`), a w pasku filtrów i w przeglądarce modeli jest kontrolką `Views/TriStateToggle` (`ImageStudio.Shared`). Kontrolka nie zna NSFW: etykietę i teksty segmentów dostaje przez właściwości (`Label`, `OnlyText`/`AnyText`/`ExcludeText`, `*Tip`), więc ten sam widget nadaje się do innego filtra trójstanowego. Segmenty to `RadioButton`, więc zawsze dokładnie jeden jest zaznaczony.

## Modele czatu jak modele obrazów

`ChatModelInfo` niesie `OwnedBy`/`InputModalities`/`Description`, a `MainViewModel.ChatModelOptions` to te same modele jako `ModelOption` (ikony: obraz, rozumowanie, ...). Selektory czatu (`ChatViewModel.ModelOptionsView` / `SelectedModelOption`) i ulepszania (`ChatModelsView` / `SelectedEnhancerModelOption`) używają `ModelPicker` z `HasFilters`; filtry (`ChatFilter`, `EnhancerFilter`) leżą w `MainViewModel`, a panel czatu (DataContext = `ChatViewModel`) podaje je przez `ModelPicker.FiltersContext`. Wybór jako `string` (`Chat.SelectedModel`, `SelectedEnhancerModel`) zostaje źródłem prawdy; opcje go tylko odzwierciedlają (null z selektora po ukryciu przez filtr jest ignorowany).

Ceny modeli tekstowych to `ModelPricing` w tokenach (`Unit = TokenUnit`, `PriceNow` = wejście za 1M, `OutputPrice` = wyjście, `CachePrice` = odczyt z cache); UberLLM czyta je z `price_now` na tym samym rynku co media, OpenRouter z `pricing.prompt/completion` katalogu. Pozycja modelu w listach (`ModelItemTemplate` w `App.xaml`) ma dymek po najechaniu: nazwa, opis i koszty z `ModelOption.CostText` (cena bieżąca, katalogowa, rabat, dane rynku albo informacja o braku ceny).

## Zapamiętywanie rozmiarów

To zachowanie podpinane (`Views/WindowMemory`, dane w `windows.json` przez `WindowStateStore`): `WindowMemory.Remember` jest włączone globalnie w stylu `WindowBase` (każde okno ze stylem zapamiętuje rozmiar przy zamknięciu; okno główne też położenie i maksymalizację; okna `NoResize`/`SizeToContent` pomijają rozmiar), a `WindowMemory.ColumnKey` na `ColumnDefinition` zapamiętuje szerokość kolumny po przeciągnięciu `GridSplittera` (lewy panel głównego okna). Nowe okno musi używać `Style="{StaticResource WindowBase}"`.
