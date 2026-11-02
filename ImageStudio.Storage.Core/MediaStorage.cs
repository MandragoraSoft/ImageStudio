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
// IsFolder dodaje do pola przycisk wyboru folderu
public sealed record StorageSetting(string Key, string LabelKey, string Value, bool IsFolder = false);

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

    // Folder na dysku, w którym wtyczka trzyma pliki danego rodzaju (null = brak, np. chmura)
    string? LocalFolder(MediaKind kind) => null;
}
