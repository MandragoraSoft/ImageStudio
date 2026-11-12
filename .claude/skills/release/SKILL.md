---
name: release
description: "Wydawanie nowej wersji ImageStudio: numer semver, notatki, testy, commit, push, GitHub Actions, tag i pliki exe. Użyj przy prośbie o release, publikację, wydanie wersji lub poprawę opisu wydania."
---

# Wydanie nowej wersji

Pełny opis procesu: `docs/RELEASING.md`. Pipeline: `.github/workflows/release.yml`. Skrypt wykonujący mechaniczne kroki: [tools/Release.ps1](../../../tools/Release.ps1).

**Wydanie jest publiczne i trudne do wycofania.** Nie wypychaj commitów ani nie uruchamiaj pipeline'u bez potwierdzenia przez użytkownika numeru wersji i pełnej treści notatek, chyba że użytkownik wyraźnie zlecił publikację bez dodatkowego pytania. Jeśli wybór wersji lub opis nie są podane, zaproponuj numer i pokaż gotowe notatki w formularzu potwierdzenia.

## Przygotowanie

1. Ustal ostatnie wydanie przez `gh release list -L1`. Semver: PATCH = poprawki, MINOR = nowe funkcje, MAJOR = zmiana łamiąca dane użytkownika; przyrostek, np. `1.6.0-beta.1`, tworzy wydanie wstępne.
2. Przejrzyj zmiany od ostatniego tagu: `git log --oneline <ostatni-tag>..main`. Czytaj diff/commity, jeśli sam tytuł nie wystarcza do rzetelnego opisu. Uwzględnij testy i znane ograniczenia; nie przedstawiaj niezweryfikowanych rzeczy jako sprawdzonych.
3. Przygotuj `docs/release-notes/<wersja>.md` po polsku, dla użytkowników. Wzoruj się na `docs/release-notes/1.5.0.md`; uwzględnij nowości lub poprawki, znane ograniczenia oraz trzy dokładne nazwy plików do pobrania:
   - `ImageStudio-<wersja>-Setup.exe`
   - `ImageStudio-<wersja>-win-x64-samodzielna.exe`
   - `ImageStudio-<wersja>-win-x64-wymaga-dotnet.exe`
4. Pokaż użytkownikowi numer wersji i pełną treść notatek do zatwierdzenia. Dopiero po akceptacji przejdź do publikacji.

## Publikacja

1. Upewnij się, że `main` zawiera zmiany przeznaczone do wydania. Skrypt wymaga repozytorium `prachwal/uberllm-proxy`, zsynchronizowanego `main` bez obcych zmian (dopuszcza wyłącznie zmiany w pliku notatek tej wersji albo pojedynczy lokalny commit zawierający tylko te notatki) oraz braku wcześniejszego wydania/tagu o tej nazwie.
2. Uruchom z katalogu repozytorium:
   ```powershell
   pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0
   ```
   Skrypt sprawdza notatki, uwierzytelnienie GitHub i synchronizację gałęzi, uruchamia kontrolę tłumaczeń, Release build i testy. Następnie pokazuje notatki i prosi o potwierdzenie przez `ShouldProcess`. Po akceptacji zatwierdza same notatki (z wymaganym trailerem Co-authored-by, jeśli nie są jeszcze zatwierdzone), wypycha `main`, uruchamia `release.yml`, czeka na wynik i sprawdza tag oraz dokładnie trzy pliki wydania.
3. `-WhatIf` wykonuje kontrole i testy, ale pomija commit, push i publikację:
   ```powershell
   pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0 -WhatIf
   ```
4. Po sukcesie podaj użytkownikowi link do GitHub Release, wynik pipeline'u i nazwy plików. Nie twierdź, że instalator został ręcznie przetestowany na czystym Windowsie — pipeline tego nie robi.

Skrypt wymaga Windows, PowerShell 7, Git, .NET SDK 10, GitHub CLI `gh` zalogowanego do `github.com` oraz gałęzi `main` zsynchronizowanej z `origin/main`.

## Gdy pipeline się wywróci

- Błąd walidacji przed publikacją: popraw wskazany problem; nie obchodź kontroli czystości drzewa ani istniejących tagów.
- Testy lub build nie przechodzą: nie publikuj. Napraw problem i ponów proces po ponownej weryfikacji notatek.
- Uruchomienie pipeline'u zakończyło się błędem: sprawdź logi wskazanego runu. Jeśli wydanie ani tag nie powstały, po naprawie można uruchomić pipeline ponownie z tą samą wersją.
- Wydanie lub tag już istnieje: nie nadpisuj. Użyj nowej wersji. Kasowanie wydania/tagu (`gh release delete v<wersja> --cleanup-tag`) wymaga osobnej, jawnej zgody użytkownika.
- Poprawa samego opisu istniejącego wydania: po zmianie pliku użyj `gh release edit v<wersja> --notes-file docs/release-notes/<wersja>.md`; potwierdź z użytkownikiem treść przed zmianą publicznego opisu.

Ręczne uruchomienie pipeline'u (bez skryptu) jest możliwe tylko z `main`: `gh workflow run release.yml --ref main -f version=<wersja>`. Po nim użyj `gh run watch <id> --exit-status`, a następnie sprawdź `gh release view v<wersja> --json url,assets`.
