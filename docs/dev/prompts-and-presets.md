# Przebudowa promptu przez AI i zestawy (presety)

_Kiedy czytać: dotykasz edytora promptu, przebudowy promptu, opisu obrazu przez AI albo zestawów._

## Obrazy wejściowe edycji

Panel edycji pokazuje wszystkie `MainViewModel.References` jako przewijane miniatury. Kolejność zmienia przeciąganie kafelków lub przyciski strzałek; pierwszy obraz jest podstawowy. Zmiana pierwszej pozycji aktualizuje `SourceImage` i proporcje wyjścia, a wysyłka oraz opis obrazów używają kolejności kolekcji. Miniatury są wczytywane ze ścieżek referencji z ograniczeniem rozmiaru dekodowania i bez blokowania plików. Pliki, linki, schowek i App Control korzystają z tej samej listy.

## Przebudowa promptu przez AI

Edytor generowania, edycji, wideo i zestawów ma zakładkę „Rozmowa AI”: osobny czat korzysta z `ChatViewModel`, aktualnego dostawcy, wybranego modelu, strumienia i załączników. Bieżące pola i rodzaj edytora są kontekstem każdego żądania.

- Rozmowa Generowania, Edycji i Wideo (osobna dla każdego) jest wznawiana po ponownym otwarciu edytora wg ustawienia „Rozmowy AI” (`UserState.PromptChatHistory`): na dysku (domyślnie, także po restarcie), tylko do zamknięcia aplikacji (`InMemoryPromptChatStore`) albo nie zapamiętuj (bez magazynu).
- Po wprowadzeniu izolacji rozmowy zaczynają się od nowa pod kluczami `isolated-v1:*`: starsze pliki pozostają na dysku, ale nie są wznawiane, ponieważ mogły zawierać dane innych zakładek.
- Anulowanie odrzuca zmiany pól promptu, ale nie rozmowę; „Nowa rozmowa” zaczyna czystą rozmowę bez zmiany pól.
- Szkic wiadomości i oczekujące załączniki nie są przywracane po zamknięciu okna. Zmiana trybu nie usuwa wcześniej zapisanych plików.
- Zamknięcie w trakcie odpowiedzi przerywa ją. Jeśli żadne narzędzie nie zostało wykonane, cofa niedokończoną turę; jeśli zostało, historia wywołań i wyników zostaje, a przerwane wywołanie dostaje wynik `chat.msg.toolStopped`.
- Rozmowy zestawów nie mają stałego klucza, więc nie są zapisywane.
- Model może prowadzić swobodną rozmowę; kompletną propozycję podaje jako JSON (sam obiekt lub blok `json`). „Wczytaj propozycję do promptu” waliduje ostatnią zakończoną odpowiedź i dopiero wtedy zmienia widoczne pola; przycisk cofania przywraca pola i historię. Błędy, niekompletna odpowiedź oraz Stop nie wczytują wyniku.
- `PromptConversationTools` udostępnia tylko jawnie dozwolone, włączone narzędzia: datę, modele, domyślne modele, przebudowę i stan bieżącego edytora; ten ostatni zwraca pola szkicu zamiast promptów wszystkich zakładek.
- Pamięć, pliki, galeria, zestawy, sterowanie promptami i zewnętrzne MCP są zablokowane również przy bezpośrednim wywołaniu, a wspólna notatka pamięci nie trafia do instrukcji systemowej. Zwykły czat zachowuje dotychczasowe narzędzia. Jednorazowa przebudowa pozostaje w zakładce „Przebudowa”.

„Analizuj obrazy w rozmowie” wysyła do wybranego modelu bieżące obrazy zakładki Edycja lub Wideo oraz oczekujące załączniki rozmowy, bez duplikatów. Obrazy z poprzednich wiadomości nie są ponownie dołączane; wcześniejsze opisy pozostają w historii. Gdy nie ma bieżących obrazów, otwiera wybór plików. Opis zostaje w historii jako kontekst kolejnych tur, ale nie zmienia pól promptu ani szkicu i oczekujących załączników. Błąd lub Stop usuwa niekompletną turę analizy; sama analiza nie udostępnia modelowi narzędzi, które pozostają dostępne w zwykłych wiadomościach. Dotychczasowy „Opisz obraz” nadal dopisuje opis do promptu.

Jednorazowa przebudowa (`IAiProvider.EnhancePromptAsync` -> `OpenAiCompat.EnhanceAsync`): trzy niezależne części to `system` (jak wykonać), `concept` (REQUEST, co pokazać) i `negative` (czego unikać).

- Model dostaje aktualne wartości i odpowiada poprawnym JSON-em `{system, concept, negative, changes}`.
- Trzy pierwsze pola muszą być tekstem; system i negative mogą być puste, concept nie.
- Brak pola, inny typ lub niepoprawny JSON powoduje błąd bez zmiany edytora; `changes` jest opcjonalnym opisem zmian.
- Tryb odpowiedzi (`PromptResponseMode`) wybiera dostawca. OpenAI przy oficjalnym `api.openai.com` i modelach `gpt-4o-mini`, `gpt-4o-mini-2024-07-18`, `gpt-4o-2024-08-06` dostaje ścisły `json_schema` (`strict`, wszystkie pola w `required`, `additionalProperties: false`; `changes` jest w schemacie wymagane, a lokalnie opcjonalne). Pozostali OpenAI, OpenRouter i dostawcy zgodni z OpenAI dostają `json_object`; UberLLM i ChatGPT (Responses API) używają samej instrukcji.
- Obniżenie trybu (`json_schema` -> `json_object` -> instrukcja) następuje tylko po jednoznacznym odrzuceniu pola `response_format` (`ApiErrors.RejectsResponseFormat`: HTTP 400/422 i zarówno wskazanie pola, jak i kod lub komunikat o braku obsługi). Każdy tryb co najwyżej raz. Błędy schematu (`invalid_json_schema`), autoryzacji, limitów, braku modelu, błędy serwera i niepoprawna treść są zgłaszane od razu, bez ponawiania. Odmowa modelu (`refusal`) jest błędem.
- Poprzednie przebudowy (`PromptRevision`, ostatnie 6) są dołączane jako kontekst rozmowy wraz z wejściem i wynikiem negative; starsze rewizje bez tych opcjonalnych pól pozostają zgodne.
- Historia żyje w `PromptEdit.History` i w `MainViewModel` osobno dla zakładek Generowanie, Edycja i Wideo. Cofnięcie przywraca wszystkie widoczne pola i historię. Edytor otwarty z zestawów używa tej samej przebudowy, zaś ukryte pola pozostają ukryte. Narzędzie MCP `rebuild_prompt` przyjmuje i zwraca `negative_prompt`. Wideo obsługuje przebudowę w rozmowie, ale nie jednorazową.

## Zestawy (presety)

To pliki JSON w katalogu użytkownika (`PresetStore`, rodzaj `generate` albo `edit`). Edytor promptu (`PromptEditorWindow`) dostaje listę zestawów i delegat zapisu jako parametry (nie zna `PresetStore`). Okno zapisu zestawu proponuje nazwę i opis przez AI (`PresetSaveViewModel`). Edytor promptu ma też "Opisz obraz (AI)" (delegat `describeImages` z `IDialogService.ShowPromptEditor`; `MainViewModel.DescribeImagesAsync` wysyła obrazy zakładki albo wskazane pliki do modelu tekstowego z obsługą obrazów).

Układ edytora: dwie kolumny.

- Lewa pokazuje osobno prompt `SYSTEM`, `REQUEST / TREŚĆ` i — tylko gdy przekazano wartość nie-null — `PROMPT NEGATYWNY` / `NEGATIVE PROMPT`.
- Edytory generowania, edycji obrazu i wideo pokazują SYSTEM, REQUEST i negative prompt, każdy ze swoim osobnym stanem (`NegativePrompt`, `EditNegativePrompt`, `VideoNegativePrompt`, a dla wideo także `VideoSystemPrompt`; wideo nie ma zestawów, więc system i negative zapisują się tylko w `UserState`; `StudioApi` składa system z requestem przed wysyłką wideo jak przy obrazach, ale tylko gdy system jest niepusty, i jest to część tekstu promptu, nie rola API; wpis galerii i powtórzenie niosą już złożony prompt; anulowanie niczego nie zmienia, a edycja i wideo zapisują się w `UserState`).
- Edycja i wideo wysyłają wartość wyłącznie natywnym parametrem `negative_prompt` modelu, którego schemat go potwierdza; w pozostałych przypadkach status pokazuje ostrzeżenie i nic nie jest wysyłane ani dopisywane do promptu (szczegóły w providers.md).
- Generowanie ma tę samą zasadę (`ImageOptions.NegativeNotice`, patrz providers.md).
- Prawa kolumna zawiera zakładki przebudowy i rozmowy; jej szerokość (domyślnie 360 px) zmienia `GridSplitter` i zapamiętuje `WindowMemory.ColumnKey`.
- Pole instrukcji (`InstructionBox`, min. 160) wypełnia wolną wysokość, bo korzeń `SidebarHost` ma `MinHeight` równe `ViewportHeight` przewijania.
- Gdy nie ma wspomagania, prawa kolumna i splitter się zwijają bez nadpisywania zapamiętanej szerokości.
- Lewa kolumna przewija się, gdy pola nie mieszczą się w oknie.
- Jeden pasek statusu (`StatusText`) pokazuje wszystko przez `SetStatus(tekst, warning)`; błędy idą kolorem `WarningBrush`, a `MessageBox` zostaje tylko przy potwierdzeniach. Przycisk „Nadpisz” jest aktywny tylko przy zaznaczonym zestawie.

Wczytanie zestawu w edytorze generowania wstawia również jego negative prompt; nadpisanie zapisuje go razem z systemem i treścią. Edytor zestawu (`EditInEditorCommand`) przekazuje negative prompt dla obu rodzajów (`generate` i `edit`). Zestaw `edit` wczytuje wartość do `EditNegativePrompt` i zapisuje ją z zakładki Edycja; starsze zestawy edycji bez tej wartości dają puste pole, a zapisana tam wcześniej wartość (kopia z generowania) jest po prostu wczytywana jako negative edycji. Zestawów wideo nie ma. Pole jest częścią `PromptEdit` jako wartość nullable: `null` oznacza, że jest ukryte, a pusty tekst oznacza widoczne i wyczyszczone pole.

Okno zestawów (`PresetsViewModel`, `PresetsWindow`) edytuje wszystkie pola zestawu (nazwa, opis, model, rozmiar, seed, jakość, prompty, parametry ulepszania); rodzaj i model ulepszania są tylko do odczytu. Zapis waliduje nazwę (niepusta, unikalna w obrębie rodzaju bez względu na wielkość liter) i rozmiar (> 0). Przy zmianie modelu zapisywany jest `Preset.Provider` (id aktywnego dostawcy, z `PresetsContext`); zestawy bez tego pola (stare pliki) wczytują się z `null`. Model spoza bieżącej listy zostaje w zestawie, a okno pokazuje ostrzeżenie. Przejście na inny zestaw, wczytanie i duplikowanie przy niezapisanych zmianach pytają przez `IDialogService.Confirm`. Duplikat dostaje nazwę „X (kopia)” (z numerem, gdy zajęta). Wyszukiwanie i filtr rodzaju działają na pełnej liście w pamięci.

Układ okna zestawów (1100 × 720, prawa kolumna 460): prawa strona to nazwa i opis z przyciskami „Podpowiedz” (`SuggestNameCommand` / `SuggestDescriptionCommand`, widoczne tylko przy `CanSuggest`), potem model obrazu, a poniżej zwijane `Expander`y: Prompt (rozwinięty, z „Edytuj w edytorze…” przy `CanEditInEditor`), Prompt systemowy, Negatywny, „Rozmiar, seed, jakość” (nagłówek to `DetailsSummary`; kafelki proporcji wiążą `AspectPresets`/`SelectedAspect`) i „Wspomaganie AI” (model wspomagania, instrukcja techniczna, parametry). Stan zwinięcia nie jest zapamiętywany. Dolny pasek ma po prawej „Zapisuj automatycznie” (`AutoSave`, zapamiętywane w `UserState.PresetsAutoSave`), „Zapisz zmiany” i „Zamknij” (`IsCancel` bez `Click`; pytanie o zmiany idzie przez `Closing`).

Selektor modelu w oknie zestawów to `ModelPicker` z `HasFilters`. Jego widok modeli i `ModelFilter` zakładki (`GenerateFilter` / `EditFilter`) tworzy `MainViewModel.OpenPresets` i przekazuje w `PresetsContext.Pickers`. Widok jest osobnym `ListCollectionView` nad tą samą listą (współdzielony zsynchronizowałby zaznaczenie z głównym oknem); subskrypcja filtra jest zdejmowana po zamknięciu okna. Nie używaj tymczasowego `CollectionViewSource` (patrz komentarz w `MainViewModel`).
