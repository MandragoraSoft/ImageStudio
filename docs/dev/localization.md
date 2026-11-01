# Tłumaczenia interfejsu (PL, EN)

_Kiedy czytać: dodajesz albo zmieniasz jakikolwiek tekst widoczny dla użytkownika, albo tłumaczysz kolejny ekran._

## Jak to działa

- **Słowniki** to płaskie pliki JSON klucz -> tekst: `ImageStudio.Shared/Localization/Strings.pl.json` (język źródłowy) i `Strings.en.json`. Są wbudowane w `ImageStudio.Shared` (`EmbeddedResource` z `WithCulture="false"`: bez tego MSBuild uznaje `.pl` w nazwie pliku za kulturę i wkłada zasób do biblioteki satelickiej, której nie ma w publikacji do jednego pliku).
- **`Loc`** (`ImageStudio.Localization`): `Loc.T("klucz")`, `Loc.T("klucz", arg0, arg1)` (format `string.Format`, np. `{0:0.####}`), `Loc.Set("system" | "pl" | "en")`, zdarzenie `Loc.LanguageChanged`. Brak klucza w angielskim wraca do polskiego, brak w obu pokazuje sam klucz. Bez wywołania `Loc.Set` jest polski (testy zakładają polski).
- **XAML:** `{loc:T klucz}` (`xmlns:loc="clr-namespace:ImageStudio.Localization;assembly=ImageStudio.Shared"`; w plikach samego `ImageStudio.Shared` bez `;assembly=...`, bo kompilator XAML nie zna własnej biblioteki z tą składnią). To powiązanie z `LocalizationSource`, więc **zmiana języka odświeża otwarte okna bez restartu**. Tekst z wartością w środku to dwa `Run`: `<Run Text="{loc:T voice.speed}"/><Run Text="{Binding Speed}"/>`, nie `StringFormat`.
- **Kod:** teksty statusu, komunikaty błędów, etykiety liczone w ViewModelu: `Loc.T(...)`. Tekst zapisany w trwałych danych (np. dopisek w galerii) zostaje w języku, w którym powstał.
- **Ustawienie:** Ustawienia → Aplikacja → „Język interfejsu” (`AppSettings.Language`, plik `settings.json`, klucz `Ui:Language`): `system` (polski w systemie polskim, w innych angielski), `pl`, `en`. Start: `App.OnStartup` wywołuje `Loc.Set`, zapis ustawień wywołuje je od razu.
- Zakładki (`IMainTab.Title`) i okna zwykłym tekstem (nie powiązaniem) odświeżają się ręcznie: `MainWindow` nasłuchuje `Loc.LanguageChanged` (przez `Dispatcher`, bo zdarzenie jest statyczne) i przypisuje nagłówki zakładek od nowa. Listy budowane raz (np. `VoiceViewModel.Languages`) pokażą nowy język przy kolejnym otwarciu okna.

## Reguły przy nowym tekście

1. Nie wpisuj tekstu widocznego dla użytkownika na sztywno: dodaj klucz do **obu** słowników (`grupa.nazwa`, np. `voice.err.save`) i użyj `{loc:T}` albo `Loc.T`.
2. Klucz ma te same parametry (`{0}`, `{1:0.####}`) w obu językach.
3. Menu: skróty klawiszowe (`_Plik` / `_File`) nie mogą się dublować w jednym menu w żadnym z języków.
4. Nazwy własne i techniczne (UberLLM, OpenRouter, NSFW, nazwy modeli, `mp3`) zostają bez tłumaczenia.
5. Dev-komunikaty o błędach programisty (np. brak `DataTemplate`) i prompty wysyłane do modeli zostają po polsku/angielsku jak są; to nie UI.
6. Testy nie ustawiają języka na stałe: `Loc.Set("en")` tylko na czas testu i zawsze powrót do `pl` (patrz `UseLanguage` w `LocalizationTests`).

`LocalizationTests` pilnuje: identyczne klucze w obu słownikach, te same parametry, każdy klucz użyty w XAML i w `Loc.T("...")` istnieje, brak nieużywanych kluczy (poza `lang.*` składanymi w kodzie), a okno główne i Ustawienia faktycznie zmieniają język.

## Narzędzie sprawdzające: `tools/Check-Translations.ps1`

```powershell
pwsh tools/Check-Translations.ps1            # raport; kod wyjścia 1 przy błędach
pwsh tools/Check-Translations.ps1 -Fix       # sortuje klucze i normalizuje oba pliki (UTF-8, wcięcie 2, nowa linia na końcu)
pwsh tools/Check-Translations.ps1 -Pending   # dodatkowo: pliki z tekstami po polsku wpisanymi na sztywno
pwsh tools/Check-Translations.ps1 -Strict    # ostrzeżenia też dają kod 1 (pipeline wydania)
```

Błędy: nieprawidłowy JSON, klucz powtórzony w pliku (parser JSON po cichu bierze ostatni, więc to jedyny sposób, żeby to zauważyć), klucz tylko w jednym języku, pusty tekst, różne parametry (`{0}`, `{1:0.####}`) w PL i EN, zła nazwa klucza (`grupa.nazwa`), klucz użyty w XAML (`{loc:T}`) albo `Loc.T("...")`, którego nie ma w słowniku. Ostrzeżenia: nieużywany klucz (poza prefiksami składanymi w kodzie, np. `lang.`), tekst EN identyczny z PL (lista wyjątków w skrypcie: `$sameInBothLanguages`), klucze nie w kolejności.

Skrypt i testy `LocalizationTests` sprawdzają to samo z dwóch stron: skrypt daje pełny raport i naprawia kolejność, testy blokują błędy w `dotnet test`. Zmieniając reguły, zmień oba. Nie sprawdza on duplikatów skrótów klawiszowych w menu ani jakości tłumaczenia.

## Co jest przetłumaczone

Menu, stopka, nazwy zakładek, okno Ustawień (Aplikacja, Dźwięk, Dostawca AI, widoki dostawców), selektor modeli i panel filtrów, panel proporcji (`SizePanel`), dymki ikon modeli i koszty modelu (`ModelOption.CostText`), cała zakładka Głos (panel, wyniki, komunikaty `VoiceViewModel`, nazwy języków), domyślne urządzenie audio.

## Do przetłumaczenia (kolejne etapy)

Szacunkowa lista plików z tekstami po polsku (liczba to orientacyjna liczba tekstów; w kodzie liczone tylko łańcuchy z polskimi znakami, więc rzeczywista jest wyższa):

| Obszar | Pliki |
|---|---|
| Zakładki lewego panelu | `ImageStudio.Tabs.Generate/GenerationPanel.xaml` (17), `ImageStudio.Tabs.Edit/EditPanel.xaml` (18), `ImageStudio.Tabs.Video/VideoPanel.xaml` (12), `ImageStudio.Tabs.Chat/ChatPanel.xaml` (16), `ToolsPanel.xaml` (16) |
| Panel wyniku | `Views/OutputPanel.xaml` (17) + `.cs`, `Views/ModelMetricsPanel.xaml` (6), `Views/EnhancerPanel.xaml` |
| Czat | `Views/ChatConversation.xaml` (11), `ViewModels/ChatViewModel.cs` (12), `ToolsViewModel.cs`, `McpToolHost.cs`, `AppServerViewModel.cs` |
| Okna | `PromptEditorWindow.xaml` (23) + `.cs`, `PresetsWindow.xaml` (22), `PresetSaveWindow.xaml`, `ModelBrowserWindow.xaml` (13), ich ViewModele |
| Komunikaty głównego okna | `MainViewModel.*.cs` (wideo, galeria, referencje, generowanie, AppControl), `ModelParameter.cs`, `ImageDownloadService.cs`, `WpfDialogService.cs` |
| Narzędzia MCP | `Services/AppControlTools.cs` (21; opisy narzędzi widzi model, nie użytkownik: zostaw po angielsku albo polsku, ale spójnie) |
| Dostawcy (komunikaty błędów) | `ImageStudio.Providers.*` (kilka tekstów; biblioteki dostawców nie referują `ImageStudio.Shared`, więc teksty dla użytkownika zwracaj kodem błędu albo tłumacz w ViewModelu) |

Kolejność: zakładki lewego panelu i panel wyniku (najczęściej oglądane), potem okna, na końcu komunikaty ViewModeli. Po każdym etapie zostaw w tabeli tylko to, co zostało.

## Dodanie kolejnego języka

1. Plik `Strings.<kod>.json` z kompletem kluczy (kopia `Strings.en.json`) i `EmbeddedResource` w `ImageStudio.Shared.csproj` (z `WithCulture="false"`).
2. W `Loc`: słownik, `Resolve` (mapowanie języka systemu), opcja w `AppSettingsTab.Languages` + klucz `settings.language.<kod>`.
3. Test parzystości dla nowego słownika w `LocalizationTests`.
