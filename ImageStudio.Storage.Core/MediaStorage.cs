namespace ImageStudio.Storage;

public enum MediaKind { Image, Video, Audio }

// Operacje wejścia/wyjścia magazynu wyników (obrazy, wideo, dźwięk). "Lokalizacja" to nieprzezroczysty tekst wybrany
// przez wtyczkę (dla dysku ścieżka, dla chmury np. identyfikator pliku); aplikacja tylko go przechowuje
// (GalleryEntry.FilePath) i oddaje wtyczce.
public interface IMediaStorage
{
    // Zapisuje plik i zwraca jego lokalizację; fileName to propozycja aplikacji (bez katalogu), wtyczka może ją zmienić
    Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task<byte[]> ReadAsync(string location, CancellationToken ct = default);
    Task<bool> ExistsAsync(string location, CancellationToken ct = default);

    // Brak pliku nie jest błędem
    Task DeleteAsync(string location, CancellationToken ct = default);

    // Ścieżka lokalna do odtwarzania, miniatur i kopiowania; wtyczka zdalna pobiera plik do pamięci podręcznej
    Task<string> GetLocalPathAsync(string location, CancellationToken ct = default);
}

// Jedno pole konfiguracji wtyczki. LabelKey to klucz tłumaczenia (brak klucza = wyświetlony tekst klucza);
// IsFolder dodaje do pola przycisk wyboru folderu; IsSecret: wartość trafia do IStorageSecrets, nigdy do pliku
// ustawień (wtyczka zapisuje ją w Configure); Masked: pole z ukrytymi znakami (hasło), gdy fałsz, pole jest jawne
// (np. Client ID, który chcesz zobaczyć, ale nie trzymać w zwykłym pliku)
// Note: krótka informacja pod polem (np. skąd pochodzi wartość)
public sealed record StorageSetting(string Key, string LabelKey, string Value, bool IsFolder = false, bool IsSecret = false,
                                    bool Masked = false, StorageStatus? Note = null);

// Przycisk w zakładce wtyczki (np. "Zaloguj"); wywołanie to RunActionAsync(Key)
public sealed record StorageAction(string Key, string LabelKey);

// Stan do pokazania pod polami: klucz tłumaczenia z opcjonalnym parametrem {0}
public sealed record StorageStatus(string Key, string? Detail = null);

// Sekrety wtyczek (tokeny, hasła) w magazynie aplikacji szyfrowanym DPAPI; wtyczka nie zna pliku ani formatu
public interface IStorageSecrets
{
    string? Load(string name);

    // Pusta wartość usuwa sekret
    void Save(string value, string name);
}

// Wtyczka magazynu w osobnej bibliotece ImageStudio.Storage.* (jak dostawcy w ImageStudio.Providers.*): oprócz operacji
// opisuje swoje ustawienia, które okno Ustawień pokazuje w zakładce "Zapis"
public interface IMediaStoragePlugin : IMediaStorage
{
    string Id { get; }
    string DisplayName { get; }

    // Pola z bieżącymi wartościami
    IReadOnlyList<StorageSetting> Settings { get; }

    // Wartości po kluczach z Settings; pusta wartość = domyślna wtyczki
    void Configure(IReadOnlyDictionary<string, string> values);

    // Folder na dysku, w którym wtyczka trzyma pliki danego rodzaju (null = brak)
    string? LocalFolder(MediaKind kind) => null;

    // Przyciski (np. logowanie), ich obsługa (poprzedzona Configure z bieżącymi wartościami pól), stan i opis pod polami
    IReadOnlyList<StorageAction> Actions => [];
    Task RunActionAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

    // Czy przycisk jest teraz dostępny (np. "Zaloguj" tylko dla niezalogowanego)
    bool CanRun(string key) => true;
    StorageStatus? Status => null;
    string? HelpKey => null;
}
