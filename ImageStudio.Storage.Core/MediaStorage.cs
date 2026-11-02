namespace ImageStudio.Storage;

public enum MediaKind { Image, Video, Audio }

// Kontrakt wtyczki magazynu wyników (obrazy, wideo, dźwięk) w osobnej bibliotece ImageStudio.Storage.*, jak dostawcy w
// ImageStudio.Providers.*. "Lokalizacja" to nieprzezroczysty tekst wybrany przez wtyczkę (dla dysku ścieżka, dla chmury
// np. identyfikator pliku); aplikacja tylko go przechowuje (GalleryEntry.FilePath) i oddaje wtyczce.
public interface IMediaStorage
{
    string Id { get; }
    string DisplayName { get; }

    // Zapisuje plik i zwraca jego lokalizację; fileName to propozycja aplikacji (bez katalogu), wtyczka może ją zmienić
    Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task<byte[]> ReadAsync(string location, CancellationToken ct = default);
    Task<bool> ExistsAsync(string location, CancellationToken ct = default);

    // Brak pliku nie jest błędem
    Task DeleteAsync(string location, CancellationToken ct = default);

    // Ścieżka lokalna do odtwarzania, miniatur i kopiowania; wtyczka zdalna pobiera plik do pamięci podręcznej
    Task<string> GetLocalPathAsync(string location, CancellationToken ct = default);
}
