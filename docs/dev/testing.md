# Testy

_Kiedy czytać: piszesz albo poprawiasz testy w `ImageStudio.Tests`._

Projekt `ImageStudio.Tests` testuje ViewModele na tym samym kontenerze co aplikacja: `TestApp.Create()` składa `AddImageStudioCore()` z atrapami (`FakeProvider`, `FakeDialogs`, `FakeThemeService`, `FakeProviderFactory`) i magazynami w pamięci (`Memory*Store`), a `app.Vm` / `app.Get<T>()` zwracają usługi (bez UI i sieci). Konkretny magazyn lub atrapę podmienia się w `TestApp.Create(services => ...)`. Testy z oknami WPF idą przez `WpfHost.Run`. Testy ustawiają `IMAGESTUDIO_DATA_DIR` na katalog tymczasowy (`TestEnvironment.cs`), więc nie dotykają danych użytkownika; ta zmienna nadpisuje `AppPaths.UserDataDir`. Logika w code-behind okien WPF nie jest pokryta, dlatego warto ją trzymać w ViewModelach.

## Zasady z praktyki

- Widoki modeli (`ICollectionView`) zmieniaj w testach tylko z wątku, który je utworzył: po `await` na innym wątku kolekcja rzuci wyjątek. Dlatego nie wstawiaj `Task.Yield` przed zmianą kolekcji.
- Zdarzenia sprzętu (nagrywanie, koniec odtwarzania) wracają przez `SynchronizationContext.Post`, więc testy czekają na skutek (wzór: `Until` w `VoiceViewModelTests`), a nie sprawdzają od razu.
- Domyślne metody interfejsu (`IAiProvider.ReloadAsync`, metody dźwięku) są widoczne tylko przez typ interfejsu: rzutuj dostawcę na `IAiProvider`.
- `RelayCommand.Execute` nie sprawdza `CanExecute`: ochronę (np. przed kasowaniem cudzego pliku) testuj wywołaniem `Execute`, nie tylko `CanExecute`.
- Testy z prawdziwym serwerem lokalnym biorą wolny port przez `TestPorts.Free()` (losowe porty bywały flaky).
- Gdy aplikacja jest uruchomiona, `bin` jest zablokowany: buduj do osobnego katalogu (`-p:BaseOutputPath=<katalog>`).

## Testy na żywo (chmury)

`CloudLiveTests` (atrybut `LiveCloudFact`) robi CRUD na prawdziwych kontach OneDrive i Google Drive przykładowymi plikami (PNG, MP3, MP4: zapis, odczyt po skasowaniu kopii lokalnej, aktualizacja tej samej nazwy, usunięcie) i sprząta po sobie. Biorą sekrety z prawdziwego katalogu użytkownika, więc najpierw zaloguj się w Ustawieniach → Zapis. W zwykłym przebiegu są pominięte; uruchomienie: `$env:IMAGESTUDIO_LIVE_CLOUD = "1"; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudLiveTests"`. Atrapy HTTP dla tych samych wtyczek: `CloudStorageTests`.

## Testy na żywo (płatne, domyślnie pomijane)

- `CloudLiveTests`: `IMAGESTUDIO_LIVE_CLOUD=1` (konta chmurowe zalogowane w aplikacji).
- `LiveGuiE2ETests`: `IMAGESTUDIO_LIVE_GUI=1` i `UBERLLM_API_KEY`. Cały przebieg na wątku UI (`WpfHost.RunAsync`) z prawdziwym `MainWindow`, prawdziwym dostawcą i modelem czatu obsługującym narzędzia: obraz załączony w czacie, model woła `imagestudio__edit_image`, wynik w galerii (kolor, proporcje 3:4), okno pokazuje rozmowę i galerię. Opcjonalnie `IMAGESTUDIO_E2E_CHAT` (domyślnie `anthropic/claude-haiku-5.5`), `IMAGESTUDIO_E2E_EDIT` (`venice/grok-imagine-edit`) i `IMAGESTUDIO_E2E_OUT` (katalog na zrzuty okna). Koszt jednego przebiegu to ok. 0,03 USD. Klucz z `SecretStore` można podać bez wypisywania: w PowerShellu odszyfruj `secrets.bin` (`ProtectedData`, `CurrentUser`) do `$env:UBERLLM_API_KEY`.
- Galeria w oknie jest w zwiniętym `Expanderze`, więc w teście trzeba ją rozwinąć (`IsExpanded = true`), zanim `ListBox` pojawi się w drzewie wizualnym. Kolekcje i `ICollectionView` muszą powstać na wątku UI, więc test z oknem tworzy `TestApp` wewnątrz `WpfHost.RunAsync` i zwalnia go na zewnątrz.
