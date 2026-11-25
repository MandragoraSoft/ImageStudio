# Panel zarządzania archiwum (zaznaczanie, przenoszenie do chmury, znaczek magazynu)

Decyzje użytkownika: osobne okno (`WindowBase`, z menu Narzędzia), przełącznik źródła = wpisy magazynu lokalnego / chmury, „Przenieś do chmury” = zapis w aktywnej chmurze, zmiana `StorageId`/`FilePath`, usunięcie starej kopii po udanym zapisie, ikona przy miniaturze wg właściciela wpisu (`StorageId`).

Zakres: galeria obrazów i wideo (`generate`, `edit`, `video`). Poza zakresem: galeria głosu, wpisy `transcript` (cudzy plik), istniejące „Przenieś do archiwum” (folder `GalleryArchiveFolder`, bez zmian).

## Fakty z rozpoznania

- `GalleryEntry` (`Configuration/GalleryStore.cs`): `FilePath`, `StorageId` (null/„local” = lokalnie). `MediaStorageHost.Owner(storageId)` zwraca wtyczkę właściciela; `Active` to aktywna wtyczka zapisu.
- Chmura (`CloudMirrorStorage`) zapisuje do lokalnego lustra i zwraca ścieżkę lustra; wysyłka idzie w tle (kolejka trwała).
- `MediaStorageHost.ReadAsync/DeleteAsync/...` dostają tylko ścieżkę, więc nie da się z niej wyznaczyć właściciela: zostają na `Active`. Nowy kod zawsze woła `host.Owner(entry.StorageId)` (jak `StudioApi.StorageFor`).
- Galeria główna: `MainViewModel.Gallery` (`ObservableCollection<GalleryEntry>`), zapis `_galleryStore.Save(Gallery)`. `GalleryEntry` nie jest `INotifyPropertyChanged`, więc po zmianie właściciela wpis podmieniamy w kolekcji (`Gallery[i] = entry`), co odświeża szablon.
- Nowe okno: `Style="{StaticResource WindowBase}"`, otwierane przez `IDialogService` (`WpfDialogService`, `FakeDialogs` w testach). Teksty: klucze w `Strings.pl.json` i `.en.json`; sprawdzenie `pwsh tools/Check-Translations.ps1 -Strict`.

## Etapy (każdy: build 0 błędów, testy zielone, commit lokalny, bez push)

### Etap 1: model i przenoszenie (bez UI)
- `GalleryEntry`: `[JsonIgnore] public bool IsInCloud => !string.IsNullOrEmpty(StorageId) && StorageId != "local";`
- `MediaStorageHost.MoveAsync(GalleryEntry entry, IMediaStoragePlugin target, CancellationToken ct)`: wymaga `entry.Kind` w (`generate`,`edit`,`video`) i `target.Id != entry.StorageId` (inaczej `ArgumentException`); czyta bajty przez `Owner(entry.StorageId).ReadAsync(entry.FilePath)`, zapisuje `target.WriteAsync(kind, Path.GetFileName(entry.FilePath), bytes)` (`kind` = Video dla `video`, inaczej Image), dopiero potem ustawia `entry.FilePath`/`entry.StorageId`, a na końcu kasuje starą kopię w poprzednim właścicielu; błąd kasowania nie cofa przeniesienia (wyjątek tylko z odczytu/zapisu, wpis wtedy bez zmian).
- Testy (`MediaStorageHostTests`): przeniesienie local → atrapa chmury (wpis zmienia właściciela i ścieżkę, stary plik skasowany), błąd zapisu zostawia wpis i stary plik, transcript/ten sam magazyn → wyjątek.

### Etap 2: ikona przy miniaturze
- `OutputPanel.xaml`: na miniaturze mały znaczek Segoe MDL2 (dysk `&#xE8B7;` lokalnie, chmura `&#xE753;`) w rogu, sterowany `IsInCloud`, z tooltipem (`gallery.storage.local` / `gallery.storage.cloud`). Wzór: znaczek wideo z `MultiDataTrigger` w tym samym szablonie. Klucze PL/EN.
- Test: `GalleryEntry.IsInCloud` dla null/„local”/„onedrive”.

### Etap 3: ViewModel panelu
- `ViewModels/ArchiveManagerViewModel`: kolekcja `ArchiveItem` (`ObservableObject`: `Entry`, `IsSelected`) odfiltrowana wg `Source` (`Local`/`Cloud`; lokalne = `!IsInCloud`), wyłącznie wpisy `generate`/`edit`/`video`; `Source`, `SelectAllCommand`, `ClearSelectionCommand`, `MoveToCloudCommand` (CanExecute: źródło lokalne, zaznaczenie > 0, `host.Active` to nie `local`, nie trwa przenoszenie), `Status`, `IsMoving`, `ActiveStorageName`.
- Konstruktor: `(ObservableCollection<GalleryEntry> gallery, MediaStorageHost host, Action save)`. Przenoszenie po kolei, po każdym wpisie `gallery[i] = entry` i `save()`, błąd jednego wpisu nie przerywa reszty; status „Przeniesiono N, błędy M”. Bez `MessageBox` i `Application.Current`.
- Testy (`ArchiveManagerViewModelTests`) na atrapach: filtr źródła, zaznaczanie, przeniesienie kilku, częściowy błąd, brak aktywnej chmury blokuje komendę.

### Etap 4: okno, menu, dialog
- `Views/ArchiveManagerWindow.xaml` (`WindowBase`): przełącznik źródła (dwa `RadioButton`), lista miniatur z `CheckBox`, „Zaznacz wszystko / Odznacz”, przycisk „Przenieś do chmury (nazwa aktywnej chmury)”, pasek statusu. Bez logiki w code-behind.
- `IDialogService.ShowArchiveManager(ArchiveManagerViewModel)`, `WpfDialogService`, `FakeDialogs` (licznik).
- `MainViewModel.OpenArchiveManagerCommand` (nowy plik partial `MainViewModel.ArchiveManager.cs`), pozycja w menu Narzędzia (`menu.archiveManager`), klucze `archive.*` PL/EN, test powiązania menu w `ShellTests` i test komendy.

### Etap 5: dokumentacja
- `docs/dev/media-gallery-voice.md` (panel, znaczek, przenoszenie), wiersz/akapit w `docs/dev/architecture.md` (`MoveAsync`, host a Owner); `-Strict` tłumaczeń.

## Kontrola głównej sesji po każdym etapie
`dotnet build ImageStudio.slnx`, `dotnet test ImageStudio.Tests`, `pwsh tools/Check-Translations.ps1 -Strict`, przegląd diffu, commit.

## Poprawki po pierwszym użyciu (2026-11-25)

Błąd: lustro chmury leży domyślnie w tym samym folderze co dysk lokalny, więc zapis nadpisywał ten sam plik, a kasowanie „starej kopii” usuwało wynik (naprawione w `84ff17b`). Dodatkowo panel pokazywał puste źródło „Dysk lokalny”, bo listuje tylko wpisy galerii (maks. 200), a w folderze są też pliki spoza galerii.

### Etap 6: bezpieczne przenoszenie
- `MoveAsync`: po zapisie w celu sprawdź, że `target.ReadAsync(location)` zwraca tyle samo bajtów co źródło; inaczej `IOException`, wpis i stary plik bez zmian. Dopiero potem zmiana wpisu i kasowanie starej kopii (pomijane dla tej samej ścieżki). Testy: cel zwraca lokalizację, ale pliku nie ma / ma inną długość → wyjątek, stary plik zostaje.

### Etap 7: widoczność plików z folderów lokalnych
- `ArchiveManagerViewModel`: źródło „Dysk lokalny” pokazuje też pliki z folderów obrazów i wideo wtyczki `local` (`host.Owner("local").LocalFolder(kind)`, bez podfolderów), których nie ma w galerii (porównanie pełnej ścieżki bez rozróżniania wielkości liter): obrazy png/jpg/jpeg/webp/gif/bmp, wideo mp4/webm/mov/mkv. Dla każdego powstaje `GalleryEntry` (nowe `Id`, `Kind` generate/video, `StorageId` local, czas z `LastWriteTime`, miniatura z `GalleryStore.Thumbnail`), niezapisany w galerii.
- Po udanym przeniesieniu taki wpis trafia do galerii (`Insert(0)` i przycięcie do `GalleryStore.MaxEntries` jak przy nowym wyniku), potem `save()`. Wpisy galerii bez istniejącego pliku nie są pokazywane w źródle lokalnym.
- Testy na katalogu tymczasowym (atrapa wtyczki lokalnej z `LocalFolder` lub prawdziwy `LocalMediaStorage` z `Roots`): sieroty widoczne, pliki z galerii bez duplikatów, przeniesienie sieroty dodaje wpis do galerii.
