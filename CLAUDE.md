# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projekt

ImageStudio: aplikacja WPF (.NET 10, `net10.0-windows`, x64) do generowania i edycji obrazów, wideo oraz czatu z modelami AI. Kod, komentarze i UI są po polsku; trzymaj ten język. Repozytorium nazywa się `uberllm-proxy` z historycznych powodów. `NOTES.md` opisuje dawne proxy Node (`proxy.mjs`), którego już nie ma w repo, więc nie traktuj go jako opisu obecnego kodu.

## Komendy

```powershell
dotnet build ImageStudio.slnx
dotnet test ImageStudio.Tests                       # xUnit, całość
dotnet test ImageStudio.Tests --filter "FullyQualifiedName~PromptEditorTests"   # jedna klasa/test
dotnet run --project ImageStudio                    # Debug otwiera też konsolę z logami (OutputType Exe)
dotnet publish ImageStudio -c Release -r win-x64 --self-contained   # wersja samodzielna
```

Przy dziwnych błędach brakujących plików `obj` (np. `BG1002`, `CS2001`) usuń katalogi `bin`/`obj` i zbuduj z `-m:1`; równoległy build kilku projektów XAML bywa wyścigiem.

Wersje pakietów są centralne (`Directory.Packages.props`), a TFM, `UseWPF`, nullable i `PlatformTarget` w `Directory.Build.props`. W csproj dodawaj `PackageReference` bez `Version`. Nie ma lintera.

## Architektura

- **MVVM na CommunityToolkit.Mvvm.** `MainViewModel` jest `partial` i rozbity na pliki per funkcja (`.Generation`, `.Video`, `.Settings`, `.Gallery`, `.References`, `.Size`, `.AppControl`, `.Helpers`). Nowa funkcja głównego okna = nowy plik partial, nie rozrost `MainViewModel.cs`. Stan użytkownika trzyma `UserState`, a dane (ustawienia, galeria, zestawy, klucze) leżą w `AppPaths.UserDataDir`. Klucze API są szyfrowane przez `SecretStore` (DPAPI).
- **Dostawcy AI za `IAiProvider`** (`ImageStudio.Providers.Core`): UberLLM, OpenRouter i OpenAI to osobne projekty `ImageStudio.Providers.*`, a kontrakty i wspólny kod OpenAI (`OpenAiCompat`) są w `Core`. Wszystkie zachowały namespace `ImageStudio.Services`, więc XAML musi odwoływać się do nich z `;assembly=ImageStudio.Providers.Core`. DLL-e dostawców (referencje `Private="false"`) trafiają do podfolderu `providers\` (build i publish, targety w `ImageStudio.csproj`), a `App` wczytuje je przez `AssemblyLoadContext.Resolving`. Aplikacja rozmawia tylko z interfejsem. Nowy dostawca = nowy projekt + wpis w `ProviderRegistry` (+ widok ustawień w `Views/Providers`). Kontrakt zawiera też `DefaultTextModel` (stała `DefaultTextModelId` w dostawcy, widoczna w `ProviderRegistry`). Użytkownik nadpisuje go w Ustawieniach przez standardowy `ModelPicker` (`ProviderModelPrefs.DefaultTextModel`; `null` = domyślny dostawcy), a `MainViewModel` używa wartości efektywnej przy wyborze modelu ulepszania i czatu. Kontrakt ma też `ReloadAsync` (katalog dostawcy: modele obrazów, wideo, tekstu i ceny jednym wywołaniem, część nieudana = `null` + `Warnings`; domyślna implementacja składa go z getterów, dostawca z cache ją nadpisuje). Używają jej `InitializeAsync` i przycisk "Odśwież" w `ModelPicker` (`ReloadCommand`, komenda `ReloadModelsCommand`), a wybory użytkownika zostają. Zmiana dostawcy przechodzi przez `MainViewModel.SwitchProviderAsync` (zeruje filtry i wybory, potem ładuje listy od nowa). Modele z różnych źródeł trafiają do wspólnego `ModelOption`, a cena pochodzi osobno z `GetPricingAsync`.
- **Selektor modeli:** jeden komponent `Views/ModelPicker` (akordeon + lista + opcjonalny panel filtrów `ModelFilterBar`) używany w panelach Generowanie, Edycja, Wideo, Czat i Ulepszanie oraz w oknie Ustawień (domyślny model tekstowy dostawcy); przyjmuje `ModelOption` albo zwykłe nazwy. Filtry (`HasFilters`) działają na `MainViewModel` przez odziedziczony DataContext, a dotyczą tylko modeli generowania i edycji.
- **Okna dialogowe przez `IDialogService`** (interfejs i rekordy wyników w `ViewModels/`, implementacja `WpfDialogService` w `Views/`, `FakeDialogs` w `ImageStudio.Tests/Fakes.cs`). Zmieniając sygnaturę interfejsu, zaktualizuj fake'a. ViewModele nie wołają `MessageBox` (użyj `Confirm`) ani `Application.Current`; wątek UI przechwytuje `SynchronizationContext` w konstruktorze `MainViewModel`.
- **Okno Ustawień = wstrzykiwane zakładki** (`ISettingsTab`: `Title`, `Order`, `Apply(SettingsResult)`). `MainViewModel.OpenSettingsAsync` podaje zakładki do `IDialogService.ShowSettings`; dziś `AppSettingsTab` (ogólne) i `ProviderSettingsTab` (dostawca). `SettingsViewModel` tylko je porządkuje i składa wynik. Nowa zakładka = ViewModel implementujący `ISettingsTab` + widok + `DataTemplate` w `SettingsWindow.xaml`; brak szablonu wykrywa `SettingsWindow.EnsureTabViews` (wyjątek przed otwarciem okna).
- **Zapamiętywanie rozmiarów** to zachowanie podpinane (`Views/WindowMemory`, dane w `windows.json` przez `WindowStateStore`): `WindowMemory.Remember` jest włączone globalnie w stylu `WindowBase` (każde okno ze stylem zapamiętuje rozmiar przy zamknięciu; okno główne też położenie i maksymalizację; okna `NoResize`/`SizeToContent` pomijają rozmiar), a `WindowMemory.ColumnKey` na `ColumnDefinition` zapamiętuje szerokość kolumny po przeciągnięciu `GridSplittera` (lewy panel głównego okna). Nowe okno musi używać `Style="{StaticResource WindowBase}"`.
- **Czat i narzędzia MCP:** `ChatViewModel` + `ToolsViewModel` jako klient narzędzi MCP (`McpToolHost`, konfiguracja w `McpConfig`). Osobno `McpAppServer` (lokalny serwer MCP) wystawia sterowanie aplikacją przez `IAppControl` / `AppControlTools`, implementowane przez `MainViewModel.AppControl`.
- **Przebudowa promptu przez AI** (`IAiProvider.EnhancePromptAsync` -> `OpenAiCompat.EnhanceAsync`): prompt ma dwie osobne części, `system` (konfiguracja generowania: jak wykonać) i `concept` (oczekiwany wynik: co pokazać). Model odpowiada JSON-em o stałej strukturze `{system, concept, changes}`; OpenAI i OpenRouter dostają `response_format` (z ponowieniem bez niego, gdy endpoint go odrzuci), UberLLM nie, bo API odrzuca nieznane klucze. Poprzednie przebudowy (`PromptRevision`, ostatnie 6) są dołączane jako kontekst rozmowy; historia żyje w `PromptEdit.History` i w `MainViewModel` osobno dla zakładek Generowanie i Edycja.
- **Zestawy (presety)** to pliki JSON w katalogu użytkownika (`PresetStore`, rodzaj `generate` albo `edit`). Edytor promptu (`PromptEditorWindow`) dostaje listę zestawów i delegat zapisu jako parametry (nie zna `PresetStore`). Okno zapisu zestawu proponuje nazwę i opis przez AI (`PresetSaveViewModel`). Edytor promptu ma też "Opisz obraz (AI)" (delegat `describeImages` z `IDialogService.ShowPromptEditor`; `MainViewModel.DescribeImagesAsync` wysyła obrazy zakładki albo wskazane pliki do modelu tekstowego z obsługą obrazów).
- Motywy i style (własny szablon ComboBox, wspólne `DataTemplate` modeli) są w `App.xaml`.

## Testy

Projekt `ImageStudio.Tests` testuje głównie ViewModele z fake'ami z `Fakes.cs` (bez UI i sieci). Testy ustawiają `IMAGESTUDIO_DATA_DIR` na katalog tymczasowy (`TestEnvironment.cs`), więc nie dotykają danych użytkownika; ta zmienna nadpisuje `AppPaths.UserDataDir`. Logika w code-behind okien WPF nie jest pokryta, dlatego warto ją trzymać w ViewModelach.
