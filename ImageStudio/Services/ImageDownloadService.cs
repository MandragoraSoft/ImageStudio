using System.IO;
using System.Net.Http;
using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Pobiera obraz z linku do pliku w katalogu danych, żeby referencje były zwykłymi ścieżkami.
// Osobny HttpClient bez klucza API: klucz nie idzie na obce hosty.
public sealed class ImageDownloadService
{
    private const long MaxBytes = 25L * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Podaj adres http:// lub https://.");

        using var response = await _http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new InvalidOperationException("Obraz jest większy niż 25 MB.");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException("Obraz jest większy niż 25 MB.");

        // Sprawdzamy sygnaturę pliku, nie nagłówek Content-Type (serwery bywają niedokładne)
        var ext = bytes switch
        {
            [0xFF, 0xD8, ..] => ".jpg",
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
            _ => throw new InvalidOperationException("Link nie wskazuje na obraz JPG, PNG ani WEBP."),
        };

        // Orientacja z EXIF: zapisujemy wersję wyprostowaną (po zmianie format to PNG)
        var upright = ImageOrientation.Normalize(bytes);
        var outExt = ReferenceEquals(upright, bytes) ? ext : ".png";

        var dir = Path.Combine(AppPaths.UserDataDir, "refs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}{outExt}");
        await File.WriteAllBytesAsync(path, upright, ct);
        return path;
    }
}
