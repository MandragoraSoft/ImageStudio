---
name: release
description: Wydawanie nowej wersji ImageStudio (tag, wydanie GitHub, instalator i dwa exe przez pipeline). Użyj, gdy użytkownik prosi o wydanie, opublikowanie nowej wersji, release, tag albo uzupełnienie opisu wydania. Opisuje notatki wydania, numerację i uruchomienie pipeline.
---

# Wydanie nowej wersji

Pełny opis procesu: `docs/RELEASING.md`. Pipeline: `.github/workflows/release.yml`. Ten skill to skrót dla agenta.

**Wydanie jest widoczne publicznie dla użytkowników i trudne do wycofania.** Zanim wypchniesz commity i uruchomisz pipeline, potwierdź z użytkownikiem numer wersji i treść notatek, chyba że wprost kazał wydać bez pytania.

## Kroki

1. **Ustal numer** (semver): PATCH = poprawki, MINOR = nowa funkcja, MAJOR = zmiana łamiąca dane użytkownika. Przyrostek (`1.2.0-beta.1`) tworzy wydanie wstępne. Ostatnia wersja: `gh release list -L1`.
2. **Zbierz zmiany od ostatniego tagu**: `git log --oneline <ostatni-tag>..HEAD` (i czytaj commity, nie tylko tytuły, jeśli są niejasne).
3. **Napisz notatki** w `docs/release-notes/<wersja>.md`, po polsku, dla użytkownika, nie dla programisty. Struktura jak w `docs/release-notes/1.1.0.md`: co nowego, poprawki, znane ograniczenia, który plik pobrać (instalator, samodzielny exe, exe wymagający .NET). Nie opisuj rzeczy, których nie sprawdzono, jako działających: zaznacz, co jest niezweryfikowane.
4. **Testy lokalnie**: `dotnet test ImageStudio.Tests` (przy uruchomionej aplikacji użyj `-p:BaseOutputPath=<katalog tymczasowy>`).
5. **Commit i push na `main`** (notatki idą razem ze zmianami): pipeline wydaje tylko z `main` albo z tagu `v*`.
6. **Uruchom**: `gh workflow run release.yml --ref main -f version=<wersja>`, potem `gh run watch <id> --exit-status`.
7. **Sprawdź**: `gh release view v<wersja> --json url,assets` (powinny być trzy pliki: `Setup.exe`, `win-x64-samodzielna.exe`, `win-x64-wymaga-dotnet.exe`) i podaj użytkownikowi link.

## Gdy pipeline się wywróci

- Brak `docs/release-notes/<wersja>.md`, zła wersja albo wersja już istniejąca: przebieg kończy się przed budowaniem; popraw i uruchom ponownie.
- Padły testy: wydanie nie powstaje; napraw i wypchnij, uruchom ponownie z tą samą wersją.
- Wydanie istnieje, ale jest złe: nie nadpisuj, wydaj kolejny PATCH. Usunięcie: `gh release delete v<wersja> --cleanup-tag` (tylko za zgodą użytkownika).
- Poprawa samego opisu: zmień plik w repozytorium i `gh release edit v<wersja> --notes-file docs/release-notes/<wersja>.md`.
