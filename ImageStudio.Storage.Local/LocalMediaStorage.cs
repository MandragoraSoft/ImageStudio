using System.IO;
using ImageStudio.Storage;

namespace ImageStudio.Storage.Local;

// Domyślny magazyn: foldery Obrazy, Wideo i Muzyka\ImageStudio; lokalizacja to pełna ścieżka pliku
public sealed class LocalMediaStorage : IMediaStorage
{
    public string Id => "local";
    public string DisplayName => "Dysk lokalny";

    // Katalog na rodzaj; testy i ustawienia mogą go podmienić
    public Dictionary<MediaKind, string> Roots { get; } = new()
    {
        [MediaKind.Image] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ImageStudio"),
        [MediaKind.Video] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ImageStudio"),
        [MediaKind.Audio] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ImageStudio"),
    };

    public async Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var dir = Roots[kind];
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(fileName));
        await File.WriteAllBytesAsync(path, data, ct);
        return path;
    }

    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => File.ReadAllBytesAsync(location, ct);
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Task.FromResult(File.Exists(location));

    public Task DeleteAsync(string location, CancellationToken ct = default)
    {
        File.Delete(location); // nieistniejący plik nie zgłasza błędu
        return Task.CompletedTask;
    }

    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => Task.FromResult(location);
}
