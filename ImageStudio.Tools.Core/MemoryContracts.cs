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
