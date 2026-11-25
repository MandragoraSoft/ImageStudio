# Moduł chmury MEGA i wymienne wtyczki magazynu: plan implementacji

Data: 2026-11-25. Plan pisze sesja główna; etapy wykonują subagenci Haiku po kolei (nigdy dwa naraz w jednym katalogu roboczym), sesja główna sprawdza build, testy i diff po każdym etapie. Każdy etap = osobny commit.

## Cel

1. Nowy moduł chmury **MEGA** (`ImageStudio.Storage.Mega`) działający jak OneDrive/Google Drive (model lustra: kopia lokalna + wysyłka w tle).
2. Moduły chmury **wymienne**: nowy moduł = projekt `ImageStudio.Storage.<Nazwa>` + `ProjectReference` w `ImageStudio.csproj` (i w testach, i w `.slnx`). Nic więcej w kodzie aplikacji: DI, lista w Ustawieniach i czyszczenie sekretów znajdują go same, także w exe jednoplikowym.
3. **Logowanie w Ustawieniach → Zapis** także dla poświadczeń (e-mail + hasło + kod 2FA), nie tylko OAuth w przeglądarce.
4. Dodatkowo: trwała kolejka wysyłek przy wylogowaniu, operacje kierowane do wtyczki-właściciela pliku, status logowania/wysyłki na żywo.

## Decyzje użytkownika (obowiązują)

1. Odkrywanie wtyczek przez refleksję po bibliotekach `ImageStudio.Storage.*`; deterministyczny porządek, `local` zawsze pierwsza i domyślna; mały obiekt kontekstu zamiast DI we wtyczce.
2. MEGA przez nieoficjalną bibliotekę NuGet `MegaApiClient`; najpierw badanie (etap 0) z bramką STOP; dostęp do MEGA za własnym interfejsem `IMegaClient` (atrapa w testach).
3. Rozszerzony kontrakt logowania: rodzaje pól, akcja z wartościami pól, prośba o dane w trakcie akcji (kod 2FA). Hasło nigdy w `UserState.StorageConfig`, w logach ani w `Configure`; czyszczone po udanym logowaniu; trwa tylko sesja w `IStorageSecrets` (DPAPI). Zgodność wsteczna Local/OneDrive/Google.
4. Zakres dodatkowy: (a) trwała kolejka oczekujących, (b) kierowanie do właściciela lokalizacji, (c) status na żywo. Poza tym zachowanie OneDrive i Google się nie zmienia.
5. Rozdzielenie ogólnego lustra i uwierzytelniania w `ImageStudio.Storage.Cloud`.

## Fakty z kodu (punkt wyjścia)

- Kontrakt: `ImageStudio.Storage.Core/MediaStorage.cs:28-35` (`StorageSetting`, `StorageAction`, `StorageStatus`), `:48-70` (`IMediaStoragePlugin`: `RunActionAsync(string key, CancellationToken)`, `CanRun`, `Status`, `HelpKey` jako metody domyślne).
- `ImageStudio.Storage.Cloud/CloudMirrorStorage.cs`: konstruktor tworzy `HttpClient` i `OAuthFlow` (`:27-35`); nazwy sekretów `storage-{Id}`, `storage-{Id}-client`, `storage-{Id}-clientid` (`:75-77`); `Settings`/`Configure`/`Actions`/`CanRun`/`Status`/`RunActionAsync` niewirtualne i zakodowane pod OAuth (`:79-141`); zapis i usuwanie zlecają pracę w tle, a przy braku tokenu nic nie robią (`:156-176`), kolejka tylko w pamięci (`:17-19`, `:181-227`).
- `OneDriveStorage`/`GoogleDriveStorage` dziedziczą po `CloudMirrorStorage`, konstruktory `(IStorageSecrets, HttpMessageHandler?, Func<string,string?>?)` i `(IStorageSecrets, HttpMessageHandler, TimeSpan)` (`OneDriveStorage.cs:12-16`, `GoogleDriveStorage.cs:14-18`).
- DI: `ImageStudio/Composition/ServiceCollectionExtensions.cs:34-42` rejestruje jawnie Local, OneDrive (z `OutgoingRequestRegistry.CreateHandler()` i 10 min), Google; `using` na OneDrive/GoogleDrive w `:4-6` to jedyne użycie tych typów w aplikacji.
- `MediaStorageHost` (`ImageStudio/Services/MediaStorageHost.cs:10-14`): pierwsza wtyczka domyślna; `ReadAsync`/`ExistsAsync`/`DeleteAsync`/`GetLocalPathAsync` idą do `Active` (`:40-43`); `WithoutSecrets` wycina pola `IsSecret` (`:30-35`).
- Jedyni wołający odczyt/usuwanie przez kontrakt: `StudioApi.DeleteEntryAsync` (`ImageStudio/Services/StudioApi.cs:433`) i `ExportAsync` (`:451`). `GalleryEntry.StorageId` istnieje (`ImageStudio/Configuration/GalleryStore.cs:25-26`), ustawia go `StudioApi.WriteResultAsync` (`StudioApi.cs:18-24`); stare wpisy mają `null`. Archiwizacja galerii traktuje `null` jako lokalne (`MainViewModel.Gallery.cs:206-210`).
- Zakładka Zapis: `ImageStudio/ViewModels/StorageSettingsTab.cs` (`StorageOption.RunAsync` woła `Configure` ze wszystkimi polami i `RunActionAsync(key, ct)`, `:79-108`; status tylko po akcji, `:67-74`); widok `ImageStudio/Views/StorageSettingsTabView.xaml:19-63`. Tworzona w `MainViewModel.Settings.cs:57`, wynik stosowany w `:65-67`.
- `PluginResolver` (`ImageStudio/Services/PluginResolver.cs:17-27`) ładuje z `storage\` przy `Private=false`; `ImageStudio.csproj:15-16` (`PluginsInBundle`), `:62-66` (referencje magazynów), `:84-106` (`StorageDll` z `@(ReferencePath)`).
- Czyszczenie: `UserDataCleanup.cs:157-166` ma zakodowane nazwy `secrets-storage-onedrive*.bin` i `secrets-storage-googledrive*.bin`; blokada zapisów w `UserDataMutationGate` (`:131-133`) używa tej samej listy. `SecretStore` zapisuje `name` jako `secrets-{name}.bin` (`SecretStore.cs:11-12`); `StorageSecrets` przepuszcza dowolną nazwę (`ImageStudio/Services/StorageSecrets.cs:7-11`).
- Przekazanie na wątek UI w istniejącym kodzie: `SynchronizationContext` złapany w konstruktorze + `Post` (`ImageStudio/Services/Activity.cs:25`, `:75`).
- Testy: `CloudStorageTests` (`MemorySecrets`, `StubHandler`, `Mirror<T>`), `Signed_out_storage_only_saves_locally` (`:78-90`) sprawdza status `storage.status.signedOut`; `OutgoingRequestIntegrationTests.cs:231` pobiera `OneDriveStorage` z kontenera po typie konkretnym; `TestApp.cs:206` rejestruje własny `LocalMediaStorage`; `UserDataCleanupTests.cs:175-203` sprawdza listę plików sekretów.
- Tłumaczenia: klucz jest „użyty”, gdy występuje jako literał `"grupa.nazwa"` w dowolnym `.cs` poza testami (`tools/Check-Translations.ps1:130`, `LocalizationTests.cs:79-81`), więc `LabelKey` w kodzie wtyczki wystarcza. Nazwy kluczy: tylko litery i cyfry między kropkami (bez `-`/`_`).

## Architektura docelowa

```
ImageStudio.Storage.Core        kontrakt: IMediaStorage, IMediaStoragePlugin (+ akcja z wartościami, Resume, StatusChanged),
                                StorageSetting(+Kind, Transient), StorageFieldKind, StoragePrompt, IStorageInteraction,
                                StoragePluginContext
ImageStudio.Storage.Local       LocalMediaStorage (bez kontekstu; rejestrowana jawnie jako pierwsza)
ImageStudio.Storage.Cloud       CloudMirrorStorage (lustro: cache, trwała kolejka, status, odczyt/usuwanie, akcje signin/signout)
                                └─ OAuthCloudStorage (OAuth + HttpClient: clientid, sekret, tokeny, przeglądarka)
ImageStudio.Storage.OneDrive    OneDriveStorage : OAuthCloudStorage       (ctor(StoragePluginContext))
ImageStudio.Storage.GoogleDrive GoogleDriveStorage : OAuthCloudStorage    (ctor(StoragePluginContext))
ImageStudio.Storage.Mega        MegaStorage : CloudMirrorStorage + IMegaClient + MegaApiClientAdapter (ctor(StoragePluginContext))
ImageStudio (aplikacja)         StoragePluginDiscovery (Composition), MediaStorageHost.Owner(), StorageSettingsTab (pola, prośba o kod,
                                status na żywo), StorageSecrets (strażnik prefiksu), UserDataCleanup (wzorzec secrets-storage-*.bin)
```

### Strategia czy klasa bazowa (decyzja 5)

Wybieram **klasę bazową z dwoma poziomami**: `CloudMirrorStorage` (ogólne lustro, abstrakcyjne człony uwierzytelniania) i `OAuthCloudStorage : CloudMirrorStorage` (całe obecne OAuth). Uzasadnienie: obecne testy i kod używają `CloudMirrorStorage` jako typu (`Mirror<T> where T : CloudMirrorStorage`, `CloudLiveTests.CrudAsync(CloudMirrorStorage)`, `.Cache`, `.FlushAsync`, `.ReadRemoteAsync`, `.BrowserOverride` na OneDrive), więc podział dziedziczenia nie zmienia ani jednego wywołania. Strategia wymagałaby nowego interfejsu, dwóch implementacji, przekazywania sekretów/HTTP do strategii i przepinania `Settings`/`Configure`/`Actions` przez delegację, czyli więcej kodu dla tej samej elastyczności. Uwierzytelnianie i transport są tu sprzężone (OAuth potrzebuje `HttpClient`, MEGA własnego klienta), więc naturalnie siedzą w podklasie razem z wywołaniami API. MEGA dziedziczy wprost po lustrze i nie kopiuje kolejki, cache ani statusu.

### Kontrakty (pełne sygnatury, docelowo po etapach 1, 3, 4, 6)

```csharp
namespace ImageStudio.Storage;

public enum StorageFieldKind { Text, Folder, Email, Password, Code }

public sealed record StorageSetting(string Key, string LabelKey, string Value, bool IsFolder = false, bool IsSecret = false,
                                    bool Masked = false, StorageStatus? Note = null,
                                    StorageFieldKind? Kind = null, bool Transient = false)
{
    public StorageFieldKind EffectiveKind => Kind ?? (IsFolder ? StorageFieldKind.Folder : Masked ? StorageFieldKind.Password : StorageFieldKind.Text);
}

public sealed record StorageAction(string Key, string LabelKey, string? WorkingKey = null);
public sealed record StorageStatus(string Key, string? Detail = null);
public sealed record StoragePrompt(string Key, string LabelKey, StorageFieldKind Kind = StorageFieldKind.Code, StorageStatus? Message = null);

public interface IStorageInteraction
{
    Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct);
}

public static class StorageInteraction { public static IStorageInteraction None { get; } }

public sealed record StoragePluginContext(IStorageSecrets Secrets, Func<HttpMessageHandler> CreateHttpHandler, TimeSpan HttpTimeout);

public interface IMediaStoragePlugin : IMediaStorage
{
    // ... dotychczasowe człony bez zmian ...
    Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                        CancellationToken ct = default) => RunActionAsync(key, ct);      // etap 1
    void Resume() { }                                                                       // etap 4
    event EventHandler? StatusChanged { add { } remove { } }                                // etap 6
}
```

### Przepływ logowania (MEGA)

1. Użytkownik wybiera MEGA, wpisuje e-mail (pole `Kind=Email, Transient`) i hasło (`Kind=Password, Masked, Transient`), klika „Zaloguj”.
2. `StorageOption.RunAsync`: `Configure` dostaje **tylko pola nietrwałe = false** (foldery cache); pola `Transient` idą jako `inputs` do `RunActionAsync(key, inputs, this, ct)`.
3. `MegaStorage.SignInAsync` → `IMegaClient.LoginAsync(email, hasło, null)`. Przy 2FA klient rzuca `MegaMfaRequiredException` → `interaction.AskAsync(StoragePrompt("mfa", ...))` → w zakładce pojawia się pole kodu + „Potwierdź” (Anuluj logowanie przerywa) → `LoginAsync(email, hasło, kod)`.
4. Wynik to zserializowana sesja; w sekrecie `storage-mega` zapisujemy `{"Email":..., "Session":...}`. Hasło nie jest nigdzie zapisywane; po sukcesie VM czyści pola `Transient`.
5. `Resume()` wysyła zaległe pliki; status „Zalogowano jako e-mail” (na żywo przez `StatusChanged`).

### Przepływ zapisu offline (wszystkie chmury)

1. `WriteAsync` → plik w cache lokalnym → wpis `PendingOperation("upload", ścieżka)` w trwałej kolejce (sekret `storage-{Id}-pending`, JSON) → zadanie w tle.
2. Zadanie: brak tokenu/sesji → wpis zostaje; sukces → wpis usunięty; błąd → wpis zostaje, błąd w statusie.
3. `DeleteAsync` zastępuje wpis tej samej lokalizacji wpisem `"delete"`.
4. `Resume()` (start aplikacji w `MediaStorageHost.Load` i po udanym logowaniu) planuje wszystkie wpisy kolejki.

### Odkrywanie wtyczek

- `GetEntryAssembly().GetReferencedAssemblies()` **nie zadziała**: kompilator zapisuje w metadanych tylko biblioteki, których typów kod używa (po etapie 3 aplikacja nie używa typów OneDrive/Google/MEGA), a w testach wejściem jest testhost.
- Zamiast tego target MSBuild w `ImageStudio.csproj` dopisuje do assembly aplikacji `[assembly: AssemblyMetadata("ImageStudio.StoragePlugin", "<nazwa biblioteki>")]` dla każdej `@(StorageDll)` (ta sama lista, z której dziś powstaje `storage\`). Działa w zwykłym buildzie (DLL w `storage\`, ładuje `PluginResolver`), w exe jednoplikowym (DLL w paczce, ładuje domyślny kontekst) i w testach (DLL obok testów).
- `StoragePluginDiscovery.FindTypes(typeof(ServiceCollectionExtensions).Assembly)`: `Assembly.Load(nazwa)` → publiczne nieabstrakcyjne typy `IMediaStoragePlugin` z konstruktorem `(StoragePluginContext)` → sortowanie po `Type.FullName` (ordinal). `LocalMediaStorage` nie ma tego konstruktora, więc jest rejestrowana jawnie i zawsze pierwsza. Porządek po etapie 7: `local`, `googledrive`, `mega`, `onedrive` (dziś `local`, `onedrive`, `googledrive`: zmiana kolejności na liście jest świadoma).
- `MediaStorageHost` odrzuca wtyczki o `Id` niezgodnym z `^[a-z0-9]+$` i duplikaty (pierwsza wygrywa).
- Sekrety: `StorageSecrets` przyjmuje tylko nazwy `^storage-[a-z0-9]+(-[a-z0-9]+)*$`, więc każdy sekret wtyczki to plik `secrets-storage-*.bin`; czyszczenie „Logowania i klucze” kasuje pliki pasujące do wzorca (bez listy wtyczek).

### Czego NIE robimy

- Nie wprowadzamy obiektu-strategii uwierzytelniania ani listy nazw sekretów deklarowanej przez wtyczkę (wzorzec prefiksu wystarcza i obejmuje też kolejkę).
- Nie dodajemy spinnera `Activity.Begin` dla wysyłek w tle (OneDrive/Google też go nie mają; żądania i tak widać w rejestrze żądań, o ile klient używa handlera z kontekstu).
- Nie zmieniamy limitu 250 MB OneDrive, nie dodajemy listowania plików z innego komputera, udostępniania linków MEGA, kosza ani wersji.
- Nie tłumaczymy istniejących komunikatów wyjątków OAuth (zostają po polsku jak są).
- Nie tworzymy osobnego pliku ani kategorii czyszczenia dla kolejki (leży w sekretach wtyczki).
- Nie przenosimy galerii na odczyt przez `IMediaStorage` (opisane w `docs/dev/architecture.md` jako „jeszcze nie przez kontrakt”).
- Nie dajemy statusu na żywo wtyczce Local (nie ma stanu).

## Reguły dla każdego wykonawcy (wklejane do każdego zlecenia)

- Kod, komentarze i UI po polsku; pliki z końcami linii CRLF i kodowaniem UTF-8 (sprawdź: istniejące pliki mają CRLF; narzędzie Edit zachowuje końce linii, nowy plik po zapisaniu przekonwertuj: `pwsh -c "$p='<ścieżka>'; $t=[IO.File]::ReadAllText($p) -replace '(?<!\r)\n',\"`r`n\"; [IO.File]::WriteAllText($p,$t,[Text.UTF8Encoding]::new($false))"`).
- DI tylko w `ImageStudio/Composition/ServiceCollectionExtensions.cs`. ViewModele bez `MessageBox` i `Application.Current`.
- Teksty dla użytkownika tylko przez klucze w obu słownikach (`ImageStudio.Shared/Localization/Strings.pl.json` i `.en.json`) + `{loc:T klucz}`/`Loc.T("klucz")`. Po dopisaniu kluczy: `pwsh tools/Check-Translations.ps1 -Fix`, potem `-Strict` musi zwrócić 0.
- Hasła, tokeny i sesje tylko przez `IStorageSecrets`; nigdy do logów, `UserState`, `StorageConfig`, komunikatów wyjątków.
- Każdy klient HTTP z handlerem od `StoragePluginContext.CreateHttpHandler()` (w aplikacji `OutgoingRequestRegistry.CreateHandler()`).
- Testy w `ImageStudio.Tests`, bez sieci (atrapy jak `CloudStorageTests.MemorySecrets`/`StubHandler`); okna przez `WpfHost.Run`; obiekty WPF zmieniaj z wątku, który je utworzył.
- Nie commituj, nie zmieniaj plików spoza listy etapu. Przed edycją przeczytaj cały plik.
- Gdy aplikacja jest uruchomiona (błąd zablokowanego pliku), buduj i testuj z `-p:BaseOutputPath=C:\Temp\is-build\` i nie zabijaj procesu.
- STOP ogólny: jeśli krok wymaga zmiany pliku spoza listy, jeśli istniejący test trzeba zmienić inaczej niż opisano albo jeśli coś w kodzie nie zgadza się z „Faktami z kodu”, przerwij i zgłoś (co, gdzie, dlaczego) zamiast improwizować.

Komendy weryfikacji (wspólne):

```powershell
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
dotnet test ImageStudio.Tests --filter "FullyQualifiedName~<Klasa1>|FullyQualifiedName~<Klasa2>"
pwsh tools/Check-Translations.ps1 -Strict
```

---

## Etap 0: badanie MegaApiClient (bez zmian w repozytorium)

**Cel:** fakty o bibliotece i decyzja GO/STOP dla etapu 7. Etapy 1-6 nie zależą od wyniku i mogą iść równolegle w czasie (ale nie w tym samym katalogu roboczym: etap 0 nie dotyka repozytorium).

**Pliki:** brak zmian w repozytorium. Projekt próbny tylko w katalogu tymczasowym (scratchpad).

**Pytania (każde z dowodem: URL albo wynik komendy):**

1. Najnowsza wersja na nuget.org, data wydania, liczba pobrań, data ostatniego commita w repozytorium (github.com/gpailler/MegaApiClient).
2. Licencja (plik LICENSE w repo, pole licencji na nuget.org).
3. TFM paczki (np. `netstandard2.0`, `net46`); czy restore i build działa w projekcie `net10.0-windows` (projekt próbny).
4. Logowanie: dokładne sygnatury `Login(...)`, `LoginAsync(...)`, `GenerateAuthInfos(...)`, typ `AuthInfos`, `LogonSessionToken` (pola, czy serializowalny do JSON), czy da się wznowić sesję bez hasła (`Login(LogonSessionToken)` lub odpowiednik), `Logout()` (czy unieważnia sesję po stronie serwera).
5. 2FA/MFA: czy jest parametr kodu (np. `mfaKey`) i jaki wyjątek/kod (`ApiResultCode`) zwraca logowanie bez kodu na koncie z 2FA.
6. Operacje: `GetNodes`, `CreateFolder`, `UploadAsync(Stream, string, INode, IProgress<double>, DateTime?, CancellationToken)` (dokładna sygnatura), `DownloadAsync`/`Download`, `Delete(INode, bool moveToTrash)`; czy nazwa pliku może się powtarzać w folderze; jak rozpoznać „brak pliku”.
7. Transport: czy konstruktor `MegaApiClient` przyjmuje `IWebClient`, a `WebClient` przyjmuje `HttpMessageHandler` (sygnatura konstruktora); jeśli nie, czy `IWebClient` jest publiczny i jakie ma metody (do własnej implementacji na `HttpClient` z naszym handlerem).
8. Bezpieczeństwo wątków (czy jedna instancja obsłuży równoległe wywołania), limity rozmiaru, znane otwarte zgłoszenia o niedziałającym logowaniu (issues z ostatnich 12 miesięcy).
9. Dziennikowanie: czy biblioteka coś loguje (i czy mogłaby zalogować hasło/klucz).

**BRAMKA DECYZYJNA (wynik w pierwszej linii raportu):**

- **GO**: licencja MIT/Apache-2.0/BSD; build na `net10.0-windows` przechodzi; jest wznowienie sesji bez hasła; są upload/download/delete/folder.
- **GO-Z-ZASTRZEŻENIEM** (sesja główna pyta użytkownika): brak 2FA; brak wstrzyknięcia `HttpMessageHandler` (wtedy własny `IWebClient`); ostatnie wydanie starsze niż 2 lata.
- **STOP**: licencja nie-permisywna lub brak; brak wznowienia sesji (wymagałoby trzymania hasła: zakazane); build na `net10.0` nie przechodzi; otwarte, niezałatane zgłoszenia, że logowanie do MEGA nie działa.

### ZLECENIE DLA HAIKU (etap 0)

```
Jesteś badaczem. NIE zmieniaj niczego w c:\Users\przem\source\ImageStudio. Zbadaj bibliotekę NuGet MegaApiClient
(nuget.org/packages/MegaApiClient, github.com/gpailler/MegaApiClient: README, LICENSE, Releases, pliki źródłowe
IMegaApiClient.cs, MegaApiClient.cs, WebClient*.cs, Exceptions.cs/ApiResultCode, Node.cs) i odpowiedz na pytania 1-9
z sekcji "Etap 0" pliku docs/superpowers/plans/2026-11-25-modul-chmury-mega.md (przeczytaj ją). Użyj WebFetch.
Sprawdź kompilację: w katalogu tymczasowym (scratchpad, NIE w repo) `dotnet new classlib -f net10.0 -n MegaProbe`,
w csproj ustaw <TargetFramework>net10.0-windows</TargetFramework>, `dotnet add package MegaApiClient`, w Class1.cs
napisz metodę, która tylko kompiluje się z wywołaniami: new MegaApiClient(...), GenerateAuthInfos/Login (z kodem 2FA,
jeśli istnieje), Login(LogonSessionToken) lub odpowiednik, GetNodes, CreateFolder, UploadAsync, DownloadAsync/Download,
Delete, Logout; `dotnet build`. Nie loguj się do MEGA, nie wysyłaj żadnych danych.
Raport (max 60 linii): pierwsza linia "DECYZJA: GO | GO-Z-ZASTRZEŻENIEM | STOP" wg bramki z planu; potem tabela
pytanie -> odpowiedź -> dowód (URL lub fragment wyniku komendy); potem dokładne sygnatury C# skopiowane ze źródła
(logowanie, sesja, 2FA, upload/download/delete, konstruktory MegaApiClient i WebClient); potem lista ryzyk.
Jeśli czegoś nie da się ustalić, napisz "nieustalone" i dlaczego; nie zgaduj.
```

### KONTROLA SESJI GŁÓWNEJ (etap 0)

- `git status` czysty (etap nic nie zmienia).
- Sprawdź 2-3 dowody z raportu (np. LICENSE, sygnatura `Login(LogonSessionToken)`), bo od nich zależy etap 7.
- Przy STOP: etapy 1-6 i 8 (bez części MEGA) idą dalej; etap 7 wstrzymany, decyzja do użytkownika.
- Przy GO: wpisz do sekcji „Etap 7 → Fakty z etapu 0” w tym pliku wersję paczki i sygnatury (Haiku w etapie 7 ma je mieć czarno na białym).

---

## Etap 1: kontrakt logowania w Storage.Core i zakładka Zapis (bez zmiany zachowania)

**Cel:** rodzaje pól, pola nietrwałe, akcja z wartościami pól, prośba o kod w trakcie akcji. Żadna istniejąca wtyczka jeszcze tego nie używa; OneDrive/Google/Local działają identycznie.

**Pliki:**

- zmiana `ImageStudio.Storage.Core/MediaStorage.cs`
- zmiana `ImageStudio/ViewModels/StorageSettingsTab.cs`
- zmiana `ImageStudio/Views/StorageSettingsTabView.xaml`
- zmiana `ImageStudio/Services/MediaStorageHost.cs` (tylko `WithoutSecrets`)
- zmiana `ImageStudio.Shared/Localization/Strings.pl.json`, `Strings.en.json`
- nowy `ImageStudio.Tests/StorageLoginContractTests.cs`

**Kod: `MediaStorage.cs`** (dopisać/zmienić; reszta pliku bez zmian):

```csharp
// Rodzaj pola w zakładce Zapis: Text i Email to zwykłe pole tekstowe, Folder dodaje przycisk wyboru folderu,
// Password ma ukryte znaki, Code to krótki kod jednorazowy (np. 2FA)
public enum StorageFieldKind { Text, Folder, Email, Password, Code }

// (dotychczasowy komentarz) + Kind: rodzaj pola (null = wynika ze starych flag IsFolder/Masked); Transient: wartość tylko
// dla akcji (np. hasło do logowania): nie trafia do Configure, do pliku ustawień ani do sekretów, okno czyści ją po udanej akcji
public sealed record StorageSetting(string Key, string LabelKey, string Value, bool IsFolder = false, bool IsSecret = false,
                                    bool Masked = false, StorageStatus? Note = null,
                                    StorageFieldKind? Kind = null, bool Transient = false)
{
    public StorageFieldKind EffectiveKind => Kind ?? (IsFolder ? StorageFieldKind.Folder : Masked ? StorageFieldKind.Password : StorageFieldKind.Text);
}

// Przycisk w zakładce wtyczki; WorkingKey: klucz tekstu pokazywanego w trakcie akcji (null = storage.status.working)
public sealed record StorageAction(string Key, string LabelKey, string? WorkingKey = null);

// Prośba wtyczki o dodatkową wartość w trakcie akcji (np. kod 2FA); Message: opis nad polem
public sealed record StoragePrompt(string Key, string LabelKey, StorageFieldKind Kind = StorageFieldKind.Code, StorageStatus? Message = null);

// Rozmowa z użytkownikiem w trakcie akcji (zakładka Zapis pokazuje pole i przycisk Potwierdź)
public interface IStorageInteraction
{
    // Wartość wpisana przez użytkownika; anulowanie (ct albo przycisk Anuluj) kończy się OperationCanceledException
    Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct);
}

// Brak rozmówcy: akcja wołana bez okna (stara ścieżka RunActionAsync(key))
public static class StorageInteraction
{
    public static IStorageInteraction None { get; } = new NoInteraction();

    private sealed class NoInteraction : IStorageInteraction
    {
        public Task<string?> AskAsync(StoragePrompt prompt, CancellationToken ct) =>
            throw new InvalidOperationException("Ta operacja wymaga danych od użytkownika (Ustawienia, zakładka Zapis).");
    }
}
```

W `IMediaStoragePlugin` dopisać pod `RunActionAsync(string key, ...)`:

```csharp
    // Akcja z wartościami pól nietrwałych (Transient) i możliwością zapytania użytkownika; domyślnie stara ścieżka
    Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                        CancellationToken ct = default) => RunActionAsync(key, ct);
```

**Kod: `StorageSettingsTab.cs`:**

- `StorageField`: dodać `public StorageFieldKind Kind { get; } = setting.EffectiveKind;`, `public bool IsTransient { get; } = setting.Transient;`; zmienić `IsFolder => Kind == StorageFieldKind.Folder`, `IsMasked => Kind == StorageFieldKind.Password` (zgodne ze starymi flagami przez `EffectiveKind`).
- `StorageOption` implementuje jawnie `IStorageInteraction`; w konstruktorze `_ui = SynchronizationContext.Current` (wzór `Activity.cs:25`), `SubmitPromptCommand = new RelayCommand(() => _prompt?.TrySetResult(PromptValue.Trim()));`.
- Nowe właściwości `[ObservableProperty]`: `_isPrompting` (bool), `_promptLabel`, `_promptMessage`, `_promptValue` (string, domyślnie `""`); pole `private TaskCompletionSource<string?>? _prompt;`; `public IRelayCommand SubmitPromptCommand { get; }`.
- Komendy akcji przekazują cały `StorageAction`: `new AsyncRelayCommand(() => RunAsync(a), () => !IsBusy && _plugin.CanRun(a.Key))`.
- `RunAsync(StorageAction action)`:

```csharp
    // Pola nietrwałe (hasło, e-mail logowania) idą tylko do akcji; Configure dostaje resztę (np. Client ID, foldery)
    private async Task RunAsync(StorageAction action)
    {
        IsBusy = true;
        ErrorText = "";
        StatusText = Loc.T(action.WorkingKey ?? "storage.status.working");
        foreach (var item in Actions) item.Command.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        try
        {
            _plugin.Configure(Fields.Where(f => !f.IsTransient).ToDictionary(f => f.Key, f => f.Value));
            var inputs = Fields.Where(f => f.IsTransient).ToDictionary(f => f.Key, f => f.Value);
            await _plugin.RunActionAsync(action.Key, inputs, this, _cts.Token);
            foreach (var field in Fields.Where(f => f.IsTransient)) field.Value = ""; // hasło nie zostaje w oknie po sukcesie
            IsBusy = false;
            Refresh();
        }
        catch (OperationCanceledException) { IsBusy = false; Refresh(); }
        catch (Exception ex) { IsBusy = false; Refresh(ex.Message); }
        finally
        {
            EndPrompt();
            _cts.Dispose();
            _cts = null;
        }
    }

    async Task<string?> IStorageInteraction.AskAsync(StoragePrompt prompt, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            _prompt = answer;
            PromptLabel = Loc.T(prompt.LabelKey);
            PromptMessage = prompt.Message is { } m ? Loc.T(m.Key, m.Detail) : "";
            PromptValue = "";
            IsPrompting = true;
        });
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        try { return await answer.Task; }
        finally { OnUi(EndPrompt); }
    }

    private void EndPrompt()
    {
        _prompt = null;
        IsPrompting = false;
        PromptValue = "";
        PromptLabel = "";
        PromptMessage = "";
    }

    // Wtyczka może pytać z wątku tła: zmiany właściwości wracają na wątek okna (jak Activity.Notify)
    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
```

- `StorageSettingsTab.Apply`: `StorageConfig = Options.ToDictionary(o => o.Id, o => o.Fields.Where(f => !f.IsTransient).ToDictionary(f => f.Key, f => f.Value))`.

**Kod: `MediaStorageHost.WithoutSecrets`:** zbiór wycinanych kluczy `Settings.Where(s => s.IsSecret || s.Transient)` (obrona, gdyby pole nietrwałe przyszło inną drogą). Komentarz: „bez pól haseł i pól nietrwałych”.

**Widok `StorageSettingsTabView.xaml`:** po przycisku `storage.cancel` (linia ~58-60) dodać:

```xml
            <StackPanel Margin="0,8,0,0" Visibility="{Binding Selected.IsPrompting, Converter={StaticResource BoolToVisible}}">
                <TextBlock Text="{Binding Selected.PromptMessage}" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding Selected.PromptLabel}" Style="{StaticResource SectionHeader}"/>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBox Text="{Binding Selected.PromptValue, UpdateSourceTrigger=PropertyChanged}"/>
                    <Button Grid.Column="1" Content="{loc:T storage.prompt.submit}" Command="{Binding Selected.SubmitPromptCommand}"
                            Margin="6,0,0,0" Padding="12,2" Style="{StaticResource SecondaryButton}"/>
                </Grid>
            </StackPanel>
```

Bez `IsDefault` (Enter zatwierdza okno Ustawień).

**Tłumaczenia:**

| klucz | PL | EN |
|---|---|---|
| `storage.prompt.submit` | Potwierdź | Confirm |

**Testy `StorageLoginContractTests`** (atrapa `FakeLoginPlugin : IMediaStoragePlugin` w pliku testu: pola `email` (`Kind: Email, Transient: true`), `password` (`Kind: Password, Masked: true, Transient: true`), `image` (`IsFolder: true`); akcja `new("signin", "storage.cloud.signIn", "storage.status.working")`; zapamiętuje ostatnie `Configure` i `inputs`; opcjonalnie pyta o kod przez `interaction.AskAsync`; flaga `Fail` rzuca `InvalidOperationException("zły login")`; `ReadAsync`/`WriteAsync`… rzucają `NotSupportedException`). `StorageOption` twórz przez `await Task.Run(() => new StorageOption(plugin))` (bez kontekstu synchronizacji xUnit zmiany właściwości są natychmiastowe). Pomocnik `static async Task WaitUntilAsync(Func<bool> condition)` (pętla co 10 ms, limit 5 s, potem `Assert.Fail`).

- `Setting_kind_falls_back_to_the_old_flags` (`IsFolder` → Folder, `Masked` → Password, nic → Text, jawny `Kind` wygrywa).
- `Action_gets_transient_values_as_inputs_and_configure_never_sees_them`
- `Transient_fields_are_cleared_after_a_successful_action_and_kept_after_a_failure`
- `Plugin_can_ask_for_a_code_and_gets_the_submitted_value` (start komendy bez await, `WaitUntilAsync(() => option.IsPrompting)`, `PromptValue = " 123456 "`, `SubmitPromptCommand.Execute(null)`, plugin dostał `"123456"`, potem `IsPrompting == false`).
- `Cancel_while_asking_for_a_code_ends_the_action_without_error` (`CancelCommand.Execute(null)` → `IsBusy == false`, `ErrorText == ""`, `IsPrompting == false`).
- `Transient_fields_never_reach_the_settings_result_or_the_persisted_config` (`StorageSettingsTab.Apply` i `MediaStorageHost.WithoutSecrets` bez `email`/`password`).
- `Old_plugin_without_the_new_overload_runs_its_action` (atrapa implementująca tylko `RunActionAsync(string, CancellationToken)`; wywołanie przez `IMediaStoragePlugin` z `inputs` trafia do starej metody).
- `Storage_view_shows_a_password_box_and_hides_the_prompt` (`WpfHost.Run`, wzór `SettingsTabsTests.Window_shows_a_view_for_each_tab_and_rejects_a_tab_without_view` i `Current()` z tego pliku: okno z `StorageSettingsTab` dla hosta `[new LocalMediaStorage(), fake]`, wybrana atrapa; jest `PasswordBox`, panel z `storage.prompt.submit` zwinięty).

**Kryteria akceptacji:** build i całość testów zielone; `CloudStorageTests`, `StorageSettingsTabTests`, `SettingsTabsTests` bez zmian i zielone; `Check-Translations -Strict` = 0.

**STOP:** gdy CommunityToolkit nie generuje właściwości dla `StorageOption` jawnie implementującej interfejs (błąd kompilacji, którego nie rozwiązuje zmiana nazw pól); gdy `LocalizationTests` zgłasza klucz, którego nie ma w tabeli.

### ZLECENIE DLA HAIKU (etap 1)

```
Wykonaj ETAP 1 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md.
Przeczytaj najpierw sekcje "Fakty z kodu", "Kontrakty", "Reguły dla każdego wykonawcy" i cały "Etap 1".
Zmieniasz tylko pliki z listy etapu. Kod nowych typów i metod przepisz z planu (komentarze po polsku, CRLF).
Nie zmieniaj OneDrive/Google/Local ani CloudMirrorStorage. Klucz storage.prompt.submit dopisz do obu słowników,
potem `pwsh tools/Check-Translations.ps1 -Fix`. Napisz testy StorageLoginContractTests (nazwy metod z planu).
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`; `dotnet test ImageStudio.Tests --filter "FullyQualifiedName~StorageLoginContractTests|FullyQualifiedName~StorageSettingsTabTests|FullyQualifiedName~SettingsTabsTests|FullyQualifiedName~CloudStorageTests"`;
`dotnet test ImageStudio.Tests`; `pwsh tools/Check-Translations.ps1 -Strict`.
Stań i zgłoś, jeśli zachodzi warunek STOP z planu albo któryś istniejący test pada. Nie commituj.
Raport: lista zmienionych plików, wyniki komend (liczby testów), odstępstwa od planu.
```

### KONTROLA SESJI GŁÓWNEJ (etap 1)

```powershell
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
pwsh tools/Check-Translations.ps1 -Strict
git diff --stat
git diff ImageStudio/ViewModels/StorageSettingsTab.cs ImageStudio.Storage.Core/MediaStorage.cs
```

W diffie: `Configure` dostaje tylko `!IsTransient`; `Apply` filtruje `IsTransient`; czyszczenie pól po sukcesie (nie po błędzie); `TaskCompletionSource` z `RunContinuationsAsynchronously`; rejestracja `ct.Register` w `using`; brak `Application.Current`/`Dispatcher` w VM; `IsDefault` nie dodany.

---

## Etap 2: rozdzielenie lustra i OAuth w Storage.Cloud

**Cel:** `CloudMirrorStorage` = ogólne lustro z abstrakcyjnym uwierzytelnianiem; całe OAuth w nowej `OAuthCloudStorage`. OneDrive/Google bez zmian zachowania, wszystkie testy zielone bez modyfikacji.

**Pliki:**

- zmiana `ImageStudio.Storage.Cloud/CloudMirrorStorage.cs`
- nowy `ImageStudio.Storage.Cloud/OAuthCloudStorage.cs`
- zmiana `ImageStudio.Storage.OneDrive/OneDriveStorage.cs`, `ImageStudio.Storage.GoogleDrive/GoogleDriveStorage.cs` (tylko klasa bazowa `: OAuthCloudStorage`)
- nowy `ImageStudio.Tests/CloudMirrorBaseTests.cs`

**`CloudMirrorStorage` po zmianie** (przenieść, nie przepisywać logiki kolejki):

```csharp
public abstract class CloudMirrorStorage : IMediaStoragePlugin
{
    // pola _pending, _backgroundGate, _byFile, _lastError bez zmian (bez _env)

    protected CloudMirrorStorage(IStorageSecrets secrets) => Secrets = secrets;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string? HelpKey { get; }

    protected IStorageSecrets Secrets { get; }
    public LocalMediaStorage Cache { get; } = new();

    // --- uwierzytelnianie: OAuth (OAuthCloudStorage) albo poświadczenia z sesją (np. MEGA) ---
    protected abstract bool SignedIn { get; }
    // Kto jest zalogowany (np. e-mail) do statusu; null = sam "Zalogowano."
    protected virtual string? AccountLabel => null;
    // Token albo sesja do operacji w chmurze; null = niezalogowany
    protected abstract Task<string?> AccessTokenAsync(CancellationToken ct);
    protected virtual IEnumerable<StorageSetting> AuthSettings => [];
    protected virtual void ConfigureAuth(IReadOnlyDictionary<string, string> values) { }
    protected virtual string SignInLabelKey => "storage.cloud.signIn";
    protected virtual string? SignInWorkingKey => null;
    protected abstract Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct);
    protected abstract Task SignOutAsync(CancellationToken ct);

    // --- operacje w chmurze po nazwie pliku (bez zmian) ---
    protected abstract Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct);
    protected abstract Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct);
    protected abstract Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct);

    public IReadOnlyList<StorageSetting> Settings => [.. AuthSettings, .. Cache.Settings];

    public void Configure(IReadOnlyDictionary<string, string> values)
    {
        ConfigureAuth(values);
        Cache.Configure(values);
    }

    public string? LocalFolder(MediaKind kind) => Cache.LocalFolder(kind);

    public IReadOnlyList<StorageAction> Actions => [new("signin", SignInLabelKey, SignInWorkingKey), new("signout", "storage.cloud.signOut")];
    public bool CanRun(string key) => key switch { "signin" => !SignedIn, "signout" => SignedIn, _ => true };

    public StorageStatus? Status => _lastError is not null ? new("storage.status.error", _lastError)
        : !SignedIn ? new("storage.status.signedOut")
        : AccountLabel is { } account ? new("storage.status.signedInAs", account) : new("storage.status.signedIn");

    public Task RunActionAsync(string key, CancellationToken ct = default) =>
        RunActionAsync(key, new Dictionary<string, string>(), StorageInteraction.None, ct);

    public async Task RunActionAsync(string key, IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction,
                                     CancellationToken ct = default)
    {
        _lastError = null;
        if (key == "signout") await SignOutAsync(ct);
        else if (key == "signin") await SignInAsync(inputs, interaction, ct);
    }

    // WriteAsync, DeleteAsync, FlushAsync, Background, RunBackgroundAsync, ReadRemoteAsync, ReadAsync, ExistsAsync,
    // GetLocalPathAsync: przenieść bez zmian (AccessTokenAsync jest teraz abstrakcyjne)
}
```

**`OAuthCloudStorage.cs`** (nowy; wszystko, co dziś jest OAuth/HTTP w `CloudMirrorStorage.cs`, przeniesione bez zmian logiki):

```csharp
// Wtyczki chmurowe logowane przez OAuth w przeglądarce (OneDrive, Google Drive): Client ID (i sekret), tokeny w sekrecie
// storage-{Id}, odświeżanie tokenu, klient HTTP z handlerem aplikacji (rejestr żądań)
public abstract class OAuthCloudStorage : CloudMirrorStorage
{
    protected OAuthCloudStorage(IStorageSecrets secrets, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
        : this(secrets, handler, environment, null) { }

    protected OAuthCloudStorage(IStorageSecrets secrets, HttpMessageHandler? handler, Func<string, string?>? environment, TimeSpan? timeout)
        : base(secrets) { /* ciało dzisiejszego konstruktora CloudMirrorStorage.cs:29-34 bez linii Secrets = ... */ }

    protected abstract OAuthEndpoints Endpoints { get; }
    protected virtual bool UsesClientSecret => false;
    protected abstract string EnvClientIdVar { get; }
    protected virtual string? EnvClientSecretVar => null;
    protected HttpClient Http { get; }
    public Action<string>? BrowserOverride { get; set; }
    public OAuthFlow Flow { get; }
    public string ClientId => Secrets.Load(ClientIdName) ?? "";

    protected override bool SignedIn => Secrets.Load(TokenName) is not null;
    protected override string? AccountLabel => TokenEmail();
    protected override IEnumerable<StorageSetting> AuthSettings { get; }   // ciało: dzisiejsze Settings bez Cache.Settings (:83-88)
    protected override void ConfigureAuth(IReadOnlyDictionary<string, string> values) { }  // dzisiejsze :104-106
    protected override async Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct) { } // dzisiejsze :138-140
    protected override Task SignOutAsync(CancellationToken ct) { Secrets.Save("", TokenName); return Task.CompletedTask; }
    protected override async Task<string?> AccessTokenAsync(CancellationToken ct) { } // dzisiejsze :146-153
    protected static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct) { } // dzisiejsze :254-258

    // prywatne: _env, OpenBrowser, EffectiveClientId, EffectiveClientSecret, Fallback, TokenName, ClientSecretName,
    // ClientIdName, SourceNote, TokenEmail: przenieść bez zmian
}
```

`AuthSettings` jako właściwość z ciałem `get { ... }` (nie `{ get; }`), żeby czytała sekrety przy każdym odczycie jak dziś.

**Testy `CloudMirrorBaseTests`** (atrapa `TestMirror : CloudMirrorStorage` w pliku testu: `Id => "test"`, sesja w sekrecie `storage-test` jako zwykły tekst, `AccountLabel` = wartość sesji, `SignInAsync` bierze `inputs["email"]` i zapisuje sesję, `SignOutAsync` ją kasuje, `Remote` = `ConcurrentDictionary<string, byte[]>` dla Upload/Download/Delete; foldery cache w katalogu tymczasowym jak `CloudStorageTests.Mirror`; `MemorySecrets` skopiuj z `CloudStorageTests`):

- `Credential_mirror_signs_in_with_inputs_and_uploads_in_the_background`
- `Sign_out_clears_the_session_and_the_status_says_signed_out`
- `Status_shows_the_account_label_after_sign_in`
- `OAuth_plugins_keep_their_fields_and_actions` (OneDrive: klucze pól `clientid, image, video, audio`; Google: `clientid, clientsecret, image, video, audio`; akcje `signin, signout` z etykietami `storage.cloud.signIn`/`storage.cloud.signOut`).

**Tłumaczenia:** brak.

**Kryteria akceptacji:** `CloudStorageTests`, `OutgoingRequestIntegrationTests`, `CloudLiveTests` (pominięte) kompilują się i przechodzą **bez zmian w ich plikach**; nowa klasa testów zielona; całość zielona.

**STOP:** gdy którykolwiek istniejący test wymaga zmiany; gdy `OneDriveStorage`/`GoogleDriveStorage` potrzebują zmian poza klasą bazową.

### ZLECENIE DLA HAIKU (etap 2)

```
Wykonaj ETAP 2 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Fakty z kodu", "Strategia czy klasa bazowa", "Reguły dla każdego wykonawcy" i cały "Etap 2").
To przeniesienie kodu, nie przepisywanie: logikę OAuth z CloudMirrorStorage.cs przenieś do nowego OAuthCloudStorage.cs
bez zmian zachowania; kolejka w tle, cache i odczyt zostają w CloudMirrorStorage. W OneDriveStorage.cs i
GoogleDriveStorage.cs zmień tylko klasę bazową na OAuthCloudStorage. NIE zmieniaj żadnego istniejącego testu.
Dopisz ImageStudio.Tests/CloudMirrorBaseTests.cs z metodami z planu.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`; `dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudStorageTests|FullyQualifiedName~CloudMirrorBaseTests|FullyQualifiedName~OutgoingRequestIntegrationTests"`; `dotnet test ImageStudio.Tests`.
Stań i zgłoś, jeśli istniejący test pada lub wymaga zmiany. Nie commituj. Raport: pliki, wyniki, odstępstwa.
```

### KONTROLA SESJI GŁÓWNEJ (etap 2)

```powershell
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
git diff --stat   # ImageStudio.Tests: tylko nowy CloudMirrorBaseTests.cs
git diff ImageStudio.Storage.Cloud
```

W diffie: nazwy sekretów `storage-{Id}`, `-client`, `-clientid` bez zmian; `Http.Timeout` jak dziś; `RunActionAsync` czyści `_lastError` przed akcją; `CanRun`/`Status` w bazie; `EnsureOkAsync` dostępne w OneDrive/Google.

---

## Etap 3: odkrywanie wtyczek przez refleksję i sekrety po prefiksie

**Cel:** DI, lista w Ustawieniach i czyszczenie sekretów nie znają nazw wtyczek chmurowych.

**Pliki:**

- zmiana `ImageStudio.Storage.Core/MediaStorage.cs` (`StoragePluginContext`)
- zmiana `ImageStudio.Storage.OneDrive/OneDriveStorage.cs`, `ImageStudio.Storage.GoogleDrive/GoogleDriveStorage.cs` (konstruktor z kontekstem)
- nowy `ImageStudio/Composition/StoragePluginDiscovery.cs`
- zmiana `ImageStudio/Composition/ServiceCollectionExtensions.cs`
- zmiana `ImageStudio/ImageStudio.csproj` (target metadanych)
- zmiana `ImageStudio/Services/MediaStorageHost.cs` (walidacja `Id`)
- zmiana `ImageStudio/Services/StorageSecrets.cs` (strażnik nazwy)
- zmiana `ImageStudio/Configuration/UserDataCleanup.cs`
- zmiana `ImageStudio.Tests/OutgoingRequestIntegrationTests.cs` (tylko linia ~231)
- nowy `ImageStudio.Tests/StoragePluginDiscoveryTests.cs`; zmiana `ImageStudio.Tests/UserDataCleanupTests.cs` (nowe testy)

**Kod: kontekst w `MediaStorage.cs`:**

```csharp
// Wszystko, czego wtyczka potrzebuje od aplikacji, bez znajomości DI. Wtyczkę odkrytą przez refleksję aplikacja tworzy
// konstruktorem (StoragePluginContext): sekrety (DPAPI, nazwy "storage-<id>..."), nowy handler HTTP (rejestr żądań
// aplikacji: lista w stopce, anulowanie) i limit czasu pojedynczego żądania
public sealed record StoragePluginContext(IStorageSecrets Secrets, Func<HttpMessageHandler> CreateHttpHandler, TimeSpan HttpTimeout);
```

**OneDrive/Google:** dodać `public OneDriveStorage(StoragePluginContext context) : base(context.Secrets, context.CreateHttpHandler(), null, context.HttpTimeout) { }` (analogicznie Google). Usunąć konstruktor `(IStorageSecrets, HttpMessageHandler, TimeSpan)`, jeśli `grep` nie znajdzie innych użyć niż `ServiceCollectionExtensions.cs`.

**`ImageStudio.csproj`:** po targecie `CollectPluginDlls` dodać:

```xml
  <!-- Lista bibliotek magazynu (te same co storage\) jako [assembly: AssemblyMetadata("ImageStudio.StoragePlugin", nazwa)]:
       z niej StoragePluginDiscovery odkrywa wtyczki. GetReferencedAssemblies() ich nie zna (kompilator pomija
       biblioteki, których typów kod nie używa), a metadane działają też w exe jednoplikowym i w testach -->
  <Target Name="AddStoragePluginMetadata" BeforeTargets="GetAssemblyAttributes" DependsOnTargets="CollectPluginDlls">
    <ItemGroup>
      <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
        <_Parameter1>ImageStudio.StoragePlugin</_Parameter1>
        <_Parameter2>%(StorageDll.Filename)</_Parameter2>
      </AssemblyAttribute>
    </ItemGroup>
  </Target>
```

Sprawdzenie po buildzie: `Select-String "ImageStudio.StoragePlugin" ImageStudio/obj/Debug/net10.0-windows/ImageStudio.AssemblyInfo.cs` → 4 wiersze (Local, Cloud, OneDrive, GoogleDrive). Brak wierszy = STOP.

**`StoragePluginDiscovery.cs`:**

```csharp
using System.Reflection;
using ImageStudio.Services;
using ImageStudio.Storage;
using Microsoft.Extensions.Logging;

namespace ImageStudio.Composition;

// Odkrywanie wtyczek magazynu: nazwy bibliotek z metadanych assembly aplikacji (target AddStoragePluginMetadata
// w ImageStudio.csproj), z nich publiczne typy IMediaStoragePlugin z konstruktorem (StoragePluginContext).
// Porządek stały: po pełnej nazwie typu. LocalMediaStorage nie ma takiego konstruktora: rejestruje ją jawnie
// AddImageStudioCore jako pierwszą (domyślną)
public static class StoragePluginDiscovery
{
    public const string MetadataKey = "ImageStudio.StoragePlugin";
    private static readonly ILogger Log = AppLog.For<MediaStorageHost>();

    public static IReadOnlyList<Type> FindTypes(Assembly app)
    {
        var types = new List<Type>();
        var names = app.GetCustomAttributes<AssemblyMetadataAttribute>()
                       .Where(a => a.Key == MetadataKey && !string.IsNullOrWhiteSpace(a.Value))
                       .Select(a => a.Value!).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                types.AddRange(PluginTypes(Assembly.Load(new AssemblyName(name))));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ReflectionTypeLoadException)
            {
                // Brak biblioteki (usunięta z storage\) albo jej zależności: aplikacja działa bez tej wtyczki
                Log.LogWarning("Pominięto wtyczkę magazynu {Name}: {Error}", name, ex.Message);
            }
        }
        return types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<Type> PluginTypes(Assembly assembly) =>
        assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }
                                               && typeof(IMediaStoragePlugin).IsAssignableFrom(t)
                                               && t.GetConstructor([typeof(StoragePluginContext)]) is not null);

    public static IMediaStoragePlugin Create(Type type, StoragePluginContext context) =>
        (IMediaStoragePlugin)Activator.CreateInstance(type, context)!;
}
```

**`ServiceCollectionExtensions.cs`:** usunąć `using ImageStudio.Storage.GoogleDrive;`/`OneDrive;` i linie 38-41; w ich miejsce:

```csharp
        services.AddSingleton(sp => new StoragePluginContext(sp.GetRequiredService<IStorageSecrets>(),
            () => sp.GetRequiredService<OutgoingRequestRegistry>().CreateHandler(), TimeSpan.FromMinutes(10)));
        // Wtyczki chmurowe odkrywane z bibliotek ImageStudio.Storage.* (nowy moduł = projekt + ProjectReference)
        foreach (var type in StoragePluginDiscovery.FindTypes(typeof(ServiceCollectionExtensions).Assembly))
            services.AddSingleton(typeof(IMediaStoragePlugin), sp => StoragePluginDiscovery.Create(type, sp.GetRequiredService<StoragePluginContext>()));
```

Rejestracja `LocalMediaStorage` (linie 35-36) zostaje przed pętlą.

**`MediaStorageHost`:** w konstruktorze `Plugins = plugins.Where(p => ValidId.IsMatch(p.Id)).DistinctBy(p => p.Id).ToList();` z `private static readonly Regex ValidId = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);` i komentarzem: „Id trafia do nazw sekretów i plików (storage-<id>): tylko małe litery i cyfry; duplikat traci pierwszeństwo”.

**`StorageSecrets.cs`:**

```csharp
// Sekrety wtyczek magazynu w SecretStore (DPAPI), żeby biblioteki wtyczek nie znały formatu pliku. Nazwa musi zaczynać się
// od "storage-": wtyczka nie odczyta kluczy dostawców AI, a czyszczenie danych znajduje jej pliki (secrets-storage-*.bin)
public sealed class StorageSecrets(ISecretStore secrets) : IStorageSecrets
{
    internal const string NamePattern = "storage-[a-z0-9]+(?:-[a-z0-9]+)*";
    private static readonly Regex ValidName = new($"^{NamePattern}$", RegexOptions.CultureInvariant);

    public string? Load(string name) => secrets.Load(Check(name));
    public void Save(string value, string name) => secrets.Save(value, Check(name));

    private static string Check(string name) =>
        ValidName.IsMatch(name) ? name : throw new ArgumentException($"Niedozwolona nazwa sekretu wtyczki magazynu: {name}", nameof(name));
}
```

**`UserDataCleanup.cs`:**

- Z `ProviderSecretFileNames` usunąć sześć nazw `secrets-storage-*` (zostają sekrety dostawców AI).
- W `UserDataCleanupService` dodać `internal static readonly Regex StorageSecretFileName = new($"^secrets-{StorageSecrets.NamePattern}\\.bin$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);` (wymaga `using ImageStudio.Services;`).
- W `Clean` przy `ProviderLoginsAndKeys` po `DeleteKnownFiles(ProviderSecretFileNames, ...)` dodać `DeleteStorageSecrets(ref deleted, failures);`:

```csharp
    // Sekrety wtyczek magazynu (logowania, sesje, kolejki wysyłek) po wzorcu nazwy: nowa wtyczka nie wymaga zmian tutaj
    private void DeleteStorageSecrets(ref int deleted, List<string> failures)
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "secrets-storage-*.bin", SearchOption.TopDirectoryOnly))
            if (StorageSecretFileName.IsMatch(Path.GetFileName(file)))
                TryDelete(file, ref deleted, failures);
    }
```

- `UserDataMutationGate`: w `BlockedPaths` dodać `public bool StorageSecrets { get; set; }`; w `AddBlockedTargets` przy `ProviderLoginsAndKeys` ustawić `blocked.StorageSecrets = true`; w `IsBlockedUnsafe` przed `return` dodać: gdy `blocked.StorageSecrets` i katalog `path` równa się `root` (`StringComparison.OrdinalIgnoreCase`) i `UserDataCleanupService.StorageSecretFileName.IsMatch(Path.GetFileName(path))` → `true`.

**`OutgoingRequestIntegrationTests.cs:231`:** `var cloud = container.GetServices<IMediaStoragePlugin>().OfType<OneDriveStorage>().Single();` (jedyna zmiana w tym pliku).

**Testy `StoragePluginDiscoveryTests`:**

- `App_assembly_lists_every_storage_library_in_metadata` (metadane `ImageStudio.StoragePlugin` w `typeof(ServiceCollectionExtensions).Assembly` zawierają `ImageStudio.Storage.Local`, `.OneDrive`, `.GoogleDrive`).
- `Container_discovers_cloud_plugins_after_local_in_a_fixed_order` (`TestApp.Create()`, `app.Get<MediaStorageHost>().Plugins.Select(p => p.Id)` == `["local", "googledrive", "onedrive"]`).
- `Only_concrete_plugins_with_a_context_constructor_are_found` (`PluginTypes(typeof(StoragePluginDiscoveryTests).Assembly)`: publiczna atrapa `ContextPlugin(StoragePluginContext)` jest, `NoContextPlugin()` i abstrakcyjna nie; atrapy jako `public sealed class` w pliku testu).
- `Host_ignores_invalid_and_duplicate_ids_and_keeps_the_first`
- `Storage_secrets_accept_only_storage_names` (`new StorageSecrets(new MemorySecretStore())`: `"openai"` → `ArgumentException`, `"storage-mega-pending"` OK).

**Testy w `UserDataCleanupTests`:**

- `Storage_plugin_secrets_are_found_by_name_pattern` (pliki `secrets-storage-mega.bin`, `secrets-storage-mega-pending.bin` usunięte; `secrets-storagex.bin`, `secrets-storage-.bin`, `secrets-custom.bin` zostają).
- `Cleanup_blocks_writes_of_any_storage_secret` (wzór istniejących testów blokady; `UserDataMutationGate.IsBlocked(root, Path.Combine(root, "secrets-storage-nowa.bin"))` po czyszczeniu z `ProviderLoginsAndKeys: true` = true; `secrets-custom.bin` = false).

**Tłumaczenia:** brak (komunikat dziennika i wyjątku programisty zostają po polsku zgodnie z `docs/dev/localization.md`).

**Kryteria akceptacji:** linie metadanych w `ImageStudio.AssemblyInfo.cs`; `UserDataCleanupTests.Provider_credentials_are_opt_in_and_only_known_secret_files_are_removed` przechodzi bez zmian; całość zielona. Dodatkowo publikacja jednoplikowa: `dotnet publish ImageStudio -p:PublishProfile=Portable -o C:\Temp\is-pub` kończy się sukcesem (uruchomienie exe i sprawdzenie listy w Ustawieniach robi sesja główna).

**STOP:** brak metadanych po buildzie (target nie zadziałał); `Assembly.Load` nie znajduje biblioteki w testach; jakikolwiek istniejący test poza `OutgoingRequestIntegrationTests.cs:231` wymaga zmiany.

### ZLECENIE DLA HAIKU (etap 3)

```
Wykonaj ETAP 3 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Fakty z kodu", "Odkrywanie wtyczek", "Reguły dla każdego wykonawcy" i cały "Etap 3").
Kod przepisz z planu. W UserDataCleanup.cs zmieniaj tylko opisane miejsca (lista nazw, nowa metoda, blokada).
W OutgoingRequestIntegrationTests.cs zmień tylko linię pobierającą OneDriveStorage z kontenera.
Po buildzie sprawdź metadane: `Select-String "ImageStudio.StoragePlugin" ImageStudio/obj/Debug/net10.0-windows/ImageStudio.AssemblyInfo.cs`
(oczekiwane 4 wiersze). Weryfikacja: `dotnet build ImageStudio.slnx -m:1`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~StoragePluginDiscoveryTests|FullyQualifiedName~UserDataCleanupTests|FullyQualifiedName~OutgoingRequestIntegrationTests|FullyQualifiedName~CloudStorageTests|FullyQualifiedName~StorageSettingsTabTests"`;
`dotnet test ImageStudio.Tests`; `dotnet publish ImageStudio -p:PublishProfile=Portable -o C:\Temp\is-pub`.
Stań przy warunku STOP z planu. Nie commituj. Raport: pliki, wynik Select-String, wyniki testów i publish, odstępstwa.
```

### KONTROLA SESJI GŁÓWNEJ (etap 3) — kontrola kodu osobiście (refleksja, czyszczenie danych)

```powershell
dotnet build ImageStudio.slnx -m:1
Select-String "ImageStudio.StoragePlugin" ImageStudio/obj/Debug/net10.0-windows/ImageStudio.AssemblyInfo.cs
dotnet test ImageStudio.Tests
dotnet publish ImageStudio -p:PublishProfile=Portable -o C:\Temp\is-pub   # uruchom exe: Ustawienia → Zapis pokazuje Local, Google Drive, OneDrive
dotnet build ImageStudio -p:BaseOutputPath=C:\Temp\is-build\               # w bin\...\storage\ są DLL-e, aplikacja startuje z listą wtyczek
git diff ImageStudio/Configuration/UserDataCleanup.cs ImageStudio/Services/StorageSecrets.cs ImageStudio/Composition
```

W diffie: wzorzec nazwy wspólny (`StorageSecrets.NamePattern`) dla strażnika i czyszczenia; `EnumerateFiles` tylko `TopDirectoryOnly`; kasowanie przez istniejące `TryDelete` (bez obchodzenia zabezpieczeń reparse point); blokada w bramce obejmuje wzorzec; `catch` w odkrywaniu nie połyka innych wyjątków; brak `GetEntryAssembly`.

---

## Etap 4: trwała kolejka oczekujących i wysyłka po zalogowaniu

**Cel:** plik zapisany przy wylogowaniu (albo z nieudaną wysyłką, albo przerwaną zamknięciem aplikacji) trafia do chmury po zalogowaniu lub po restarcie.

**Pliki:**

- zmiana `ImageStudio.Storage.Core/MediaStorage.cs` (`Resume`)
- zmiana `ImageStudio.Storage.Cloud/CloudMirrorStorage.cs`
- zmiana `ImageStudio/Services/MediaStorageHost.cs` (`Load` woła `Resume`)
- zmiana słowników
- zmiana `ImageStudio.Tests/CloudMirrorBaseTests.cs` (nowe testy), `ImageStudio.Tests/CloudStorageTests.cs` (tylko asercja statusu w `Signed_out_storage_only_saves_locally`)

**Kontrakt:** w `IMediaStoragePlugin`: `// Wznawia zaległe operacje w tle (np. wysyłki z kolejki); wołane przy starcie i po logowaniu` `void Resume() { }`.

**`CloudMirrorStorage`:**

```csharp
// Operacja na chmurze czekająca w trwałej kolejce; Op: "upload" albo "delete"; Id odróżnia kolejne zapisy tej samej
// lokalizacji: zadanie starszego zapisu nie może usunąć z kolejki wpisu nowszego (inaczej nadpisanie pliku w trakcie
// wysyłki zostawiłoby w chmurze starą zawartość)
public sealed record PendingOperation(string Op, string Location, Guid Id);

    // Trwała kolejka (przeżywa wylogowanie i restart): wpis znika dopiero po udanej operacji; nowsza operacja na tej samej
    // lokalizacji zastępuje starszą (usunięcie po zapisie = samo usunięcie)
    // ponytail: kolejka jako JSON w IStorageSecrets (sekret storage-{Id}-pending, czyszczony z "Logowaniami i kluczami");
    // osobny plik, gdy kolejka zacznie liczyć tysiące wpisów
    private readonly object _queueGate = new();
    private string QueueName => $"storage-{Id}-pending";

    public IReadOnlyList<PendingOperation> PendingOperations { get { lock (_queueGate) return LoadQueue(); } }

    private List<PendingOperation> LoadQueue()
    {
        try { return Secrets.Load(QueueName) is { } json ? JsonSerializer.Deserialize<List<PendingOperation>>(json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void SaveQueue(List<PendingOperation> queue) => Secrets.Save(queue.Count == 0 ? "" : JsonSerializer.Serialize(queue), QueueName);

    private void Enqueue(PendingOperation op)
    {
        lock (_queueGate)
        {
            var queue = LoadQueue();
            queue.RemoveAll(x => string.Equals(x.Location, op.Location, StringComparison.OrdinalIgnoreCase));
            queue.Add(op);
            SaveQueue(queue);
        }
    }

    private void Dequeue(PendingOperation op)
    {
        lock (_queueGate)
        {
            var queue = LoadQueue();
            if (queue.Remove(op)) SaveQueue(queue);
        }
    }

    private bool IsQueued(PendingOperation op) { lock (_queueGate) return LoadQueue().Contains(op); }

    private void Schedule(PendingOperation op) => Background(Path.GetFileName(op.Location), async () =>
    {
        if (!IsQueued(op)) return;                                                        // już wykonana (np. dwa wznowienia)
        if (await AccessTokenAsync(CancellationToken.None) is not { } token) return;      // niezalogowany: czeka w kolejce
        var name = Path.GetFileName(op.Location);
        if (op.Op == "delete") await DeleteRemoteAsync(token, name, CancellationToken.None);
        else if (File.Exists(op.Location)) await UploadAsync(token, name, await File.ReadAllBytesAsync(op.Location), CancellationToken.None);
        Dequeue(op);                                                                      // brak pliku lokalnego: nie ma czego wysłać
    });

    public void Resume()
    {
        if (!SignedIn) return;
        foreach (var op in PendingOperations) Schedule(op);
    }
```

- `WriteAsync`: po `Cache.WriteAsync` → `var op = new PendingOperation("upload", path, Guid.NewGuid()); Enqueue(op); Schedule(op); return path;` (bez kopiowania `data` do tablicy).
- `DeleteAsync`: po `Cache.DeleteAsync` → `var op = new PendingOperation("delete", location, Guid.NewGuid()); Enqueue(op); Schedule(op);`.
- `RunActionAsync`: po udanym `SignInAsync` wywołać `Resume()`.
- `Status`: dla niezalogowanego `PendingOperations.Count is > 0 and var n ? new("storage.status.signedOutPending", n.ToString(CultureInfo.InvariantCulture)) : new("storage.status.signedOut")`.
- Wyjątek w zadaniu: jak dziś (`_lastError`); wpis zostaje i wróci przy następnym `Resume`.

**`MediaStorageHost.Load`:** `Apply(...)`, potem `foreach (var plugin in Plugins) plugin.Resume();` (wszystkie wtyczki, nie tylko aktywna: kolejka poprzedniej też ma dojść).

**Tłumaczenia:**

| klucz | PL | EN |
|---|---|---|
| `storage.status.signedOutPending` | Niezalogowano: pliki do wysłania ({0}) czekają na zalogowanie; do tego czasu są tylko lokalnie. | Signed out: files to upload ({0}) are waiting for sign-in; until then they are local only. |

**Testy (`CloudMirrorBaseTests`, na `TestMirror`):**

- `File_saved_while_signed_out_is_uploaded_after_sign_in`
- `Pending_uploads_survive_a_restart` (nowa instancja na tych samych `MemorySecrets`, zalogowana, `Resume()`, `FlushAsync()`)
- `Delete_while_signed_out_replaces_the_pending_upload_with_a_remote_delete`
- `Failed_upload_stays_pending_and_is_retried_on_resume`
- `Pending_entry_of_a_missing_local_file_is_dropped`
- `Double_resume_uploads_once`
- `Overwrite_while_an_upload_is_in_flight_still_uploads_the_latest_content` (`TestMirror` z `UploadAsync` czekającym na `TaskCompletionSource`: pierwszy zapis `"a"` startuje wysyłkę i blokuje się, drugi zapis tej samej nazwy `"b"` trafia do kolejki, bramka się otwiera, po `FlushAsync()` ostatnia wysłana zawartość to `"b"`, a `PendingOperations` jest puste)
- `Host_load_resumes_every_plugin` (atrapa `IMediaStoragePlugin` licząca `Resume`, `new MediaStorageHost([...]).Load(new UserState())`)

**Zmiana istniejącego testu (jedyna dozwolona):** `CloudStorageTests.Signed_out_storage_only_saves_locally`: oczekiwany status `("storage.status.signedOutPending", "1")` i `Assert.True(secrets.Values.ContainsKey("storage-onedrive-pending"))` (test musi dostać nazwaną zmienną `secrets`).

**Druga dozwolona zmiana istniejącego testu (decyzja sesji głównej po STOP Haiku):** `OutgoingRequestIntegrationTests.Cloud_delete_waits_for_cancelled_upload_retry` zmienia nazwę na `Cloud_delete_supersedes_a_cancelled_upload_retry`, a oczekiwana sekwencja żądań na `[PUT, DELETE]` (zamiast `[PUT, PUT, DELETE]`) z komentarzem w teście: usunięcie zastępuje oczekujące wysłanie w kolejce, więc przerwane (anulowane) wysłanie nie jest ponawiane po usunięciu pliku; stan końcowy w chmurze ten sam, żadne żądanie wysłania nie następuje po usunięciu. Asercja `Assert.False(File.Exists(path))` zostaje.

**Kryteria akceptacji:** całość zielona; `Check-Translations -Strict` = 0.

**STOP:** gdy inne istniejące testy chmur (np. `GoogleDrive_parallel_uploads_share_one_folder`, `OutgoingRequestIntegrationTests` z `FlushAsync`) padają albo trwają wyraźnie dłużej; gdy `UserState` nie ma konstruktora bez parametrów.

### ZLECENIE DLA HAIKU (etap 4)

```
Wykonaj ETAP 4 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Przepływ zapisu offline", "Reguły dla każdego wykonawcy" i cały "Etap 4"). Kod kolejki przepisz z planu;
istniejące Background/RunBackgroundAsync zostają bez zmian. Klucz storage.status.signedOutPending dopisz do obu
słowników, potem `pwsh tools/Check-Translations.ps1 -Fix`. Z istniejących testów zmień wyłącznie
CloudStorageTests.Signed_out_storage_only_saves_locally tak, jak opisuje plan.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudMirrorBaseTests|FullyQualifiedName~CloudStorageTests|FullyQualifiedName~OutgoingRequestIntegrationTests|FullyQualifiedName~StorageSettingsTabTests"`;
`dotnet test ImageStudio.Tests`; `pwsh tools/Check-Translations.ps1 -Strict`.
Stań przy warunku STOP. Nie commituj. Raport: pliki, wyniki, odstępstwa.
```

### KONTROLA SESJI GŁÓWNEJ (etap 4)

```powershell
dotnet test ImageStudio.Tests
pwsh tools/Check-Translations.ps1 -Strict
git diff ImageStudio.Storage.Cloud/CloudMirrorStorage.cs ImageStudio.Tests/CloudStorageTests.cs
```

W diffie: każda zmiana kolejki pod `_queueGate`; `Dequeue` dopiero po udanej operacji; brak `Secrets.Load` poza blokadą w ścieżce zapisu; w kolejce tylko ścieżki lokalne (żadnych tokenów); `Resume` nie planuje nic bez logowania.

---

## Etap 5: operacje kierowane do wtyczki-właściciela

**Cel:** usunięcie i eksport wpisu idą do wtyczki, która zapisała plik (`GalleryEntry.StorageId`), nie do aktywnej. Stare wpisy (`StorageId == null`) i nieznane identyfikatory = `local`.

**Pliki:**

- zmiana `ImageStudio/Services/MediaStorageHost.cs`
- zmiana `ImageStudio/Services/StudioApi.cs` (linie ~433 i ~451)
- nowy `ImageStudio.Tests/MediaStorageHostTests.cs`; zmiana `ImageStudio.Tests/StudioTests.cs` (nowy test w `StudioApiTests`)

**Kod `MediaStorageHost`:**

```csharp
    // Wtyczka, która zapisała plik (GalleryEntry.StorageId). Brak identyfikatora (starsze wpisy) i wtyczka usunięta
    // z aplikacji = dysk lokalny: wtyczki chmurowe trzymają lustro w folderach lokalnych, więc ścieżka i tak jest lokalna
    public IMediaStoragePlugin Owner(string? storageId) =>
        Plugins.FirstOrDefault(p => p.Id == storageId) ?? Plugins.FirstOrDefault(p => p.Id == "local") ?? Plugins[0];
```

Metody `IMediaStorage` hosta (`ReadAsync` itd.) zostają na `Active` (bez identyfikatora nie wiadomo, czyj to plik); komentarz nad nimi: „Operacje na istniejących wpisach idą przez Owner(entry.StorageId), patrz StudioApi”.

**Kod `StudioApi`:**

```csharp
    // Plik wpisu obsługuje wtyczka, która go zapisała, nawet gdy aktywna jest już inna
    private IMediaStorage StorageFor(GalleryEntry entry) => storage is MediaStorageHost host ? host.Owner(entry.StorageId) : storage;
```

`DeleteEntryAsync`: `await StorageFor(entry).DeleteAsync(entry.FilePath, ct);`; `ExportAsync`: `await StorageFor(entry).ReadAsync(entry.FilePath, ct)`.

**Testy:**

- `MediaStorageHostTests.Owner_is_the_plugin_that_wrote_the_entry`
- `MediaStorageHostTests.Legacy_and_unknown_entries_belong_to_local` (`null`, `""`, `"zniknela"` → `local`, także gdy `local` nie jest pierwsza na liście)
- `StudioApiTests.Delete_and_export_use_the_owner_not_the_active_plugin` (dwie atrapy rejestrujące wywołania `DeleteAsync`/`ReadAsync`; wpis `StorageId = "cloud"` przez `_library.AddMedia(...)`; aktywna `local`; wzór `Switching_storage_during_write_keeps_the_writer_and_its_identity`, `StudioTests.cs:157`).

**Tłumaczenia:** brak.

**Kryteria akceptacji:** całość zielona; `Non_plugin_storage_remains_supported_without_an_identifier` bez zmian.

**STOP:** gdy znajdziesz inne miejsce wołające `ReadAsync`/`DeleteAsync`/`GetLocalPathAsync`/`ExistsAsync` na hoście (poza `StudioApi.cs:433`, `:451`): zgłoś zamiast poprawiać.

### ZLECENIE DLA HAIKU (etap 5)

```
Wykonaj ETAP 5 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Fakty z kodu", "Reguły dla każdego wykonawcy" i cały "Etap 5"). Najpierw grep po
"\.ReadAsync(|\.DeleteAsync(|\.GetLocalPathAsync(|\.ExistsAsync(" w ImageStudio/ i ImageStudio.Tabs.* (bez obj):
jeśli poza StudioApi.cs są wywołania na MediaStorageHost/IMediaStorage, stań i zgłoś. Kod przepisz z planu.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~MediaStorageHostTests|FullyQualifiedName~StudioApiTests|FullyQualifiedName~StorageSettingsTabTests"`;
`dotnet test ImageStudio.Tests`. Nie commituj. Raport: wynik grep, pliki, wyniki, odstępstwa.
```

### KONTROLA SESJI GŁÓWNEJ (etap 5)

```powershell
dotnet test ImageStudio.Tests
git diff ImageStudio/Services
```

W diffie: `Owner` nie zwraca `Active`; `StudioApi` nie zmienia `WriteResultAsync`.

---

## Etap 6: status na żywo w zakładce Zapis

**Cel:** stan logowania, liczba oczekujących i błąd wysyłki w tle odświeżają się w otwartym oknie Ustawień.

**Pliki:**

- zmiana `ImageStudio.Storage.Core/MediaStorage.cs` (zdarzenie)
- zmiana `ImageStudio.Storage.Cloud/CloudMirrorStorage.cs`
- zmiana `ImageStudio/ViewModels/StorageSettingsTab.cs`
- zmiana `ImageStudio/ViewModels/MainViewModel.Settings.cs` (zwolnienie zakładki)
- zmiana słowników (teksty pomocy OneDrive/Google)
- zmiana `ImageStudio.Tests/StorageLoginContractTests.cs` (nowe testy, atrapa z licznikiem subskrybentów)

**Kontrakt:** w `IMediaStoragePlugin`: `// Zmiana Status (logowanie, kolejka, błąd wysyłki w tle); może przyjść z wątku tła` `event EventHandler? StatusChanged { add { } remove { } }`.

**`CloudMirrorStorage`:** `public event EventHandler? StatusChanged;`, `protected void OnStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);`, `private void SetError(string? message) { _lastError = message; OnStatusChanged(); }`. Wszystkie przypisania `_lastError` (w `RunBackgroundAsync` i `RunActionAsync`) przez `SetError`; `OnStatusChanged()` po zmianie kolejki (`Enqueue`, `Dequeue` gdy usunięto) i po zakończeniu `RunActionAsync` (sukces lub wyjątek: w `finally`).

**`StorageOption`:** implementuje `IDisposable`; w konstruktorze `_plugin.StatusChanged += OnPluginStatusChanged;`, w `Dispose` `-=`. Obsługa:

```csharp
    // Wysyłka w tle zmienia stan wtyczki: odświeżenie na wątku okna; w trakcie akcji stan ustawia RunAsync
    private void OnPluginStatusChanged(object? sender, EventArgs e) => OnUi(() => { if (!IsBusy) Refresh(); });
```

**`StorageSettingsTab`:** `IDisposable`, `Dispose()` zwalnia wszystkie `Options`.

**`MainViewModel.Settings.cs:57`:** przed wywołaniem `ShowSettings` `using var storageTab = new StorageSettingsTab(current, _storage);` i przekazanie `storageTab` w liście zakładek.

**Tłumaczenia (zmiana istniejących tekstów, oba języki):**

- `storage.onedrive.help` i `storage.googledrive.help`: ostatnie zdanie PL „Nowe pliki są wysyłane w tle; błąd wysyłki zobaczysz tu po ponownym otwarciu Ustawień.” → „Nowe pliki są wysyłane w tle (także te zapisane przed zalogowaniem); stan i błąd wysyłki widać tu na bieżąco.”; EN „New files are uploaded in the background; an upload error shows here after reopening Settings.” → „New files are uploaded in the background (including those saved before signing in); upload status and errors show here live.”

**Testy (`StorageLoginContractTests`):**

- `Open_tab_shows_a_background_upload_error_without_reopening` (`TestMirror` z etapu 2 jest w `CloudMirrorBaseTests`: zrób go `internal` albo skopiuj minimalną wersję; opcja przez `Task.Run`; zapis z błędem wysyłki, `FlushAsync`, `WaitUntilAsync(() => option.ErrorText != "")`).
- `Sign_in_elsewhere_refreshes_the_status_and_buttons`
- `Disposed_tab_stops_listening_to_plugins` (atrapa z jawnym `event` liczącym `add`/`remove`).
- `Settings_dialog_releases_the_storage_tab` (`TestApp`: zastąp wtyczkę atrapą licznikową przez `TestApp.Create(services => services.AddSingleton<IMediaStoragePlugin>(fake))`, `OpenSettingsCommand`, potem licznik subskrybentów = 0).

**Kryteria akceptacji:** całość zielona; `Check-Translations -Strict` = 0.

**STOP:** gdy zdarzenie musi być wywołane pod blokadą `_queueGate`/`_backgroundGate` (ryzyko zakleszczenia z UI): wywołuj je po wyjściu z `lock`; jeśli się nie da, zgłoś.

### ZLECENIE DLA HAIKU (etap 6)

```
Wykonaj ETAP 6 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Reguły dla każdego wykonawcy" i cały "Etap 6"). Zdarzenie StatusChanged wywołuj zawsze poza lock.
W ViewModelu przekazanie na wątek UI tylko przez istniejące OnUi (SynchronizationContext), bez Dispatcher/Application.
Zmień teksty pomocy storage.onedrive.help i storage.googledrive.help w obu słownikach dokładnie jak w planie,
potem `pwsh tools/Check-Translations.ps1 -Fix`.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~StorageLoginContractTests|FullyQualifiedName~CloudMirrorBaseTests|FullyQualifiedName~CloudStorageTests|FullyQualifiedName~StorageSettingsTabTests|FullyQualifiedName~SettingsTabsTests"`;
`dotnet test ImageStudio.Tests`; `pwsh tools/Check-Translations.ps1 -Strict`. Nie commituj. Raport: pliki, wyniki, odstępstwa.
```

### KONTROLA SESJI GŁÓWNEJ (etap 6)

```powershell
dotnet test ImageStudio.Tests
pwsh tools/Check-Translations.ps1 -Strict
git diff ImageStudio/ViewModels ImageStudio.Storage.Cloud
```

W diffie: `-=` w `Dispose`; `using var storageTab` w `OpenSettingsAsync`; brak wywołania zdarzenia wewnątrz `lock`. Ręcznie: uruchomiona aplikacja, zakładka Zapis otwarta, wygenerowany obraz przy OneDrive bez sieci → błąd pojawia się bez zamykania okna.

---

## Etap 7: wtyczka ImageStudio.Storage.Mega

**Warunek wejścia:** etap 0 = GO (albo GO-Z-ZASTRZEŻENIEM zaakceptowane przez użytkownika); etapy 1-6 wykonane.

**Fakty z etapu 0 (wpisane przez sesję główną; decyzja użytkownika: MegaApiClient + własny IWebClient + przypięcie Newtonsoft.Json):**

- Paczka `MegaApiClient` **1.10.5** (2025-09-10), licencja MIT, tryb „passive maintenance”; kompiluje się w `net10.0-windows` (projekt próbny). Przechodnia zależność `Newtonsoft.Json 10.0.2` ma lukę GHSA-5crp-9r3c-p9vr (NU1903): przypiąć `Newtonsoft.Json 13.0.3` przez `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` + `<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />` w `Directory.Packages.props`.
- Sygnatury (przestrzeń `CG.Web.MegaApiClient`): `new MegaApiClient(IWebClient)`; `Task<LogonSessionToken> LoginAsync(string email, string password, string mfaKey = null)`; `Task LoginAsync(LogonSessionToken)` (wznowienie sesji bez hasła); `Task LogoutAsync()`; `bool IsLoggedIn`; `Task<IEnumerable<INode>> GetNodesAsync()`; `Task<INode> CreateFolderAsync(string, INode parent)`; `Task<INode> UploadAsync(Stream, string name, INode parent, IProgress<double> progress = null, DateTime? modificationDate = null, CancellationToken? ct = null)`; `Task<Stream> DownloadAsync(INode, IProgress<double> progress = null, CancellationToken? ct = null)`; `Task DeleteAsync(INode, bool moveToTrash = true)`. `LogonSessionToken(string sessionId, byte[] masterKey)` z właściwościami `SessionId` i `MasterKey` (to klucz główny konta: tylko do `IStorageSecrets`).
- 2FA: parametr `mfaKey` to kod; konto z 2FA bez kodu daje `ApiException` z `ApiResultCode.TwoFactorAuthenticationError` (-26), `HashcashRequired` = -27, `RequestFailedRetry` = -3 (publiczne `ApiResultCode`). Czy dokładnie -26 wraca przy logowaniu bez kodu: **zweryfikować w teście na żywo**, adapter mapuje -26 na `MegaMfaRequiredException`.
- Człony `INode` (`Name`, `Type`/`NodeType`, `ParentId`, `Id`) i `Delete`/„brak węzła” **do potwierdzenia refleksją** w 7b (patrz zlecenie).
- Transport: `IWebClient` jest publiczny (`int BufferSize {get;set;}`; `string PostRequestJson(Uri, string)`; `string PostRequestJson(Uri, string, string hashcash)`; `string PostRequestRaw(Uri, Stream)`; `Stream PostRequestRawAsStream(Uri, Stream)`; `Stream GetRequestRaw(Uri)`), a `WebClient` NIE przyjmuje `HttpMessageHandler` (statyczny `HttpClient` w środku). Dlatego **własny `HttpClientWebClient : IWebClient`** w projekcie wtyczki = port klasy `WebClient` (MIT, 167 linii; kopia źródła v1.10.5 do porównania: `C:\Users\przem\AppData\Local\Temp\claude\4e332fc39a96dd89\c4164a36-aa4a-41dc-8e2d-14de9855ac82\scratchpad\megasrc\WebClient_HttpClient.cs`, oryginał `https://raw.githubusercontent.com/gpailler/MegaApiClient/v1.10.5/MegaApiClient/WebClient_HttpClient.cs`). Zachowanie do przeniesienia 1:1: POST `application/json` (JSON) i `application/octet-stream` (surowe), nagłówek `X-Hashcash` gdy podany, odpowiedź HTTP 500 z `ReasonPhrase == "Server Too Busy"` zwraca strumień z tekstem `((long)ApiResultCode.RequestFailedRetry)`, odpowiedź HTTP 402 z nagłówkiem `X-Hashcash` zwraca strumień z tekstem `[{(long)ApiResultCode.HashcashRequired}, '{wartość}']` (to właśnie obsługa hashcash z wydania 1.10.5: bez niej logowanie kończy się 402), pozostałe błędy `EnsureSuccessStatusCode`. Zmiany względem oryginału: `HttpClient` z handlera kontekstu (`new HttpClient(handler, disposeHandler: true) { Timeout = timeout }`), `.Result` → `.GetAwaiter().GetResult()` (wywołania i tak idą w `Task.Run` adaptera, a handler aplikacji obsługuje tylko ścieżkę asynchroniczną), bez dyrektyw `#if` per TFM, `ToBytes()` (wewnętrzne w bibliotece) → `Encoding.UTF8.GetBytes`, nagłówek User-Agent jak w oryginale (`GenerateUserAgent`), komentarz z atrybucją MIT na górze pliku. Jeśli którykolwiek potrzebny typ jest `internal` → STOP.

**Podział etapu 7 (decyzja sesji głównej):** **7a** wtyczka bez zależności NuGet (projekt, `IMegaClient`, `MegaStorage`, adapter-atrapa, tłumaczenia, testy na atrapie, rejestracja w csproj/slnx, lista w teście odkrywania); **7b** biblioteka i transport (`Directory.Packages.props` z przypięciem, `HttpClientWebClient` + testy, prawdziwy `MegaApiClientAdapter`, test na żywo). Zlecenia 7a i 7b poniżej zastępują zlecenie „całość”.

**Pliki:**

- nowy `ImageStudio.Storage.Mega/ImageStudio.Storage.Mega.csproj`
- nowy `ImageStudio.Storage.Mega/IMegaClient.cs`, `MegaStorage.cs`, `MegaApiClientAdapter.cs`
- zmiana `Directory.Packages.props` (`<PackageVersion Include="MegaApiClient" Version="<z etapu 0>" />`)
- zmiana `ImageStudio.slnx` (w folderze `/Storage/`: `<Project Path="ImageStudio.Storage.Mega/ImageStudio.Storage.Mega.csproj" />`)
- zmiana `ImageStudio/ImageStudio.csproj` (`<ProjectReference Include="..\ImageStudio.Storage.Mega\ImageStudio.Storage.Mega.csproj" Private="$(PluginsInBundle)" />` przy pozostałych magazynach)
- zmiana `ImageStudio.Tests/ImageStudio.Tests.csproj` (`ProjectReference` bez `Private`)
- zmiana słowników
- nowy `ImageStudio.Tests/MegaStorageTests.cs`; zmiana `ImageStudio.Tests/CloudLiveTests.cs` (jeden test); zmiana `ImageStudio.Tests/StoragePluginDiscoveryTests.cs` (oczekiwana lista `local, googledrive, mega, onedrive`)

Żadnych zmian w `ServiceCollectionExtensions.cs`, `UserDataCleanup.cs`, `MediaStorageHost.cs`, `StorageSettingsTab.cs`: jeśli są potrzebne, wymienność nie działa → STOP.

**csproj:**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\ImageStudio.Storage.Cloud\ImageStudio.Storage.Cloud.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MegaApiClient" />
  </ItemGroup>

</Project>
```

**`IMegaClient.cs`:**

```csharp
namespace ImageStudio.Storage.Mega;

// Dostęp do MEGA za małym interfejsem: testy podstawiają atrapę bez sieci, produkcja MegaApiClientAdapter.
// Sesja to nieprzezroczysty tekst (zserializowany token sesji MEGA) trzymany w sekretach; hasło nie wychodzi poza LoginAsync
public interface IMegaClient
{
    // Logowanie e-mailem i hasłem; mfaCode: kod 2FA albo null. Konto z 2FA bez kodu: MegaMfaRequiredException
    Task<string> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct);

    // Unieważnia sesję na serwerze
    Task LogoutAsync(string session, CancellationToken ct);

    // Operacje w folderze aplikacji (tworzony przy pierwszym użyciu); Upload zastępuje plik o tej samej nazwie
    Task UploadAsync(string session, string folder, string fileName, byte[] data, CancellationToken ct);
    Task<byte[]?> DownloadAsync(string session, string folder, string fileName, CancellationToken ct);   // null = brak pliku
    Task DeleteAsync(string session, string folder, string fileName, CancellationToken ct);             // brak pliku = OK
}

public sealed class MegaMfaRequiredException() : Exception("Konto MEGA wymaga kodu logowania dwuetapowego.");
```

**`MegaStorage.cs`:**

```csharp
using System.Text.Json;
using ImageStudio.Localization;
using ImageStudio.Storage.Cloud;

namespace ImageStudio.Storage.Mega;

// MEGA (nieoficjalna biblioteka MegaApiClient) w modelu lustra: pliki w folderze ImageStudio na koncie. Logowanie e-mailem
// i hasłem w zakładce Zapis (opcjonalnie kod 2FA); w sekrecie storage-mega zostaje tylko e-mail i sesja, nigdy hasło
public sealed class MegaStorage : CloudMirrorStorage
{
    public const string FolderName = "ImageStudio";
    private readonly IMegaClient _client;

    public MegaStorage(StoragePluginContext context)
        : this(context.Secrets, new MegaApiClientAdapter(context.CreateHttpHandler(), context.HttpTimeout)) { }

    public MegaStorage(IStorageSecrets secrets, IMegaClient client) : base(secrets) => _client = client;

    public override string Id => "mega";
    public override string DisplayName => "MEGA";
    public override string? HelpKey => "storage.mega.help";
    protected override string SignInLabelKey => "storage.mega.signIn";
    protected override string? SignInWorkingKey => "storage.mega.working";

    private sealed record MegaSession(string Email, string Session);
    private string SessionName => $"storage-{Id}";

    private MegaSession? LoadSession()
    {
        try { return Secrets.Load(SessionName) is { } json ? JsonSerializer.Deserialize<MegaSession>(json) : null; }
        catch (JsonException) { return null; }
    }

    protected override bool SignedIn => LoadSession() is not null;
    protected override string? AccountLabel => LoadSession()?.Email;
    protected override Task<string?> AccessTokenAsync(CancellationToken ct) => Task.FromResult(LoadSession()?.Session);

    protected override IEnumerable<StorageSetting> AuthSettings =>
    [
        new("email", "storage.mega.email", "", Kind: StorageFieldKind.Email, Transient: true),
        new("password", "storage.mega.password", "", Masked: true, Kind: StorageFieldKind.Password, Transient: true),
    ];

    protected override async Task SignInAsync(IReadOnlyDictionary<string, string> inputs, IStorageInteraction interaction, CancellationToken ct)
    {
        var email = inputs.GetValueOrDefault("email")?.Trim() ?? "";
        var password = inputs.GetValueOrDefault("password") ?? "";
        if (email.Length == 0 || password.Length == 0) throw new InvalidOperationException(Loc.T("storage.mega.missingCredentials"));

        string session;
        try
        {
            session = await _client.LoginAsync(email, password, null, ct);
        }
        catch (MegaMfaRequiredException)
        {
            var code = await interaction.AskAsync(new StoragePrompt("mfa", "storage.mega.mfaCode", StorageFieldKind.Code,
                                                                    new StorageStatus("storage.mega.mfaPrompt")), ct);
            if (string.IsNullOrWhiteSpace(code)) throw new OperationCanceledException(ct);
            session = await _client.LoginAsync(email, password, code.Trim(), ct);
        }

        Secrets.Save(JsonSerializer.Serialize(new MegaSession(email, session)), SessionName);
    }

    // Najpierw lokalnie: błąd sieci przy unieważnianiu nie może zostawić użytkownika zalogowanym
    protected override async Task SignOutAsync(CancellationToken ct)
    {
        var session = LoadSession();
        Secrets.Save("", SessionName);
        if (session is null) return;
        try { await _client.LogoutAsync(session.Session, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { } // sesja i tak zapomniana lokalnie
    }

    protected override Task UploadAsync(string accessToken, string fileName, byte[] data, CancellationToken ct) =>
        _client.UploadAsync(accessToken, FolderName, fileName, data, ct);

    protected override Task<byte[]?> DownloadRemoteAsync(string accessToken, string fileName, CancellationToken ct) =>
        _client.DownloadAsync(accessToken, FolderName, fileName, ct);

    protected override Task DeleteRemoteAsync(string accessToken, string fileName, CancellationToken ct) =>
        _client.DeleteAsync(accessToken, FolderName, fileName, ct);
}
```

**`MegaApiClientAdapter.cs`** (jedyny plik zależny od biblioteki; nazwy metod z „Faktów z etapu 0”):

- `internal sealed class MegaApiClientAdapter : IMegaClient`, konstruktor `(HttpMessageHandler handler, TimeSpan timeout)`: klient MEGA z transportem na tym handlerze (konstruktor `WebClient` z handlerem albo własna implementacja `IWebClient` na `new HttpClient(handler) { Timeout = timeout }`, zgodnie z etapem 0).
- `SemaphoreSlim _gate = new(1, 1)` wokół każdego wywołania (biblioteka nie jest przeznaczona do równoległych wywołań na jednej instancji; wysyłki w tle idą równolegle dla różnych plików).
- Przed operacją: jeśli bieżąca sesja ≠ `session`, wznowienie sesji z tekstu (np. `Login(LogonSessionToken)`); `LoginAsync` zwraca token sesji zserializowany `JsonSerializer`.
- Folder: wyszukanie katalogu `folder` w korzeniu (`GetNodes`), utworzenie, gdy brak; identyfikator w polu (z blokadą jak `GoogleDriveStorage.FolderIdAsync`).
- `UploadAsync`: usuń pliki o tej samej nazwie w folderze, potem upload strumienia `MemoryStream(data)`.
- `DownloadAsync`: brak węzła o nazwie → `null`.
- `DeleteAsync`: usuń wszystkie węzły-pliki o tej nazwie (do kosza = `false`, zgodnie z OneDrive/Google, które kasują trwale; jeśli etap 0 pokaże, że kosz jest domyślny i bezpieczniejszy, zgłoś).
- Mapowanie błędu 2FA (kod z etapu 0) → `MegaMfaRequiredException`. Komunikaty innych wyjątków przepuszczane bez dopisywania e-maila/hasła.
- Synchroniczne API biblioteki opakuj `Task.Run(..., ct)`; asynchroniczne wywołuj wprost.
- Adapter nie ma testów jednostkowych (sieć); pokrywa go test na żywo.

**Tłumaczenia:**

| klucz | PL | EN |
|---|---|---|
| `storage.mega.email` | Adres e-mail konta MEGA | MEGA account e-mail |
| `storage.mega.help` | Pliki trafiają do folderu ImageStudio na Twoim koncie MEGA (kopia lokalna zostaje). Wpisz e-mail i hasło konta i kliknij Zaloguj; przy włączonym logowaniu dwuetapowym aplikacja poprosi o kod. Hasło nie jest zapisywane: aplikacja trzyma tylko sesję MEGA zaszyfrowaną DPAPI, a Wyloguj ją unieważnia. Pliki zapisane przed zalogowaniem zostaną wysłane po zalogowaniu; stan i błąd wysyłki widać tu na bieżąco. Obsługa MEGA korzysta z nieoficjalnej biblioteki MegaApiClient. | Files go to the ImageStudio folder in your MEGA account (the local copy stays). Enter the account e-mail and password and click Sign in; with two-step login enabled the app asks for a code. The password is not saved: the app keeps only the MEGA session encrypted with DPAPI, and Sign out revokes it. Files saved before signing in are uploaded after sign-in; upload status and errors show here live. MEGA support uses the unofficial MegaApiClient library. |
| `storage.mega.mfaCode` | Kod logowania dwuetapowego | Two-step login code |
| `storage.mega.mfaPrompt` | Konto ma włączone logowanie dwuetapowe. Wpisz 6-cyfrowy kod z aplikacji uwierzytelniającej i kliknij Potwierdź. | The account has two-step login enabled. Enter the 6-digit code from your authenticator app and click Confirm. |
| `storage.mega.missingCredentials` | Podaj adres e-mail i hasło konta MEGA. | Enter the MEGA account e-mail and password. |
| `storage.mega.password` | Hasło MEGA (nie jest zapisywane) | MEGA password (not saved) |
| `storage.mega.signIn` | Zaloguj | Sign in |
| `storage.mega.working` | Loguję do MEGA… | Signing in to MEGA… |

**Testy `MegaStorageTests`** (atrapa `FakeMegaClient : IMegaClient`: słownik plików `(folder, nazwa) → bajty`, lista wywołań z sesją, flaga „wymaga 2FA” i oczekiwany kod, flaga błędu wylogowania; `MemorySecrets` jak w `CloudStorageTests`; `FakeInteraction : IStorageInteraction` zwracająca zadany kod albo rzucająca `OperationCanceledException`; cache w katalogu tymczasowym):

- `Sign_in_with_email_and_password_stores_only_the_email_and_session` (żadna wartość w `secrets.Values` nie zawiera hasła; `Status` = `("storage.status.signedInAs", e-mail)`)
- `Two_factor_account_asks_for_a_code_and_retries_with_it`
- `Cancelled_code_prompt_leaves_the_user_signed_out`
- `Missing_email_or_password_is_explained`
- `Upload_download_and_delete_use_the_app_folder_and_the_session`
- `Sign_out_forgets_the_session_even_if_the_server_logout_fails`
- `Files_saved_before_sign_in_are_uploaded_after_it`
- `Password_never_reaches_configure_or_the_persisted_config` (`StorageOption` na `MegaStorage` + `StorageSettingsTab.Apply` + `MediaStorageHost.WithoutSecrets`)
- `Mega_is_discovered_without_changes_in_the_app` (`TestApp.Create()`: `Plugins` zawiera `mega`; `StoragePluginDiscovery.PluginTypes(typeof(MegaStorage).Assembly)` zawiera `MegaStorage`)

`CloudLiveTests`: `[LiveCloudFact] public Task Mega_create_read_update_delete_with_sample_files() => CrudAsync(new MegaStorage(new StoragePluginContext(new UserSecrets(), () => new HttpClientHandler(), TimeSpan.FromMinutes(10))));` (zalogowanie wcześniej w aplikacji: Ustawienia → Zapis → MEGA).

**Kryteria akceptacji:** build, całość testów, `Check-Translations -Strict` zielone; `git diff --stat` nie pokazuje zmian w `ImageStudio/Composition`, `ImageStudio/Configuration`, `ImageStudio/Services`, `ImageStudio/ViewModels`; po buildzie `MegaApiClient.dll` jest w katalogu wyjściowym aplikacji (`ImageStudio/bin/Debug/net10.0-windows/`), a `ImageStudio.Storage.Mega.dll` w `storage\`; publikacja Portable przechodzi.

**STOP:** etap 0 ≠ GO bez decyzji użytkownika; API biblioteki inne niż w „Faktach z etapu 0”; brak `MegaApiClient.dll` w wyjściu; potrzeba zmiany kodu aplikacji poza listą; nie da się podpiąć handlera z kontekstu.

### ZLECENIE DLA HAIKU (etap 7a)

```
Wykonaj CZĘŚĆ 7a ETAPU 7 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Kontrakty", "Przepływ logowania", "Reguły dla każdego wykonawcy" i cały "Etap 7" razem z "Fakty z etapu 0" i "Podział etapu 7").
ZAKRES 7a (tylko to): projekt ImageStudio.Storage.Mega (csproj z jedyną referencją do Storage.Cloud, BEZ PackageReference MegaApiClient);
IMegaClient.cs i MegaStorage.cs przepisane z planu (dopasuj modyfikatory dostępu członków do faktycznych członków CloudMirrorStorage: SignedIn,
AccessTokenAsync, SignInAsync, SignOutAsync, AccountLabel, AuthSettings, SignInLabelKey, SignInWorkingKey; jeśli nazwa lub sygnatura się różni, użyj faktycznej z kodu);
MegaApiClientAdapter.cs jako ATRAPA: `internal sealed class MegaApiClientAdapter : IMegaClient` z konstruktorem (HttpMessageHandler handler, TimeSpan timeout), który nic nie robi i nie rzuca,
a wszystkie metody rzucają NotSupportedException("Adapter MEGA powstaje w etapie 7b");
wpis w ImageStudio.slnx (folder /Storage/), ProjectReference w ImageStudio/ImageStudio.csproj (Private="$(PluginsInBundle)") i w ImageStudio.Tests/ImageStudio.Tests.csproj (bez Private);
klucze tłumaczeń z tabeli planu do obu słowników, potem `pwsh tools/Check-Translations.ps1 -Fix`;
nowy ImageStudio.Tests/MegaStorageTests.cs z 9 testami z planu (atrapa FakeMegaClient, FakeInteraction, MemorySecrets jak w CloudMirrorBaseTests: są internal);
w ImageStudio.Tests/StoragePluginDiscoveryTests.cs zmień oczekiwaną listę Id na ["local","googledrive","mega","onedrive"].
NIE rób w 7a: Directory.Packages.props, CloudLiveTests.cs, HttpClientWebClient, żadnej zależności NuGet. NIE zmieniaj ServiceCollectionExtensions, UserDataCleanup, MediaStorageHost, StorageSettingsTab ani niczego w ImageStudio/Composition|Configuration|Services|ViewModels:
wtyczka ma się odkryć sama; gdyby to było konieczne, STOP i zgłoś.
Hasło nie może trafić do sekretów, logów ani komunikatów (test Sign_in_with_email_and_password_stores_only_the_email_and_session to sprawdza).
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`; `Test-Path ImageStudio/bin/Debug/net10.0-windows/storage/ImageStudio.Storage.Mega.dll`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~MegaStorageTests|FullyQualifiedName~StoragePluginDiscoveryTests|FullyQualifiedName~CloudMirrorBaseTests|FullyQualifiedName~CloudStorageTests"`;
`dotnet test ImageStudio.Tests`; `pwsh tools/Check-Translations.ps1 -Strict`; `git diff --stat` (brak zmian w ImageStudio/Composition|Configuration|Services|ViewModels).
Jeśli aplikacja ImageStudio jest uruchomiona i pliki są zablokowane, buduj i testuj z `-p:BaseOutputPath=C:\Temp\is-build\` i nie zabijaj procesu.
Nie commituj. Raport: pliki, wyniki komend (liczby testów), odstępstwa.
```

### ZLECENIE DLA HAIKU (etap 7b)

```
Wykonaj CZĘŚĆ 7b ETAPU 7 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Reguły dla każdego wykonawcy", cały "Etap 7" z "Fakty z etapu 0"; część 7a jest zrobiona i zacommitowana).
ZAKRES 7b (tylko to):
1. Directory.Packages.props: <PackageVersion Include="MegaApiClient" Version="1.10.5" />, <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" /> oraz <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
   (przypięcie przechodniej zależności bez luki NU1903); w ImageStudio.Storage.Mega.csproj dodaj <PackageReference Include="MegaApiClient" /> (bez Version).
2. Zanim napiszesz adapter, wypisz refleksją (mały projekt/konsola w katalogu scratchpad, NIE w repo) publiczne człony: CG.Web.MegaApiClient.INode, NodeType, MegaApiClient.LogonSessionToken, ApiException, ApiResultCode, Options
   oraz widoczność typów potrzebnych w porcie transportu (IWebClient, ApiResultCode: muszą być public). Wynik w raporcie. Jeśli IWebClient albo ApiResultCode jest internal albo sygnatury różnią się od "Faktów z etapu 0": STOP.
3. Nowy ImageStudio.Storage.Mega/HttpClientWebClient.cs: `public sealed class HttpClientWebClient : IWebClient`, port klasy WebClient wg akapitu "Transport" w "Faktach z etapu 0"
   (kopia źródła jest w ścieżce podanej w planie; to MIT: zostaw komentarz atrybucji na górze pliku). Konstruktor (HttpMessageHandler handler, TimeSpan timeout).
4. Prawdziwy MegaApiClientAdapter.cs (zastąp atrapę) wg punktów planu: klient `new MegaApiClient(new HttpClientWebClient(handler, timeout))`, SemaphoreSlim wokół wywołań, sesja jako JSON własnego DTO (SessionId, MasterKey),
   wznowienie `LoginAsync(LogonSessionToken)` gdy sesja się zmienia, folder aplikacji tworzony przy pierwszym użyciu (z blokadą), Upload zastępuje plik o tej nazwie, Download zwraca null gdy brak, Delete trwale (moveToTrash: false) i bez błędu, gdy pliku brak,
   ApiException z ApiResultCode.TwoFactorAuthenticationError -> MegaMfaRequiredException; hasło nie jest polem, nie trafia do komunikatów ani logów.
5. Nowy ImageStudio.Tests/HttpClientWebClientTests.cs (bez sieci, atrapa HttpMessageHandler): Post_json_sends_content_type_and_hashcash_header; Payment_required_with_hashcash_header_is_returned_as_the_api_error_array;
   Server_too_busy_is_returned_as_the_retry_code; Requests_go_through_the_supplied_handler.
6. W ImageStudio.Tests/CloudLiveTests.cs dopisz tylko test Mega z planu (LiveCloudFact); nie zmieniaj reszty pliku.
NIE zmieniaj niczego w ImageStudio/Composition|Configuration|Services|ViewModels. Hasła nie loguj.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1` (w logu NIE ma być NU1903); `dotnet list ImageStudio.Storage.Mega package --include-transitive --vulnerable`;
`Test-Path ImageStudio/bin/Debug/net10.0-windows/MegaApiClient.dll`; `(Get-Item ImageStudio/bin/Debug/net10.0-windows/Newtonsoft.Json.dll).VersionInfo.FileVersion` (13.x);
`Test-Path ImageStudio/bin/Debug/net10.0-windows/storage/ImageStudio.Storage.Mega.dll`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~HttpClientWebClientTests|FullyQualifiedName~MegaStorageTests|FullyQualifiedName~StoragePluginDiscoveryTests"`; `dotnet test ImageStudio.Tests`;
`pwsh tools/Check-Translations.ps1 -Strict`; `dotnet publish ImageStudio -p:PublishProfile=Portable -o C:\Temp\is-pub` (ścieżkę w bashu podaj w cudzysłowie lub użyj PowerShell).
Jeśli aplikacja ImageStudio jest uruchomiona i pliki są zablokowane, buduj i testuj z `-p:BaseOutputPath=C:\Temp\is-build\` i nie zabijaj procesu.
Stań przy warunku STOP (typ internal, brak MegaApiClient.dll w wyjściu, NU1903 po przypięciu, potrzeba zmiany kodu aplikacji). Nie commituj.
Raport: wynik refleksji (człony INode/NodeType/Options), pliki, wyniki komend, odstępstwa, niepewności co do API.
```

### (zastąpione podziałem 7a/7b, nie używać) ZLECENIE DLA HAIKU (etap 7, całość)

```
Wykonaj ETAP 7 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md
(przeczytaj "Kontrakty", "Przepływ logowania", "Reguły dla każdego wykonawcy" i cały "Etap 7" z "Faktami z etapu 0").
Utwórz projekt ImageStudio.Storage.Mega dokładnie wg listy plików; kod MegaStorage i IMegaClient przepisz z planu,
MegaApiClientAdapter napisz wg punktów planu, używając WYŁĄCZNIE sygnatur z "Faktów z etapu 0".
Nie zmieniaj ServiceCollectionExtensions, UserDataCleanup, MediaStorageHost ani StorageSettingsTab: wtyczka ma się
odkryć sama. Hasła nie zapisuj nigdzie, nie loguj, nie dołączaj do komunikatów. Klucze z tabeli dopisz do obu
słowników, potem `pwsh tools/Check-Translations.ps1 -Fix`.
Weryfikacja: `dotnet build ImageStudio.slnx -m:1`; `Test-Path ImageStudio/bin/Debug/net10.0-windows/MegaApiClient.dll`;
`Test-Path ImageStudio/bin/Debug/net10.0-windows/storage/ImageStudio.Storage.Mega.dll`;
`dotnet test ImageStudio.Tests --filter "FullyQualifiedName~MegaStorageTests|FullyQualifiedName~StoragePluginDiscoveryTests|FullyQualifiedName~CloudMirrorBaseTests|FullyQualifiedName~CloudStorageTests"`;
`dotnet test ImageStudio.Tests`; `pwsh tools/Check-Translations.ps1 -Strict`;
`dotnet publish ImageStudio -p:PublishProfile=Portable -o C:\Temp\is-pub`.
Stań przy warunku STOP. Nie commituj. Raport: pliki, wyniki komend, odstępstwa, niepewności co do API biblioteki.
```

### KONTROLA SESJI GŁÓWNEJ (etap 7) — kontrola kodu osobiście (hasła, sesja, zależność zewnętrzna)

```powershell
dotnet build ImageStudio.slnx -m:1
dotnet test ImageStudio.Tests
pwsh tools/Check-Translations.ps1 -Strict
git diff --stat      # brak zmian w ImageStudio/Composition|Configuration|Services|ViewModels
git diff ImageStudio.Storage.Mega Directory.Packages.props ImageStudio/ImageStudio.csproj
Select-String -Path ImageStudio.Storage.Mega/*.cs -Pattern "password" -Context 0,1
```

W diffie: `password` występuje tylko w `SignInAsync` i w wywołaniu `LoginAsync`; adapter nie trzyma hasła w polu; sesja serializowana tylko do `Secrets`; handler z kontekstu używany; `SemaphoreSlim` wokół wywołań; wersja paczki z etapu 0. Ręcznie (opcjonalnie): test na żywo `$env:IMAGESTUDIO_LIVE_CLOUD = "1"; dotnet test ImageStudio.Tests --filter "FullyQualifiedName~CloudLiveTests.Mega"` po zalogowaniu w aplikacji.

---

## Etap 8: dokumentacja i skill „nowy moduł chmury”

**Pliki:**

- zmiana `docs/dev/architecture.md` (sekcje „Magazyn wyników” i „Wtyczki chmurowe”)
- nowy `docs/storage-mega.md` (dla użytkownika; wzór `docs/storage-onedrive.md`)
- zmiana `docs/dev/testing.md` (sekcja testów na żywo: MEGA; atrapy `MegaStorageTests`, `CloudMirrorBaseTests`)
- zmiana `docs/dev/tabs-and-settings.md` (akapit czyszczenia: „lokalne dane logowania magazynów OneDrive/Google Drive” → „sekrety wszystkich wtyczek magazynu (pliki `secrets-storage-*.bin`: logowania, sesje, kolejki wysyłek)”)
- nowy `.claude/skills/new-storage-module/SKILL.md`
- zmiana `CLAUDE.md` (wiersz tabeli + pozycja na liście skilli)

**Treść `architecture.md` (do zapisania, zwięźle):** odkrywanie (metadane `ImageStudio.StoragePlugin`, `StoragePluginDiscovery`, konstruktor `(StoragePluginContext)`, porządek, `local` pierwsza); kontrakt logowania (`StorageFieldKind`, `Transient`, `RunActionAsync` z `inputs` i `IStorageInteraction`, `StoragePrompt`); podział `CloudMirrorStorage`/`OAuthCloudStorage`; trwała kolejka (`storage-<id>-pending`, `Resume`); `Owner(StorageId)`; `StatusChanged`; nazwy sekretów tylko `storage-…` (strażnik w `StorageSecrets`, czyszczenie po wzorcu). Usunąć zdania nieaktualne: „Nowa wtyczka = rejestracja `IMediaStoragePlugin` w `AddImageStudioCore`”, „Kolejka zadań jest tylko w pamięci”, „błąd wysyłki … po ponownym otwarciu”.

**`docs/storage-mega.md`:** logowanie (e-mail, hasło, 2FA), co jest zapisywane (sesja DPAPI, nie hasło), folder `ImageStudio`, wylogowanie unieważnia sesję, praca offline (kolejka), czyszczenie danych, ograniczenia (nieoficjalna biblioteka, brak listowania plików z innych komputerów), test na żywo.

**Skill `.claude/skills/new-storage-module/SKILL.md`:**

```markdown
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
```

**`CLAUDE.md`:** w tabeli „Gdzie czytać dalej” wiersz `| Magazyn wyników, moduły chmury (OneDrive, Google Drive, MEGA), logowanie w zakładce Zapis | [docs/dev/architecture.md](docs/dev/architecture.md) |`; na liście skilli: `- **`new-storage-module`**: nowy moduł magazynu wyników / chmury (projekt `ImageStudio.Storage.*`, odkrywany sam), z listą kontrolną (sekrety, logowanie, testy).`

**Kryteria akceptacji:** pliki istnieją, CRLF; `dotnet test ImageStudio.Tests` zielone (testy nie czytają dokumentacji, ale `LocalizationTests` przechodzi bez zmian); brak opisów niezgodnych z kodem.

**STOP:** gdy w kodzie coś działa inaczej niż w planie (np. inna kolejność wtyczek): opisz kod, nie plan, i zgłoś różnicę.

### ZLECENIE DLA HAIKU (etap 8)

```
Wykonaj ETAP 8 z c:\Users\przem\source\ImageStudio\docs\superpowers\plans\2026-11-25-modul-chmury-mega.md.
Przed pisaniem przeczytaj aktualny kod: ImageStudio.Storage.Core/MediaStorage.cs, ImageStudio.Storage.Cloud/*.cs,
ImageStudio.Storage.Mega/*.cs, ImageStudio/Composition/StoragePluginDiscovery.cs, ImageStudio/Services/MediaStorageHost.cs,
ImageStudio/Services/StorageSecrets.cs; dokumentacja ma opisywać kod, nie plan. Styl: po polsku, zwięźle, listy zamiast
długich akapitów (jak docs/dev/*.md). Skill i wpisy CLAUDE.md przepisz z planu. Pliki z CRLF. Nie zmieniaj kodu.
Weryfikacja: `dotnet test ImageStudio.Tests`; `git diff --stat` (tylko pliki z listy etapu). Nie commituj.
Raport: pliki, rozbieżności kod/plan.
```

### KONTROLA SESJI GŁÓWNEJ (etap 8)

`git diff --stat` (tylko dokumenty, skill, `CLAUDE.md`); przeczytać `docs/storage-mega.md` i nowy akapit `architecture.md` pod kątem zgodności z kodem; `CLAUDE.md` bez rozrastania poza dwa wpisy.

---

## Ryzyka i otwarte pytania

1. **MegaApiClient** (nieoficjalna, możliwie nieutrzymywana): zmiana protokołu MEGA może zepsuć logowanie bez naszej winy. Bramka w etapie 0; adapter za `IMegaClient` ogranicza koszt wymiany biblioteki.
2. **Target MSBuild** (`BeforeTargets="GetAssemblyAttributes"`, batching po `StorageDll`): jeśli SDK zmieni nazwy targetów, metadane znikną i wtyczki chmurowe też (testy `StoragePluginDiscoveryTests` to wychwycą). Rezerwa: wygenerowany plik `.cs` z listą nazw w `obj`.
3. **Zależność NuGet wtyczki przy `Private=false`**: zakładam, że `MegaApiClient.dll` trafi do katalogu aplikacji jako przechodnia zależność paczki; weryfikacja w etapie 7 (STOP, gdy nie).
4. **Kolejka w sekretach**: każdy zapis wyniku = zapis DPAPI pliku kolejki; przy setkach wyników na raz to sekundy narzutu. Oznaczone `ponytail:`.
5. **Kolejność wtyczek na liście** zmienia się (Google Drive przed OneDrive). Do akceptacji przez użytkownika; alternatywa: dodatkowe `Order` w kontrakcie.
6. **Wzorzec nazw sekretów zamiast listy od wtyczki**: odstępstwo od dosłownego „sekrety pochodzące od wtyczki”; strażnik w `StorageSecrets` gwarantuje, że nic nie wymknie się czyszczeniu. Do akceptacji.
7. **Sesja MEGA = klucz główny konta** (token sesji zawiera materiał klucza): w DPAPI użytkownika jest tak samo chroniona jak tokeny OAuth, ale jej wyciek daje dostęp do plików; „Wyloguj” unieważnia ją na serwerze.
8. **Ponowna wysyłka po restarcie** obejmuje też pliki z nieudaną wysyłką (dziś przepadają): zmiana zachowania OneDrive/Google na korzyść danych, mieści się w decyzji 4a.

## Szacunek

9 etapów (0-8). Największe i najbardziej ryzykowne: **etap 7** (nowa zależność, adapter na nieznanym API; podzielić: 7a projekt + `IMegaClient` + `MegaStorage` + testy na atrapie, 7b adapter + test na żywo), **etap 3** (MSBuild + refleksja + czyszczenie danych; można podzielić: 3a odkrywanie i DI, 3b sekrety po prefiksie i `UserDataCleanup`), **etap 1** (VM z prośbą o kod, wątki). Etapy 2, 4, 5, 6 są średnie i mechaniczne; 8 mały.

## Dla sesji głównej

- **Kolejność:** etap 0 (Haiku z WebFetch, nie dotyka repozytorium) można puścić od razu; równolegle w repozytorium idą kolejno 1 → 2 → 3 → 4 → 5 → 6, każdy z kontrolą i commitem przed następnym. Etap 7 dopiero po GO z etapu 0 i po 6; etap 8 na końcu (przy STOP MEGA: etap 8 bez `docs/storage-mega.md` i bez MEGA w opisach).
- **Agent:** `Agent` z `model: "haiku"`, `subagent_type: "general-purpose"`, prompt = blok „ZLECENIE DLA HAIKU” danego etapu. Po raporcie: „KONTROLA SESJI GŁÓWNEJ”, potem commit (np. „Etap 3: odkrywanie wtyczek magazynu przez refleksję”).
- **Osobista kontrola kodu (nie tylko testy):** etap 1 (`StorageOption`: przepływ hasła i kodu, wątki), etap 3 (refleksja, `StorageSecrets`, `UserDataCleanup` i bramka zapisów), etap 4 (kolejka: brak tokenów w kolejce, blokady), etap 7 (hasło, sesja, adapter, wersja paczki). Etapy 2, 5, 6, 8: wystarczy diff i testy.
- **Decyzje czekające na użytkownika:** wynik bramki etapu 0 (zwłaszcza GO-Z-ZASTRZEŻENIEM: brak 2FA albo brak wstrzyknięcia handlera); akceptacja nowej kolejności wtyczek (ryzyko 5); akceptacja wzorca nazw sekretów zamiast listy od wtyczki (ryzyko 6); czy usuwanie w MEGA ma iść do kosza (domyślnie trwale, jak OneDrive/Google).
