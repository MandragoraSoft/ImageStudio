using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Configuration;

// Zestaw ustawień: prompt, model, rozmiar, parametry obrazu i ulepszania promptu.
// Nazwa i opis są w pliku; nazwa pliku jest generowana automatycznie i nie zmienia się przy zmianie nazwy.
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // "generate" lub "edit": zestaw wczytuje się do odpowiedniej zakładki
    public string Kind { get; set; } = "generate";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string Prompt { get; set; } = "";
    public string? Model { get; set; }
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public string? SeedText { get; set; }
    public string? NegativePrompt { get; set; }
    public string? Quality { get; set; }

    public string? EnhancerModel { get; set; }
    public string? EnhancerInstruction { get; set; }
    public Dictionary<string, ParamState> Parameters { get; set; } = [];

    [JsonIgnore]
    public string FilePath { get; set; } = "";

    // Etykiety do siatki zestawów (nie trafiają do pliku)
    [JsonIgnore]
    public string KindLabel => Kind == "edit" ? "Edycja" : "Generowanie";

    [JsonIgnore]
    public string UpdatedLocal => UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    [JsonIgnore]
    public string SizeLabel => $"{Width} × {Height}";
}

// Magazyn zestawów: jeden plik JSON na zestaw w %LOCALAPPDATA%\ImageStudio\presets
public static class PresetStore
{
    private static string Directory => Path.Combine(AppPaths.UserDataDir, "presets");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Najnowsze na górze; uszkodzone pliki są pomijane, żeby nie blokowały listy
    public static List<Preset> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var presets = new List<Preset>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(file));
                if (preset is null) continue;
                preset.FilePath = file;
                presets.Add(preset);
            }
            catch (JsonException)
            {
                // pomijamy uszkodzony plik
            }
        }
        return presets.OrderByDescending(p => p.UpdatedUtc).ToList();
    }

    // Nazwa pliku: data-czas plus krótki losowy znacznik, np. 20261031-134522-3fa9c1.json
    public static Preset Create(Preset preset)
    {
        System.IO.Directory.CreateDirectory(Directory);
        preset.FilePath = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..31] + ".json");
        Save(preset);
        return preset;
    }

    // Aktualizacja zapisuje w tym samym pliku; data zmiany przesuwa zestaw na górę listy
    public static void Save(Preset preset)
    {
        preset.UpdatedUtc = DateTime.UtcNow;
        File.WriteAllText(preset.FilePath, JsonSerializer.Serialize(preset, Options));
    }

    public static void Delete(Preset preset)
    {
        if (File.Exists(preset.FilePath)) File.Delete(preset.FilePath);
    }
}
