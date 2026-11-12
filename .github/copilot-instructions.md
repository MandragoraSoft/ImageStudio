# ImageStudio — instrukcje projektu

ImageStudio to aplikacja WPF dla Windows (`net10.0-windows`, x64) do pracy z modelami AI: obrazami, wideo, czatem i mową. Nazwa repozytorium jest historyczna; `NOTES.md` opisuje usunięte proxy Node, nie bieżącą aplikację.

## Praca w repozytorium

- Odpowiadaj po polsku; kod i komentarze dostosuj do istniejących wzorców projektu. Teksty interfejsu lokalizuj w PL i EN.
- Przed zmianami sprawdź istniejącą implementację i dokumentację danego obszaru. Zachowuj cudze zmiany; nie cofaj ich ani nie nadpisuj bez zgody.
- Wprowadzaj skupione zmiany, aktualizuj bezpośrednio powiązaną dokumentację i dodawaj testy zmienionego zachowania. Nie ukrywaj błędów ani nie zapisuj sekretów w kodzie, ustawieniach lub logach.
- Dla nowych funkcji i większych zmian stosuj proces opisany w [CLAUDE.md](../CLAUDE.md): rozpoznanie, plan i etapowa implementacja z subagentami, jeśli środowisko je obsługuje. Używaj odpowiednich istniejących skilli projektu, jeśli są dostępne; w przeciwnym razie przeczytaj wskazaną dokumentację.
- Utrzymuj te instrukcje i [CLAUDE.md](../CLAUDE.md) spójne, gdy zmieniają się wspólne zasady projektu.

## Architektura

- Jedyny composition root to `ImageStudio/Composition/ServiceCollectionExtensions`: `AddImageStudioCore` i `AddImageStudioInfrastructure`. Dostęp do danych użytkownika prowadź przez interfejsy z `ImageStudio/Configuration/Stores.cs`; ViewModele nie korzystają bezpośrednio ze statycznych magazynów, `AppSettings.Load` ani `ThemeService`.
- Stosuj MVVM z CommunityToolkit.Mvvm. `MainViewModel` jest `partial`: nową funkcję głównego okna umieść w osobnym pliku partial. ViewModele nie wywołują `MessageBox` ani `Application.Current`; dialogi prowadź przez `IDialogService`, a zmieniając jego kontrakt, aktualizuj `FakeDialogs`.
- Dostawców AI wywołuj przez `IAiProvider`. Nową metodę kontraktu wyposaż w implementację domyślną, jawne przekazanie w `ActivityAiProvider` oraz obsługę w `FakeProvider`. Zachowuj nieznane możliwości modeli jako nieznane, zamiast zakładać wsparcie.
- Zapis wyników prowadź przez `IMediaStorage` i aktywny `MediaStorageHost`, a sekrety przez `ISecretStore`/`SecretStore` (DPAPI). Nie zakładaj, że lokalizacja pliku w galerii jest zawsze lokalną ścieżką.
- Ruch HTTP w aplikacji podłączaj do `OutgoingRequestRegistry` przez osobny handler dla każdego klienta; propaguj anulowanie. Operacje sieciowe poza `IAiProvider` zgłaszaj także przez `Activity.Begin(...)`, aby działał wskaźnik w stopce. Nie ujawniaj adresów ze ścieżkami, parametrów ani nagłówków w liście żądań.

## Budowanie i sprawdzanie

```powershell
dotnet build ImageStudio.slnx
dotnet test ImageStudio.Tests
dotnet test ImageStudio.Tests --filter "FullyQualifiedName~PromptEditorTests"
dotnet run --project ImageStudio
pwsh -NoProfile -File tools/Check-Translations.ps1 -Strict
```

- Wersje pakietów utrzymuj w `Directory.Packages.props`; `PackageReference` w projektach dodawaj bez `Version`. Wspólne właściwości i wersja aplikacji są w `Directory.Build.props`.
- Gdy uruchomiona aplikacja blokuje pliki wyjściowe, użyj osobnego `-p:BaseOutputPath=<katalog>`; nie zamykaj procesu użytkownika. Przy wyścigach kompilacji XAML stosuj `-m:1`; nie usuwaj katalogów bez upewnienia się, że są właściwym celem.
- Testy wykorzystują `TestApp` i atrapy; widoki WPF testuj przez `WpfHost.Run`. Nie uruchamiaj płatnych ani chmurowych testów na żywo bez wyraźnej zgody.
- Publikacja i wydanie wymagają procedury z [docs/RELEASING.md](../docs/RELEASING.md) oraz skilla `release`, jeśli jest dostępny. Nie publikuj bez wymaganych potwierdzeń.

## Dokumentacja zależna od zadania

Przed pracą w danym obszarze przeczytaj właściwy dokument; same odnośniki nie dołączają automatycznie jego treści:

| Obszar | Dokument |
|---|---|
| DI, MVVM, dialogi i magazyny | [architecture.md](../docs/dev/architecture.md) |
| Okno główne, stopka, selektory i motywy | [ui-shell.md](../docs/dev/ui-shell.md) |
| Zakładki i Ustawienia | [tabs-and-settings.md](../docs/dev/tabs-and-settings.md) |
| Dostawcy i kontrakty | [providers.md](../docs/dev/providers.md) |
| Prompty i presety | [prompts-and-presets.md](../docs/dev/prompts-and-presets.md) |
| Czat i narzędzia | [chat.md](../docs/dev/chat.md) |
| Galeria, multimedia i głos | [media-gallery-voice.md](../docs/dev/media-gallery-voice.md) |
| Lokalizacja | [localization.md](../docs/dev/localization.md) |
| Testy | [testing.md](../docs/dev/testing.md) |
