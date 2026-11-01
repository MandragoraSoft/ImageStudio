---
description: Wydanie ImageStudio krok po kroku (kontrola, notatki, commit, pipeline, weryfikacja). Argument: numer wersji albo pusty (zaproponuję).
argument-hint: "[wersja] [bez-pytania]"
---

Przeprowadź wydanie ImageStudio według skilla `release` i `docs/RELEASING.md`. Argumenty: `$ARGUMENTS`.

1. **Stan repozytorium:** `git status --short`, gałąź musi być `main`; `gh release list -L3` i `git log --oneline <ostatni-tag>..HEAD`. Gdy nie ma nic nowego od ostatniego tagu, powiedz to i zakończ.
2. **Wersja:** weź z argumentów; bez niej zaproponuj (PATCH = poprawki, MINOR = nowa funkcja, MAJOR = zmiana łamiąca dane) i uzasadnij w jednym zdaniu.
3. **Kontrole lokalne:** `pwsh tools/Check-Translations.ps1 -Strict` i `dotnet test ImageStudio.Tests` (przy uruchomionej aplikacji `-p:BaseOutputPath=<katalog tymczasowy>`). Przy błędzie zatrzymaj się i pokaż go.
4. **Notatki:** napisz `docs/release-notes/<wersja>.md` po polsku, dla użytkownika (struktura jak w poprzednich), z sekcją „Który plik pobrać”. Nie opisuj niesprawdzonego jako działającego.
5. **Potwierdzenie:** pokaż wersję i notatki i zapytaj o zgodę, chyba że w argumentach jest `bez-pytania`. Wydanie jest publiczne i trudne do wycofania.
6. **Publikacja:** commit notatek (z resztą niezacommitowanych zmian tylko za zgodą), `git push`, `gh workflow run release.yml --ref main -f version=<wersja>`, `gh run watch <id> --exit-status`.
7. **Weryfikacja:** `gh release view v<wersja> --json url,assets`; mają być trzy pliki (Setup, samodzielna, wymaga-dotnet). Podaj link. Gdy pipeline padł: pokaż `gh run view <id> --log-failed`, napraw przyczynę i uruchom ponownie z tą samą wersją (wydanie, które powstało błędne, nie jest nadpisywane: wydaj kolejny PATCH).
