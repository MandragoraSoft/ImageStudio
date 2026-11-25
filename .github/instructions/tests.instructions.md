---
name: 'Testy ImageStudio'
description: 'Zasady testów xUnit i WPF oraz atrap usług projektu.'
applyTo: 'ImageStudio.Tests/**'
---

# Testy

- Przeczytaj `docs/dev/testing.md`. Testy ViewModeli twórz przez `TestApp` z istniejącymi atrapami i magazynami w pamięci; widoki WPF uruchamiaj przez `WpfHost.Run`.
- Dodawaj skoncentrowane testy zmienionego zachowania i istotnych przypadków brzegowych. Testy automatyczne nie korzystają z prawdziwej sieci ani danych użytkownika; płatne i chmurowe testy na żywo uruchamiaj tylko za wyraźną zgodą.
- `ICollectionView` i jego kolekcję zmieniaj na wątku, na którym powstały. Domyślne metody interfejsu dostawcy wywołuj przez `IAiProvider`.
- `RelayCommand.Execute` nie sprawdza `CanExecute`: zabezpieczenia operacji testuj także przez bezpośrednie wykonanie komendy.
- W testach lokalnego serwera pobieraj port przez `TestPorts.Free()`. Po czasowej zmianie języka przywróć `Loc` do polskiego.
- Uruchamiaj najmniejszy zestaw obejmujący zmianę, np. `dotnet test ImageStudio.Tests --filter "FullyQualifiedName~NazwaKlasy"`.
