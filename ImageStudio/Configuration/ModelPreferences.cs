using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Preferencje modeli jednego dostawcy: tryb listy (wszystkie / tylko preferowane) i lista preferowanych id
public sealed class ProviderModelPrefs
{
    public bool PreferredOnly { get; set; }
    public HashSet<string> Preferred { get; set; } = [];
}

// Kontrakt zapisu preferencji: %LOCALAPPDATA%\ImageStudio\models.json, klucz = id dostawcy AI
public sealed class ModelPreferences
{
    public Dictionary<string, ProviderModelPrefs> Providers { get; set; } = [];

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "models.json");

    public ProviderModelPrefs For(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var prefs))
            Providers[providerId] = prefs = new ProviderModelPrefs();
        return prefs;
    }

    public static ModelPreferences Load()
    {
        if (!File.Exists(FilePath)) return new ModelPreferences();

        try
        {
            return JsonSerializer.Deserialize<ModelPreferences>(File.ReadAllText(FilePath)) ?? new ModelPreferences();
        }
        catch (JsonException)
        {
            // Uszkodzony plik nie blokuje startu; lista modeli zostaje pełna
            return new ModelPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
