using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Jedna zapamiętana informacja (o użytkowniku albo preferencja) z pamięci czatu
public sealed record MemoryItem(string Id, string Text, DateTimeOffset Created);

// Pamięć czatu między sesjami: model zapisuje ją narzędziem memory_save, a użytkownik może ją przejrzeć i usunąć (memory.json)
public interface IMemoryStore
{
    IReadOnlyList<MemoryItem> Load();
    void Save(IEnumerable<MemoryItem> items);

    // Ścieżka pliku do podglądu; null = brak pliku (magazyn w pamięci)
    string? FilePath => null;
}

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
        Directory.CreateDirectory(AppPaths.UserDataDir);
        File.WriteAllText(FilePath!, JsonSerializer.Serialize(items.ToList(), Options));
    }
}
