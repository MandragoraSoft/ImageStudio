using System.Text.RegularExpressions;
using ImageStudio.Configuration;
using ImageStudio.Storage;

namespace ImageStudio.Services;

// Aktywna wtyczka magazynu wyników: ViewModele widzą jeden IMediaStorage, a wybór wtyczki i jej ustawienia
// (UserState.StorageId, StorageConfig) zmienia zakładka Zapis w Ustawieniach. Zapis stanu na dysk robi MainViewModel
public sealed class MediaStorageHost : IMediaStorage
{
    // Id trafia do nazw sekretów i plików (storage-<id>): tylko małe litery i cyfry; duplikat traci pierwszeństwo
    private static readonly Regex ValidId = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);

    public MediaStorageHost(IEnumerable<IMediaStoragePlugin> plugins)
    {
        Plugins = plugins.Where(p => ValidId.IsMatch(p.Id)).DistinctBy(p => p.Id).ToList();
        Active = Plugins[0]; // pierwsza zarejestrowana wtyczka jest domyślna
    }

    public IReadOnlyList<IMediaStoragePlugin> Plugins { get; }
    public IMediaStoragePlugin Active { get; private set; }

    // Nieznana wtyczka (usunięta z aplikacji) wraca do domyślnej; wszystkie wtyczki (nie tylko aktywna) wznawiają
    // zaległe wysyłki, bo kolejka poprzedniej też ma dojść
    public void Load(UserState state)
    {
        Apply(state.StorageId, state.StorageConfig);
        foreach (var plugin in Plugins) plugin.Resume();
    }

    public void Apply(string? activeId, IReadOnlyDictionary<string, Dictionary<string, string>>? config)
    {
        foreach (var plugin in Plugins)
            if (config is not null && config.TryGetValue(plugin.Id, out var values)) plugin.Configure(values);
        Active = Plugins.FirstOrDefault(p => p.Id == activeId) ?? Plugins[0];
    }

    // Wtyczka, która zapisała plik (GalleryEntry.StorageId). Brak identyfikatora (starsze wpisy) i wtyczka usunięta
    // z aplikacji = dysk lokalny: wtyczki chmurowe trzymają lustro w folderach lokalnych, więc ścieżka i tak jest lokalna
    public IMediaStoragePlugin Owner(string? storageId) =>
        Plugins.FirstOrDefault(p => p.Id == storageId) ?? Plugins.FirstOrDefault(p => p.Id == "local") ?? Plugins[0];

    // Konfiguracja do pliku stanu: bez pól haseł i pól nietrwałych (te zapisuje wtyczka w sekretach albo wcale)
    public Dictionary<string, Dictionary<string, string>> WithoutSecrets(IReadOnlyDictionary<string, Dictionary<string, string>> config) =>
        config.ToDictionary(p => p.Key, p =>
        {
            var secret = Plugins.FirstOrDefault(x => x.Id == p.Key)?.Settings.Where(s => s.IsSecret || s.Transient).Select(s => s.Key).ToHashSet() ?? [];
            return p.Value.Where(v => !secret.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
        });

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return a == b; }
    }

    private static readonly string[] MovableKinds = ["generate", "edit", "video"];

    // Przenosi plik wpisu do innego magazynu: najpierw zapis w celu, dopiero potem zmiana wpisu i kasowanie starej kopii.
    // Błąd kasowania starej kopii nie cofa przeniesienia (plik zostaje w poprzednim magazynie)
    public async Task MoveAsync(GalleryEntry entry, IMediaStoragePlugin target, CancellationToken ct = default)
    {
        if (!MovableKinds.Contains(entry.Kind)) throw new ArgumentException($"Wpisu typu {entry.Kind} nie można przenieść.", nameof(entry));
        var from = Owner(entry.StorageId);
        if (target.Id == from.Id) throw new ArgumentException("Plik jest już w tym magazynie.", nameof(target));

        var bytes = await from.ReadAsync(entry.FilePath, ct);
        var kind = entry.Kind == "video" ? MediaKind.Video : MediaKind.Image;
        var location = await target.WriteAsync(kind, Path.GetFileName(entry.FilePath), bytes, ct);
        // Weryfikacja przed zmianą wpisu: cel musi mieć plik o tej samej długości, inaczej stara kopia zostaje nietknięta
        if (!await target.ExistsAsync(location, ct) || (await target.ReadAsync(location, ct)).Length != bytes.Length)
            throw new IOException($"Zapis w magazynie {target.Id} nie zachował pliku: {location}");

        var oldLocation = entry.FilePath;
        entry.FilePath = location;
        entry.StorageId = target.Id;
        // Lustro chmury bywa w tym samym folderze co magazyn lokalny: zapis trafił na ten sam plik, więc nie kasujemy go
        if (SamePath(oldLocation, location)) return;
        try
        {
            await from.DeleteAsync(oldLocation, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ponytail: stara kopia zostaje w poprzednim magazynie, bez ponawiania i bez logu
        }
    }

    public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        Active.WriteAsync(kind, fileName, data, ct);

    // Operacje na istniejących wpisach idą przez Owner(entry.StorageId), patrz StudioApi
    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => Active.ReadAsync(location, ct);
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Active.ExistsAsync(location, ct);
    public Task DeleteAsync(string location, CancellationToken ct = default) => Active.DeleteAsync(location, ct);
    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => Active.GetLocalPathAsync(location, ct);
}
