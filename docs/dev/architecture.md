# Architektura aplikacji: DI, MVVM, dialogi

_Kiedy czytać: zmieniasz składanie usług, dodajesz ViewModel, magazyn danych albo okno dialogowe._

## Dependency injection (Microsoft.Extensions.DependencyInjection, z frameworka ASP.NET Core).

Jedyny composition root to `Composition/ServiceCollectionExtensions`: `AddImageStudioCore()` (ViewModele, zakładki jako `IMainTab`, okno główne, usługi niezależne od infrastruktury) i `AddImageStudioInfrastructure()` (magazyny plikowe `File*Store`, `WpfDialogService`, `WpfThemeService`, prawdziwy dostawca z `IProviderFactory`). Dane użytkownika są za interfejsami z `Configuration/Stores.cs` (`IUserStateStore`, `IGalleryStore`, `IPresetStore`, `ISecretStore`, `ISettingsStore`, `IWindowStateStore`); nie wołaj w ViewModelach statycznych `PresetStore`/`GalleryStore`/`SecretStore`/`AppSettings.Load` ani `ThemeService`. Wyjątki: `WindowMemory.Store` (zachowanie tworzone przez XAML, composition root podmienia właściwość) oraz wnętrze `MainViewModel`, które samo tworzy `ChatViewModel`, `ToolsViewModel` i serwer MCP (cykl przez `IAppControl`). Kontener nie jest zwalniany przy wyjściu, bo `McpToolHost` jest tylko `IAsyncDisposable`; w testach zwalniaj przez `DisposeAsync`.

## MVVM na CommunityToolkit.Mvvm.

`MainViewModel` jest `partial` i rozbity na pliki per funkcja (`.Generation`, `.Video`, `.Settings`, `.Gallery`, `.References`, `.Size`, `.AppControl`, `.Helpers`). Nowa funkcja głównego okna = nowy plik partial, nie rozrost `MainViewModel.cs`. Stan użytkownika trzyma `UserState`, a dane (ustawienia, galeria, zestawy, klucze) leżą w `AppPaths.UserDataDir`. Klucze API są szyfrowane przez `SecretStore` (DPAPI).

## Okna dialogowe przez `IDialogService`

(interfejs i rekordy wyników w `ViewModels/`, implementacja `WpfDialogService` w `Views/`, `FakeDialogs` w `ImageStudio.Tests/Fakes.cs`). Zmieniając sygnaturę interfejsu, zaktualizuj fake'a. ViewModele nie wołają `MessageBox` (użyj `Confirm`) ani `Application.Current`; wątek UI przechwytuje `SynchronizationContext` w konstruktorze `MainViewModel`.
