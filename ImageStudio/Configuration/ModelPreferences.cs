using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Configuration;

// Preferencje modeli jednego dostawcy: tryb listy (wszystkie / tylko preferowane) i lista preferowanych id
public sealed class ProviderModelPrefs
{
    public bool PreferredOnly { get; set; }

    // Preferowany model tekstowy użytkownika (ulepszanie promptów, czat); null = domyślny dostawcy
    public string? DefaultTextModel { get; set; }
    public HashSet<string> Preferred { get; set; } = [];
}

// Kontrakt zapisu preferencji: %LOCALAPPDATA%\ImageStudio\models.json, klucz = id dostawcy AI
public sealed class ModelPreferences
{
    public Dictionary<string, ProviderModelPrefs> Providers { get; set; } = [];

    // Własna ścieżka pliku zamiast profilu użytkownika (testy); null = domyślny models.json
    [JsonIgnore]
    public string? PathOverride { get; set; }

    private static string DefaultPath => Path.Combine(AppPaths.UserDataDir, "models.json");

    public ProviderModelPrefs For(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var prefs))
            Providers[providerId] = prefs = new ProviderModelPrefs();
        return prefs;
    }

    public static ModelPreferences Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (!File.Exists(file)) return new ModelPreferences { PathOverride = path };

        try
        {
            var loaded = JsonSerializer.Deserialize<ModelPreferences>(File.ReadAllText(file)) ?? new ModelPreferences();
            loaded.PathOverride = path;
            return loaded;
        }
        catch (JsonException)
        {
            // Uszkodzony plik nie blokuje startu; lista modeli zostaje pełna
            return new ModelPreferences { PathOverride = path };
        }
    }

    public void Save()
    {
        var file = PathOverride ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
