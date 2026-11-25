using ImageStudio.Configuration;
using ImageStudio.Storage;

namespace ImageStudio.Services;

// Aktywna wtyczka magazynu wyników: ViewModele widzą jeden IMediaStorage, a wybór wtyczki i jej ustawienia
// (UserState.StorageId, StorageConfig) zmienia zakładka Zapis w Ustawieniach. Zapis stanu na dysk robi MainViewModel
public sealed class MediaStorageHost : IMediaStorage
{
    public MediaStorageHost(IEnumerable<IMediaStoragePlugin> plugins)
    {
        Plugins = plugins.ToList();
        Active = Plugins[0]; // pierwsza zarejestrowana wtyczka jest domyślna
    }

    public IReadOnlyList<IMediaStoragePlugin> Plugins { get; }
    public IMediaStoragePlugin Active { get; private set; }

    // Nieznana wtyczka (usunięta z aplikacji) wraca do domyślnej
    public void Load(UserState state) => Apply(state.StorageId, state.StorageConfig);

    public void Apply(string? activeId, IReadOnlyDictionary<string, Dictionary<string, string>>? config)
    {
        foreach (var plugin in Plugins)
            if (config is not null && config.TryGetValue(plugin.Id, out var values)) plugin.Configure(values);
        Active = Plugins.FirstOrDefault(p => p.Id == activeId) ?? Plugins[0];
    }

    // Konfiguracja do pliku stanu: bez pól haseł i pól nietrwałych (te zapisuje wtyczka w sekretach albo wcale)
    public Dictionary<string, Dictionary<string, string>> WithoutSecrets(IReadOnlyDictionary<string, Dictionary<string, string>> config) =>
        config.ToDictionary(p => p.Key, p =>
        {
            var secret = Plugins.FirstOrDefault(x => x.Id == p.Key)?.Settings.Where(s => s.IsSecret || s.Transient).Select(s => s.Key).ToHashSet() ?? [];
            return p.Value.Where(v => !secret.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
        });

    public Task<string> WriteAsync(MediaKind kind, string fileName, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        Active.WriteAsync(kind, fileName, data, ct);

    public Task<byte[]> ReadAsync(string location, CancellationToken ct = default) => Active.ReadAsync(location, ct);
    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Active.ExistsAsync(location, ct);
    public Task DeleteAsync(string location, CancellationToken ct = default) => Active.DeleteAsync(location, ct);
    public Task<string> GetLocalPathAsync(string location, CancellationToken ct = default) => Active.GetLocalPathAsync(location, ct);
}
