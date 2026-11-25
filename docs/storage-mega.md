# MEGA jako magazyn wyników: konfiguracja

Wtyczka `ImageStudio.Storage.Mega` wysyła wygenerowane pliki do folderu **ImageStudio** na koncie MEGA. Kopia lokalna zostaje. Opis architektury: [dev/architecture.md](dev/architecture.md). Pozostałe chmury: [storage-onedrive.md](storage-onedrive.md), [storage-google-drive.md](storage-google-drive.md).

## Stan sprawdzenia

- Biblioteka MegaApiClient 1.10.5 jest nieoficjalna (MIT).
- Logowanie (e-mail, hasło, kod 2FA), kod błędu `-26`, węzeł `Root` i gzip **nie zostały jeszcze sprawdzone na prawdziwym koncie MEGA**.
- Adapter pokrywa tylko test na żywo `CloudLiveTests.Mega_create_read_update_delete_with_sample_files`, uruchamiany ręcznie (sekcja „Test na żywo” poniżej).

## Logowanie

1. Ustawienia → zakładka **Zapis** → „Miejsce zapisu” → **MEGA**.
2. Wpisz adres e-mail i hasło konta, kliknij **Zaloguj**.
3. Przy włączonym logowaniu dwuetapowym aplikacja poprosi o kod z aplikacji uwierzytelniającej. Wpisz go i kliknij **Potwierdź**.
4. Gdy status pokazuje „Zalogowano”, kliknij **Zapisz**.

Zapisywane są tylko adres e-mail i sesja MEGA, szyfrowane DPAPI w `secrets-storage-mega.bin` w katalogu danych aplikacji. Hasła i kodu 2FA aplikacja nie zapisuje.

Sesja działa jak klucz do konta: kto ma dostęp do pliku sesji na tym koncie Windows, może działać na koncie MEGA.

## Wylogowanie

- **Wyloguj** kasuje sesję lokalnie od razu, a potem próbuje unieważnić ją na serwerze MEGA.
- Brak połączenia nie blokuje wylogowania. Wtedy sesja może pozostać ważna po stronie MEGA do czasu jej wygaśnięcia lub unieważnienia w ustawieniach konta MEGA.

## Pliki na koncie

- Folder `ImageStudio` tworzy się w korzeniu konta przy pierwszym zapisie.
- Nowy plik o tej samej nazwie zastępuje starą wersję: najpierw wysyłany jest nowy plik, potem usuwane są starsze kopie.
- Usunięcie pliku w aplikacji usuwa go **trwale** na koncie MEGA. Plik nie trafia do kosza MEGA.

## Praca offline

- Pliki zapisują się lokalnie od razu, także bez logowania.
- Wysyłka czeka w kolejce zapisanej na dysku. Kolejka przetrwa zamknięcie aplikacji i wylogowanie.
- Po zalogowaniu (lub po ponownym uruchomieniu, gdy sesja jest ważna) kolejka wysyła się sama.
- Błędy wysyłki pojawiają się w statusie pod polami zakładki Zapis.
- Usunięcie pliku, który czeka na wysyłkę, anuluje tę wysyłkę.

## Czyszczenie danych

„Ustawienia → Aplikacja”, kategoria „Logowania i klucze dostawców”, usuwa sesję i kolejkę MEGA (oraz inne sekrety magazynów). Nie kasuje plików na koncie MEGA.

## Ograniczenia

- Biblioteka jest nieoficjalna. Zmiana protokołu MEGA może zepsuć logowanie lub wysyłkę bez zmiany w aplikacji.
- Galeria nie pobiera plików z konta, gdy kopia lokalna nie istnieje (np. na innym komputerze).

## Test na żywo (dla programisty)

Po zalogowaniu w aplikacji (Ustawienia → Zapis):

```powershell
$env:IMAGESTUDIO_LIVE_CLOUD = "1"; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudLiveTests"
```

Test zapisuje, odczytuje, aktualizuje i usuwa przykładowe pliki na prawdziwym koncie MEGA i sprząta po sobie. Bez zmiennej jest pominięty.
