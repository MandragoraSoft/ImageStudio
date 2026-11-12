# Przebudowa promptu przez AI i zestawy (presety)

_Kiedy czytać: dotykasz edytora promptu, przebudowy promptu, opisu obrazu przez AI albo zestawów._

## Obrazy wejściowe edycji

Panel edycji pokazuje wszystkie `MainViewModel.References` jako przewijane miniatury. Kolejność zmienia przeciąganie kafelków lub przyciski strzałek; pierwszy obraz jest podstawowy. Zmiana pierwszej pozycji aktualizuje `SourceImage` i proporcje wyjścia, a wysyłka oraz opis obrazów używają kolejności kolekcji. Miniatury są wczytywane ze ścieżek referencji z ograniczeniem rozmiaru dekodowania i bez blokowania plików. Pliki, linki, schowek i App Control korzystają z tej samej listy.

## Przebudowa promptu przez AI

(`IAiProvider.EnhancePromptAsync` -> `OpenAiCompat.EnhanceAsync`): prompt ma dwie osobne części, `system` (konfiguracja generowania: jak wykonać) i `concept` (oczekiwany wynik: co pokazać). Model odpowiada JSON-em o stałej strukturze `{system, concept, changes}`; OpenAI i OpenRouter dostają `response_format` (z ponowieniem bez niego, gdy endpoint go odrzuci), UberLLM nie, bo API odrzuca nieznane klucze. Poprzednie przebudowy (`PromptRevision`, ostatnie 6) są dołączane jako kontekst rozmowy; historia żyje w `PromptEdit.History` i w `MainViewModel` osobno dla zakładek Generowanie i Edycja.

## Zestawy (presety)

To pliki JSON w katalogu użytkownika (`PresetStore`, rodzaj `generate` albo `edit`). Edytor promptu (`PromptEditorWindow`) dostaje listę zestawów i delegat zapisu jako parametry (nie zna `PresetStore`). Okno zapisu zestawu proponuje nazwę i opis przez AI (`PresetSaveViewModel`). Edytor promptu ma też "Opisz obraz (AI)" (delegat `describeImages` z `IDialogService.ShowPromptEditor`; `MainViewModel.DescribeImagesAsync` wysyła obrazy zakładki albo wskazane pliki do modelu tekstowego z obsługą obrazów).

Układ edytora: dwie kolumny. Lewa to konfiguracja (`SystemSection`, gdy jest) i koncept (`PromptBox`, wypełnia resztę). Prawa (`SidebarHost`, 360 px) to „Opisz obraz” i sekcja przebudowy (`RebuildSection`); pole instrukcji (`InstructionBox`, min. 160) wypełnia wolną wysokość tej kolumny, bo jej korzeń `Grid` ma `MinHeight` równe `ViewportHeight` przewijania. Gdy obu nie ma, prawa kolumna się zwija. Jeden pasek statusu (`StatusText`) pokazuje wszystko przez `SetStatus(tekst, warning)`; błędy idą kolorem `WarningBrush`, a `MessageBox` zostaje tylko przy potwierdzeniach. Przycisk „Nadpisz” jest aktywny tylko przy zaznaczonym zestawie.

Okno zestawów (`PresetsViewModel`, `PresetsWindow`) edytuje wszystkie pola zestawu (nazwa, opis, model, rozmiar, seed, jakość, prompty, parametry ulepszania); rodzaj i model ulepszania są tylko do odczytu. Zapis waliduje nazwę (niepusta, unikalna w obrębie rodzaju bez względu na wielkość liter) i rozmiar (> 0). Przy zmianie modelu zapisywany jest `Preset.Provider` (id aktywnego dostawcy, z `PresetsContext`); zestawy bez tego pola (stare pliki) wczytują się z `null`. Model spoza bieżącej listy zostaje w zestawie, a okno pokazuje ostrzeżenie. Przejście na inny zestaw, wczytanie i duplikowanie przy niezapisanych zmianach pytają przez `IDialogService.Confirm`. Duplikat dostaje nazwę „X (kopia)” (z numerem, gdy zajęta). Wyszukiwanie i filtr rodzaju działają na pełnej liście w pamięci.

Układ okna zestawów (1100 × 720, prawa kolumna 460): prawa strona to nazwa i opis z przyciskami „Podpowiedz” (`SuggestNameCommand` / `SuggestDescriptionCommand`, widoczne tylko przy `CanSuggest`), potem model obrazu, a poniżej zwijane `Expander`y: Prompt (rozwinięty, z „Edytuj w edytorze…” przy `CanEditInEditor`), Prompt systemowy, Negatywny, „Rozmiar, seed, jakość” (nagłówek to `DetailsSummary`; kafelki proporcji wiążą `AspectPresets`/`SelectedAspect`) i „Wspomaganie AI” (model wspomagania, instrukcja techniczna, parametry). Stan zwinięcia nie jest zapamiętywany. Dolny pasek ma po prawej „Zapisuj automatycznie” (`AutoSave`, zapamiętywane w `UserState.PresetsAutoSave`), „Zapisz zmiany” i „Zamknij” (`IsCancel` bez `Click`; pytanie o zmiany idzie przez `Closing`).

Selektor modelu w oknie zestawów to `ModelPicker` z `HasFilters`. Jego widok modeli i `ModelFilter` zakładki (`GenerateFilter` / `EditFilter`) tworzy `MainViewModel.OpenPresets` i przekazuje w `PresetsContext.Pickers`. Widok jest osobnym `ListCollectionView` nad tą samą listą (współdzielony zsynchronizowałby zaznaczenie z głównym oknem); subskrypcja filtra jest zdejmowana po zamknięciu okna. Nie używaj tymczasowego `CollectionViewSource` (patrz komentarz w `MainViewModel`).
