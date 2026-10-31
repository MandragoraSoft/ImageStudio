using System.IO;

namespace ImageStudio.Configuration;

// Klucz i adres upstreamu z zmiennych środowiskowych albo z pliku .env.
// Zmienne z środowiska mają pierwszeństwo przed .env.
public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://api.uberllm.dev";

    public string ApiKey { get; }
    public string BaseUrl { get; }

    private AppSettings(string apiKey, string baseUrl)
    {
        ApiKey = apiKey;
        BaseUrl = baseUrl;
    }

    public static AppSettings Load()
    {
        var file = FindEnvFile(AppContext.BaseDirectory);
        var fileValues = file is null ? new Dictionary<string, string>() : ParseEnv(File.ReadAllLines(file));

        string Get(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? (fileValues.TryGetValue(key, out var v) ? v : "");

        var baseUrl = Get("UBERLLM_BASE_URL");
        return new AppSettings(
            Get("UBERLLM_API_KEY"),
            string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl);
    }

    // Idzie w górę od katalogu aplikacji, aż znajdzie .env (w dev: katalog projektu nad ImageStudio\)
    private static string? FindEnvFile(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Dictionary<string, string> ParseEnv(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }
        return values;
    }
}
