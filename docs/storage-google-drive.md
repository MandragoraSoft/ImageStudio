# Google Drive jako magazyn wyników: konfiguracja

Wtyczka `ImageStudio.Storage.GoogleDrive` wysyła wygenerowane pliki do folderu **ImageStudio** w Moim dysku Google (kopia lokalna zostaje). Potrzebuje własnego klienta OAuth, którego Google przypisuje do projektu w Google Cloud. Opis architektury: [dev/architecture.md](dev/architecture.md).

## Stan projektu (zrobione przez `gcloud`)

| Element | Wartość |
|---|---|
| Konto | przemyslaw.rachwal@gmail.com |
| ID projektu | `imagestudio-drive-512492` |
| Nazwa projektu | ImageStudio Drive |
| Włączone API | Google Drive API (`drive.googleapis.com`) |
| Projekt domyślny `gcloud` | ustawiony na powyższy |

Odtworzenie od zera (np. na innym koncie):

```powershell
$id = "imagestudio-drive-" + (Get-Random -Minimum 100000 -Maximum 999999)
gcloud projects create $id --name="ImageStudio Drive"
gcloud config set project $id
gcloud services enable drive.googleapis.com
```

## Krok 1. Ekran zgody OAuth (konsola, ręcznie)

Tego `gcloud` nie robi. Otwórz: https://console.cloud.google.com/auth/overview?project=imagestudio-drive-512492

1. „Rozpocznij” / „Get started”.
2. Nazwa aplikacji: `ImageStudio`, e-mail pomocy: Twój.
3. Odbiorcy: **Zewnętrzni** (External).
4. E-mail kontaktowy: Twój. Zaakceptuj zasady i utwórz.
5. **Dostęp do danych** (Data access) → Dodaj zakresy → `https://www.googleapis.com/auth/drive.file` → Zapisz.
6. **Odbiorcy** (Audience) → Użytkownicy testowi → Dodaj → `przemyslaw.rachwal@gmail.com`.

Aplikacja zostaje w trybie **Testing**. Dopóki tak jest, logować mogą się tylko dodani użytkownicy testowi, a token odświeżający wygasa po 7 dniach (wtedy trzeba kliknąć „Zaloguj” ponownie). Żeby tego uniknąć, w Odbiorcach wybierz „Opublikuj aplikację”. Zakres `drive.file` nie wymaga pełnej weryfikacji Google, pojawi się tylko ostrzeżenie o niezweryfikowanej aplikacji.

## Krok 2. Klient OAuth (konsola, ręcznie)

Otwórz: https://console.cloud.google.com/auth/clients?project=imagestudio-drive-512492

1. „Utwórz klienta” / „Create client”.
2. Typ aplikacji: **Aplikacja komputerowa** (Desktop app).
3. Nazwa: `ImageStudio desktop`.
4. Skopiuj **Client ID** (kończy się na `.apps.googleusercontent.com`) i **Client secret**.

Sekret klienta aplikacji desktopowej nie jest naprawdę tajny (Google wymaga go mimo to przy wymianie kodu), ale nie wklejaj go do repozytorium ani do plików ustawień.

## Krok 3. Logowanie w ImageStudio

1. Ustawienia → zakładka **Zapis** → lista „Miejsce zapisu” → **Google Drive**.
2. Wklej Client ID i sekret klienta.
3. „Zaloguj w przeglądarce”. Wybierz konto, przy ostrzeżeniu „Ta aplikacja nie została zweryfikowana” kliknij *Zaawansowane → Przejdź do ImageStudio*, zaakceptuj dostęp do plików utworzonych przez aplikację.
4. Po powrocie status pokazuje „Zalogowano”. Zapisz Ustawienia.

Nowe pliki są wysyłane w tle do folderu `ImageStudio` w Moim dysku. Stan logowania i błąd wysyłki widać w zakładce Zapis na bieżąco; pliki zapisane przed zalogowaniem są wysyłane po zalogowaniu.

## Rozwiązywanie problemów

| Objaw | Przyczyna |
|---|---|
| `access_denied` / „aplikacja w trakcie testów” | Konto nie jest dodane jako użytkownik testowy (krok 1.6). |
| `redirect_uri_mismatch` | Klient nie jest typu „Aplikacja komputerowa”. Utwórz go ponownie (krok 2). |
| `invalid_client` przy wymianie kodu | Brak albo błędny sekret klienta w zakładce Zapis. |
| Po tygodniu „Sesja wygasła” | Aplikacja w trybie Testing (token wygasa po 7 dniach): opublikuj ją albo zaloguj się ponownie. |
| `accessNotConfigured` / 403 przy wysyłce | Drive API nie jest włączone w projekcie: `gcloud services enable drive.googleapis.com`. |
| Plik wysłany, ale niewidoczny na dysku | Zakres `drive.file` pokazuje tylko pliki utworzone przez aplikację. Szukaj folderu `ImageStudio` w Moim dysku. |

## Sprzątanie

```powershell
gcloud projects delete imagestudio-drive-512492
```
Usuwa projekt (z 30-dniowym okresem na przywrócenie) razem z klientem OAuth. Pliki już wysłane na Dysk zostają.
