using System.IO;
using ImageStudio.Storage;

namespace ImageStudio.Storage.Local;

// Domyślny magazyn: foldery Obrazy, Wideo i Muzyka\ImageStudio; lokalizacja to pełna ścieżka pliku
public sealed class LocalMediaStorage : IMediaStoragePlugin
{
    private static readonly Dictionary<MediaKind, string> Defaults = new()
    {
        [MediaKind.Image] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ImageStudio"),
        [MediaKind.Video] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ImageStudio"),
        [MediaKind.Audio] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ImageStudio"),
    };

    public string Id => "local";
    public string DisplayName => "Dysk lokalny";

    // Katalog na rodzaj; ustawiany też z zakładki Zapis (Configure)
    public Dictionary<MediaKind, string> Roots { get; } = new(Defaults);

    public IReadOnlyList<StorageSetting> Settings =>
    [
        new("image", "storage.local.image", Roots[MediaKind.Image], IsFolder: true),
        new("video", "storage.local.video", Roots[MediaKind.Video], IsFolder: true),
        new("audio", "storage.local.audio", Roots[MediaKind.Audio], IsFolder: true),
    ];

    public void Configure(IReadOnlyDictionary<string, string> values)
    {
        foreach (var kind in Enum.GetValues<MediaKind>())
            if (values.TryGetValue(Key(kind), out var folder))
                Roots[kind] = string.IsNullOrWhiteSpace(folder) ? Defaults[kind] : folder.Trim();
    }

    public string? LocalFolder(MediaKind kind) => Roots[kind];

    private static string Key(MediaKind kind) => kind.ToString().ToLowerInvariant();

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
