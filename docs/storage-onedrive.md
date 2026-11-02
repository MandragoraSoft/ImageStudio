# OneDrive jako magazyn wyników: konfiguracja

Wtyczka `ImageStudio.Storage.OneDrive` wysyła wygenerowane pliki do folderu **Aplikacje\ImageStudio** na OneDrive (kopia lokalna zostaje). Aplikacja widzi tylko ten folder (zakres `Files.ReadWrite.AppFolder`), nie cały dysk. Opis architektury: [dev/architecture.md](dev/architecture.md). Wariant dla Google: [storage-google-drive.md](storage-google-drive.md).

## Stan rejestracji (zrobione przez `az`)

| Element | Wartość |
|---|---|
| Nazwa rejestracji | ImageStudio |
| **Client ID** (Application ID) | `f8e5d8af-c80b-4a57-9589-9a6b423ca853` |
| Object ID | `913fd66d-bb1d-49f5-92f8-6fac5b996ce4` |
| Tenant domowy | `197021aa-14eb-4790-a77e-ef58f5c4089f` |
| Konta | służbowe i szkolne oraz osobiste Microsoft (`AzureADandPersonalMicrosoftAccount`) |
| Klient | publiczny (aplikacja komputerowa), bez sekretu |
| Adres przekierowania | `http://localhost` (port dobiera aplikacja) |
| Uprawnienie | Microsoft Graph, delegowane: `Files.ReadWrite.AppFolder` |

Client ID nie jest tajny. Sekret klienta nie istnieje, bo logowanie używa PKCE.

Odtworzenie od zera (np. na innym koncie):

```powershell
az login
az ad app create --display-name "ImageStudio" `
  --sign-in-audience AzureADandPersonalMicrosoftAccount `
  --public-client-redirect-uris "http://localhost" `
  --is-fallback-public-client true `
  --required-resource-accesses '[{\"resourceAppId\":\"00000003-0000-0000-c000-000000000000\",\"resourceAccess\":[{\"id\":\"8019c312-3263-48e6-825e-2b833497195b\",\"type\":\"Scope\"}]}]' `
  --query appId -o tsv
```
Zwrócona wartość to Client ID. Identyfikator uprawnienia `8019c312-…` to `Files.ReadWrite.AppFolder`; sprawdzisz go poleceniem
`az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "oauth2PermissionScopes[?value=='Files.ReadWrite.AppFolder'].id" -o tsv`.

## Logowanie w ImageStudio

1. Ustawienia → zakładka **Zapis** → „Miejsce zapisu” → **OneDrive**.
2. Wklej Client ID z tabeli powyżej (Client ID i token trafiają do `SecretStore`, nie do `state.json`).
3. „Zaloguj w przeglądarce”, wybierz konto Microsoft i zaakceptuj dostęp do folderu aplikacji.
4. Gdy status pokazuje „Zalogowano”, kliknij **Zapisz**.

Nowe pliki trafiają w tle do `OneDrive\Aplikacje\ImageStudio`. Zgodę użytkownik wyraża sam; administrator nie musi nic zatwierdzać, bo `Files.ReadWrite.AppFolder` jest uprawnieniem do zgody użytkownika. W niektórych organizacjach administrator może jednak zablokować zgody użytkowników.

## Rozwiązywanie problemów

| Objaw | Przyczyna |
|---|---|
| `AADSTS50011` (adres przekierowania) | Brak `http://localhost` w platformie „Aplikacje mobilne i klasyczne”. Dodaj w Entra: Uwierzytelnianie. |
| `AADSTS700016` / aplikacja nie znaleziona | Błędny Client ID albo rejestracja w innym tenancie. |
| `AADSTS65001` (brak zgody) | Użytkownik lub administrator odrzucił zgodę, albo organizacja blokuje zgody użytkowników. |
| `AADSTS7000218` (wymagany sekret) | Wyłączone „Zezwalaj na przepływy klienta publicznego”: włącz w Uwierzytelnianie → Ustawienia zaawansowane. |
| Błąd 403 przy wysyłce | Konto bez licencji OneDrive (np. służbowe bez SharePoint) albo brak uprawnienia w rejestracji. |
| Plik wysłany, ale nie widać go w głównym katalogu | Folder aplikacji to `Aplikacje\ImageStudio`, nie korzeń dysku. |

## Publikacja dla innych użytkowników

Ta rejestracja jest wielotenantowa i działa dla innych kont, więc Client ID można wbudować jako domyślny. Przy pierwszym logowaniu innych osób zobaczą oznaczenie „niezweryfikowany wydawca”, dopóki nie przejdziesz weryfikacji wydawcy (Microsoft Cloud Partner Program).

## Sprzątanie

```powershell
az ad app delete --id f8e5d8af-c80b-4a57-9589-9a6b423ca853
```
