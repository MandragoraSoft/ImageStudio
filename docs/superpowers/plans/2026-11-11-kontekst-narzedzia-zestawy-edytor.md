# Kontekst czatu, narzędzia, zestawy i edytor promptów: plan implementacji

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wskaźnik zużycia kontekstu w czacie, lepsze zarządzanie narzędziami czatu z podglądem zajmowanego kontekstu, pełna edycja zapisanych zestawów i dwukolumnowy edytor promptów.

**Architecture:** Cztery niezależne etapy wykonywane po kolei (3 → 4 → 2 → 1 według numeracji użytkownika; w planie Task 1–4). Liczenie tokenów to jedna statyczna klasa `TokenEstimator` (heurystyka znaki/4) używana przez czat i panel narzędzi; rozmiar okna modelu to nowe opcjonalne pole `ChatModelInfo.ContextLength` czytane z katalogów dostawców. Logika trafia do ViewModeli (testowalne na `TestApp`), XAML tylko wiąże.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit (`ImageStudio.Tests`, `TestApp`, atrapy z `Fakes.cs`).

**Spec:** uzgodniony w rozmowie 2026-11-11; streszczenie decyzji jest w sekcjach zadań poniżej (osobnego pliku spec nie ma).

## Global Constraints

- Przeczytaj `CLAUDE.md` w katalogu głównym repo przed pracą; jego reguły obowiązują w każdym zadaniu.
- Kod, komentarze i UI po polsku.
- Każdy tekst dla użytkownika: klucz w `ImageStudio.Shared/Localization/Strings.pl.json` i `Strings.en.json` + `{loc:T klucz}` / `Loc.T("klucz")`. Po zmianach uruchom `pwsh tools/Check-Translations.ps1` (0 błędów). Reguły: `docs/dev/localization.md`.
- ViewModele: bez `MessageBox`, bez `Application.Current`, bez statycznych magazynów. Zmiana `IDialogService` wymaga aktualizacji `FakeDialogs` (`ImageStudio.Tests/Fakes.cs`).
- Selektor modeli to `ModelPicker` z `HasFilters="True"` i `ICollectionView` (patrz `docs/dev/ui-shell.md`).
- Nowy kolor: obie palety (`ThemeService`, `App.xaml`), pilnuje `ThemeContrastTests`. Najpierw sprawdź, czy istniejący pędzel (np. `WarningBrush`) nie wystarczy.
- Nowa metoda/pole kontraktu dostawcy: parametry opcjonalne na końcu rekordu, żeby istniejące wywołania się kompilowały.
- Testy: nowy kod = testy w `ImageStudio.Tests` na `TestApp` z atrapami, bez sieci i UI (`docs/dev/testing.md`). TDD: najpierw test, który nie przechodzi.
- Build i testy: `dotnet build ImageStudio.slnx`, `dotnet test ImageStudio.Tests`. Jeśli pliki w `bin` są zablokowane (aplikacja działa), buduj z `-p:BaseOutputPath=<katalog tymczasowy>`; nie zabijaj procesu `ImageStudio`.
- Git: pracuj na gałęzi `funkcje/kontekst-narzedzia-zestawy-edytor` (już utworzona). Jeden commit na zadanie, po polsku w stylu „Dodaj …”, zakończony linią `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. Nie rób `git push`, nie zmieniaj gałęzi `main`.
- Nie dodawaj pakietów NuGet. Nie rób refaktoryzacji spoza zakresu zadania.
- Zadanie jest skończone dopiero, gdy cały `dotnet test ImageStudio.Tests` przechodzi; w raporcie podaj liczby (zaliczone/niezaliczone/pominięte) i wklej treść ewentualnych błędów. Nie pisz, że działa, jeśli nie uruchomiłeś.

## Review Focus

1. Model bez znanego okna kontekstu (większość dostawców): wskaźnik pokazuje same tokeny, bez procentu i bez dzielenia przez zero/null. Test w Task 1.
2. Sesja bez żadnej odpowiedzi z `usage` (nowa rozmowa, dostawca nie podaje usage): wskaźnik pokazuje szacunek z `~`, nie „0”. Test w Task 1.
3. Zużycie większe niż limit (ponad 100%): pasek zatrzymuje się na 100%, tekst pokazuje prawdziwy procent. Test w Task 1.
4. Profil narzędzi zapisany, zanim pojawiło się nowe narzędzie (nowy serwer MCP): nowe narzędzie zostaje włączone, zastosowanie profilu nie rzuca wyjątku dla nazw, których już nie ma. Test w Task 2.
5. Plik zestawu sprzed zmiany (bez pola `Provider`) oraz zestaw z modelem, którego nie ma na liście: wczytuje się, edycja innych pól nie kasuje modelu, okno pokazuje ostrzeżenie. Test w Task 3.

---

### Task 1: Wskaźnik zużycia kontekstu w czacie

**Files:**
- Create: `ImageStudio/Services/TokenEstimator.cs`
- Modify: `ImageStudio.Providers.Core/AiContracts.cs:13-14` (`ChatModelInfo`)
- Modify: `ImageStudio.Providers.Core/OpenAiCompat.cs` (nowy pomocnik `ReadContextLength`)
- Modify: dostawcy parsujący katalog modeli czatu: `ImageStudio.Providers.OpenRouter/OpenRouterProvider.cs` (`RawModel`, `FetchModelsAsync` ~431-447, `GetChatModelsAsync` 73-79), `ImageStudio.Providers.Kilo/KiloProvider.cs` (`ParseChatModels`/`ToInfo`), `ImageStudio.Providers.UberLlm/ImageGenerationService.cs:143-170`, `ImageStudio.Providers.Mistral/MistralProvider.cs`, `ImageStudio.Providers.OpenCodeGo/OpenCodeGoProvider.cs`, oraz OpenAi/ChatGpt, jeśli parsują obiekt modelu z JSON
- Modify: `ImageStudio/ViewModels/ChatViewModel.cs`
- Modify: `ImageStudio/Views/ChatConversation.xaml` (między linią 232 `ToolsWarning` a `Grid` z polem wpisywania, linia 234)
- Modify: `ImageStudio.Shared/Localization/Strings.pl.json`, `Strings.en.json`
- Modify: `docs/dev/chat.md` (krótki akapit o wskaźniku)
- Test: `ImageStudio.Tests/ContextUsageTests.cs` (nowy), dopisek w testach dostawcy OpenRouter (wzór: istniejące testy z `StubHandler`)

**Interfaces:**
- Produces (używane w Task 2):
  - `public static class TokenEstimator` w przestrzeni `ImageStudio.Services`:
    - `public static int Estimate(string? text)` → `0` dla null/pustego, inaczej `(text.Length + 3) / 4`
    - `public static int Estimate(ToolDefinition tool)` → suma `Estimate` z `Name`, `Description`, `ParametersJson`
    - `public static string Short(int tokens)` → `< 1000`: liczba (`"950"`); `< 1_000_000`: zaokrąglone tysiące z `k` (`54321` → `"54k"`); inaczej jedna cyfra po kropce z `M` (`1_250_000` → `"1.3M"`), kultura niezmienna
  - `ChatModelInfo(..., string? Description = null, int? ContextLength = null)`: nowy ostatni parametr; `null` = katalog nie podaje
  - `OpenAiCompat.ReadContextLength(JsonElement model)` → `int?`: pierwsza liczba całkowita `> 0` spośród pól `context_length`, `max_context_length`, `context_window`, `top_provider.context_length`; inaczej `null`
  - `ChatViewModel`:
    - `public int ContextUsed`: z ostatniej wiadomości asystenta, która ma `Usage.Input`: `Input + (Output ?? 0)`; gdy żadna nie ma, szacunek `Estimate(instrukcja systemowa z BuildHistory) + suma Estimate(definicji Tools.Tools) + suma Estimate(m.Content) po Messages`
    - `public bool ContextEstimated`: `true`, gdy `ContextUsed` pochodzi z szacunku
    - `public int? ContextLimit`: `_modelInfo(SelectedModel)?.ContextLength`
    - `public double ContextPercent`: `ContextLimit` null lub ≤ 0 → `0`; inaczej `100.0 * ContextUsed / ContextLimit` (bez obcinania)
    - `public double ContextBarValue`: `Math.Min(100, ContextPercent)`
    - `public bool HasContextLimit`
    - `public string ContextLevel`: `"critical"` gdy `ContextPercent >= 90`, `"warn"` gdy `>= 75`, inaczej `"normal"`
    - `public string ContextText`: z limitem `"{p}% · {used} / {limit}"` (p = procent zaokrąglony do całości, liczby przez `Short`), bez limitu `Loc.T("chat.context.tokens", used)`; przy szacunku przedrostek `~` przed liczbą zużycia
    - `public bool HasContext`: `Messages.Count > 0`
    - `public void RefreshContext()`: zgłasza zmianę wszystkich powyższych właściwości
  - Odświeżanie: `RefreshContext()` wołane przy `Messages.CollectionChanged`, w `Complete(...)` po ustawieniu `Usage`, po zmianie `SelectedModel`, `SystemPrompt`, przy zmianie właściwości `Tools` (istniejąca subskrypcja w konstruktorze, linia 69) oraz w `RefreshReasoningSupport()` (katalog modeli właśnie się wczytał).

Decyzje:
- Wskaźnik jest ukryty, gdy `HasContext == false`.
- Wygląd: `Grid` z `ProgressBar` (Height 4, Maximum 100, Value = `ContextBarValue`, widoczny tylko gdy `HasContextLimit`) i `TextBlock` z `ContextText` (styl `MutedTextBlock`, wyrównany do prawej). Kolor paska i tekstu przez `DataTrigger` na `ContextLevel`: `warn` i `critical`. Tooltip: `{loc:T chat.context.tip}`.
- Klucze: `chat.context.tokens` („{0} tok.” / „{0} tok.”), `chat.context.tip` („Zajętość okna kontekstowego modelu. ~ oznacza szacunek.” / odpowiednik EN).
- Dostawcy: wypełniaj `ContextLength` wyłącznie przez `OpenAiCompat.ReadContextLength` na elemencie JSON modelu. Nie wpisuj limitów na sztywno i nie zgaduj. Dostawca, którego kod nie ma dostępu do surowego elementu JSON modelu, zostaje z `null`.

- [ ] **Step 1: Testy `TokenEstimator`** w `ContextUsageTests.cs`: `Estimate(null) == 0`, `Estimate("") == 0`, `Estimate("abcd") == 1`, `Estimate("abcde") == 2`, `Short(950) == "950"`, `Short(54321) == "54k"`, `Short(128000) == "128k"`, `Short(1_250_000) == "1.3M"`; `Estimate(new ToolDefinition("ab", "abcd", "{}"))` równe sumie trzech składników.
- [ ] **Step 2:** Uruchom `dotnet test ImageStudio.Tests --filter "FullyQualifiedName~ContextUsageTests"`, oczekiwany błąd kompilacji (brak typu).
- [ ] **Step 3:** Zaimplementuj `TokenEstimator`; testy przechodzą.
- [ ] **Step 4: Testy `ReadContextLength`** (JSON w tekście, `JsonDocument.Parse`): `{"context_length":128000}` → 128000; `{"max_context_length":32000}` → 32000; `{"top_provider":{"context_length":8000}}` → 8000; `{"context_length":0}` → null; `{"context_length":"abc"}` → null; `{}` → null.
- [ ] **Step 5:** Dodaj `ContextLength` do `ChatModelInfo` i `ReadContextLength` do `OpenAiCompat`; podłącz u dostawców wymienionych w Files. Dopisz test OpenRouter: odpowiedź `/models` z `context_length: 200000` daje `ChatModelInfo.ContextLength == 200000` (wzór: istniejące testy OpenRouter z `StubHandler`).
- [ ] **Step 6: Testy `ChatViewModel`** (wzór tworzenia: `ChatViewModelTests`, `UsageTests`; `modelInfo` przekazany w konstruktorze zwraca `ChatModelInfo` z wybranym `ContextLength`):
  - `Usage_from_last_reply_sets_context`: odpowiedź z `TokenUsage(1000, 200, null)`, limit 4000 → `ContextUsed == 1200`, `ContextEstimated == false`, `ContextPercent == 30`, `ContextText == "30% · 1k / 4k"`, `ContextLevel == "normal"`.
  - `Unknown_limit_shows_tokens_only`: `ContextLength == null` → `HasContextLimit == false`, `ContextPercent == 0`, `ContextText` zawiera `"1k"` i nie zawiera `"%"`.
  - `No_usage_falls_back_to_estimate`: odpowiedź bez `Usage` → `ContextEstimated == true`, `ContextUsed > 0`, `ContextText` zawiera `"~"`.
  - `Over_limit_caps_bar_not_text`: usage 5000+0, limit 4000 → `ContextBarValue == 100`, `ContextPercent == 125`, `ContextLevel == "critical"`.
  - `Levels`: 75% → `"warn"`, 90% → `"critical"`, 74% → `"normal"`.
  - `Empty_chat_hides_indicator`: nowa sesja → `HasContext == false`.
  - `New_session_resets`: po odpowiedzi `NewChatCommand.Execute(null)` → `HasContext == false`.
- [ ] **Step 7:** Zaimplementuj właściwości i odświeżanie w `ChatViewModel`; testy przechodzą.
- [ ] **Step 8:** Dodaj wskaźnik do `ChatConversation.xaml` i klucze tłumaczeń; `pwsh tools/Check-Translations.ps1` bez błędów.
- [ ] **Step 9:** `dotnet build ImageStudio.slnx` i cały `dotnet test ImageStudio.Tests`.
- [ ] **Step 10:** Zaktualizuj `docs/dev/chat.md`, commit „Dodaj wskaźnik zużycia kontekstu w czacie, rozmiar okna modelu z katalogu dostawcy i testy”.

---

### Task 2: Zarządzanie narzędziami czatu i podgląd zajmowanego kontekstu

**Files:**
- Modify: `ImageStudio/ViewModels/ToolsViewModel.cs`
- Modify: `ImageStudio/ViewModels/ChatViewModel.cs` (rozbicie kontekstu)
- Modify: `ImageStudio/Configuration/UserState.cs` (profile)
- Modify: `ImageStudio/ViewModels/MainViewModel.cs:179-185` (przekazanie i zapis profili) oraz metoda `SaveState` (~494)
- Modify: `ImageStudio.Tabs.Chat/ToolsPanel.xaml`
- Modify: `ImageStudio/Views/ChatConversation.xaml` (tooltip wskaźnika)
- Modify: `Strings.pl.json`, `Strings.en.json`, `docs/dev/chat.md` (popraw też zdanie o wyłączonych narzędziach, linia ~41)
- Test: `ImageStudio.Tests/ToolManagementTests.cs` (nowy); wzory: `ToolTests.cs` (`McpTests`, `ChatToolLoopTests`), `BuiltinToolsTests.cs`, `AppControlTests.cs` (`AppToolsInChatTests`)

**Interfaces:**
- Consumes: `TokenEstimator.Estimate(ToolDefinition)`, `TokenEstimator.Estimate(string?)`, `TokenEstimator.Short(int)`, `ChatViewModel.RefreshContext()` z Task 1.
- Produces:
  - `ToolItem`: nowy parametr konstruktora `int tokens` (po `description`), właściwości `int Tokens`, `string TokensText` (`"~" + TokenEstimator.Short(Tokens)`), `[ObservableProperty] bool _isVisible = true`
  - `ToolGroupItem`: `int Tokens` (suma `Tokens` włączonych narzędzi), `int TotalTokens` (suma wszystkich), `string TokensText` (`"~" + Short(Tokens)`), `bool IsVisible` (którekolwiek narzędzie widoczne); `RaiseState` zgłasza też `Tokens` i `TokensText`
  - `ToolsViewModel`:
    - `public int ToolTokens`: suma `TokenEstimator.Estimate` po `Tools`
    - `public string TokensSummary`: `UseTools` ? `Loc.T("tools.tokens.summary", TokenEstimator.Short(ToolTokens))` : `""`
    - `[ObservableProperty] string _toolSearch = ""`: po zmianie ustawia `IsVisible` narzędzi (dopasowanie bez rozróżniania wielkości liter do `Title` lub `Description`; pusty tekst = wszystkie widoczne)
    - `[ObservableProperty] bool _sortBySize`: `true` = `ToolGroups` uporządkowane malejąco po `TotalTokens`; `false` = dotychczasowa kolejność; zmiana woła `RebuildGroups()`
    - profile: `ObservableCollection<string> ProfileNames` (posortowane), `[ObservableProperty] string? _selectedProfile`, `[ObservableProperty] string _profileName = ""`, `IReadOnlyDictionary<string, List<string>> Profiles`, polecenia `SaveProfileCommand` (nazwa = `ProfileName.Trim()`, niepusta; zapisuje kopię `_disabled`; ta sama nazwa nadpisuje; ustawia `SelectedProfile`), `ApplyProfileCommand` (CanExecute: `SelectedProfile` nie null; `_disabled` = zbiór z profilu; `RebuildGroups()`; `OnToolsChanged()`), `DeleteProfileCommand` (CanExecute jak wyżej)
    - nowy opcjonalny parametr konstruktora na końcu: `IReadOnlyDictionary<string, List<string>>? profiles = null`
    - `OnToolsChanged()` zgłasza dodatkowo `ToolTokens`, `TokensSummary`; zmiana profili zgłasza `Profiles`
  - `UserState.ToolProfiles`: `Dictionary<string, List<string>> ToolProfiles { get; set; } = [];` (nazwa profilu → nazwy wyłączonych narzędzi)
  - `ChatViewModel.ContextBreakdown` (`string`, wiele linii, do tooltipa): linie z `Loc.T` dla czterech składników szacunkowych: instrukcja systemowa, definicje narzędzi, historia (wiadomości `user` i `assistant`), wyniki narzędzi (rola `tool`); każda jako `~{Short}`; zgłaszana w `RefreshContext()`

Decyzje:
- **Naprawa błędu:** `ToolsViewModel.Tools` (linia 247) filtruje narzędzia MCP: `_host.Tools.Where(d => !_disabled.Contains(d.Name))`. Wyłączone narzędzie MCP nie trafia do żądania ani do `Summary`.
- Do szacunku narzędzia potrzebna jest pełna `ToolDefinition` (ze schematem): w `RebuildGroups` zbuduj słownik nazwa → definicja z `_builtin.Definitions` (z przedrostkiem `BuiltinTools.ChatPrefix`), `_appTools.AllDefinitions` (z `AppControlTools.ChatPrefix`) i `_host.ServerTools`.
- Profil trzyma nazwy wyłączone, więc narzędzie nieznane w chwili zapisu profilu zostaje włączone; nazwy z profilu, których już nie ma, są po prostu ignorowane.
- `MainViewModel`: przekaż `_state.ToolProfiles` do konstruktora; w obsłudze `Tools.PropertyChanged` zapisuj stan także dla `nameof(ToolsViewModel.Profiles)`; w `SaveState` przepisuj `Tools.Profiles` do `_state.ToolProfiles`.
- `ToolsPanel.xaml`: pod `tools.available` pole szukania (`ToolSearch`, `UpdateSourceTrigger=PropertyChanged`) i `CheckBox` „Sortuj wg rozmiaru” (`SortBySize`); w nagłówku grupy po `Counter` tekst `TokensText`; przy narzędziu `TokensText` po prawej (styl `MutedTextBlock`); `Visibility` grup i narzędzi związane z `IsVisible` (`BoolToVis`); pod `Summary` (linia 72) tekst `TokensSummary`; nowa sekcja „Profile” (klucz `tools.profiles`): `ComboBox` z `ProfileNames`/`SelectedProfile`, przyciski Zastosuj i Usuń, `TextBox` `ProfileName` z przyciskiem Zapisz.
- `ChatConversation.xaml`: tooltip wskaźnika z Task 1 pokazuje `chat.context.tip` oraz `ContextBreakdown`.
- Klucze: `tools.tokens.summary` („~{0} tok. w każdym żądaniu”), `tools.search` („Szukaj narzędzia”), `tools.sortBySize`, `tools.profiles`, `tools.profile.apply`, `tools.profile.save`, `tools.profile.delete`, `tools.profile.name`, `chat.context.system`, `chat.context.tools`, `chat.context.history`, `chat.context.toolResults` (format „Instrukcja systemowa: {0}” itd.), z odpowiednikami EN.

- [ ] **Step 1: Test błędu MCP** `Disabled_mcp_tool_is_not_sent`: połączony serwer z narzędziem (wzór: `McpTests` w `ToolTests.cs` lub `McpEndToEndTests`), wyłącz je przez `ToolItem.IsEnabled = false` → `ToolsViewModel.Tools` nie zawiera tej nazwy, a `Summary` liczy o jedno mniej. Uruchom, potwierdź, że nie przechodzi.
- [ ] **Step 2:** Popraw filtr w `Tools`; test przechodzi.
- [ ] **Step 3: Testy szacunku:** `Tool_item_shows_estimated_tokens` (`ToolItem.Tokens == TokenEstimator.Estimate(definicja)` dla narzędzia wbudowanego), `Group_tokens_count_only_enabled` (po wyłączeniu jednego narzędzia `Tokens` spada o jego `Tokens`, `TotalTokens` bez zmian), `Tool_tokens_sum_matches_tools` (`ToolTokens == Tools.Sum(Estimate)`), `Tokens_summary_empty_when_tools_off`.
- [ ] **Step 4:** Zaimplementuj szacunki.
- [ ] **Step 5: Testy szukania i sortowania:** `Search_hides_non_matching_tools` (fraza z nazwy jednego narzędzia: tylko ono i jego grupa mają `IsVisible == true`; pusta fraza przywraca wszystkie), `Search_is_case_insensitive`, `Sort_by_size_orders_groups_descending`, `Sort_off_restores_default_order`.
- [ ] **Step 6:** Zaimplementuj szukanie i sortowanie.
- [ ] **Step 7: Testy profili:** `Save_and_apply_profile_restores_disabled_set`, `Saving_same_name_overwrites`, `Delete_profile_removes_it`, `Empty_name_is_not_saved`, `Profile_ignores_unknown_names_and_keeps_new_tools_enabled` (profil z nazwą `"nie__istnieje"` stosuje się bez wyjątku; narzędzie spoza profilu włączone), `Profiles_are_persisted_in_state` (przez `TestApp`: zapis profilu → `MemoryUserStateStore` ma `ToolProfiles` z tą nazwą; nowa instancja je wczytuje).
- [ ] **Step 8:** Zaimplementuj profile, `UserState.ToolProfiles` i spięcie w `MainViewModel`.
- [ ] **Step 9: Test rozbicia:** `Context_breakdown_lists_four_parts` (`ContextBreakdown` ma 4 linie; po dodaniu wyniku narzędzia linia wyników narzędzi nie pokazuje `~0`).
- [ ] **Step 10:** Zaimplementuj `ContextBreakdown`, zmień `ToolsPanel.xaml` i tooltip, dodaj klucze; `pwsh tools/Check-Translations.ps1`.
- [ ] **Step 11:** Build i cały zestaw testów; aktualizacja `docs/dev/chat.md`; commit „Dodaj szacunek tokenów narzędzi, wyszukiwanie, sortowanie i profile narzędzi czatu, napraw wysyłanie wyłączonych narzędzi MCP, testy”.

---

### Task 3: Edycja zapisanych zestawów

**Files:**
- Modify: `ImageStudio/Configuration/Preset.cs` (pole `Provider`)
- Modify: `ImageStudio/ViewModels/PresetsViewModel.cs`
- Modify: `ImageStudio/Views/PresetsWindow.xaml` (+ `.xaml.cs`, jeśli potrzebne)
- Modify: `ImageStudio/ViewModels/IDialogService.cs`, `ImageStudio/Views/WpfDialogService.cs:105-112` (`ShowPresets`), wywołanie `ShowPresets` w `MainViewModel` (znajdź grepem), `ImageStudio.Tests/Fakes.cs` (`FakeDialogs.ShowPresets`)
- Modify: `ImageStudio/ViewModels/MainViewModel.Settings.cs` (`CapturePreset` ~190-206 zapisuje `Provider`)
- Modify: `ImageStudio.Tests/TestApp.cs` (`NewPresetsViewModel`, ~236)
- Modify: `Strings.pl.json`, `Strings.en.json`, `docs/dev/prompts-and-presets.md`
- Test: `ImageStudio.Tests/PresetsViewModelTests.cs` (dopisz), nowa klasa `PresetEditingTests` w tym samym pliku

**Interfaces:**
- Produces:
  - `Preset.Provider` (`string?`, Id dostawcy modelu; `null` w starych plikach)
  - `public sealed record PresetsContext(Func<string, IReadOnlyList<ModelOption>> ModelsFor, string? ProviderId)` w `ImageStudio/ViewModels/` (`ModelsFor("generate" | "edit")` zwraca bieżące modele danego rodzaju). Jeśli `ModelPicker` z filtrami wymaga więcej (np. `ICollectionView` i obiektu filtru z `MainViewModel`), rozszerz rekord o te elementy, kierując się `docs/dev/ui-shell.md` i istniejącym użyciem `ModelPicker` poza głównym oknem.
  - `IDialogService.ShowPresets(PresetsContext? context = null)`
  - `PresetsViewModel(IDialogService dialogs, IPresetStore presets, LegacyPromptImporter importer, PresetsContext? context = null)`
  - pola edycji (wszystkie `[ObservableProperty]`): `EditName`, `EditDescription` (istnieją), `EditPrompt`, `EditSystemPrompt`, `EditNegativePrompt`, `EditSeedText`, `EditQuality` (`string`), `EditWidth`, `EditHeight` (`int`), `EditModel` (`string?`); `ObservableCollection<PresetParamItem> EditParameters`
  - `public sealed partial class PresetParamItem : ObservableObject` z `string Key`, `[ObservableProperty] bool _enabled`, `[ObservableProperty] double _value`
  - `bool IsDirty`: którekolwiek pole edycji różni się od zaznaczonego zestawu
  - `string ModelWarning`: `Loc.T("presets.msg.modelMissing", EditModel)`, gdy `EditModel` nie jest pusty, kontekst podał niepustą listę modeli tego rodzaju i nie ma na niej tego Id; inaczej `""`
  - `SaveDetailsCommand` (nazwa zostaje, bo wiąże ją XAML i testy): zapisuje wszystkie pola; CanExecute = `IsDirty`
  - `DuplicatePresetCommand` (CanExecute: jest zaznaczenie)
  - `[ObservableProperty] string _searchText = ""`, `[ObservableProperty] string _kindFilter = "all"` (`"all"`, `"generate"`, `"edit"`), `IReadOnlyList<string> KindFilters`

Decyzje:
- `Preset.Parameters` to parametry modelu ulepszania promptu (`ParamState`: `Enabled`, `Value`). Edycja: dla każdego istniejącego klucza pole wyboru i pole liczbowe; kluczy się nie dodaje ani nie usuwa.
- Zapis: walidacja nazwy niepustej (`presets.msg.emptyName`, istnieje) i unikalnej w obrębie tego samego `Kind`, bez rozróżniania wielkości liter, z pominięciem samego siebie (`presets.msg.duplicateName`); `EditWidth` i `EditHeight` muszą być `> 0` (`presets.msg.badSize`). Błąd walidacji ustawia `Status` i niczego nie zapisuje.
- Zmiana `EditModel` na model z bieżącej listy ustawia przy zapisie `Preset.Provider = context.ProviderId`. Gdy modelu nie zmieniono, `Provider` zostaje bez zmian (także `null`).
- Model spoza listy nie jest podmieniany: pole pokazuje zapisane Id, a `ModelWarning` ostrzega.
- Przejście na inny zestaw przy `IsDirty`: `IDialogService.Confirm(Loc.T("presets.confirmUnsaved", nazwa), Loc.T("presets.confirmUnsaved.title"))`; tak = zapis poprzedniego (z tą samą walidacją; przy błędzie walidacji zmiany są odrzucane, a `Status` mówi dlaczego), nie = odrzucenie zmian. Użyj `partial void OnSelectedPresetChanging(Preset? oldValue, Preset? newValue)`; uważaj, żeby `Reload` nie wywołał pytania rekurencyjnie (flaga).
- Duplikat: kopia wszystkich pól, nazwa `Loc.T("presets.copyName", nazwa)` („{0} (kopia)” / „{0} (copy)”); gdy zajęta w tym rodzaju, dopisz ` 2`, ` 3`…; `IPresetStore.Create`, po czym lista odświeżona i kopia zaznaczona.
- Wyszukiwanie: `Presets` pokazuje zestawy, których `Name`, `Description` lub `Prompt` zawiera `SearchText` (bez rozróżniania wielkości liter) i których `Kind` pasuje do `KindFilter`. Trzymaj pełną listę w polu prywatnym, a `Presets` przebudowuj.
- `PresetsWindow.xaml`: nad siatką pole szukania i `ComboBox` rodzaju (z `ItemTemplate`, nie `DisplayMemberPath`); prawa kolumna to pola edycji w kolejności: nazwa, opis, model (`ModelPicker`, `HasFilters="True"`) z `ModelWarning` pod spodem, rozmiar (dwa pola), seed, jakość, prompt, prompt systemowy, prompt negatywny, parametry ulepszania; przycisk zapisu z istniejącym `presets.saveMeta` zmień na klucz `presets.saveChanges` („Zapisz zmiany”); w dolnym pasku przycisk Duplikuj (`presets.duplicate`). Rodzaj i model ulepszania zostają tylko do odczytu.
- `FakeDialogs.ShowPresets` dziś rzuca `NotSupportedException`; dostosuj sygnaturę, zachowanie może zostać.

- [ ] **Step 1: Testy zapisu pól** (`PresetEditingTests`, wzór tworzenia VM: `TestApp.NewPresetsViewModel`, magazyn `app.Presets`):
  - `Editing_prompt_and_size_saves_to_store`: zmiana `EditPrompt`, `EditWidth = 768` → po `SaveDetailsCommand.Execute(null)` zestaw w magazynie ma nowe wartości.
  - `Save_is_disabled_without_changes`: `SaveDetailsCommand.CanExecute(null) == false` zaraz po zaznaczeniu.
  - `Empty_name_is_rejected`, `Duplicate_name_in_same_kind_is_rejected` (drugi zestaw `generate` o tej nazwie → brak zapisu, `Status` niepusty), `Same_name_in_other_kind_is_allowed`, `Non_positive_size_is_rejected`.
  - `Parameters_are_editable`: zmiana `EditParameters[0].Value` i `Enabled` zapisuje się w `Preset.Parameters`.
- [ ] **Step 2:** Uruchom, potwierdź niepowodzenie; zaimplementuj pola, `IsDirty`, walidację i zapis.
- [ ] **Step 3: Testy modelu i dostawcy:**
  - `Changing_model_stores_provider`: kontekst z modelami `["a/x", "a/y"]` i `ProviderId = "openrouter"`; `EditModel = "a/y"` → po zapisie `Model == "a/y"`, `Provider == "openrouter"`.
  - `Missing_model_shows_warning_and_is_kept`: zestaw z `Model = "stary/model"`; `ModelWarning` niepusty; zmiana samego `EditPrompt` i zapis zostawia `Model == "stary/model"`.
  - `Old_preset_without_provider_keeps_null`: zestaw z `Provider == null`, zapis innego pola → `Provider` dalej `null`.
  - `No_context_means_no_warning`: VM bez kontekstu → `ModelWarning == ""`.
  - W `MainViewModel`: `Saved_preset_records_provider` (zapis zestawu z głównego okna ustawia `Preset.Provider` na Id aktywnego dostawcy; wzór: testy w `CompositionTests` 111-126).
- [ ] **Step 4:** Zaimplementuj `Preset.Provider`, `PresetsContext`, zmianę `IDialogService.ShowPresets`, `WpfDialogService`, `FakeDialogs`, `CapturePreset`.
- [ ] **Step 5: Testy niezapisanych zmian, duplikatu i szukania:**
  - `Switching_with_unsaved_changes_asks_and_saves` (`FakeDialogs.ConfirmAnswer = true` → poprzedni zestaw zapisany), `Switching_declined_discards_changes`.
  - `Duplicate_creates_copy_with_all_fields` (nazwa „X (kopia)”, te same `Prompt`, `Model`, `Width`, `Parameters`; kopia zaznaczona; oryginał bez zmian), `Second_duplicate_gets_numbered_name`.
  - `Search_filters_by_name_and_prompt`, `Kind_filter_limits_list`, `Clearing_search_restores_list`.
- [ ] **Step 6:** Zaimplementuj.
- [ ] **Step 7:** Przebuduj `PresetsWindow.xaml`, dodaj klucze, `pwsh tools/Check-Translations.ps1`. Dopisz test okna przez `WpfHost.Run` (wzór: `WindowMemoryTests`): okno z VM tworzy się bez wyjątku.
- [ ] **Step 8:** Build i cały zestaw testów; aktualizacja `docs/dev/prompts-and-presets.md`; commit „Dodaj edycję zapisanych zestawów (model, treść, rozmiar, parametry), duplikowanie, wyszukiwanie i testy”.

---

### Task 4: Dwukolumnowy edytor promptów

**Files:**
- Modify: `ImageStudio/Views/PromptEditorWindow.xaml`, `ImageStudio/Views/PromptEditorWindow.xaml.cs`
- Modify: `Strings.pl.json`, `Strings.en.json`, `docs/dev/prompts-and-presets.md`
- Test: `ImageStudio.Tests/PromptEditorWindowTests.cs` (nowy, przez `WpfHost.Run`; wzór: `WindowMemoryTests.cs`)

**Interfaces:**
- Consumes: nic z wcześniejszych zadań. Konstruktor `PromptEditorWindow(title, initial, rebuild, enhancerContext, presets, savePreset, describeImages)` i `Result` zostają bez zmian (używa ich `WpfDialogService.ShowPromptEditor`).
- Produces: wewnętrzna metoda `SetStatus(string text, bool warning = false)` i jeden `TextBlock x:Name="StatusText"`.

Decyzje:
- Okno: `Width="1100" Height="720" MinWidth="760" MinHeight="480"`.
- Układ głównego `Grid`: wiersz 0 pasek zestawów (`PresetBar`, na całą szerokość), wiersz 1 `*` z dwiema kolumnami (`*` z `MinWidth="360"`, odstęp 12, prawa `360`), wiersz 2 dolny pasek.
- Lewa kolumna: `SystemSection` (nagłówek, licznik, `SystemBox` Height 130, podpowiedź), pod nią nagłówek promptu z licznikiem i `PromptBox` wypełniający resztę (`*`).
- Prawa kolumna w `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`): `DescribeButton`, potem `RebuildSection` (instrukcja `InstructionBox` Height 90 z licznikiem; przyciski Przebuduj, Cofnij, Domyślna instrukcja w `WrapPanel`; wiersz kontekstu `HistoryLabel` z `ClearHistoryButton`; `Expander` `EnhancerHost` z `EnhancerPanel`).
- Dolny pasek: po lewej `StatusText` (zawijany, `MutedTextBlock`; przy `warning` kolor `WarningBrush`), po prawej podpowiedź `prompt.hint` jako tooltip przycisku Zastosuj, przyciski Wyczyść wszystko, Anuluj, Zastosuj.
- `PresetStatus`, `DescribeStatus`, `ChangesStatus`, `RebuildStatus` znikają; wszystkie ich ustawienia w code-behind idą przez `SetStatus`. Zachowaj pozostałe `x:Name` (`PresetBar`, `PresetBox`, `SystemSection`, `SystemBox`, `SystemCounter`, `PromptHeader`, `PromptCounter`, `PromptBox`, `DescribeButton`, `RebuildSection`, `InstructionBox`, `InstructionCounter`, `RebuildButton`, `UndoButton`, `HistoryLabel`, `ClearHistoryButton`, `EnhancerHost`, `ClearButton`), bo code-behind ukrywa sekcje według argumentów konstruktora. Gdy `RebuildSection` i `DescribeButton` są ukryte, prawa kolumna ma szerokość 0 (prompt zajmuje całe okno).
- Oba czyszczenia zostają jako funkcje (kontekst osobno, wszystko osobno), zgodnie ze szkicem układu; zmienia się tylko ich położenie.
- Teksty wpisane na sztywno w `PromptEditorWindow.xaml.cs` (m.in. linie ~75, ~80, ~102, ~131, ~188) przechodzą do `Loc.T` z nowymi kluczami `prompt.msg.*`. `MessageBox` przy błędzie zapisu zestawu (~192) zastąp `SetStatus(..., warning: true)`.
- Poprawki: przycisk Nadpisz (`x:Name="OverwriteButton"`) ma `IsEnabled == false`, gdy `PresetBox.SelectedItem == null` (obsłuż `SelectionChanged`); po Cofnij status jest czyszczony; `Describe_Click` nie ustawia `PromptBox.IsReadOnly` niezależnie od `SetBusy` (blokadą pola zarządza tylko `SetBusy`).
- Zachowanie edytora (wynik `Result`, skróty, przebudowa, historia) się nie zmienia; istniejące `PromptEditorTests` i `PromptEditorPresetStateTests` muszą przechodzić bez modyfikacji.

- [ ] **Step 1: Testy okna** (`WpfHost.Run`, bez `ShowDialog`; elementy przez `FindName`):
  - `Window_builds_with_all_sections`: wszystkie argumenty podane → `PresetBar`, `SystemSection`, `RebuildSection`, `DescribeButton` widoczne, `StatusText` istnieje.
  - `Window_builds_minimal`: `presets`, `rebuild`, `describeImages` równe null i `initial.System == null` → `PresetBar`, `SystemSection`, `RebuildSection` mają `Visibility.Collapsed`, okno tworzy się bez wyjątku.
  - `Overwrite_is_disabled_without_selection`: `PresetBox.SelectedItem = null` → `OverwriteButton.IsEnabled == false`; po wybraniu zestawu `true`.
  - `Old_status_blocks_are_gone`: `FindName("RebuildStatus")`, `("ChangesStatus")`, `("DescribeStatus")`, `("PresetStatus")` zwracają null.
- [ ] **Step 2:** Uruchom, potwierdź niepowodzenie.
- [ ] **Step 3:** Przebuduj XAML i code-behind według decyzji; testy przechodzą.
- [ ] **Step 4:** Przenieś teksty do tłumaczeń; `pwsh tools/Check-Translations.ps1` bez błędów oraz `pwsh tools/Check-Translations.ps1 -Pending` nie wypisuje `PromptEditorWindow`.
- [ ] **Step 5:** Build i cały zestaw testów; aktualizacja `docs/dev/prompts-and-presets.md`; commit „Przebuduj edytor promptów na układ dwukolumnowy ze wspólnym paskiem statusu, przenieś teksty do tłumaczeń, testy”.

---

### Task 5: Trójstanowy filtr NSFW jako kontrolka wielokrotnego użytku

Dodane 2026-11-11 na prośbę użytkownika: dzisiejsze pole „Ukryj NSFW” (włącz/wyłącz) ma się stać filtrem trójstanowym: **tylko pokaż** – **brak filtra** – **tylko ukryj**, zrealizowanym jako osobna kontrolka trójstanowa.

**Files:**
- Create: `ImageStudio.Shared/TriState.cs` (enum), `ImageStudio.Shared/Views/TriStateToggle.xaml` + `.xaml.cs` (kontrolka)
- Modify: `ImageStudio/ViewModels/ModelFilter.cs` (pole `_hideNsfw`, linia 28; `Matches`, linia 73)
- Modify: `ImageStudio/ViewModels/ModelBrowserViewModel.cs` (linie ~75, ~105, ~112)
- Modify: `ImageStudio.Shared/Views/ModelFilterBar.xaml:26`, `ImageStudio/Views/ModelBrowserWindow.xaml:38`
- Modify: `Strings.pl.json`, `Strings.en.json` (klucz `filter.nsfw`, linia 133), `docs/dev/ui-shell.md` (akapit o filtrach i o nowej kontrolce)
- Test: `ImageStudio.Tests/NsfwFilterTests.cs` (nowy); popraw `ImageStudio.Tests/ModelBrowserViewModelTests.cs:86` (`vm.HideNsfw = true`)

**Interfaces:**
- Produces:
  - `public enum TriState { Only, Any, Exclude }` w przestrzeni `ImageStudio` (projekt `ImageStudio.Shared`): `Only` = tylko elementy z cechą, `Any` = brak filtra, `Exclude` = tylko elementy bez cechy
  - `TriStateToggle : UserControl` w `ImageStudio.Views` (assembly `ImageStudio.Shared`) z właściwościami zależności: `TriState Value` (wiązanie dwukierunkowe domyślnie, domyślna wartość `Any`), `string Label`, `string OnlyText`, `string AnyText`, `string ExcludeText`. Kontrolka nie wie nic o NSFW: teksty przychodzą z zewnątrz.
  - `ModelFilter.Nsfw` (`TriState`, `[ObservableProperty]`, domyślnie `TriState.Any`) zamiast `HideNsfw`; zmiana woła `Refresh()`
  - `ModelBrowserViewModel.Nsfw` (`TriState`) zamiast `HideNsfw`; zmiana woła `View.Refresh()`

Decyzje:
- Właściwość `HideNsfw` znika z obu klas (bez zgodności wstecz: nie jest zapisywana w stanie ani w ustawieniach; potwierdź grepem, że nic poza wymienionymi plikami jej nie używa).
- Dopasowanie: `Only` przepuszcza wyłącznie modele z `Nsfw == true`, `Exclude` wyłącznie z `Nsfw == false`, `Any` wszystkie. Pozostałe filtry działają jak dotąd i łączą się z tym spójnikiem „i”.
- Wygląd kontrolki: etykieta i trzy przylegające przyciski-segmenty w jednym rzędzie (`RadioButton` w stylu `ToggleButton` albo trzy `ToggleButton`), kolejność od lewej: `OnlyText`, `AnyText`, `ExcludeText`; dokładnie jeden zaznaczony. Kolory wyłącznie z istniejących pędzli motywu (`DynamicResource`), obsługa klawiatury (Tab, strzałki, spacja) jak w zwykłych `RadioButton`. Kontrolka ma się mieścić w wąskim panelu filtrów (`ModelFilterBar`), więc segmenty mają krótkie teksty i `ToolTip` z pełnym opisem.
- Użycie w obu miejscach: `<views:TriStateToggle Value="{Binding Nsfw}" Label="{loc:T filter.nsfw}" OnlyText="{loc:T filter.tri.only}" AnyText="{loc:T filter.tri.any}" ExcludeText="{loc:T filter.tri.exclude}"/>`; XAML między projektami wymaga `;assembly=ImageStudio.Shared`.
- Klucze: `filter.nsfw` zmienia treść na „NSFW” (PL i EN; to ten sam tekst w obu językach, więc sprawdź w `docs/dev/localization.md`, jak projekt oznacza celowo identyczne tłumaczenia, żeby `Check-Translations -Strict` nie zgłaszał ostrzeżenia); nowe `filter.tri.only` („Tylko” / „Only”), `filter.tri.any` („Wszystkie” / „All”), `filter.tri.exclude` („Ukryj” / „Hide”) oraz podpowiedzi `filter.tri.only.tip`, `filter.tri.any.tip`, `filter.tri.exclude.tip`.
- Wybór „tylko preferowane” przy starcie (`ApplyPreferredOnly`) i `ModelFilter.Reset()` zostają bez zmian; `Reset()` nie zmienia `Nsfw` (tak jak dziś nie zmienia `HideNsfw`).

- [ ] **Step 1: Testy filtra** (`NsfwFilterTests`; `ModelFilter` tworzony wprost, modele jako `ModelOption` z `Nsfw` true i false):
  - `Any_shows_all_models` (domyślny stan: oba modele przechodzą `Matches`)
  - `Exclude_hides_nsfw_models` (przechodzi tylko model z `Nsfw == false`)
  - `Only_shows_only_nsfw_models` (przechodzi tylko model z `Nsfw == true`)
  - `Changing_nsfw_raises_changed` (zdarzenie `Changed` po każdej zmianie wartości)
  - `Nsfw_combines_with_search` (`Only` + `SearchText` niepasujący do modelu NSFW → nic nie przechodzi)
  - `Reset_keeps_nsfw_choice`
  - odpowiednie trzy testy dla `ModelBrowserViewModel.Nsfw` (wzór: istniejący test w `ModelBrowserViewModelTests.cs` okolice linii 86)
- [ ] **Step 2:** Uruchom, potwierdź błąd kompilacji (brak `TriState` / `Nsfw`).
- [ ] **Step 3:** Dodaj `TriState`, zamień `HideNsfw` na `Nsfw` w obu ViewModelach; testy przechodzą.
- [ ] **Step 4: Testy kontrolki** przez `WpfHost.Run`: `Toggle_defaults_to_any` (środkowy segment zaznaczony), `Clicking_segment_updates_value` (zaznaczenie pierwszego segmentu ustawia `Value == TriState.Only`, trzeciego `TriState.Exclude`), `Setting_value_updates_segments` (ustawienie `Value = TriState.Exclude` zaznacza trzeci segment i odznacza pozostałe), `Texts_come_from_properties` (segmenty pokazują `OnlyText`, `AnyText`, `ExcludeText`).
- [ ] **Step 5:** Zaimplementuj `TriStateToggle`, podmień `CheckBox` w `ModelFilterBar.xaml` i `ModelBrowserWindow.xaml`, dodaj klucze; `pwsh tools/Check-Translations.ps1 -Strict` bez błędów.
- [ ] **Step 6:** Build i cały zestaw testów; aktualizacja `docs/dev/ui-shell.md`; commit „Zamień filtr NSFW na trójstanowy (tylko, wszystkie, ukryj) z kontrolką TriStateToggle, testy”.
