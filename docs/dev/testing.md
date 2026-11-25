# Testy

_Kiedy czytać: piszesz albo poprawiasz testy w `ImageStudio.Tests`._

Projekt `ImageStudio.Tests` testuje ViewModele na tym samym kontenerze co aplikacja: `TestApp.Create()` składa `AddImageStudioCore()` z atrapami (`FakeProvider`, `FakeDialogs`, `FakeThemeService`, `FakeProviderFactory`) i magazynami w pamięci (`Memory*Store`; rozmowy edytora: `app.PromptChatSessions`), a `app.Vm` / `app.Get<T>()` zwracają usługi (bez UI i sieci). Konkretny magazyn lub atrapę podmienia się w `TestApp.Create(services => ...)`. Testy z oknami WPF idą przez `WpfHost.Run`. Testy ustawiają `IMAGESTUDIO_DATA_DIR` na katalog tymczasowy (`TestEnvironment.cs`), więc nie dotykają danych użytkownika; ta zmienna nadpisuje `AppPaths.UserDataDir`. Logika w code-behind okien WPF nie jest pokryta, dlatego warto ją trzymać w ViewModelach. Testy rozmów edytora (`PromptChatHistoryTests`) obejmują tryby zapisu, odtworzenie w nowym kontenerze na wspólnych magazynach, niezależność kluczy generate/edit/video, Stop po wykonanym narzędziu oraz zamknięcie okna w trakcie odpowiedzi przez `WpfHost.RunAsync`.

## Zasady z praktyki

- Widoki modeli (`ICollectionView`) zmieniaj w testach tylko z wątku, który je utworzył: po `await` na innym wątku kolekcja rzuci wyjątek. Dlatego nie wstawiaj `Task.Yield` przed zmianą kolekcji.
- Zdarzenia sprzętu (nagrywanie, koniec odtwarzania) wracają przez `SynchronizationContext.Post`, więc testy czekają na skutek (wzór: `Until` w `VoiceViewModelTests`), a nie sprawdzają od razu.
- Domyślne metody interfejsu (`IAiProvider.ReloadAsync`, metody dźwięku) są widoczne tylko przez typ interfejsu: rzutuj dostawcę na `IAiProvider`.
- `RelayCommand.Execute` nie sprawdza `CanExecute`: ochronę (np. przed kasowaniem cudzego pliku) testuj wywołaniem `Execute`, nie tylko `CanExecute`.
- Testy z prawdziwym serwerem lokalnym biorą wolny port przez `TestPorts.Free()` (losowe porty bywały flaky).

## Testy na żywo (chmury)

`CloudLiveTests` (atrybut `LiveCloudFact`) robi CRUD na prawdziwych kontach OneDrive, Google Drive i MEGA przykładowymi plikami (PNG, MP3, MP4: zapis, odczyt po skasowaniu kopii lokalnej, aktualizacja tej samej nazwy, usunięcie) i sprząta po sobie. Test MEGA (`Mega_create_read_update_delete_with_sample_files`) to jedyne pokrycie prawdziwego adaptera `MegaApiClientAdapter`; logowanie MEGA (2FA, kod `-26`, węzeł `Root`, gzip) nie było jeszcze sprawdzone na prawdziwym koncie, więc przy pierwszym uruchomieniu patrz na wynik i na komunikaty błędów. Biorą sekrety z prawdziwego katalogu użytkownika, więc najpierw zaloguj się w Ustawieniach → Zapis. W zwykłym przebiegu są pominięte; uruchomienie: `$env:IMAGESTUDIO_LIVE_CLOUD = "1"; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudLiveTests"`. Atrapy HTTP dla OneDrive i Google Drive: `CloudStorageTests`. Kolejka trwała, wznowienia i wysyłka w tle lustra: `CloudMirrorBaseTests` (bez sieci). MEGA bez sieci: `MegaStorageTests` na atrapie `IMegaClient` (logowanie, 2FA, sesja w sekretach, brak hasła w konfiguracji); nie sprawdza protokołu MEGA.

## Testy na żywo (płatne, domyślnie pomijane)

- `LiveGuiE2ETests`: `IMAGESTUDIO_LIVE_GUI=1` i `UBERLLM_API_KEY`. Cały przebieg na wątku UI (`WpfHost.RunAsync`) z prawdziwym `MainWindow`, prawdziwym dostawcą i modelem czatu obsługującym narzędzia: obraz załączony w czacie, model woła `imagestudio__edit_image`, wynik w galerii (kolor, proporcje 3:4), okno pokazuje rozmowę i galerię. Opcjonalnie `IMAGESTUDIO_E2E_CHAT` (domyślnie `anthropic/claude-haiku-5.5`), `IMAGESTUDIO_E2E_EDIT` (`venice/grok-imagine-edit`) i `IMAGESTUDIO_E2E_OUT` (katalog na zrzuty okna). Koszt jednego przebiegu to ok. 0,03 USD. Klucz z `SecretStore` można podać bez wypisywania: w PowerShellu odszyfruj `secrets.bin` (`ProtectedData`, `CurrentUser`) do `$env:UBERLLM_API_KEY`.
- Galeria w oknie jest w zwiniętym `Expanderze`, więc w teście trzeba ją rozwinąć (`IsExpanded = true`), zanim `ListBox` pojawi się w drzewie wizualnym. Kolekcje i `ICollectionView` muszą powstać na wątku UI, więc test z oknem tworzy `TestApp` wewnątrz `WpfHost.RunAsync` i zwalnia go na zewnątrz.
