# Zakładki okna głównego i okno Ustawień

_Kiedy czytać: dodajesz albo zmieniasz zakładkę lewego panelu, jej widok, albo zakładkę Ustawień. Lista kontrolna nowej zakładki: skill `new-main-tab`._

## Lewy panel okna głównego = wstrzykiwane zakładki w osobnych projektach

(`IMainTab` z `ImageStudio.Shared`: `Title`, `Order`, `Content` = DataContext widoku). Każda zakładka to projekt `ImageStudio.Tabs.*` (Generate, Edit, Video, Chat, Voice) z widokiem i rekordem zakładki (`GenerateTab` itd. w swoim namespace); widoki wiążą się z `MainViewModel`/`ChatViewModel` tylko przez bindingi, więc projekty nie referują aplikacji.

- Wspólne kontrolki (`ModelPicker`, `ModelFilterBar`, `SizePanel`) i `ModelOption` leżą w `ImageStudio.Shared` (namespace bez zmian; w XAML trzeba `;assembly=ImageStudio.Shared`).
- DLL-e zakładek trafiają do podfolderu `tabs\` (jak dostawcy do `providers\`, targety w `ImageStudio.csproj`, wczytuje je `App` przez `AssemblyLoadContext.Resolving`).
- Zakładki są rejestrowane w kontenerze jako `IMainTab` (dodatkowa = kolejna rejestracja); `MainWindow` dostaje je w konstruktorze i tworzy po jednym widoku na zakładkę z `DataTemplate` wg typu (brak szablonu = wyjątek przy tworzeniu okna).
- `ActiveTab` to indeks w `Tabs`; wbudowane zajmują 0-4 (Głos = 4, prawy panel `VoiceResults` zamiast obrazu; `IsOutputTab` to zakładki 0-2), a logika zależna od zakładki (`RunActiveTab`, `IsChatTab`, `TabNames`) opiera się na tych indeksach.
- Układ każdej zakładki jest ujednolicony: **Model** (`ModelPicker` z nagłówkiem `common.model`, zawsze pierwszy; w Głosie pierwszy w każdej sekcji), potem **Prompt** (Edytuj prompt, zestawy), **Wejście** (`common.inputImages`: obrazy referencyjne, załączniki, nagranie), **Parametry**, **Rozmiar** i na końcu **Akcja** (przycisk uruchamiający). Pilnuje tego `TabLayoutTests`; nowa zakładka trzyma ten porządek.
- Kolejność wyświetlania zakładek użytkownik zmienia przeciąganiem nagłówków (`MainWindow.MoveTab`, zapis w `UserState.TabOrder` jako nazwy typów zakładek, nowe zakładki wchodzą na koniec).
- Indeksy `ActiveTab` i `TabVisibility` pozostają logiczne (kolejność wbudowana, `MainWindow._tabItems`), więc wybór zakładki idzie przez `MainTabs_SelectionChanged` i `SelectActiveTab`, a nie przez `SelectedIndex`.
- Po załadowaniu katalogu `MainViewModel.UpdateTabVisibility` (`MainViewModel.Tabs.cs`) wylicza `TabVisibility` dla wbudowanych zakładek (Generuj: `Models`, Edycja: `EditModels`, Wideo: `VideoModels`, Czat: `ChatModelOptions`, Głos: mowa lub transkrypcja): zakładka bez modeli jest ukrywana w `MainWindow` (`TabItem.Visibility`), a aktywna przechodzi na pierwszą widoczną. Gdy katalog nie dał żadnych modeli (brak klucza, błąd), zostają wszystkie. Nowa wbudowana zakładka musi dopisać swój warunek w tej metodzie.

Nowa zakładka: skill `new-main-tab` (pełna lista kontrolna: projekt, referencje `Private="$(PluginsInBundle)"`, `DataTemplate`, filtry modeli, indeksy, testy).

## Okno Ustawień = wstrzykiwane zakładki

(`ISettingsTab`: `Title`, `Order`, `Apply(SettingsResult)`). `MainViewModel.OpenSettingsAsync` podaje zakładki do `IDialogService.ShowSettings`. Dziś:

- `AppSettingsTab` (ogólne, w tym tryb „Rozmowy AI”: `SettingsResult.PromptChatHistory`, zapis w `UserState`)
- `AudioSettingsTab` (domyślny mikrofon i wyjście dźwięku, po nazwie urządzenia: `SettingsResult.Microphone`/`Speaker`, trafia do `VoiceViewModel.ApplyDevices`)
- `McpSettingsTab` (limity narzędzi MCP: liczba narzędzi, po której aplikacja ostrzega, domyślna i największa liczba wyników list; `SettingsResult.McpMaxTools`/`McpListLimit`/`McpMaxListLimit`, zapis w `UserState`, stosuje `McpLimits`)
- `StorageSettingsTab` (wtyczka magazynu wyników i jej pola, np. foldery zapisu; `SettingsResult.StorageId`/`StorageConfig`, zapis w `UserState`, stosuje `MediaStorageHost`)
- `ProviderSettingsTab` (dostawca).

`SettingsViewModel` tylko je porządkuje i składa wynik. Nowa zakładka Ustawień (skill `new-main-tab`, część o Ustawieniach) = ViewModel implementujący `ISettingsTab` + widok + `DataTemplate` w `SettingsWindow.xaml`; brak szablonu wykrywa `SettingsWindow.EnsureTabViews` (wyjątek przed otwarciem okna).

### Selektywne czyszczenie danych

W `Ustawienia → Aplikacja` można wybrać kategorie i dopiero potem zatwierdzić nieodwracalne czyszczenie; odmowa potwierdzenia nie zmienia plików. Domyślnie zaznaczone są ustawienia i prompty, historie rozmów/pamięć, zestawy, indeksy galerii, konfiguracja MCP oraz pozycje i rozmiary okien. Logowania i klucze dostawców są domyślnie odznaczone.

- „Ustawienia i ostatnie prompty” usuwa `settings.json`, `models.json` (preferencje modeli) oraz `state.json`. `settings.json` przechowuje m.in. adresy, dostawcę, motyw i język; `state.json` jest wspólnym stanem aplikacji — zawiera m.in. ostatnie prompty, wybory i ustawienia narzędzi. To dlatego ostatnie prompty nie mają osobnego pliku.
- Historie i pamięć to `sessions\*.json`, `prompt-chats\*.json`, `memory.json` oraz stara historia promptów `prompts.json` używana przez `LegacyPromptImporter`. Zestawy: bezpośrednio w `presets\` rozpoznawane są poprawne JSON-y deserializowane jak przez `PresetStore.LoadAll`, z tekstowymi polami `Name`, `Kind` i `Prompt`, niezależnie od nazwy. Uszkodzone pliki i pliki innego typu pozostają. Deserializacja używa wartości domyślnych i akceptuje nawet `{}`; takie niejednoznaczne obiekty pozostają i są zgłaszane jako błąd, ponieważ bez znacznika pochodzenia nie można ich bezpiecznie odróżnić od obcych plików. Plik obcy mający te same pola jest nieodróżnialny od zestawu i może zostać usunięty; ostrzega o tym potwierdzenie.
- Galeria usuwa wyłącznie indeksy `gallery.json` i `voice.json`. Nie usuwa lokalnych obrazów, wideo, dźwięku ani plików z dysków chmurowych.
- MCP usuwa `mcp.json` i zapisany lokalnie token serwera MCP. Plik MCP może zawierać tokeny lub nagłówki jawnie, dlatego interfejs pokazuje ostrzeżenie.
- Logowania i klucze usuwają znane pliki DPAPI providerów AI (w tym sesję ChatGPT) oraz sekrety wszystkich wtyczek magazynu (pliki `secrets-storage-*.bin`: logowania, sesje, kolejki wysyłek). Nie obejmują zmiennych środowiskowych ani nie kasują plików w chmurze. Inne/nieznane pliki `secrets*.bin` są pozostawiane.
- Rozmiary i pozycje okien to wyłącznie `windows.json`.

`UserDataCleanupService` przyjmuje wyłącznie `AppPaths.UserDataDir` (nadpisywalny dla testów), kasuje znane pliki i pasujące pliki bezpośrednio w tych katalogach — bez rekursji, usuwania katalogów, przechodzenia przez reparse points ani dotykania plików wskazanych przez użytkownika. Nieznane pliki zostają. Przed czyszczeniem blokowane są zapisy wybranych kategorii, utrwalany jest niewybrany stan i zatrzymywane są żądania, rozmowy i usługi MCP. Rejestr odrzuca nowe żądania. Po próbie aplikacja jawnie kończy działanie (nie chowa się do zasobnika), również po błędzie: szczegóły błędu są wyświetlane przed zamknięciem, część plików może być już usunięta i nie jest odtwarzana. Powtórzenie czyszczenia jest możliwe po ponownym uruchomieniu.
