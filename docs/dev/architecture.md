# Architektura aplikacji: DI, MVVM, dialogi

_Kiedy czytać: zmieniasz składanie usług, dodajesz ViewModel, magazyn danych albo okno dialogowe._

## Dependency injection (Microsoft.Extensions.DependencyInjection, z frameworka ASP.NET Core).

Jedyny composition root to `Composition/ServiceCollectionExtensions`: `AddImageStudioCore()` (ViewModele, zakładki jako `IMainTab`, okno główne, usługi niezależne od infrastruktury) i `AddImageStudioInfrastructure()` (magazyny plikowe `File*Store`, `WpfDialogService`, `WpfThemeService`, prawdziwy dostawca z `IProviderFactory`). Dane użytkownika są za interfejsami z `Configuration/Stores.cs` (`IUserStateStore`, `IGalleryStore`, `IPresetStore`, `ISecretStore`, `ISettingsStore`, `IWindowStateStore`); w ViewModelach nie czytaj ani nie zapisuj danych przez statyczne `PresetStore`/`GalleryStore`/`SecretStore`/`AppSettings.Load` i nie zmieniaj motywu przez `ThemeService` (stałe i `GalleryStore.Thumbnail` są dozwolone). Wyjątki: `WindowMemory.Store` (zachowanie tworzone przez XAML, composition root podmienia właściwość) oraz wnętrze `MainViewModel`, które samo tworzy `ChatViewModel`, `ToolsViewModel` i serwer MCP (cykl przez `IAppControl`). Kontener nie jest zwalniany przy wyjściu, bo `McpToolHost` jest tylko `IAsyncDisposable`; w testach zwalniaj przez `DisposeAsync`.

## MVVM na CommunityToolkit.Mvvm.

`MainViewModel` jest `partial` i rozbity na pliki per funkcja (`.About`, `.AppControl`, `.Gallery`, `.Generation`, `.Helpers`, `.PromptConversation`, `.References`, `.Settings`, `.Size`, `.Tabs`, `.Video`). Nowa funkcja głównego okna = nowy plik partial, nie rozrost `MainViewModel.cs`. Stan użytkownika trzyma `UserState`, a dane (ustawienia, galeria, zestawy, klucze) leżą w `AppPaths.UserDataDir`. Klucze API są szyfrowane przez `SecretStore` (DPAPI).

## Okna dialogowe przez `IDialogService`

(interfejs i rekordy wyników w `ViewModels/`, implementacja `WpfDialogService` w `Views/`, `FakeDialogs` w `ImageStudio.Tests/Fakes.cs`). Zmieniając sygnaturę interfejsu, zaktualizuj fake'a. ViewModele nie wołają `MessageBox` (użyj `Confirm`) ani `Application.Current`; wątek UI przechwytuje `SynchronizationContext` w konstruktorze `MainViewModel`.

## Magazyn wyników (wtyczki `ImageStudio.Storage.*`)

Zapis wyników (obraz, wideo, synteza mowy) idzie przez `IMediaStorage` (`ImageStudio.Storage.Core/MediaStorage.cs`), nie przez `File.*` w ViewModelach.

- `WriteAsync(kind, fileName, data)` zwraca nieprzezroczystą *lokalizację* (dla dysku ścieżka, dla chmury ścieżka lokalnej kopii), którą aplikacja trzyma w `GalleryEntry.FilePath`.
- Dalej `ReadAsync`, `ExistsAsync`, `DeleteAsync` i `GetLocalPathAsync` (ścieżka do odtwarzania i miniatur).
- Wtyczka implementuje `IMediaStoragePlugin`: `Id` (tylko `[a-z0-9]+`, unikalny), `DisplayName`, `Settings` (pola z `StorageFieldKind`: Text, Folder, Email, Password, Code), `Configure`, `Actions` i `RunActionAsync(key, inputs, interaction, ct)`, `CanRun`, `Status`, `HelpKey`, `Resume()`, zdarzenie `StatusChanged`; opcjonalnie `LocalFolder`.
- Logowanie: `StoragePrompt` i `IStorageInteraction.AskAsync` pozwalają akcji zapytać użytkownika w trakcie (zakładka Zapis pokazuje pole i przycisk Potwierdź; np. kod 2FA).

Odkrywanie wtyczek:

- `StoragePluginDiscovery` czyta metadane `ImageStudio.StoragePlugin` z assembly aplikacji. Target `AddStoragePluginMetadata` w `ImageStudio.csproj` wpisuje nazwy bibliotek `ImageStudio.Storage.*` z `StorageDll`.
- Z bibliotek brane są publiczne, nieabstrakcyjne typy `IMediaStoragePlugin` z konstruktorem `(StoragePluginContext)`. Brak biblioteki (lub jej zależności) to ostrzeżenie w logu, aplikacja działa dalej.
- Porządek: po pełnej nazwie typu (stały).
- `LocalMediaStorage` (`ImageStudio.Storage.Local`) nie ma tego konstruktora. `AddImageStudioCore` rejestruje ją jawnie jako pierwszą, czyli domyślną.
- DLL-e wtyczek trafiają do podfolderu `storage\` (jak `providers\`) albo, w profilach `PluginsInBundle`, do exe.

Host i konfiguracja (`MediaStorageHost`, jedyny `IMediaStorage` widziany przez ViewModele):

- Deleguje operacje do `Active`. Wybór i ustawienia edytuje zakładka Zapis (`StorageSettingsTab`), trwały zapis to `UserState.StorageId` i `StorageConfig`.
- `Owner(storageId)` wskazuje wtyczkę, która zapisała wpis. Brak identyfikatora (starsze wpisy) albo wtyczka usunięta z aplikacji daje `local`.
- `MoveAsync(entry, target)` przenosi plik wpisu między wtyczkami (odczyt z `Owner`, zapis w celu, zmiana wpisu, kasowanie starej kopii). `ReadAsync`/`DeleteAsync` hosta znają tylko ścieżkę, więc idą do `Active`; operacje na istniejących wpisach zawsze przez `Owner`.
- `Load` stosuje konfigurację i wznawia wszystkie wtyczki, nie tylko aktywną, bo kolejka poprzedniej też ma dojść.
- `WithoutSecrets` usuwa z pliku stanu pola `IsSecret` i `Transient`.

Sekrety (`StorageSecrets` jako `IStorageSecrets` nad `SecretStore`, DPAPI):

- Dozwolone nazwy to tylko `storage-<id>` i `storage-<id>-<coś>` (wzorzec `storage-[a-z0-9]+(-[a-z0-9]+)*`). Inna nazwa rzuca `ArgumentException`.
- Pola `Transient` (hasła, kody) nie trafiają do konfiguracji, pliku ani sekretów; żyją tylko w wywołaniu akcji.
- Czyszczenie danych (`UserDataCleanup`, kategoria „Logowania i klucze”) znajduje pliki `secrets-storage-*.bin` po tym samym wzorcu, bez listy wtyczek.

Jeszcze nie przez kontrakt (działa, bo wtyczki chmurowe są lustrem folderów lokalnych): odczyt w galerii (`GalleryStore.Load` filtruje `File.Exists`, wybór wpisu czyta `File.ReadAllBytes`, `MediaElement` bierze ścieżkę), usuwanie z dysku z galerii, strażnik usuwania w `VoiceViewModel` (`OutputDir`), "Zapisz jako", nagrania mikrofonu.

### Wtyczki chmurowe (OneDrive, Google Drive, MEGA)

Podział klas:

- `CloudMirrorStorage` (`ImageStudio.Storage.Cloud`): wspólna baza modelu lustra, trwała kolejka, wysyłka w tle, status, wznawianie.
- `OAuthCloudStorage` (ta sama biblioteka): logowanie OAuth w przeglądarce, tokeny, odświeżanie. `ImageStudio.Storage.OneDrive` i `ImageStudio.Storage.GoogleDrive` dopisują tylko adresy OAuth i wywołania API.
- MEGA (`ImageStudio.Storage.Mega`): `CloudMirrorStorage` z logowaniem e-mail i hasło, bez OAuth. Szczegóły w sekcji MEGA poniżej.

Model lustra: plik zawsze trafia najpierw do kopii lokalnej (`Cache`, `LocalMediaStorage`), więc galeria i odtwarzanie działają na ścieżkach. Wysyłka idzie w tle. Błąd wysyłki nie psuje zapisu, widać go w `Status` zakładki Zapis. `FlushAsync` czeka na wysyłki (testy, zamykanie).

Trwała kolejka:

- Operacje `upload` i `delete` są zapisane jako JSON w sekrecie `storage-<id>-pending`. Przeżywają wylogowanie i restart.
- Wpis znika dopiero po udanej operacji. Bez sesji operacje czekają w kolejce. `Resume()` wołany jest przy starcie i po zalogowaniu.
- Nowsza operacja na tej samej lokalizacji zastępuje starszą. Usunięcie zastępuje więc oczekujące wysłanie tej samej lokalizacji, a anulowane wysłanie nie jest ponawiane po usunięciu.
- `Id` operacji (Guid) chroni przed utratą nowszego zapisu: zakończenie starszego zadania usuwa tylko własny wpis, więc późne nadpisanie nie kasuje nowszej zawartości z kolejki.
- Zadania dla tej samej nazwy pliku idą jedno po drugim (`Background`), żeby ponowiona wysyłka nie wyprzedziła późniejszego usunięcia.
- Anulowanie pojedynczego żądania HTTP (`UserCanceledRequestException` z rejestru żądań) ponawia zadanie z opóźnieniem rosnącym do 30 s. Inne błędy trafiają do statusu, a wpis zostaje w kolejce.
- Wpis wskazujący brak lokalnego pliku jest usuwany.
- `ponytail:` kolejka w sekrecie (każdy zapis to zapis DPAPI pliku kolejki). Przy tysiącach wpisów osobny plik.

Odczyt: `ReadAsync` i `GetLocalPathAsync` biorą kopię lokalną, a gdy jej nie ma, pobierają plik z chmury (`DownloadRemoteAsync`) i odkładają go pod tą samą ścieżką. `ExistsAsync` sprawdza tylko lokalnie. `DeleteAsync` kasuje kopię lokalną i kolejkuje usunięcie zdalne. Galeria jeszcze nie czyta przez `ReadAsync`, więc plik z innego komputera w galerii się nie pojawia.

Logowanie OAuth (`OAuthCloudStorage`, `OAuthFlow`):

- Kod + PKCE, powrót na `localhost` (Microsoft) albo `127.0.0.1` (Google) z losowym portem.
- Client ID i sekret klienta Google leżą w `storage-<id>-clientid` i `storage-<id>-client`, tokeny w `storage-<id>`.
- Gdy pole jest puste, wartość bierze się ze zmiennej środowiska (`EnvClientIdVar`, `EnvClientSecretVar`); nie jest kopiowana do `SecretStore`.
- Puste pole nie kasuje zapisanej wartości.
- Zakresy minimalne: OneDrive `Files.ReadWrite.AppFolder`, Google `drive.file`.
- Nie obsługiwane: wysyłka OneDrive powyżej 250 MB (prosty PUT, bez sesji wysyłania).

Transport: wszystkie wtyczki chmurowe, także OAuth, używają handlera z `context.CreateHttpHandler()` (rejestr żądań `OutgoingRequestRegistry`: lista w stopce, anulowanie).

Domyślne dane z `.env`: `DotEnv.LoadDefaults()` (start aplikacji) wczytuje `.env` do zmiennych procesu (zmienna z powłoki ma pierwszeństwo; `%LOCALAPPDATA%\ImageStudio\.env` zawsze, a w Debug także `.env` szukany w górę od katalogu bieżącego i aplikacji). Wtyczki OAuth używają zmiennej tylko wtedy, gdy pole w Ustawieniach jest puste (`GOOGLE_OAUTH_CLIENT_ID`, `GOOGLE_OAUTH_CLIENT_SECRET`, `AZURE_APP_CLIENT_ID`). MEGA nie korzysta z `.env`.

### MEGA (`ImageStudio.Storage.Mega`)

- Biblioteka: nieoficjalna MegaApiClient 1.10.5 (MIT; README biblioteki: "passive maintenance"). Jedyny plik z jej typami to `MegaApiClientAdapter` za interfejsem `IMegaClient`. Testy na atrapie `IMegaClient` nie sprawdzają protokołu.
- Transport: `HttpClientWebClient` to port `WebClient` z tej biblioteki (MIT, autor gpailler) na handlerze aplikacji, żeby ruch szedł przez rejestr żądań. Obsługuje hashcash (`X-Hashcash`, kod `HashcashRequired`).
- Newtonsoft.Json jest przypięty w `Directory.Packages.props` (`CentralPackageTransitivePinningEnabled`, wersja 13.0.3) z powodu luki w wersji przechodniej.
- Logowanie: `MegaStorage.SignInAsync` przyjmuje e-mail i hasło (pola `Transient`). Konto z 2FA rzuca `MegaMfaRequiredException`, wtedy akcja pyta o kod (`AskAsync`, prompt `mfa`) i loguje się ponownie. Adapter mapuje kod `TwoFactorAuthenticationError` z biblioteki na ten wyjątek.
- Sekret `storage-mega` zawiera tylko e-mail i sesję (JSON `MegaSession`), nigdy hasła. Sesja zawiera klucz główny konta, więc chroni ją DPAPI jak tokeny OAuth.
- Wylogowanie: najpierw kasuje sesję lokalnie, potem unieważnia ją na serwerze. Błąd serwera jest ignorowany.
- Folder `ImageStudio` przy korzeniu konta (`Root`) tworzony przy pierwszym użyciu.
- Wysyłka: najpierw nowy plik, potem usuwanie starych kopii o tej samej nazwie (błąd kasowania jest ignorowany, zostaje duplikat).
- Odczyt: gdy jest kilka kopii, bierze najnowszą (`CreationDate`).
- Usuwanie jest trwałe (`moveToTrash: false`), nie do kosza, jak w OneDrive i Google Drive.
- Wszystkie wywołania adaptera idą przez jedną blokadę (`SemaphoreSlim`); po nieudanym wylogowaniu klient jest tworzony od nowa.
- Stan weryfikacji: NIE sprawdzono na prawdziwym koncie logowania (w tym 2FA, kodu `-26`, węzła `Root` i gzip). Adapter pokrywa tylko `CloudLiveTests.Mega_create_read_update_delete_with_sample_files`, uruchamiany ręcznie: `$env:IMAGESTUDIO_LIVE_CLOUD = "1"`, po zalogowaniu w aplikacji.
