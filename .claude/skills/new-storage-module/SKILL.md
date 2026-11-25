---
name: new-storage-module
description: Dodawanie nowego modułu magazynu wyników / chmury do ImageStudio (projekt ImageStudio.Storage.<Nazwa>, IMediaStoragePlugin, CloudMirrorStorage). Użyj przy prośbie o nową chmurę (Dropbox, S3, MEGA...), nowy magazyn plików albo nowy sposób logowania w zakładce Zapis.
---

# Nowy moduł magazynu (chmury)

Przeczytaj `docs/dev/architecture.md` (Magazyn wyników, Wtyczki chmurowe). Wzory: `ImageStudio.Storage.Mega`
(poświadczenia + 2FA, klient za interfejsem), `ImageStudio.Storage.OneDrive` (OAuth).

## Lista kontrolna
1. Projekt `ImageStudio.Storage.<Nazwa>` (referencja do `ImageStudio.Storage.Cloud`; zależność NuGet: `PackageVersion`
   w `Directory.Packages.props`, `PackageReference` bez `Version`).
2. Klasa publiczna, nieabstrakcyjna: `: OAuthCloudStorage` (OAuth w przeglądarce) albo `: CloudMirrorStorage` (inne
   logowanie); konstruktor `(StoragePluginContext context)` (wymagany do odkrycia) + konstruktor dla testów z atrapą.
3. `Id`: tylko `[a-z0-9]+`, unikalne. Sekrety tylko przez `Secrets` i tylko pod nazwami `storage-<id>` / `storage-<id>-<coś>`
   (inne nazwy rzucają wyjątek; czyszczenie danych znajduje je po wzorcu).
4. HTTP tylko z `context.CreateHttpHandler()`; zewnętrzny klient za własnym interfejsem (atrapa w testach).
5. Hasła i kody: pola `Transient: true` (`Kind: Password/Email/Code`), kod 2FA przez `IStorageInteraction.AskAsync`;
   w sekretach tylko sesja/token. Nigdy w `Configure`, logach, komunikatach wyjątków.
6. `ProjectReference` w `ImageStudio/ImageStudio.csproj` z `Private="$(PluginsInBundle)"`, w `ImageStudio.Tests.csproj`
   i wpis w `ImageStudio.slnx` (folder `/Storage/`). Nic w `ServiceCollectionExtensions`, `UserDataCleanup`, `MediaStorageHost`.
7. Teksty: `storage.<id>.*` w obu słownikach (`help`, etykiety pól, `signIn`, `working`, komunikaty); `Check-Translations -Strict`.
8. Testy: `<Nazwa>StorageTests` na atrapie (logowanie, zapis/odczyt/usuwanie, kolejka po zalogowaniu, brak hasła
   w sekretach i konfiguracji); oczekiwana lista w `StoragePluginDiscoveryTests`; opcjonalnie `[LiveCloudFact]` w `CloudLiveTests`.
9. Dokumentacja: `docs/storage-<id>.md` dla użytkownika, akapit w `docs/dev/architecture.md`.

## Weryfikacja
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
pwsh tools/Check-Translations.ps1 -Strict
Po buildzie: DLL modułu w `bin\...\storage\`, zależności NuGet w katalogu aplikacji; `dotnet publish ImageStudio -p:PublishProfile=Portable` przechodzi.
