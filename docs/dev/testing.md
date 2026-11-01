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
