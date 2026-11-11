---
name: new-main-tab
description: Dodawanie nowej zakładki lewego panelu okna głównego ImageStudio (IMainTab, projekt ImageStudio.Tabs.*) albo nowej zakładki okna Ustawień (ISettingsTab). Użyj przy każdej prośbie o nową zakładkę, panel w lewej kolumnie lub nową sekcję Ustawień; zawiera listę kontrolną, której pominięcie kończyło się brakami (np. brak filtrów modeli w zakładce Głos).
---

# Nowa zakładka okna głównego

Zanim zaczniesz, przeczytaj `docs/dev/tabs-and-settings.md` (jak działają zakładki) i `docs/dev/ui-shell.md` (selektor modeli, filtry, kolory). Wzorem jest najmłodsza zakładka: `ImageStudio.Tabs.Voice` (z własnym panelem wyników `ImageStudio/Views/VoiceResults.xaml`) albo `ImageStudio.Tabs.Video` (z panelem wyniku wspólnym).

Przejdź całą listę, nie tylko to, o co poproszono. Zakładka Głos pominęła filtry modeli i musiała je dostać po fakcie.

## Lista kontrolna

1. **Projekt i rejestracja.**
   - Projekt `ImageStudio.Tabs.<Nazwa>` (csproj jak w `ImageStudio.Tabs.Voice`: referencja do `ImageStudio.Shared`), rekord `<Nazwa>Tab : IMainTab` z `Title`, `Order` (rosnąco: 0, 10, 20, 30, 40...) i `Content` (DataContext widoku).
   - Referencja w `ImageStudio/ImageStudio.csproj` z `Private="$(PluginsInBundle)"` (nie `false`: publikacja do jednego pliku wkłada zakładki do exe).
   - Referencja w `ImageStudio.Tests/ImageStudio.Tests.csproj` i wpis w `ImageStudio.slnx`.
   - Rejestracja `IMainTab` w `Composition/ServiceCollectionExtensions` i `DataTemplate` w `ImageStudio/MainWindow.xaml` (z `xmlns` zawierającym `;assembly=ImageStudio.Tabs.<Nazwa>`). Brak szablonu = wyjątek przy tworzeniu okna.
2. **Każdy selektor modeli = `ModelPicker` z `HasFilters="True"`.**
   - Ustaw `FiltersContext` na filtr tego selektora (`DataContext.<Nazwa>Filter` okna, gdy panel nie ma `MainViewModel` jako DataContext) oraz `ReloadCommand`.
   - `ItemsSource` musi być `ICollectionView` z własnym `ModelFilter` w `MainViewModel` (`BindFilter(imageFilter, widoki)`; `imageFilter: false` dla modeli bez wejścia obrazowego, wtedy też `ShowImageFilter="False"`), podanym w `FiltersContext`.
   - Widok odświeżaj w `MainViewModel.RefreshFilters`, a modele uwzględnij w `RefreshOwners` (lista dostawców) i w `OpenModelBrowser` (+ rodzaj w `ModelBrowserViewModel.Kinds`).
   - Pilnuje tego test `Every_model_picker_in_the_main_window_tabs_has_filters`: nie omijaj go.
2a. **Układ panelu jak w pozostałych zakładkach:** Model (`ModelPicker`, nagłówek `common.model`, pierwszy), Prompt, Wejście, Parametry, Rozmiar, Akcja (przycisk na końcu). Test: `TabLayoutTests`.
3. **Teksty tylko przez tłumaczenia.** Tytuł zakładki (`Title => Loc.T("tab.<nazwa>")`), etykiety, dymki i komunikaty ViewModelu: klucze w obu słownikach (`Strings.pl.json`, `Strings.en.json`) i `{loc:T}` / `Loc.T`. Zasady w `docs/dev/localization.md`; test `LocalizationTests` złapie brakujący klucz, nieużywany klucz i różne parametry w PL i EN.
4. **`ComboBox` z obiektami**: `ItemTemplate`, nigdy `DisplayMemberPath` (własny szablon ComboBox w `App.xaml` go ignoruje i pokazuje zapis rekordu).
5. **Panel wyników w prawej kolumnie.** Zakładka z własnym panelem (jak Czat i Głos): dodaj flagę `IsXTab` w `MainViewModel.Generation` i wyklucz ją z `IsOutputTab` (obraz i metryczka modelu są tylko dla zakładek 0-2). Panel dodaj w `MainWindow.xaml` obok `VoiceResults`.
6. **Indeksy i stan.**
   - Indeks zakładki w `TabNames` (`MainViewModel.AppControl`) i w `AppControlTools.Tabs` (+ schemat narzędzia `switch_tab`).
   - Wybory użytkownika w `UserState` (`Configuration/UserState.cs`), zapis w `MainViewModel.SaveState` przez `PropertyChanged` ViewModelu zakładki (wzór: `Voice.PropertyChanged` w konstruktorze `MainViewModel`).
7. **Sieć i sprzęt.**
   - Każde wywołanie sieciowe idące poza `IAiProvider` woła `Activity.Begin("...")` w `using`.
   - Sprzęt i zewnętrzne usługi tylko przez interfejs (jak `IAudioRecorder`/`IAudioPlayer`), z atrapą w `ImageStudio.Tests/TestApp.cs` i `Fakes.cs`.
8. **Testy** (w `ImageStudio.Tests`): ViewModel na `TestApp`, widok w prawdziwym oknie (`WpfHost.Run`), a istniejące testy liczby i tytułów zakładek (`MainTabsTests`, `CompositionTests`) popraw na nową liczbę. Widoki modeli (`ICollectionView`) zmieniaj w testach tylko z wątku, który je utworzył (bez `Task.Yield` przed zmianą kolekcji).
9. **Dokumentacja.** Dopisz zakładkę do `docs/dev/tabs-and-settings.md` (zakres indeksów) i, jeśli ma własne zasady, osobny plik w `docs/dev/`.

## Weryfikacja

```powershell
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
```

Gdy aplikacja jest uruchomiona, pliki są zablokowane: zbuduj do osobnego katalogu (`-p:BaseOutputPath=<katalog>`), nie zabijaj procesu użytkownika.

# Nowa zakładka okna Ustawień

1. ViewModel implementujący `ISettingsTab` (`Title`, `Order`, `Apply(SettingsResult)`), wzór: `ImageStudio/ViewModels/AudioSettingsTab.cs`.
2. Widok (`UserControl` w `ImageStudio/Views/`) i `DataTemplate` w `ImageStudio/Views/SettingsWindow.xaml`. Brak szablonu wykrywa `SettingsWindow.EnsureTabViews` (wyjątek przed otwarciem okna).
3. Nowe wartości w `SettingsResult` (opcjonalne parametry na końcu rekordu) i ich odczyt w `MainViewModel.OpenSettingsAsync` (lista zakładek podawana do `IDialogService.ShowSettings`).
4. Testy: `SettingsTabsTests` (wzór: `AudioSettingsTabTests`) i test okna z widokiem zakładki.
5. Listy z obiektami: `ItemTemplate`, nie `DisplayMemberPath` (jak wyżej).
6. Tytuł zakładki i wszystkie teksty widoku przez klucze tłumaczeń (`settings.tab.<nazwa>`, `{loc:T}`); tytuły są czytane przy otwarciu okna, więc nowy język zobaczysz po ponownym otwarciu.
