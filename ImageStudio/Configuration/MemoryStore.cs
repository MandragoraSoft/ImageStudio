using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Pamięć czatu w pliku memory.json w katalogu danych użytkownika (kontrakt IMemoryStore: ImageStudio.Tools.Core)
public sealed class FileMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string? FilePath => Path.Combine(AppPaths.UserDataDir, "memory.json");

    public IReadOnlyList<MemoryItem> Load()
    {
        if (!File.Exists(FilePath)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(FilePath!)) ?? [];
        }
        catch (JsonException)
        {
            return [];  // uszkodzony plik nie blokuje czatu
        }
    }

    public void Save(IEnumerable<MemoryItem> items)
    {
        var snapshot = items.ToList();
        UserDataMutationGate.TryWrite(AppPaths.UserDataDir, FilePath!, () =>
        {
            Directory.CreateDirectory(AppPaths.UserDataDir);
            File.WriteAllText(FilePath!, JsonSerializer.Serialize(snapshot, Options));
        });
    }
}
