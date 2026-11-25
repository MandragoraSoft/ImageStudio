# Wydawanie nowej wersji

Wydanie buduje pipeline `.github/workflows/release.yml` (GitHub Actions, `windows-latest`). Przygotuj opis wersji i podaj numer; mechaniczne kroki można wykonać jednym skryptem.

## Numer wersji

Semantyczny: `MAJOR.MINOR.PATCH`.

- **PATCH** (1.1.0 → 1.1.1): poprawki bez nowych funkcji.
- **MINOR** (1.1.0 → 1.2.0): nowa funkcja, np. zakładka.
- **MAJOR** (1.x → 2.0.0): zmiany łamiące (format danych użytkownika, układ plików).
- Przyrostek, np. `1.2.0-beta.1`, tworzy **wydanie wstępne**: nie jest oznaczone jako „najnowsze”, więc zwykli użytkownicy go nie dostaną.

Numer trafia do plików exe i instalatora; ręcznie nigdzie go nie wpisujesz. Wartość w `Directory.Build.props` to tylko domyślna dla lokalnych buildów; pipeline podaje właściwą przez `-p:Version`.

## Notatki wydania

Plik `docs/release-notes/<wersja>.md`, po polsku, dla użytkownika. Wzór: `docs/release-notes/1.8.0.md`. `tools/Release.ps1` wymaga:

- tytułu `# ImageStudio <wersja>` (opcjonalnie `: krótki opis`),
- sekcji `## Nowe` lub `## Poprawki`, `## Znane ograniczenia` i `## Który plik pobrać`,
- dokładnych nazw trzech plików z sekcji „Pliki w wydaniu”.

## Automatyczny skrypt

Przygotuj najpierw notatki wydania, a następnie uruchom na Windowsie z katalogu repozytorium:

```powershell
pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0
```

Skrypt sprawdza wersję i notatki, uwierzytelnienie `gh`, gałąź `main`, synchronizację z `origin/main`, istniejące tagi/wydania i zmiany w drzewie. Dopuszcza wyłącznie zmiany w pliku notatek tej wersji albo pojedynczy lokalny commit zawierający tylko te notatki. Uruchamia kontrolę tłumaczeń, build Release i wszystkie testy. Potem wyświetla notatki i prosi o potwierdzenie; po akceptacji zatwierdza same notatki (jeśli nie są jeszcze zatwierdzone), wypycha `main`, uruchamia pipeline, czeka na jego wynik i sprawdza, czy wydanie zawiera dokładnie trzy pliki.

Skrypt nie tworzy automatycznie tekstu notatek — opis funkcji i ograniczeń musi zostać sprawdzony i zaakceptowany. Wymaga PowerShell 7, Git, .NET SDK 10, `gh` zalogowanego do `github.com`, gałęzi `main` i czystego drzewa roboczego poza plikiem notatek danej wersji. Nie publikuje, gdy wersja/tag już istnieje.

Podgląd z pełnymi kontrolami i testami lokalnymi, bez commitu, push ani publikacji:

```powershell
pwsh -NoProfile -File tools/Release.ps1 -Version 1.6.0 -WhatIf
```

## Kroki ręczne

1. **Napisz notatki wydania** (sekcja „Notatki wydania” wyżej).
2. **Zatwierdź i wypchnij na `main`** (razem ze zmianami, które wchodzą do wydania). Testy lokalnie: `dotnet test ImageStudio.Tests`.
3. **Uruchom wydanie**, jedną z dróg:
   - polecenie: `gh workflow run release.yml --ref main -f version=1.2.0`,
   - albo w GitHubie: Actions → Release → Run workflow → pole „version”,
   - albo wypchnij własny tag: `git tag v1.2.0 && git push origin v1.2.0` (opis wersji musi być już w tym commicie).
4. **Obserwuj przebieg**: `gh run watch`. Podsumowanie przebiegu zawiera link do wydania i listę plików.
5. **Sprawdź wydanie**: https://github.com/MandragoraSoft/ImageStudio/releases/latest. Zainstaluj nowy `Setup.exe` na swoim komputerze nad poprzednią wersją i sprawdź, że ustawienia i dane zostały.

## Co robi pipeline

| Krok | Po co | Gdy się nie uda |
|---|---|---|
| Wersja | sprawdza format (`1.2.0` albo `1.2.0-beta.1`) | przebieg kończy się od razu |
| Wersja nie może już istnieć | wydanie ani tag o tej nazwie nie mogą istnieć | przebieg kończy się od razu, bez nadpisywania starego wydania |
| Notatki wydania | wymaga niepustego `docs/release-notes/<wersja>.md` | przebieg kończy się przed budowaniem |
| Build i testy | budowanie rozwiązania i wszystkie testy | **wydanie nie powstaje** |
| Publikacja i paczki | dwa exe w jednym pliku i instalator Inno Setup | wydanie nie powstaje |
| Artefakt przebiegu | te same pliki w zakładce przebiegu | |
| Wydanie | tag, wydanie z treścią z pliku i trzema plikami | |

Ręczne uruchomienie działa tylko z gałęzi `main`. Dwa wydania naraz ustawiają się w kolejce.

## Pliki w wydaniu

- `ImageStudio-<wersja>-Setup.exe`: instalator dla bieżącego użytkownika (bez uprawnień administratora), aktualizuje poprzednią wersję w miejscu. Zalecany.
- `ImageStudio-<wersja>-win-x64-samodzielna.exe`: jeden plik z wbudowanym .NET, bez instalacji.
- `ImageStudio-<wersja>-win-x64-wymaga-dotnet.exe`: mały plik, wymaga .NET 10 Desktop i ASP.NET Core Runtime.

Pliki nie są podpisane certyfikatem, więc Windows SmartScreen może pokazać ostrzeżenie.

## Gdy coś pójdzie źle

- **Przebieg padł przed wydaniem** (testy, brak notatek): popraw i uruchom ponownie z tą samą wersją; nic nie powstało.
- **Wydanie jest, ale ma błąd**: nie nadpisuj go. Wydaj kolejny PATCH. Usunięcie złego wydania z tagiem (`gh release delete v1.2.0 --cleanup-tag`) wymaga osobnej, jawnej zgody; numeru nie używaj ponownie.
- **Poprawa samego opisu**: `gh release edit v1.2.0 --notes-file docs/release-notes/1.2.0.md` (po zmianie pliku w repozytorium).

## Co nie jest zautomatyzowane

- Automatyczne aktualizacje w aplikacji (użytkownik pobiera nowy instalator sam).
- Podpisywanie plików certyfikatem.
- Testy instalatora na czystym Windowsie (sprawdzasz ręcznie w kroku 5).
