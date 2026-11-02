# Architektura aplikacji: DI, MVVM, dialogi

_Kiedy czytać: zmieniasz składanie usług, dodajesz ViewModel, magazyn danych albo okno dialogowe._

## Dependency injection (Microsoft.Extensions.DependencyInjection, z frameworka ASP.NET Core).

Jedyny composition root to `Composition/ServiceCollectionExtensions`: `AddImageStudioCore()` (ViewModele, zakładki jako `IMainTab`, okno główne, usługi niezależne od infrastruktury) i `AddImageStudioInfrastructure()` (magazyny plikowe `File*Store`, `WpfDialogService`, `WpfThemeService`, prawdziwy dostawca z `IProviderFactory`). Dane użytkownika są za interfejsami z `Configuration/Stores.cs` (`IUserStateStore`, `IGalleryStore`, `IPresetStore`, `ISecretStore`, `ISettingsStore`, `IWindowStateStore`); nie wołaj w ViewModelach statycznych `PresetStore`/`GalleryStore`/`SecretStore`/`AppSettings.Load` ani `ThemeService`. Wyjątki: `WindowMemory.Store` (zachowanie tworzone przez XAML, composition root podmienia właściwość) oraz wnętrze `MainViewModel`, które samo tworzy `ChatViewModel`, `ToolsViewModel` i serwer MCP (cykl przez `IAppControl`). Kontener nie jest zwalniany przy wyjściu, bo `McpToolHost` jest tylko `IAsyncDisposable`; w testach zwalniaj przez `DisposeAsync`.

## MVVM na CommunityToolkit.Mvvm.

`MainViewModel` jest `partial` i rozbity na pliki per funkcja (`.Generation`, `.Video`, `.Settings`, `.Gallery`, `.References`, `.Size`, `.AppControl`, `.Helpers`). Nowa funkcja głównego okna = nowy plik partial, nie rozrost `MainViewModel.cs`. Stan użytkownika trzyma `UserState`, a dane (ustawienia, galeria, zestawy, klucze) leżą w `AppPaths.UserDataDir`. Klucze API są szyfrowane przez `SecretStore` (DPAPI).

## Okna dialogowe przez `IDialogService`

(interfejs i rekordy wyników w `ViewModels/`, implementacja `WpfDialogService` w `Views/`, `FakeDialogs` w `ImageStudio.Tests/Fakes.cs`). Zmieniając sygnaturę interfejsu, zaktualizuj fake'a. ViewModele nie wołają `MessageBox` (użyj `Confirm`) ani `Application.Current`; wątek UI przechwytuje `SynchronizationContext` w konstruktorze `MainViewModel`.

## Magazyn wyników (wtyczki `ImageStudio.Storage.*`)

Zapis wyników (obraz, wideo, synteza mowy) idzie przez `IMediaStorage` z `ImageStudio.Storage.Core`, nie przez `File.*` w ViewModelach. Kontrakt: `WriteAsync(kind, fileName, data)` zwraca nieprzezroczystą *lokalizację* (dla dysku ścieżka, dla chmury np. id pliku), którą aplikacja trzyma w `GalleryEntry.FilePath`; dalej `ReadAsync`, `ExistsAsync`, `DeleteAsync` i `GetLocalPathAsync` (ścieżka lokalna do odtwarzania i miniatur, wtyczka zdalna pobiera do pamięci podręcznej). Domyślna wtyczka `ImageStudio.Storage.Local` (`LocalMediaStorage`, katalogi w `Roots`) jest rejestrowana w `AddImageStudioInfrastructure`, DLL-e trafiają do `storage\` (jak `providers\`). Nowy magazyn (OneDrive, Google Drive) = osobny projekt `ImageStudio.Storage.<Nazwa>` z `IMediaStorage` + referencja w `ImageStudio.csproj` i rejestracja w DI. W testach `TestApp` podaje `LocalMediaStorage` z katalogami w temp.

Jeszcze nie przez kontrakt (do zrobienia przed pierwszą wtyczką zdalną): odczyt w galerii (`GalleryStore.Load` filtruje `File.Exists`, wybór wpisu czyta `File.ReadAllBytes`, `MediaElement` bierze ścieżkę), usuwanie z dysku z galerii, strażnik usuwania w `VoiceViewModel` (`OutputDir`), "Zapisz jako", nagrania mikrofonu, wybór aktywnego magazynu w Ustawieniach.

Wtyczka implementuje `IMediaStoragePlugin` (`Id`, `DisplayName`, `Settings` = pola konfiguracji z kluczami tłumaczeń, `Configure`, opcjonalnie `LocalFolder`). `MediaStorageHost` (jedyny `IMediaStorage` widziany przez ViewModele) deleguje do aktywnej wtyczki; wybór i ustawienia edytuje zakładka Zapis w Ustawieniach (`StorageSettingsTab`), a trwały zapis to `UserState.StorageId`/`StorageConfig`. Nowa wtyczka = rejestracja `IMediaStoragePlugin` w `AddImageStudioCore` (pierwsza jest domyślna); klucze tłumaczeń jej pól dopisz do słowników.
