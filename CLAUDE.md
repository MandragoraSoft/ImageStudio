# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projekt

ImageStudio: aplikacja WPF (.NET 10, `net10.0-windows`, x64) do generowania i edycji obrazów, wideo, czatu oraz mowy (synteza i rozpoznawanie) z modelami AI. Kod, komentarze i UI są po polsku; trzymaj ten język. Repozytorium nazywa się `uberllm-proxy` z historycznych powodów. `NOTES.md` opisuje dawne proxy Node (`proxy.mjs`), którego już nie ma w repo, więc nie traktuj go jako opisu obecnego kodu.

## Komendy

```powershell
dotnet build ImageStudio.slnx
dotnet test ImageStudio.Tests                       # xUnit, całość
dotnet test ImageStudio.Tests --filter "FullyQualifiedName~PromptEditorTests"   # jedna klasa/test
dotnet run --project ImageStudio                    # Debug otwiera też konsolę z logami (OutputType Exe)
dotnet publish ImageStudio -p:PublishProfile=Portable    # publish\portable\ImageStudio.exe: jeden plik, wymaga .NET 10
dotnet publish ImageStudio -p:PublishProfile=SelfHosted  # publish\selfhosted\ImageStudio.exe: jeden plik z wbudowanym .NET
```

- Przy dziwnych błędach brakujących plików `obj` (np. `BG1002`, `CS2001`) usuń `bin`/`obj` i zbuduj z `-m:1`; równoległy build kilku projektów XAML bywa wyścigiem.
- Gdy aplikacja jest uruchomiona, jej pliki są zablokowane: buduj do osobnego katalogu (`-p:BaseOutputPath=<katalog>`) i nie zabijaj procesu użytkownika.
- Wersje pakietów są centralne (`Directory.Packages.props`), a TFM, `UseWPF`, nullable i `PlatformTarget` w `Directory.Build.props`. W csproj dodawaj `PackageReference` bez `Version`. Nie ma lintera.
- Profile publikacji ustawiają `PluginsInBundle`: dostawcy i zakładki wchodzą do exe zamiast do `providers\`/`tabs\`, a `appsettings.json` jest zasobem wbudowanym.
- Wydanie: skill `release`, opis procesu w [docs/RELEASING.md](docs/RELEASING.md); pipeline `.github/workflows/release.yml` wymaga `docs/release-notes/<wersja>.md`.

## Zasady, które obowiązują zawsze

- **DI:** jedyny composition root to `Composition/ServiceCollectionExtensions` (`AddImageStudioCore` i `AddImageStudioInfrastructure`). Dane użytkownika są za interfejsami z `Configuration/Stores.cs`; w ViewModelach nie wołaj statycznych `PresetStore`/`GalleryStore`/`SecretStore`/`AppSettings.Load` ani `ThemeService`.
- **MVVM (CommunityToolkit.Mvvm):** `MainViewModel` jest `partial`, nowa funkcja głównego okna = nowy plik partial. ViewModele nie wołają `MessageBox` (użyj `IDialogService.Confirm`) ani `Application.Current`. Zmieniając `IDialogService`, zaktualizuj `FakeDialogs`.
- **Dostawcy tylko przez `IAiProvider`.** Nowa metoda kontraktu: domyślna implementacja + jawne przekazanie w `ActivityAiProvider` + `FakeProvider` (skill `new-provider`). Domyślne metody interfejsu widać tylko przez typ interfejsu.
- **Sieć poza `IAiProvider`** (pobranie obrazu z linku, narzędzia MCP) sama woła `Activity.Begin("...")` w `using`, żeby stopka pokazała spinner.
- **Selektor modeli to zawsze `ModelPicker` z `HasFilters="True"`** i `ICollectionView` z filtrem z `MainViewModel`. `ComboBox` z obiektami używa `ItemTemplate`, nie `DisplayMemberPath` (własny szablon w `App.xaml` go ignoruje).
- **Nowy kolor kontrolki** dodawaj do obu palet (`ThemeService`, `App.xaml`); kontrast pilnuje `ThemeContrastTests`.
- **Nowe okno** musi mieć `Style="{StaticResource WindowBase}"` (zapamiętywanie rozmiaru).
- **XAML między projektami** wymaga `;assembly=...` (np. `ImageStudio.Providers.Core`, `ImageStudio.Shared`); dostawcy i zakładki mają referencje `Private="$(PluginsInBundle)"`.
- **Klucze API** są szyfrowane przez `SecretStore` (DPAPI); nie zapisuj ich do plików ustawień ani logów.
- **Testy:** nowy kod = testy w `ImageStudio.Tests` na `TestApp` z atrapami (bez sieci i UI); widoki WPF przez `WpfHost.Run`. Szczegóły i pułapki w [docs/dev/testing.md](docs/dev/testing.md).

## Gdzie czytać dalej

Pliki w `docs/dev/` czytaj na żądanie, gdy zadanie dotyka danego obszaru (to zwykłe odnośniki, nie importy `@`, więc nie zajmują kontekstu, dopóki ich nie otworzysz).

| Obszar | Plik |
|---|---|
| DI, MVVM, dialogi, magazyny danych | [docs/dev/architecture.md](docs/dev/architecture.md) |
| Menu, stopka, spinner, motywy i kolory, `ModelPicker`, filtry, ceny w listach, rozmiary okien | [docs/dev/ui-shell.md](docs/dev/ui-shell.md) |
| Zakładki lewego panelu (`IMainTab`), okno Ustawień (`ISettingsTab`) | [docs/dev/tabs-and-settings.md](docs/dev/tabs-and-settings.md) |
| `IAiProvider`, dostawcy, katalog i `ReloadAsync`, żądanie wideo UberLLM, kontrakt dźwięku | [docs/dev/providers.md](docs/dev/providers.md) |
| Przebudowa promptu przez AI, edytor promptu, zestawy (presety) | [docs/dev/prompts-and-presets.md](docs/dev/prompts-and-presets.md) |
| Czat: strumień, sesje, Stop, tokeny, rozumowanie, MCP | [docs/dev/chat.md](docs/dev/chat.md) |
| Galeria obrazów i wideo, miniatury, zakładka Głos (mowa, mikrofon, galeria głosu) | [docs/dev/media-gallery-voice.md](docs/dev/media-gallery-voice.md) |
| Testy: `TestApp`, atrapy, pułapki | [docs/dev/testing.md](docs/dev/testing.md) |
| Wydawanie wersji | [docs/RELEASING.md](docs/RELEASING.md) |
| Starszy opis architektury (może być nieaktualny) | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |

## Skille projektu (`.claude/skills/`)

- **`new-main-tab`**: nowa zakładka okna głównego (`IMainTab`) albo okna Ustawień (`ISettingsTab`), z listą kontrolną (filtry modeli, `DataTemplate`, indeksy, testy).
- **`new-provider`**: nowy dostawca AI albo nowa metoda w kontrakcie `IAiProvider`.
- **`release`**: wydanie nowej wersji (notatki, numeracja, pipeline, naprawa).

Gdy zadanie pasuje do skilla, użyj go zamiast przepisywać procedurę z pamięci. Nowy obszar z własnymi regułami: dopisz plik do `docs/dev/` i wiersz do tabeli wyżej, a procedury, które się powtarzają, zamieniaj w skill; nie rozbudowuj tego pliku.
